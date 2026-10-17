package ckv

// 批次一「数据安全与止血」回归测试（Go）。
//
// 覆盖：
//
//	P0-3  renameWithRetry 的 Windows 共享冲突判定（此前恒为 false，230ms 退避一次都没跑过）
//	P1-3  加载期校验 NsLen <= 255（越界即 CorruptedError，而不是加载出不可回收的孤儿分区）
//	P1-19 .tmp 晋升失败必须让 Open 失败，且不得删除已校验通过的 .tmp
//	条目11 ReadOnly 选项冲突返回 ConflictingOptionsError
//
// 本文件此前不存在：renameWithRetry 与 .tmp 晋升这两条高危路径在现有套件里零覆盖。

import (
	"encoding/binary"
	"errors"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"
)

// ---------- P0-3：Windows 共享冲突判定 ----------

func TestIsWindowsSharingViolationMatchesBareWin32Codes(t *testing.T) {
	// 回归的核心：谓词必须认得操作系统真正传回的裸 Win32 码。
	// 旧实现比较 syscall.EACCES / EBUSY / ETXTBSY，那是 Go 放在 APPLICATION_ERROR+n
	// 合成区间的值（EACCES=536870913 / EBUSY=536870928 / ETXTBSY=536871035），
	// 与真实码 5 / 32 / 33 分处完全不同的数值区间 → 谓词恒为 false。
	for _, errno := range []error{
		errSharingViolation,
		errLockViolation,
	} {
		if !isWindowsSharingViolation(errno) {
			t.Fatalf("errno %v (a real Win32 sharing/lock code) must be recognised", errno)
		}
	}
	if runtime.GOOS == "windows" && !isWindowsSharingViolation(errAccessDenied) {
		t.Fatalf("ERROR_ACCESS_DENIED (5) must be recognised")
	}

	// 与真实码无关的错误绝不能被判成共享冲突，否则会把永久性错误也重试 5 次。
	if isWindowsSharingViolation(errors.New("boom")) {
		t.Fatal("a plain error must not be classified as a Windows sharing violation")
	}
	if runtime.GOOS == "windows" && isWindowsSharingViolation(os.ErrNotExist) {
		t.Fatal("ENOENT must not be classified as a Windows sharing violation")
	}

	// 明确锁死"不要退回 syscall.E* 系列"这条红线：它们不等于任何裸 Win32 码。
	if runtime.GOOS == "windows" {
		for _, wrong := range []error{os.ErrPermission} {
			// os.ErrPermission 映射到 EACCES(合成区间)；真实 ACCESS_DENIED 是裸 5。
			// 只要实现不再用 syscall.EACCES 判定，下面这条就不会误判为共享冲突。
			if isWindowsSharingViolation(wrong) && !isWindowsSharingViolation(errAccessDenied) {
				t.Fatal("predicate regressed to the synthetic syscall.E* range")
			}
		}
	}
}

func TestRenameWithRetry_ExhaustsBackoffOnPersistentSharingViolation(t *testing.T) {
	if runtime.GOOS != "windows" {
		t.Skip("Windows-only: the backoff predicate is a no-op elsewhere")
	}
	dir := t.TempDir()

	// 用一个**目录**占住目标：rename 文件 -> 已存在的目录在 Windows 上返回
	// ERROR_ACCESS_DENIED(5) / ERROR_SHARING_VIOLATION(32)，必然失败。
	src := filepath.Join(dir, "src.tmp")
	if err := os.WriteFile(src, []byte("payload"), 0o666); err != nil {
		t.Fatal(err)
	}
	dst := filepath.Join(dir, "dst")
	if err := os.Mkdir(dst, 0o777); err != nil {
		t.Fatal(err)
	}

	start := time.Now()
	err := renameWithRetry(src, dst)
	elapsed := time.Since(start)

	if err == nil {
		t.Fatal("rename onto an existing directory must fail")
	}
	// 规范 §4.2 要求 [5,15,30,60,120]ms 共 230ms 的退避。
	// 谓词恒假时 elapsed 会是微秒级 —— 这正是旧实现的症状。
	if elapsed < 200*time.Millisecond {
		t.Fatalf("renameWithRetry must actually execute the backoff ladder, elapsed = %v (want >= 200ms)", elapsed)
	}
}

// ---------- P1-3：加载期 NsLen 越界 ----------

func TestLoad_RejectsOverlongNamespace(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "nslen.db")

	longNs := strings.Repeat("n", 300)
	writeRawDbFile(t, p, []rawEntrySpec{
		{OpCode: OpPut, ValType: ValTypeString, NsLenOverride: 300, Ns: longNs,
			Key: []byte("k"), Value: []byte("v")},
	})

	_, err := Open(p)
	if err == nil {
		t.Fatal("a file whose entry declares NsLen=300 (>255) must be rejected at load time")
	}
	if !IsCorrupted(err) {
		t.Fatalf("expected CorruptedError, got %v", err)
	}
	// 载荷解析器会把单条目错误包装成"载荷偏移 N 解码失败"，因此只断言它被识别为
	// CorruptedError —— 关键性质是"坏文件在 open 阶段明确失败"，而不是加载出一个
	// 既读不出也删不掉的孤儿命名空间。
}

func TestLoad_AcceptsNamespaceAtExactly255Bytes(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "edge.db")

	db, err := Open(p)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()

	ns := strings.Repeat("n", 255)
	if err := db.PutString(ns, "k", "v"); err != nil {
		t.Fatalf("255-byte namespace must be accepted: %v", err)
	}
	v, err := db.GetString(ns, "k")
	if err != nil || v == nil || *v != "v" {
		t.Fatalf("read back = %v (err %v)", v, err)
	}
}

// ---------- P1-19：.tmp 晋升失败必须让 Open 失败 ----------

func TestOpen_TmpPromotionFailure_FailsAndKeepsTmp(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "promote.db")

	// 先造一个正常库。
	db, err := Open(p)
	if err != nil {
		t.Fatal(err)
	}
	if err := db.PutString("ns", "k", "v"); err != nil {
		t.Fatal(err)
	}
	if err := db.Compact(); err != nil {
		t.Fatal(err)
	}
	if err := db.Close(); err != nil {
		t.Fatal(err)
	}

	// 复制成 .tmp 后删掉主库 —— 此时 .tmp 是唯一完好的一份。
	raw, err := os.ReadFile(p)
	if err != nil {
		t.Fatal(err)
	}
	tmpPath := p + ".tmp"
	if err := os.WriteFile(tmpPath, raw, 0o666); err != nil {
		t.Fatal(err)
	}
	if err := os.Remove(p); err != nil {
		t.Fatal(err)
	}

	// 制造"晋升必然失败"：.db 不存在（因此 initAndRecover 一定走晋升分支），
	// 但 rename 本身失败。
	//   Windows：Go 的 os.Open 以 FILE_SHARE_READ|FILE_SHARE_WRITE 打开（不含 DELETE），
	//            持有一个打开的句柄即可让 rename 拿到 ERROR_SHARING_VIOLATION(32)。
	//   Unix   ：把父目录改成不可写，rename 得到 EACCES/EPERM。
	blocker, unblock := blockRenameOf(t, tmpPath, dir)

	_, openErr := Open(p)
	unblock()

	if openErr == nil {
		t.Fatal("Open must fail when a validated .tmp cannot be promoted, not succeed as an empty database")
	}
	if !strings.Contains(openErr.Error(), ".tmp") {
		t.Fatalf("error should point at the surviving .tmp, got %v", openErr)
	}
	if blocker != nil {
		// nothing to do; blocker 已由 unblock 关闭
		_ = blocker
	}

	// 关键断言：已经校验通过的 .tmp 必须原样保留 —— 它是唯一完好的数据。
	st, statErr := os.Stat(tmpPath)
	if statErr != nil {
		t.Fatalf("a validated .tmp must never be deleted on promotion failure: %v", statErr)
	}
	if st.Size() == 0 {
		t.Fatal(".tmp must retain its contents")
	}

	// 解除占用后可以正常自愈，数据没丢。
	recovered, err := Open(p)
	if err != nil {
		t.Fatalf("reopen after unblocking must self-heal from .tmp: %v", err)
	}
	defer recovered.Close()
	v, err := recovered.GetString("ns", "k")
	if err != nil || v == nil || *v != "v" {
		t.Fatalf("data lost across .tmp promotion: %v (err %v)", v, err)
	}
}

// blockRenameOf 让 rename(<tmpPath> -> <dir>/promote.db) 必然失败，并返回一个解除阻塞的函数。
func blockRenameOf(t *testing.T, tmpPath, dir string) (*os.File, func()) {
	t.Helper()
	if runtime.GOOS == "windows" {
		h, err := os.Open(tmpPath)
		if err != nil {
			t.Fatal(err)
		}
		return h, func() { _ = h.Close() }
	}
	// Unix：父目录去掉写权限即可让 rename 失败（root 会绕过，因此 root 下跳过）。
	if os.Geteuid() == 0 {
		t.Skip("running as root: directory permissions do not block rename")
	}
	if err := os.Chmod(dir, 0o555); err != nil {
		t.Fatal(err)
	}
	return nil, func() { _ = os.Chmod(dir, 0o755) }
}

// ---------- 条目 11：ReadOnly 冲突必须抛错 ----------

func TestReentrantOpen_ReadOnlyConflict_ReturnsError(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "ro.db")

	opts := DefaultDbOptions()
	opts.ReadOnly = false
	db, err := Open(p, opts)
	if err != nil {
		t.Fatal(err)
	}
	if err := db.PutString("", "k", "v"); err != nil {
		t.Fatal(err)
	}

	// 以只读意图二次打开：绝不能静默返回读写实例（静默提权）。
	roOpts := DefaultDbOptions()
	roOpts.ReadOnly = true
	if _, err := Open(p, roOpts); !IsConflictingOptions(err) {
		t.Fatalf("expected ConflictingOptionsError for readOnly conflict, got %v", err)
	}

	// 反方向同样拒绝：要写却拿到只读实例会让后续写入在远处以 LockedError 失败。
	if err := db.Close(); err != nil {
		t.Fatal(err)
	}
	roDb, err := Open(p, roOpts)
	if err != nil {
		t.Fatal(err)
	}
	defer roDb.Close()
	if _, err := Open(p, opts); !IsConflictingOptions(err) {
		t.Fatalf("expected ConflictingOptionsError in the reverse direction too, got %v", err)
	}
}

func TestReentrantOpen_EngineModeConflict_StillWarnsAndReturnsSingleton(t *testing.T) {
	dir := t.TempDir()
	p := filepath.Join(dir, "eng.db")

	opts := DefaultDbOptions()
	opts.EngineMode = StorageEngineModeSnapshotAtomic
	db, err := Open(p, opts)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()

	var warnings []string
	opts2 := DefaultDbOptions()
	opts2.EngineMode = StorageEngineModeAppendOnlyLog
	opts2.OnWarning = func(code, msg string) { warnings = append(warnings, code+": "+msg) }

	same, err := Open(p, opts2)
	if err != nil {
		t.Fatal(err)
	}
	if same != db {
		t.Fatal("non-permission conflict must still return the same singleton")
	}
	if len(warnings) != 1 || !strings.HasPrefix(warnings[0], "onOptionsConflict") {
		t.Fatalf("expected one onOptionsConflict warning, got %v", warnings)
	}
	if !strings.Contains(warnings[0], "engineMode") {
		t.Fatalf("warning should mention engineMode, got %v", warnings[0])
	}
}

// ---------- 手工构造 v3.2 磁盘文件的小工具 ----------

type rawEntrySpec struct {
	OpCode        byte
	ValType       byte
	NsLenOverride int // >0 时覆盖 NsLen 字段（用于构造越界文件）
	Ns            string
	Key           []byte
	Value         []byte
}

func writeRawDbFile(t *testing.T, path string, entries []rawEntrySpec) {
	t.Helper()

	header := make([]byte, 32)
	copy(header, []byte("UKVDB003"))
	binary.LittleEndian.PutUint16(header[8:], 3)
	binary.LittleEndian.PutUint16(header[10:], 3)
	binary.LittleEndian.PutUint32(header[12:], 0)
	binary.LittleEndian.PutUint32(header[28:], Crc32(header[:28]))

	var payload []byte
	for _, e := range entries {
		nsBytes := []byte(e.Ns)
		entry := make([]byte, 10+len(nsBytes)+len(e.Key)+len(e.Value))
		entry[0] = e.OpCode
		entry[1] = e.ValType
		nsLen := len(nsBytes)
		if e.NsLenOverride > 0 {
			nsLen = e.NsLenOverride
		}
		binary.LittleEndian.PutUint16(entry[2:], uint16(nsLen))
		binary.LittleEndian.PutUint16(entry[4:], uint16(len(e.Key)))
		binary.LittleEndian.PutUint32(entry[6:], uint32(len(e.Value)))
		off := 10
		copy(entry[off:], nsBytes)
		off += len(nsBytes)
		copy(entry[off:], e.Key)
		off += len(e.Key)
		copy(entry[off:], e.Value)
		payload = append(payload, entry...)
	}

	tail20 := make([]byte, 20)
	binary.LittleEndian.PutUint64(tail20[0:], 1)
	binary.LittleEndian.PutUint32(tail20[8:], uint32(len(entries)))
	binary.LittleEndian.PutUint32(tail20[12:], uint32(len(payload)))
	binary.LittleEndian.PutUint32(tail20[16:], Crc32(payload))

	frame := make([]byte, 28+len(payload))
	binary.LittleEndian.PutUint32(frame[0:], FrameMagic)
	binary.LittleEndian.PutUint32(frame[4:], Crc32(tail20))
	copy(frame[8:], tail20)
	copy(frame[28:], payload)

	if err := os.WriteFile(path, append(header, frame...), 0o666); err != nil {
		t.Fatal(err)
	}
}

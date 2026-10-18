package ckv

// 规范 §3.4：AOL 尾部自愈截断前必须把整库留证到 `<db>.corrupt.<unix_ms>`。

import (
	"os"
	"path/filepath"
	"testing"
)

func TestTornTailLeavesCorruptEvidence(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "a.db")
	opts := DefaultDbOptions()
	opts.EngineMode = StorageEngineModeAppendOnlyLog

	db, err := Open(path, opts)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	if err := db.PutString("", "k", "v"); err != nil {
		t.Fatalf("put: %v", err)
	}
	db.Close()

	f, err := os.OpenFile(path, os.O_APPEND|os.O_WRONLY, 0o666)
	if err != nil {
		t.Fatalf("append open: %v", err)
	}
	if _, err := f.Write([]byte{0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF}); err != nil {
		t.Fatalf("append: %v", err)
	}
	f.Close()
	damaged, _ := os.ReadFile(path)

	reopened, err := Open(path, opts)
	if err != nil {
		t.Fatalf("reopen (self-heal expected): %v", err)
	}
	if got, _ := reopened.GetString("", "k"); got == nil || *got != "v" {
		t.Fatalf("intact frame lost: %v", got)
	}
	reopened.Close()

	backups, _ := filepath.Glob(path + ".corrupt.*")
	if len(backups) != 1 {
		t.Fatalf("expected exactly one .corrupt evidence file, got %v", backups)
	}
	saved, _ := os.ReadFile(backups[0])
	if string(saved) != string(damaged) {
		t.Fatalf("evidence must be the pre-truncation content (%d vs %d bytes)", len(saved), len(damaged))
	}
	live, _ := os.Stat(path)
	if live.Size() >= int64(len(saved)) {
		t.Fatalf("live file was not truncated: %d >= %d", live.Size(), len(saved))
	}
}

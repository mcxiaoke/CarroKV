package ckv

import (
	"fmt"
	"os"
	"time"
)

// FileLock 是跨进程**连接锁**（C9）。
//
// 自 C9 起锁是内核锁（Unix 上 flock(2)，Windows 上以独占/共享共享模式打开句柄），由
// Open 获取、持有整个连接生命周期。这样一整类失败随之消失：
//
//   - 基于锁文件内容（PID | UUID | CreateTimeMs）的时间戳判活，会把**活着**的持有者
//     误判为僵死并窃取它的锁（一次长 GC、换页或大快照就足够），于是同一份库上出现两个写者；
//   - 崩溃的持有者会留下一个锁文件，后续进程不得不去"推理"它是否有效。
//
// 锁由操作系统持有：进程退出即释放，崩溃的写者不可能把库锁死；活着的写者也不可能被误判。
// 这正是 bbolt（Open 时 flock(db, !readOnly)，持有到连接关闭）与 LMDB（fcntl 咨询锁，
// 随进程退出释放）的做法，两者都没有任何存活探测或窃取协议。
//
// 可写连接取排他锁，只读连接取共享锁：读者之间互不排斥，只排斥写者。
type FileLock struct {
	path string
	file *os.File
}

// AcquireLock 打开 `<db>.lock` 并取得内核锁，最多等待 timeout。
//
// 锁文件**只创建、不删除**：删掉它会让第二个连接锁住一个新的 inode，而第一个仍锁着旧的，
// 双方都不会报错（旧的 Dart 实现就踩过这个坑）。
func AcquireLock(dbPath string, exclusive bool, timeout time.Duration) (*FileLock, error) {
	lockPath := dbPath + ".lock"
	deadline := time.Now().Add(timeout)
	backoff := 10 * time.Millisecond

	for {
		file, err := openLockHandle(lockPath, exclusive)
		if err == nil {
			return &FileLock{path: lockPath, file: file}, nil
		}
		if !isLockContention(err) {
			return nil, newDbError("IoError", "Failed to open lock file "+lockPath, err)
		}
		if time.Now().After(deadline) {
			return nil, NewLockedError(fmt.Sprintf(
				"Failed to acquire the file lock on '%s' within %v: another connection is using this database",
				lockPath, timeout))
		}
		time.Sleep(backoff)
		if backoff < 100*time.Millisecond {
			backoff *= 2
		}
	}
}

// Path 返回锁文件路径（诊断用）。
func (fl *FileLock) Path() string {
	if fl == nil {
		return ""
	}
	return fl.path
}

// Release 释放锁。幂等；锁文件本身保留（见 AcquireLock 的说明）。
func (fl *FileLock) Release() {
	if fl == nil || fl.file == nil {
		return
	}
	_ = closeLockHandle(fl.file)
	fl.file = nil
}

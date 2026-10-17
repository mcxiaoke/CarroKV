//go:build windows

package ckv

import (
	"errors"
	"os"
	"syscall"
)

// openLockHandle 以"共享模式"打开锁文件：可写连接 share=0（任何其他打开都被拒绝），
// 只读连接 access=GENERIC_READ + share=FILE_SHARE_READ（读者之间可共存，写者被拒绝）。
func openLockHandle(path string, exclusive bool) (*os.File, error) {
	access := uint32(syscall.GENERIC_READ)
	share := uint32(syscall.FILE_SHARE_READ)
	if exclusive {
		access = uint32(syscall.GENERIC_READ | syscall.GENERIC_WRITE)
		share = 0
	}
	target, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return nil, err
	}
	handle, err := syscall.CreateFile(
		target, access, share, nil, syscall.OPEN_ALWAYS, syscall.FILE_ATTRIBUTE_NORMAL, 0)
	if err != nil {
		return nil, err
	}
	return os.NewFile(uintptr(handle), path), nil
}

func closeLockHandle(f *os.File) error {
	return f.Close()
}

// Win32 裸码常量（errSharingViolation=32 / errLockViolation=33）统一在 errors.go 定义，
// 供本文件与跨平台的 isWindowsSharingViolation 共用。

func isLockContention(err error) bool {
	return errors.Is(err, errSharingViolation) || errors.Is(err, errLockViolation)
}

// fsyncParentDir is a no-op on Windows.
func fsyncParentDir(filePath string) {}

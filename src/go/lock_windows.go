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

// Windows 里表示"文件被他人锁住"的错误码（syscall 未导出这两个常量）：
//   - 32 ERROR_SHARING_VIOLATION：目标正以不兼容的共享模式打开
//   - 33 ERROR_LOCK_VIOLATION：字节区间被其他句柄锁定
const (
	errSharingViolation syscall.Errno = 32
	errLockViolation    syscall.Errno = 33
)

func isLockContention(err error) bool {
	return errors.Is(err, errSharingViolation) || errors.Is(err, errLockViolation)
}

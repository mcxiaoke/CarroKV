package ckv

import (
	"errors"
	"fmt"
	"syscall"
)

// Base error type
type DbError struct {
	Type    string
	Message string
	Err     error
}

func (e *DbError) Error() string {
	if e.Err != nil {
		return fmt.Sprintf("%s: %s (caused by: %v)", e.Type, e.Message, e.Err)
	}
	return fmt.Sprintf("%s: %s", e.Type, e.Message)
}

func (e *DbError) Unwrap() error {
	return e.Err
}

func newDbError(errType, msg string, err error) error {
	return &DbError{
		Type:    errType,
		Message: msg,
		Err:     err,
	}
}

func NewCorruptedError(msg string) error {
	return newDbError("CorruptedError", msg, nil)
}

func NewLockedError(msg string) error {
	return newDbError("LockedError", msg, nil)
}

func NewConcurrentModificationError(msg string) error {
	return newDbError("ConcurrentModificationError", msg, nil)
}

func NewInvalidUtf8Error(msg string) error {
	return newDbError("InvalidUtf8Error", msg, nil)
}

func NewKeyTooLargeError(msg string) error {
	return newDbError("KeyTooLargeError", msg, nil)
}

func NewInvalidNamespaceError(msg string) error {
	return newDbError("InvalidNamespaceError", msg, nil)
}

func NewUnsupportedVersionError(msg string) error {
	return newDbError("UnsupportedVersionError", msg, nil)
}

func NewTypeMismatchError(msg string) error {
	return newDbError("TypeMismatchError", msg, nil)
}

// NewConflictingOptionsError 用于 reentrant open 的选项冲突。
// 目前仅 read_only 冲突会抛出：调用方绝不能静默拿到权限与所请求不一致的实例。
func NewConflictingOptionsError(msg string) error {
	return newDbError("ConflictingOptionsError", msg, nil)
}

// Windows 上表示"文件句柄被他人以不兼容方式占用"的 Win32 错误码。
//
// 必须是**裸码**（syscall 未在所有平台导出这些常量，且 Go 的 syscall.E* 系列位于
// APPLICATION_ERROR+n 的合成区间，与 Win32 真实码毫无关系——用它判断共享冲突恒为 false）。
// 无 build tag：db.go 的 renameWithRetry 判定在所有平台都要编译通过。
const (
	errAccessDenied     syscall.Errno = 5  // ERROR_ACCESS_DENIED
	errSharingViolation syscall.Errno = 32 // ERROR_SHARING_VIOLATION
	errLockViolation    syscall.Errno = 33 // ERROR_LOCK_VIOLATION
)

// Sentinel checks
func IsCorrupted(err error) bool {
	var de *DbError
	return errors.As(err, &de) && de.Type == "CorruptedError"
}

func IsLocked(err error) bool {
	var de *DbError
	return errors.As(err, &de) && de.Type == "LockedError"
}

func IsConflictingOptions(err error) bool {
	var de *DbError
	return errors.As(err, &de) && de.Type == "ConflictingOptionsError"
}

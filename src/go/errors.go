package ckv

import (
	"errors"
	"fmt"
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

// Sentinel checks
func IsCorrupted(err error) bool {
	var de *DbError
	return errors.As(err, &de) && de.Type == "CorruptedError"
}

func IsLocked(err error) bool {
	var de *DbError
	return errors.As(err, &de) && de.Type == "LockedError"
}

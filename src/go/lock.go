package ckv

import (
	"encoding/binary"
	"fmt"
	"os"
	"path/filepath"
	"sync/atomic"
	"time"
)

var lockCounter uint64 = 1

func generateUUID() [16]byte {
	now := uint64(time.Now().UnixNano())
	pid := uint64(os.Getpid())
	cnt := atomic.AddUint64(&lockCounter, 1)

	var uuid [16]byte
	binary.LittleEndian.PutUint64(uuid[0:8], now^(pid<<32))
	binary.LittleEndian.PutUint64(uuid[8:16], cnt^(now>>32))
	return uuid
}

type FileLock struct {
	path     string
	uuid     [16]byte
	acquired bool
}

func AcquireLock(dbPath string, options DbOptions) (*FileLock, error) {
	lockPath := dbPath + ".lock"
	backoffs := []time.Duration{10 * time.Millisecond, 30 * time.Millisecond, 60 * time.Millisecond, 120 * time.Millisecond}
	var totalWaited time.Duration
	timeout := time.Duration(options.LockTimeoutMs) * time.Millisecond

	for {
		uuid := generateUUID()
		pid := uint64(os.Getpid())
		nowMs := uint64(time.Now().UnixMilli())

		var content [32]byte
		binary.LittleEndian.PutUint64(content[0:8], pid)
		copy(content[8:24], uuid[:])
		binary.LittleEndian.PutUint64(content[24:32], nowMs)

		file, err := os.OpenFile(lockPath, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
		if err == nil {
			_, _ = file.Write(content[:])
			_ = file.Sync()
			_ = file.Close()
			return &FileLock{
				path:     lockPath,
				uuid:     uuid,
				acquired: true,
			}, nil
		}

		if os.IsExist(err) {
			var lockTime uint64
			f, rErr := os.Open(lockPath)
			if rErr == nil {
				var buf [32]byte
				if _, readErr := f.Read(buf[:]); readErr == nil {
					lockTime = binary.LittleEndian.Uint64(buf[24:32])
				}
				_ = f.Close()
			}

			isStale := false
			if options.ForceUnlock {
				isStale = true
			} else if lockTime > 0 && nowMs >= lockTime {
				isStale = (nowMs - lockTime) >= uint64(options.LockTimeoutMs)
			} else if fi, statErr := os.Stat(lockPath); statErr == nil {
				mtimeMs := uint64(fi.ModTime().UnixMilli())
				if nowMs >= mtimeMs {
					isStale = (nowMs - mtimeMs) >= uint64(options.LockTimeoutMs)
				}
			}

			if isStale {
				stealUUID := generateUUID()
				stealPath := fmt.Sprintf("%s.steal.%x", lockPath, stealUUID[:])
				renameErr := os.Rename(lockPath, stealPath)
				if renameErr == nil {
					_ = os.Remove(stealPath)
					continue
				}
				// On Windows, if process is still running with open handle, rename will fail.
				// We don't force remove, we fall through to backoff wait.
			}

			wait := backoffs[0]
			if len(backoffs) > 1 {
				backoffs = backoffs[1:]
			}
			time.Sleep(wait)
			totalWaited += wait

			if totalWaited >= timeout {
				return nil, NewLockedError(fmt.Sprintf(
					"Failed to acquire lock on '%s' after %v",
					filepath.Base(lockPath), totalWaited,
				))
			}
			continue
		}

		return nil, newDbError("IoError", "Failed to open lock file", err)
	}
}

func (fl *FileLock) Release() {
	if !fl.acquired {
		return
	}
	fl.acquired = false

	// Anti-ABA check: read lock file and verify UUID matches before unlinking
	f, err := os.Open(fl.path)
	if err == nil {
		var buf [32]byte
		if _, rErr := f.Read(buf[:]); rErr == nil {
			var fileUUID [16]byte
			copy(fileUUID[:], buf[8:24])
			if fileUUID == fl.uuid {
				_ = f.Close()
				_ = os.Remove(fl.path)
				return
			}
		}
		_ = f.Close()
	}
	// UUID mismatch or file deleted: do not remove (was stolen)
}

package ckv

type WarningCallback func(errType, message string)

type DbOptions struct {
	ReadOnly       bool
	LockTimeoutMs  uint32
	ForceUnlock    bool
	OnWarning      WarningCallback
}

func DefaultDbOptions() DbOptions {
	return DbOptions{
		ReadOnly:      false,
		LockTimeoutMs: 10000,
		ForceUnlock:   false,
		OnWarning:     nil,
	}
}

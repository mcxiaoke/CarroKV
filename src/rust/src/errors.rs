use std::fmt;

#[derive(Debug)]
pub enum DbError {
    Corrupted(String),
    Locked(String),
    ConcurrentModification(String),
    InvalidUtf8(String),
    KeyTooLarge(String),
    InvalidNamespace(String),
    UnsupportedVersion(String),
    Io(std::io::Error),
}

impl fmt::Display for DbError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            DbError::Corrupted(msg) => write!(f, "CorruptedError: {}", msg),
            DbError::Locked(msg) => write!(f, "LockedError: {}", msg),
            DbError::ConcurrentModification(msg) => write!(f, "ConcurrentModificationError: {}", msg),
            DbError::InvalidUtf8(msg) => write!(f, "InvalidUtf8Error: {}", msg),
            DbError::KeyTooLarge(msg) => write!(f, "KeyTooLargeError: {}", msg),
            DbError::InvalidNamespace(msg) => write!(f, "InvalidNamespaceError: {}", msg),
            DbError::UnsupportedVersion(msg) => write!(f, "UnsupportedVersionError: {}", msg),
            DbError::Io(err) => write!(f, "IoError: {}", err),
        }
    }
}

impl std::error::Error for DbError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            DbError::Io(err) => Some(err),
            _ => None,
        }
    }
}

impl From<std::io::Error> for DbError {
    fn from(err: std::io::Error) -> Self {
        DbError::Io(err)
    }
}

pub type Result<T> = std::result::Result<T, DbError>;

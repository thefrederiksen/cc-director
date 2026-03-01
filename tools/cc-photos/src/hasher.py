"""SHA-256 file hashing for duplicate detection."""

import hashlib
from pathlib import Path


def compute_sha256(file_path: Path, chunk_size: int = 65536) -> str:
    """Compute SHA-256 hash of a file.

    Args:
        file_path: Path to the file
        chunk_size: Size of chunks to read (default 64KB)

    Returns:
        Hex-encoded SHA-256 hash string
    """
    sha256_hash = hashlib.sha256()

    with open(file_path, "rb") as f:
        for chunk in iter(lambda: f.read(chunk_size), b""):
            sha256_hash.update(chunk)

    return sha256_hash.hexdigest()


def compute_sha256_quick(file_path: Path, sample_size: int = 1024 * 1024) -> str:
    """Compute a quick hash using first and last chunks of file.

    Useful for fast pre-filtering before full hash computation.
    Only use this for initial grouping, not for final duplicate detection.

    Args:
        file_path: Path to the file
        sample_size: Size of each sample (default 1MB)

    Returns:
        Hex-encoded SHA-256 hash of samples
    """
    sha256_hash = hashlib.sha256()
    file_size = file_path.stat().st_size

    with open(file_path, "rb") as f:
        # Read first chunk
        sha256_hash.update(f.read(sample_size))

        # Read last chunk if file is larger than 2x sample size
        if file_size > sample_size * 2:
            f.seek(-sample_size, 2)  # Seek from end
            sha256_hash.update(f.read(sample_size))

    return sha256_hash.hexdigest()

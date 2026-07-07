"""Batch / folder mode - catalog every image in a folder via the DevThrottle vision model.

This is the "understand my whole drive" use case: point cc-image at a folder and it walks
it (optionally recursively) for common image types, describes each image through the
DevThrottle API, and writes a structured results file (JSON and/or CSV) with columns
``path``, ``description``, and optional ``tags``/``category``.

It processes images concurrently with a bounded worker pool, backs off and retries on
rate-limit / transient errors, and supports resume: a re-run reads the existing results
file and skips images that were already described, so a large catalog can be interrupted
and continued without redoing work.
"""

import csv
import json
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Dict, List, Optional

try:
    from .devthrottle import DevThrottleVisionClient, DevThrottleTransientError
except ImportError:
    from src.devthrottle import DevThrottleVisionClient, DevThrottleTransientError


# Common raster image types worth cataloging. Kept lowercase; matched case-insensitively.
IMAGE_EXTENSIONS = frozenset(
    {".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff"}
)

DEFAULT_WORKERS = 4
MAX_RETRIES = 5
BACKOFF_BASE_SECONDS = 2.0


@dataclass
class ImageRecord:
    """One cataloged image."""

    path: str
    description: str = ""
    error: str = ""

    def as_dict(self) -> Dict[str, str]:
        record = {"path": self.path, "description": self.description}
        if self.error:
            record["error"] = self.error
        return record


@dataclass
class BatchResult:
    """Outcome of a batch run."""

    total: int = 0
    processed: int = 0
    skipped: int = 0
    failed: int = 0
    output_paths: List[Path] = field(default_factory=list)


def find_images(folder: Path, recursive: bool) -> List[Path]:
    """Return sorted image files under ``folder``.

    Args:
        folder: Directory to scan.
        recursive: Walk subdirectories when true, otherwise the top level only.
    """
    folder = Path(folder)
    if not folder.is_dir():
        raise NotADirectoryError(f"Not a folder: {folder}")

    walker = folder.rglob("*") if recursive else folder.glob("*")
    images = [
        p for p in walker
        if p.is_file() and p.suffix.lower() in IMAGE_EXTENSIONS
    ]
    return sorted(images)


def load_processed_paths(output_path: Path) -> Dict[str, Dict[str, str]]:
    """Read already-processed records from an existing JSON results file (for resume).

    Only JSON is read back for resume (it round-trips cleanly). Returns a map of
    absolute path -> record. A missing or unreadable file yields an empty map.
    """
    output_path = Path(output_path)
    if not output_path.exists() or output_path.suffix.lower() != ".json":
        return {}

    try:
        data = json.loads(output_path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return {}

    processed: Dict[str, Dict[str, str]] = {}
    for record in data if isinstance(data, list) else []:
        path = record.get("path")
        # A prior error is NOT treated as done - resume should retry failed images.
        if path and not record.get("error"):
            processed[path] = record
    return processed


def describe_one(
    client: DevThrottleVisionClient,
    image_path: Path,
    max_retries: int = MAX_RETRIES,
) -> ImageRecord:
    """Describe a single image, retrying transient/rate-limit errors with backoff."""
    path_str = str(image_path.resolve())
    attempt = 0
    while True:
        try:
            description = client.describe_image(image_path)
            return ImageRecord(path=path_str, description=description)
        except DevThrottleTransientError as ex:
            attempt += 1
            if attempt > max_retries:
                return ImageRecord(path=path_str, error=f"transient (gave up): {ex}")
            time.sleep(BACKOFF_BASE_SECONDS * (2 ** (attempt - 1)))
        except (RuntimeError, OSError, ValueError) as ex:
            return ImageRecord(path=path_str, error=str(ex))


def catalog_folder(
    folder: Path,
    output_path: Path,
    recursive: bool = False,
    workers: int = DEFAULT_WORKERS,
    write_csv: bool = True,
    write_json: bool = True,
    client: Optional[DevThrottleVisionClient] = None,
    on_progress: Optional[Callable[[int, int, ImageRecord], None]] = None,
) -> BatchResult:
    """Catalog every image in a folder and write JSON and/or CSV results.

    Args:
        folder: Directory of images.
        output_path: Results file path. Its stem is used for both the ``.json`` and ``.csv``
            outputs; the extension picks the primary format for resume.
        recursive: Walk subdirectories when true.
        workers: Concurrent worker count.
        write_csv: Also write a ``.csv`` alongside the results.
        write_json: Also write a ``.json`` alongside the results (needed for resume).
        client: Vision client (constructed from environment when None).
        on_progress: Callback ``(done, total, record)`` invoked as each image finishes.

    Returns:
        A :class:`BatchResult` summary.
    """
    folder = Path(folder)
    output_path = Path(output_path)
    client = client or DevThrottleVisionClient()

    images = find_images(folder, recursive=recursive)
    result = BatchResult(total=len(images))

    # Resume: keep already-described records, only process the rest.
    processed = load_processed_paths(_json_path(output_path)) if write_json else {}
    records: Dict[str, ImageRecord] = {
        path: ImageRecord(path=path, description=rec.get("description", ""))
        for path, rec in processed.items()
    }

    pending: List[Path] = []
    for image in images:
        if str(image.resolve()) in records:
            result.skipped += 1
        else:
            pending.append(image)

    done = result.skipped
    lock = threading.Lock()

    def handle(record: ImageRecord) -> None:
        nonlocal done
        with lock:
            records[record.path] = record
            done += 1
            if record.error:
                result.failed += 1
            else:
                result.processed += 1
            # Flush after every image so an interruption keeps all completed work.
            _write_outputs(records, output_path, write_json, write_csv)
            if on_progress:
                on_progress(done, result.total, record)

    if pending:
        with ThreadPoolExecutor(max_workers=max(1, workers)) as pool:
            futures = {pool.submit(describe_one, client, img): img for img in pending}
            for future in as_completed(futures):
                handle(future.result())
    else:
        # Nothing to process, but still (re)write the outputs from resumed records.
        _write_outputs(records, output_path, write_json, write_csv)

    if write_json:
        result.output_paths.append(_json_path(output_path))
    if write_csv:
        result.output_paths.append(_csv_path(output_path))
    return result


def _json_path(output_path: Path) -> Path:
    return output_path.with_suffix(".json")


def _csv_path(output_path: Path) -> Path:
    return output_path.with_suffix(".csv")


def _write_outputs(
    records: Dict[str, ImageRecord],
    output_path: Path,
    write_json: bool,
    write_csv: bool,
) -> None:
    """Write the current records to JSON and/or CSV (sorted by path for stable output)."""
    ordered = [records[key] for key in sorted(records)]
    output_path.parent.mkdir(parents=True, exist_ok=True)

    if write_json:
        _json_path(output_path).write_text(
            json.dumps([r.as_dict() for r in ordered], indent=2, ensure_ascii=False),
            encoding="utf-8",
        )

    if write_csv:
        with _csv_path(output_path).open("w", encoding="utf-8", newline="") as handle:
            writer = csv.writer(handle)
            writer.writerow(["path", "description", "error"])
            for record in ordered:
                writer.writerow([record.path, record.description, record.error])

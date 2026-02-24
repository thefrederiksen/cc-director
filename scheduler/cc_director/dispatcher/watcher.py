"""File system watcher for approved/ folder."""

import asyncio
import logging
import threading
from pathlib import Path
from typing import Callable, Optional

logger = logging.getLogger("cc_director.dispatcher.watcher")


class ApprovedFolderWatcher:
    """
    Watch approved/ folder for new JSON files.

    Uses watchdog library for cross-platform file system events.
    Falls back to polling if watchdog is not available.
    """

    def __init__(
        self,
        approved_path: Path,
        on_new_item: Callable[[Path], None],
        poll_interval: float = 5.0
    ):
        """
        Initialize the watcher.

        Args:
            approved_path: Path to approved/ folder
            on_new_item: Callback when new JSON file appears
            poll_interval: Seconds between polls if using fallback
        """
        self.approved_path = approved_path
        self.on_new_item = on_new_item
        self.poll_interval = poll_interval
        self._stop_event = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self._use_watchdog = self._check_watchdog()
        self._known_files: set[str] = set()

    def _check_watchdog(self) -> bool:
        """Check if watchdog is available."""
        try:
            import watchdog
            return True
        except ImportError:
            logger.info("watchdog not installed, using polling fallback")
            return False

    def start(self) -> None:
        """Start watching the approved/ folder."""
        if self._thread and self._thread.is_alive():
            logger.warning("Watcher already running")
            return

        # Initialize known files
        self._known_files = set()
        if self.approved_path.exists():
            self._known_files = {
                f.name for f in self.approved_path.glob("*.json")
            }

        self._stop_event.clear()

        if self._use_watchdog:
            self._thread = threading.Thread(
                target=self._run_watchdog,
                daemon=True,
                name="approved-watcher"
            )
        else:
            self._thread = threading.Thread(
                target=self._run_polling,
                daemon=True,
                name="approved-watcher-poll"
            )

        self._thread.start()
        logger.info(f"Started watching {self.approved_path}")

    def stop(self) -> None:
        """Stop the watcher."""
        self._stop_event.set()
        if self._thread:
            self._thread.join(timeout=5.0)
            self._thread = None
        logger.info("Watcher stopped")

    def _run_watchdog(self) -> None:
        """Run the watchdog-based watcher."""
        from watchdog.events import FileSystemEventHandler, FileCreatedEvent
        from watchdog.observers import Observer

        class Handler(FileSystemEventHandler):
            def __init__(handler_self, watcher: "ApprovedFolderWatcher"):
                handler_self.watcher = watcher

            def on_created(handler_self, event: FileCreatedEvent) -> None:
                if event.is_directory:
                    return
                path = Path(event.src_path)
                if path.suffix.lower() == ".json":
                    logger.info(f"New file detected: {path.name}")
                    handler_self.watcher.on_new_item(path)

        observer = Observer()
        observer.schedule(
            Handler(self),
            str(self.approved_path),
            recursive=False
        )
        observer.start()

        try:
            while not self._stop_event.is_set():
                self._stop_event.wait(1.0)
        finally:
            observer.stop()
            observer.join()

    def _run_polling(self) -> None:
        """Run the polling-based watcher (fallback)."""
        while not self._stop_event.is_set():
            try:
                if self.approved_path.exists():
                    current_files = {
                        f.name for f in self.approved_path.glob("*.json")
                    }

                    # Find new files
                    new_files = current_files - self._known_files
                    for filename in new_files:
                        path = self.approved_path / filename
                        logger.info(f"New file detected (poll): {filename}")
                        self.on_new_item(path)

                    self._known_files = current_files

            except Exception as e:
                logger.error(f"Error in polling loop: {e}")

            self._stop_event.wait(self.poll_interval)

    def is_running(self) -> bool:
        """Check if watcher is running."""
        return self._thread is not None and self._thread.is_alive()

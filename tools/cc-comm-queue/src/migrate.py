"""Migration script for Communication Manager - JSON to SQLite.

This script migrates existing JSON files from the directory-based storage
to the new SQLite database.

Usage:
    python migrate.py [content_path]

If content_path is not provided, it uses the default path from config.
"""

import json
import logging
import os
import shutil
import sys
from pathlib import Path
from typing import Optional

# Handle imports for both package and frozen executable
try:
    from .schema import ContentItem, Status
    from .database import Database
except ImportError:
    from schema import ContentItem, Status
    from database import Database

logging.basicConfig(level=logging.INFO, format="%(levelname)s: %(message)s")
logger = logging.getLogger(__name__)


def get_default_content_path() -> Path:
    """Get the default content path from config or use default."""
    config_path = Path.home() / ".cc_tools" / "config.json"
    if config_path.exists():
        with open(config_path, "r", encoding="utf-8") as f:
            data = json.load(f)
            cm = data.get("comm_manager", {})
            local = os.environ.get("LOCALAPPDATA", "")
            default_qp = (local + "/cc-tools/data/comm_manager/content") if local else str(Path.home() / "cc_communication_manager" / "content")
            return Path(cm.get("queue_path", default_qp))
    local = os.environ.get("LOCALAPPDATA", "")
    if local:
        return Path(local) / "cc-tools" / "data" / "comm_manager" / "content"
    return Path.home() / "cc_communication_manager" / "content"


def migrate_json_to_sqlite(content_path: Optional[Path] = None, backup: bool = True, delete_json: bool = False) -> dict:
    """Migrate JSON files to SQLite database.

    Args:
        content_path: Path to the content directory
        backup: Whether to backup JSON files before migration
        delete_json: Whether to delete JSON files after successful migration

    Returns:
        Dictionary with migration statistics
    """
    if content_path is None:
        content_path = get_default_content_path()

    logger.info("Starting migration from: %s", content_path)

    stats = {
        "pending_review": {"found": 0, "migrated": 0, "errors": 0},
        "approved": {"found": 0, "migrated": 0, "errors": 0},
        "rejected": {"found": 0, "migrated": 0, "errors": 0},
        "posted": {"found": 0, "migrated": 0, "errors": 0},
        "total_migrated": 0,
        "total_errors": 0,
    }

    # Create backup if requested
    if backup:
        backup_path = content_path / "backup_json"
        backup_path.mkdir(exist_ok=True)
        logger.info("Backup directory: %s", backup_path)

    # Initialize database
    db = Database(content_path)

    # Map folders to statuses
    folders = {
        "pending_review": Status.PENDING_REVIEW,
        "approved": Status.APPROVED,
        "rejected": Status.REJECTED,
        "posted": Status.POSTED,
    }

    migrated_files = []

    for folder_name, status in folders.items():
        folder_path = content_path / folder_name
        if not folder_path.exists():
            logger.info("Folder not found, skipping: %s", folder_path)
            continue

        json_files = list(folder_path.glob("*.json"))
        stats[folder_name]["found"] = len(json_files)
        logger.info("Found %d files in %s", len(json_files), folder_name)

        for json_file in json_files:
            try:
                # Read JSON file
                with open(json_file, "r", encoding="utf-8") as f:
                    data = json.load(f)

                # Check if already in database
                existing = db.get_by_id(data.get("id", ""))
                if existing:
                    logger.info("  Skipping (already exists): %s", json_file.name)
                    continue

                # Ensure status matches the folder
                data["status"] = status.value

                # Create ContentItem
                item = ContentItem(**data)

                # Add to database
                ticket_number = db.add_communication(item)

                stats[folder_name]["migrated"] += 1
                stats["total_migrated"] += 1
                migrated_files.append(json_file)

                logger.info("  [OK] Migrated: %s -> ticket #%d", json_file.name, ticket_number)

                # Backup the file
                if backup:
                    backup_file = backup_path / folder_name
                    backup_file.mkdir(exist_ok=True)
                    shutil.copy2(json_file, backup_file / json_file.name)

            except Exception as e:
                stats[folder_name]["errors"] += 1
                stats["total_errors"] += 1
                logger.error("  [ERROR] Failed to migrate %s: %s", json_file.name, e)

    # Delete migrated JSON files if requested
    if delete_json and migrated_files:
        logger.info("Deleting %d migrated JSON files...", len(migrated_files))
        for json_file in migrated_files:
            try:
                json_file.unlink()
                logger.info("  Deleted: %s", json_file.name)
            except Exception as e:
                logger.error("  Failed to delete %s: %s", json_file.name, e)

    # Also migrate ticket_counter.txt if it exists
    counter_file = content_path / "ticket_counter.txt"
    if counter_file.exists():
        logger.info("Note: ticket_counter.txt exists but is no longer needed (DB uses MAX(ticket_number)+1)")

    db.close()

    # Print summary
    logger.info("")
    logger.info("=" * 50)
    logger.info("Migration Summary")
    logger.info("=" * 50)
    for folder_name in folders.keys():
        s = stats[folder_name]
        logger.info("%s: %d found, %d migrated, %d errors",
                   folder_name, s["found"], s["migrated"], s["errors"])
    logger.info("-" * 50)
    logger.info("Total: %d migrated, %d errors", stats["total_migrated"], stats["total_errors"])
    logger.info("=" * 50)

    return stats


def main():
    """Main entry point."""
    content_path = None
    delete_json = False

    # Parse arguments
    args = sys.argv[1:]
    for arg in args:
        if arg == "--delete":
            delete_json = True
        elif arg == "--help":
            print(__doc__)
            print("\nOptions:")
            print("  --delete    Delete JSON files after successful migration")
            print("  --help      Show this help message")
            sys.exit(0)
        else:
            content_path = Path(arg)

    if content_path is not None and not content_path.exists():
        logger.error("Content path does not exist: %s", content_path)
        sys.exit(1)

    stats = migrate_json_to_sqlite(content_path, backup=True, delete_json=delete_json)

    if stats["total_errors"] > 0:
        sys.exit(1)


if __name__ == "__main__":
    main()

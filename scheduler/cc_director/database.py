"""SQLite database operations for cc_director."""

import sqlite3
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Optional


def _parse_datetime(value: str | bytes | datetime | None) -> datetime | None:
    """Parse datetime from SQLite storage format."""
    if value is None:
        return None
    if isinstance(value, datetime):
        return value
    if isinstance(value, bytes):
        value = value.decode("utf-8")
    if isinstance(value, str):
        # Handle various SQLite datetime formats
        for fmt in [
            "%Y-%m-%d %H:%M:%S.%f",
            "%Y-%m-%d %H:%M:%S",
            "%Y-%m-%dT%H:%M:%S.%f",
            "%Y-%m-%dT%H:%M:%S",
        ]:
            try:
                return datetime.strptime(value, fmt)
            except ValueError:
                continue
        raise ValueError(f"Cannot parse datetime: {value}")
    return None


@dataclass
class Job:
    """Represents a scheduled job."""
    id: Optional[int]
    name: str
    cron: str
    command: str
    working_dir: Optional[str]
    enabled: bool
    timeout_seconds: int
    tags: Optional[str]
    created_at: Optional[datetime]
    updated_at: Optional[datetime]
    next_run: Optional[datetime]


@dataclass
class Run:
    """Represents a job execution record."""
    id: Optional[int]
    job_id: int
    job_name: str
    started_at: datetime
    ended_at: Optional[datetime]
    exit_code: Optional[int]
    stdout: Optional[str]
    stderr: Optional[str]
    timed_out: bool
    duration_seconds: Optional[float]


SCHEMA = """
CREATE TABLE IF NOT EXISTS jobs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT UNIQUE NOT NULL,
    cron TEXT NOT NULL,
    command TEXT NOT NULL,
    working_dir TEXT,
    enabled INTEGER DEFAULT 1,
    timeout_seconds INTEGER DEFAULT 300,
    tags TEXT,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    updated_at DATETIME DEFAULT CURRENT_TIMESTAMP,
    next_run DATETIME
);

CREATE TABLE IF NOT EXISTS runs (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id INTEGER NOT NULL,
    job_name TEXT NOT NULL,
    started_at DATETIME NOT NULL,
    ended_at DATETIME,
    exit_code INTEGER,
    stdout TEXT,
    stderr TEXT,
    timed_out INTEGER DEFAULT 0,
    duration_seconds REAL,
    FOREIGN KEY (job_id) REFERENCES jobs(id)
);

CREATE INDEX IF NOT EXISTS idx_runs_job_id ON runs(job_id);
CREATE INDEX IF NOT EXISTS idx_runs_started_at ON runs(started_at);
CREATE INDEX IF NOT EXISTS idx_jobs_next_run ON jobs(next_run);
CREATE INDEX IF NOT EXISTS idx_jobs_enabled ON jobs(enabled);
"""


class Database:
    """SQLite database manager for jobs and runs."""

    def __init__(self, db_path: str):
        self.db_path = Path(db_path)
        self._connection: Optional[sqlite3.Connection] = None

    def connect(self) -> sqlite3.Connection:
        """Get or create database connection."""
        if self._connection is None:
            self.db_path.parent.mkdir(parents=True, exist_ok=True)
            self._connection = sqlite3.connect(
                str(self.db_path),
                detect_types=sqlite3.PARSE_DECLTYPES | sqlite3.PARSE_COLNAMES
            )
            self._connection.row_factory = sqlite3.Row
        return self._connection

    def close(self) -> None:
        """Close database connection."""
        if self._connection is not None:
            self._connection.close()
            self._connection = None

    def create_tables(self) -> None:
        """Initialize database schema."""
        conn = self.connect()
        conn.executescript(SCHEMA)
        conn.commit()

    # Job CRUD operations

    def add_job(self, job: Job) -> int:
        """Add a new job. Returns the job ID."""
        conn = self.connect()
        cursor = conn.execute(
            """
            INSERT INTO jobs (name, cron, command, working_dir, enabled,
                            timeout_seconds, tags, next_run)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                job.name,
                job.cron,
                job.command,
                job.working_dir,
                1 if job.enabled else 0,
                job.timeout_seconds,
                job.tags,
                job.next_run,
            ),
        )
        conn.commit()
        return cursor.lastrowid

    def get_job(self, name: str) -> Optional[Job]:
        """Get a job by name."""
        conn = self.connect()
        cursor = conn.execute("SELECT * FROM jobs WHERE name = ?", (name,))
        row = cursor.fetchone()
        if row is None:
            return None
        return self._row_to_job(row)

    def get_job_by_id(self, job_id: int) -> Optional[Job]:
        """Get a job by ID."""
        conn = self.connect()
        cursor = conn.execute("SELECT * FROM jobs WHERE id = ?", (job_id,))
        row = cursor.fetchone()
        if row is None:
            return None
        return self._row_to_job(row)

    def list_jobs(self, include_disabled: bool = False, tag: Optional[str] = None) -> list[Job]:
        """List all jobs, optionally filtering by enabled status and tag."""
        conn = self.connect()
        query = "SELECT * FROM jobs WHERE 1=1"
        params: list = []

        if not include_disabled:
            query += " AND enabled = 1"

        if tag:
            query += " AND tags LIKE ?"
            params.append(f"%{tag}%")

        query += " ORDER BY name"
        cursor = conn.execute(query, params)
        return [self._row_to_job(row) for row in cursor.fetchall()]

    def update_job(self, job: Job) -> None:
        """Update an existing job."""
        conn = self.connect()
        conn.execute(
            """
            UPDATE jobs SET
                cron = ?,
                command = ?,
                working_dir = ?,
                enabled = ?,
                timeout_seconds = ?,
                tags = ?,
                updated_at = CURRENT_TIMESTAMP,
                next_run = ?
            WHERE id = ?
            """,
            (
                job.cron,
                job.command,
                job.working_dir,
                1 if job.enabled else 0,
                job.timeout_seconds,
                job.tags,
                job.next_run,
                job.id,
            ),
        )
        conn.commit()

    def delete_job(self, name: str) -> bool:
        """Delete a job by name. Returns True if deleted."""
        conn = self.connect()
        cursor = conn.execute("DELETE FROM jobs WHERE name = ?", (name,))
        conn.commit()
        return cursor.rowcount > 0

    def set_job_enabled(self, name: str, enabled: bool) -> bool:
        """Enable or disable a job. Returns True if updated."""
        conn = self.connect()
        cursor = conn.execute(
            "UPDATE jobs SET enabled = ?, updated_at = CURRENT_TIMESTAMP WHERE name = ?",
            (1 if enabled else 0, name),
        )
        conn.commit()
        return cursor.rowcount > 0

    def update_next_run(self, job_id: int, next_run: datetime) -> None:
        """Update the next_run time for a job."""
        conn = self.connect()
        conn.execute(
            "UPDATE jobs SET next_run = ? WHERE id = ?",
            (next_run, job_id),
        )
        conn.commit()

    def get_due_jobs(self) -> list[Job]:
        """Get all enabled jobs that are due to run."""
        conn = self.connect()
        now = datetime.now()
        cursor = conn.execute(
            """
            SELECT * FROM jobs
            WHERE enabled = 1 AND next_run IS NOT NULL AND next_run <= ?
            ORDER BY next_run
            """,
            (now,),
        )
        return [self._row_to_job(row) for row in cursor.fetchall()]

    # Run operations

    def create_run(self, run: Run) -> int:
        """Create a new run record. Returns the run ID."""
        conn = self.connect()
        cursor = conn.execute(
            """
            INSERT INTO runs (job_id, job_name, started_at, ended_at, exit_code,
                            stdout, stderr, timed_out, duration_seconds)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                run.job_id,
                run.job_name,
                run.started_at,
                run.ended_at,
                run.exit_code,
                run.stdout,
                run.stderr,
                1 if run.timed_out else 0,
                run.duration_seconds,
            ),
        )
        conn.commit()
        return cursor.lastrowid

    def update_run(self, run: Run) -> None:
        """Update an existing run record."""
        conn = self.connect()
        conn.execute(
            """
            UPDATE runs SET
                ended_at = ?,
                exit_code = ?,
                stdout = ?,
                stderr = ?,
                timed_out = ?,
                duration_seconds = ?
            WHERE id = ?
            """,
            (
                run.ended_at,
                run.exit_code,
                run.stdout,
                run.stderr,
                1 if run.timed_out else 0,
                run.duration_seconds,
                run.id,
            ),
        )
        conn.commit()

    def get_run(self, run_id: int) -> Optional[Run]:
        """Get a run by ID."""
        conn = self.connect()
        cursor = conn.execute("SELECT * FROM runs WHERE id = ?", (run_id,))
        row = cursor.fetchone()
        if row is None:
            return None
        return self._row_to_run(row)

    def list_runs(
        self,
        job_name: Optional[str] = None,
        limit: int = 20,
        failed_only: bool = False,
        since: Optional[datetime] = None,
    ) -> list[Run]:
        """List runs with optional filtering."""
        conn = self.connect()
        query = "SELECT * FROM runs WHERE 1=1"
        params: list = []

        if job_name:
            query += " AND job_name = ?"
            params.append(job_name)

        if failed_only:
            query += " AND (exit_code != 0 OR exit_code IS NULL OR timed_out = 1)"

        if since:
            query += " AND started_at >= ?"
            params.append(since)

        query += " ORDER BY started_at DESC LIMIT ?"
        params.append(limit)

        cursor = conn.execute(query, params)
        return [self._row_to_run(row) for row in cursor.fetchall()]

    def get_last_run(self, job_name: str) -> Optional[Run]:
        """Get the most recent run for a job."""
        runs = self.list_runs(job_name=job_name, limit=1)
        return runs[0] if runs else None

    def cleanup_old_runs(self, days: int = 30) -> int:
        """Delete runs older than specified days. Returns count deleted."""
        conn = self.connect()
        cursor = conn.execute(
            "DELETE FROM runs WHERE started_at < datetime('now', ?)",
            (f"-{days} days",),
        )
        conn.commit()
        return cursor.rowcount

    # Helper methods

    def _row_to_job(self, row: sqlite3.Row) -> Job:
        """Convert a database row to a Job object."""
        return Job(
            id=row["id"],
            name=row["name"],
            cron=row["cron"],
            command=row["command"],
            working_dir=row["working_dir"],
            enabled=bool(row["enabled"]),
            timeout_seconds=row["timeout_seconds"],
            tags=row["tags"],
            created_at=_parse_datetime(row["created_at"]),
            updated_at=_parse_datetime(row["updated_at"]),
            next_run=_parse_datetime(row["next_run"]),
        )

    def _row_to_run(self, row: sqlite3.Row) -> Run:
        """Convert a database row to a Run object."""
        return Run(
            id=row["id"],
            job_id=row["job_id"],
            job_name=row["job_name"],
            started_at=_parse_datetime(row["started_at"]),
            ended_at=_parse_datetime(row["ended_at"]),
            exit_code=row["exit_code"],
            stdout=row["stdout"],
            stderr=row["stderr"],
            timed_out=bool(row["timed_out"]),
            duration_seconds=row["duration_seconds"],
        )

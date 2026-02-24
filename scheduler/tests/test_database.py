"""Tests for database module."""

import pytest
import tempfile
from datetime import datetime
from pathlib import Path

from cc_director.database import Database, Job, Run


@pytest.fixture
def db():
    """Create a temporary database for testing."""
    with tempfile.TemporaryDirectory() as tmpdir:
        db_path = Path(tmpdir) / "test.db"
        database = Database(str(db_path))
        database.create_tables()
        yield database
        database.close()


@pytest.fixture
def sample_job() -> Job:
    """Create a sample job for testing."""
    return Job(
        id=None,
        name="test_job",
        cron="0 7 * * *",
        command="echo hello",
        working_dir=None,
        enabled=True,
        timeout_seconds=300,
        tags="test,sample",
        created_at=None,
        updated_at=None,
        next_run=datetime(2026, 2, 21, 7, 0, 0),
    )


class TestJobCRUD:
    """Tests for job CRUD operations."""

    def test_add_job(self, db: Database, sample_job: Job):
        """Adding a job should return an ID."""
        job_id = db.add_job(sample_job)
        assert job_id > 0

    def test_get_job_by_name(self, db: Database, sample_job: Job):
        """Should retrieve job by name."""
        db.add_job(sample_job)
        job = db.get_job("test_job")
        assert job is not None
        assert job.name == "test_job"
        assert job.cron == "0 7 * * *"
        assert job.command == "echo hello"

    def test_get_job_not_found(self, db: Database):
        """Should return None for non-existent job."""
        job = db.get_job("nonexistent")
        assert job is None

    def test_list_jobs(self, db: Database, sample_job: Job):
        """Should list all enabled jobs."""
        db.add_job(sample_job)

        # Add a disabled job
        disabled_job = Job(
            id=None, name="disabled_job", cron="* * * * *",
            command="echo disabled", working_dir=None, enabled=False,
            timeout_seconds=60, tags=None, created_at=None,
            updated_at=None, next_run=None,
        )
        db.add_job(disabled_job)

        # List enabled only
        jobs = db.list_jobs(include_disabled=False)
        assert len(jobs) == 1
        assert jobs[0].name == "test_job"

        # List all
        jobs = db.list_jobs(include_disabled=True)
        assert len(jobs) == 2

    def test_list_jobs_by_tag(self, db: Database, sample_job: Job):
        """Should filter jobs by tag."""
        db.add_job(sample_job)

        jobs = db.list_jobs(tag="test")
        assert len(jobs) == 1

        jobs = db.list_jobs(tag="nonexistent")
        assert len(jobs) == 0

    def test_update_job(self, db: Database, sample_job: Job):
        """Should update job properties."""
        job_id = db.add_job(sample_job)
        job = db.get_job_by_id(job_id)
        job.cron = "*/5 * * * *"
        job.command = "echo updated"

        db.update_job(job)

        updated = db.get_job_by_id(job_id)
        assert updated.cron == "*/5 * * * *"
        assert updated.command == "echo updated"

    def test_delete_job(self, db: Database, sample_job: Job):
        """Should delete job by name."""
        db.add_job(sample_job)

        result = db.delete_job("test_job")
        assert result is True

        job = db.get_job("test_job")
        assert job is None

    def test_delete_nonexistent_job(self, db: Database):
        """Deleting non-existent job should return False."""
        result = db.delete_job("nonexistent")
        assert result is False

    def test_set_job_enabled(self, db: Database, sample_job: Job):
        """Should enable/disable job."""
        db.add_job(sample_job)

        db.set_job_enabled("test_job", False)
        job = db.get_job("test_job")
        assert job.enabled is False

        db.set_job_enabled("test_job", True)
        job = db.get_job("test_job")
        assert job.enabled is True


class TestDueJobs:
    """Tests for getting due jobs."""

    def test_get_due_jobs(self, db: Database):
        """Should return jobs that are due to run."""
        # Job due now
        due_job = Job(
            id=None, name="due_job", cron="* * * * *",
            command="echo due", working_dir=None, enabled=True,
            timeout_seconds=60, tags=None, created_at=None,
            updated_at=None, next_run=datetime(2020, 1, 1, 0, 0, 0),  # Past
        )
        db.add_job(due_job)

        # Job not due yet
        future_job = Job(
            id=None, name="future_job", cron="* * * * *",
            command="echo future", working_dir=None, enabled=True,
            timeout_seconds=60, tags=None, created_at=None,
            updated_at=None, next_run=datetime(2099, 1, 1, 0, 0, 0),  # Future
        )
        db.add_job(future_job)

        due_jobs = db.get_due_jobs()
        assert len(due_jobs) == 1
        assert due_jobs[0].name == "due_job"


class TestRunCRUD:
    """Tests for run CRUD operations."""

    def test_create_run(self, db: Database, sample_job: Job):
        """Should create a run record."""
        job_id = db.add_job(sample_job)

        run = Run(
            id=None,
            job_id=job_id,
            job_name="test_job",
            started_at=datetime.now(),
            ended_at=None,
            exit_code=None,
            stdout=None,
            stderr=None,
            timed_out=False,
            duration_seconds=None,
        )
        run_id = db.create_run(run)
        assert run_id > 0

    def test_update_run(self, db: Database, sample_job: Job):
        """Should update run with results."""
        job_id = db.add_job(sample_job)

        run = Run(
            id=None,
            job_id=job_id,
            job_name="test_job",
            started_at=datetime.now(),
            ended_at=None,
            exit_code=None,
            stdout=None,
            stderr=None,
            timed_out=False,
            duration_seconds=None,
        )
        run_id = db.create_run(run)
        run.id = run_id
        run.ended_at = datetime.now()
        run.exit_code = 0
        run.stdout = "hello"
        run.duration_seconds = 1.5

        db.update_run(run)

        updated = db.get_run(run_id)
        assert updated.exit_code == 0
        assert updated.stdout == "hello"
        assert updated.duration_seconds == 1.5

    def test_list_runs(self, db: Database, sample_job: Job):
        """Should list runs with filters."""
        job_id = db.add_job(sample_job)

        # Create some runs
        for i in range(5):
            run = Run(
                id=None,
                job_id=job_id,
                job_name="test_job",
                started_at=datetime.now(),
                ended_at=datetime.now(),
                exit_code=0 if i % 2 == 0 else 1,
                stdout="output",
                stderr=None,
                timed_out=False,
                duration_seconds=1.0,
            )
            db.create_run(run)

        # List all
        runs = db.list_runs(limit=10)
        assert len(runs) == 5

        # List failed only
        runs = db.list_runs(failed_only=True)
        assert len(runs) == 2

        # List by job name
        runs = db.list_runs(job_name="test_job")
        assert len(runs) == 5

    def test_get_last_run(self, db: Database, sample_job: Job):
        """Should get most recent run for a job."""
        job_id = db.add_job(sample_job)

        # Create runs
        for i in range(3):
            run = Run(
                id=None,
                job_id=job_id,
                job_name="test_job",
                started_at=datetime.now(),
                ended_at=datetime.now(),
                exit_code=i,
                stdout=f"run {i}",
                stderr=None,
                timed_out=False,
                duration_seconds=1.0,
            )
            db.create_run(run)

        last_run = db.get_last_run("test_job")
        assert last_run is not None
        assert last_run.exit_code == 2  # Last one created

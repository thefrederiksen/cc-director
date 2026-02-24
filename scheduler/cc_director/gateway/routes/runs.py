"""Runs API routes."""

from datetime import datetime
from typing import Optional

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel

from ...database import Run

router = APIRouter()


class RunResponse(BaseModel):
    """Response model for a run."""
    id: int
    job_id: int
    job_name: str
    started_at: datetime
    ended_at: Optional[datetime]
    exit_code: Optional[int]
    stdout: Optional[str]
    stderr: Optional[str]
    timed_out: bool
    duration_seconds: Optional[float]
    status: str  # running, success, failed, timeout


def _run_to_response(run: Run) -> RunResponse:
    """Convert a Run dataclass to a response model."""
    if run.ended_at is None:
        status = "running"
    elif run.timed_out:
        status = "timeout"
    elif run.exit_code == 0:
        status = "success"
    else:
        status = "failed"

    return RunResponse(
        id=run.id,
        job_id=run.job_id,
        job_name=run.job_name,
        started_at=run.started_at,
        ended_at=run.ended_at,
        exit_code=run.exit_code,
        stdout=run.stdout,
        stderr=run.stderr,
        timed_out=run.timed_out,
        duration_seconds=run.duration_seconds,
        status=status,
    )


@router.get("", response_model=list[RunResponse])
async def list_runs(
    request: Request,
    job_name: Optional[str] = None,
    limit: int = 50,
    failed_only: bool = False,
):
    """
    List runs with optional filtering.

    Args:
        job_name: Filter by job name
        limit: Maximum number of runs to return (default 50)
        failed_only: Only return failed/timed-out runs
    """
    db = request.app.state.db
    runs = db.list_runs(
        job_name=job_name,
        limit=limit,
        failed_only=failed_only,
    )
    return [_run_to_response(run) for run in runs]


@router.get("/{run_id}", response_model=RunResponse)
async def get_run(request: Request, run_id: int):
    """Get a run by ID."""
    db = request.app.state.db
    run = db.get_run(run_id)
    if run is None:
        raise HTTPException(status_code=404, detail=f"Run {run_id} not found")
    return _run_to_response(run)


@router.get("/job/{job_name}", response_model=list[RunResponse])
async def get_runs_for_job(
    request: Request,
    job_name: str,
    limit: int = 20,
):
    """Get recent runs for a specific job."""
    db = request.app.state.db

    # Verify job exists
    job = db.get_job(job_name)
    if job is None:
        raise HTTPException(status_code=404, detail=f"Job '{job_name}' not found")

    runs = db.list_runs(job_name=job_name, limit=limit)
    return [_run_to_response(run) for run in runs]


class RunStats(BaseModel):
    """Statistics about runs."""
    total_runs_today: int
    successful_runs_today: int
    failed_runs_today: int
    timeout_runs_today: int
    running_count: int


@router.get("/stats/today", response_model=RunStats)
async def get_run_stats(request: Request):
    """Get run statistics for today."""
    db = request.app.state.db
    running_jobs = request.app.state.running_jobs

    # Get today's midnight
    today = datetime.now().replace(hour=0, minute=0, second=0, microsecond=0)

    runs = db.list_runs(since=today, limit=1000)

    total = len(runs)
    successful = sum(1 for r in runs if r.exit_code == 0 and not r.timed_out)
    failed = sum(1 for r in runs if r.exit_code is not None and r.exit_code != 0)
    timeout = sum(1 for r in runs if r.timed_out)
    running = len(running_jobs)

    return RunStats(
        total_runs_today=total,
        successful_runs_today=successful,
        failed_runs_today=failed,
        timeout_runs_today=timeout,
        running_count=running,
    )

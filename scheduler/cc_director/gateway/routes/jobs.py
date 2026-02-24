"""Jobs API routes."""

from datetime import datetime
from typing import Optional

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel, Field

from ...cron import calculate_next_run
from ...database import Job

router = APIRouter()


class JobCreate(BaseModel):
    """Request model for creating a job."""
    name: str = Field(..., min_length=1, max_length=100)
    cron: str = Field(..., description="Cron expression (e.g., '0 9 * * *')")
    command: str = Field(..., min_length=1)
    working_dir: Optional[str] = None
    enabled: bool = True
    timeout_seconds: int = Field(default=300, ge=1, le=86400)
    tags: Optional[str] = None


class JobUpdate(BaseModel):
    """Request model for updating a job."""
    cron: Optional[str] = None
    command: Optional[str] = None
    working_dir: Optional[str] = None
    enabled: Optional[bool] = None
    timeout_seconds: Optional[int] = Field(default=None, ge=1, le=86400)
    tags: Optional[str] = None


class JobResponse(BaseModel):
    """Response model for a job."""
    id: int
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
    is_running: bool = False


def _job_to_response(job: Job, running_jobs: set) -> JobResponse:
    """Convert a Job dataclass to a response model."""
    return JobResponse(
        id=job.id,
        name=job.name,
        cron=job.cron,
        command=job.command,
        working_dir=job.working_dir,
        enabled=job.enabled,
        timeout_seconds=job.timeout_seconds,
        tags=job.tags,
        created_at=job.created_at,
        updated_at=job.updated_at,
        next_run=job.next_run,
        is_running=job.id in running_jobs,
    )


@router.get("", response_model=list[JobResponse])
async def list_jobs(
    request: Request,
    include_disabled: bool = True,
    tag: Optional[str] = None,
):
    """List all jobs."""
    db = request.app.state.db
    running_jobs = request.app.state.running_jobs
    jobs = db.list_jobs(include_disabled=include_disabled, tag=tag)
    return [_job_to_response(job, running_jobs) for job in jobs]


@router.get("/{name}", response_model=JobResponse)
async def get_job(request: Request, name: str):
    """Get a job by name."""
    db = request.app.state.db
    running_jobs = request.app.state.running_jobs
    job = db.get_job(name)
    if job is None:
        raise HTTPException(status_code=404, detail=f"Job '{name}' not found")
    return _job_to_response(job, running_jobs)


@router.post("", response_model=JobResponse, status_code=201)
async def create_job(request: Request, job_data: JobCreate):
    """Create a new job."""
    db = request.app.state.db
    running_jobs = request.app.state.running_jobs

    # Check if job with same name already exists
    existing = db.get_job(job_data.name)
    if existing is not None:
        raise HTTPException(
            status_code=409,
            detail=f"Job '{job_data.name}' already exists"
        )

    # Validate cron expression
    try:
        next_run = calculate_next_run(job_data.cron)
    except Exception as e:
        raise HTTPException(
            status_code=400,
            detail=f"Invalid cron expression: {e}"
        )

    # Create job
    job = Job(
        id=None,
        name=job_data.name,
        cron=job_data.cron,
        command=job_data.command,
        working_dir=job_data.working_dir,
        enabled=job_data.enabled,
        timeout_seconds=job_data.timeout_seconds,
        tags=job_data.tags,
        created_at=None,
        updated_at=None,
        next_run=next_run,
    )

    job_id = db.add_job(job)
    job.id = job_id

    # Re-fetch to get timestamps
    created_job = db.get_job_by_id(job_id)
    return _job_to_response(created_job, running_jobs)


@router.put("/{name}", response_model=JobResponse)
async def update_job(request: Request, name: str, job_data: JobUpdate):
    """Update an existing job."""
    db = request.app.state.db
    running_jobs = request.app.state.running_jobs

    job = db.get_job(name)
    if job is None:
        raise HTTPException(status_code=404, detail=f"Job '{name}' not found")

    # Update fields if provided
    if job_data.cron is not None:
        try:
            job.next_run = calculate_next_run(job_data.cron)
        except Exception as e:
            raise HTTPException(
                status_code=400,
                detail=f"Invalid cron expression: {e}"
            )
        job.cron = job_data.cron

    if job_data.command is not None:
        job.command = job_data.command
    if job_data.working_dir is not None:
        job.working_dir = job_data.working_dir
    if job_data.enabled is not None:
        job.enabled = job_data.enabled
    if job_data.timeout_seconds is not None:
        job.timeout_seconds = job_data.timeout_seconds
    if job_data.tags is not None:
        job.tags = job_data.tags

    db.update_job(job)

    # Re-fetch to get updated timestamps
    updated_job = db.get_job_by_id(job.id)
    return _job_to_response(updated_job, running_jobs)


@router.delete("/{name}", status_code=204)
async def delete_job(request: Request, name: str):
    """Delete a job."""
    db = request.app.state.db

    if not db.delete_job(name):
        raise HTTPException(status_code=404, detail=f"Job '{name}' not found")


@router.post("/{name}/enable", response_model=JobResponse)
async def enable_job(request: Request, name: str):
    """Enable a job."""
    db = request.app.state.db
    running_jobs = request.app.state.running_jobs

    if not db.set_job_enabled(name, True):
        raise HTTPException(status_code=404, detail=f"Job '{name}' not found")

    job = db.get_job(name)
    return _job_to_response(job, running_jobs)


@router.post("/{name}/disable", response_model=JobResponse)
async def disable_job(request: Request, name: str):
    """Disable a job."""
    db = request.app.state.db
    running_jobs = request.app.state.running_jobs

    if not db.set_job_enabled(name, False):
        raise HTTPException(status_code=404, detail=f"Job '{name}' not found")

    job = db.get_job(name)
    return _job_to_response(job, running_jobs)


@router.post("/{name}/trigger", response_model=dict)
async def trigger_job(request: Request, name: str):
    """
    Trigger immediate execution of a job.

    Sets the job's next_run to now, so it will be picked up
    on the next scheduler check.
    """
    db = request.app.state.db

    job = db.get_job(name)
    if job is None:
        raise HTTPException(status_code=404, detail=f"Job '{name}' not found")

    if not job.enabled:
        raise HTTPException(
            status_code=400,
            detail=f"Job '{name}' is disabled. Enable it first."
        )

    # Set next_run to now
    db.update_next_run(job.id, datetime.now())

    return {"message": f"Job '{name}' triggered for immediate execution"}

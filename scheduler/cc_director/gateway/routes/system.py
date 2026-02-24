"""System API routes - health, status, config."""

from datetime import datetime
from typing import Optional

from fastapi import APIRouter, Request
from pydantic import BaseModel

router = APIRouter()


class HealthResponse(BaseModel):
    """Health check response."""
    status: str
    timestamp: datetime


class StatusResponse(BaseModel):
    """Service status response."""
    status: str
    timestamp: datetime
    total_jobs: int
    enabled_jobs: int
    running_jobs: int
    database_path: str
    uptime_seconds: Optional[float] = None


class ConfigResponse(BaseModel):
    """Current configuration response."""
    db_path: str
    log_dir: str
    log_level: str
    check_interval: int
    gateway_host: str
    gateway_port: int


# Track service start time
_start_time: Optional[datetime] = None


def set_start_time():
    """Set the service start time."""
    global _start_time
    _start_time = datetime.now()


@router.get("/health", response_model=HealthResponse)
async def health_check():
    """
    Health check endpoint.

    Returns 200 OK if the service is healthy.
    """
    return HealthResponse(
        status="healthy",
        timestamp=datetime.now(),
    )


@router.get("/status", response_model=StatusResponse)
async def get_status(request: Request):
    """
    Get service status.

    Returns information about the running service.
    """
    db = request.app.state.db
    running_jobs = request.app.state.running_jobs

    all_jobs = db.list_jobs(include_disabled=True)
    enabled_jobs = [j for j in all_jobs if j.enabled]

    uptime = None
    if _start_time is not None:
        uptime = (datetime.now() - _start_time).total_seconds()

    return StatusResponse(
        status="running",
        timestamp=datetime.now(),
        total_jobs=len(all_jobs),
        enabled_jobs=len(enabled_jobs),
        running_jobs=len(running_jobs),
        database_path=str(db.db_path),
        uptime_seconds=uptime,
    )


@router.get("/config", response_model=ConfigResponse)
async def get_config(request: Request):
    """
    Get current configuration.

    Note: Sensitive values are not exposed.
    """
    db = request.app.state.db
    config = getattr(request.app.state, "config", None)

    return ConfigResponse(
        db_path=str(db.db_path),
        log_dir=str(config.log_dir) if config else "./logs",
        log_level=config.log_level if config else "INFO",
        check_interval=config.check_interval if config else 60,
        gateway_host=getattr(config, "gateway_host", "0.0.0.0"),
        gateway_port=getattr(config, "gateway_port", 6060),
    )

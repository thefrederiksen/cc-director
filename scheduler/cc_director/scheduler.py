"""cc_scheduler CLI - job management for cc_director."""

import sys
from datetime import datetime, timedelta
from typing import Optional

import click

from .config import Config
from .cron import calculate_next_run, describe_cron, validate_cron
from .database import Database, Job
from .executor import execute_job


def get_db(config_file: Optional[str] = None) -> Database:
    """Get database instance from config."""
    config = Config.load(config_file)
    db = Database(str(config.get_db_path()))
    db.create_tables()
    return db


def format_datetime(dt: Optional[datetime]) -> str:
    """Format datetime for display."""
    if dt is None:
        return "-"
    return dt.strftime("%Y-%m-%d %H:%M:%S")


def format_duration(seconds: Optional[float]) -> str:
    """Format duration for display."""
    if seconds is None:
        return "-"
    if seconds < 60:
        return f"{seconds:.1f}s"
    minutes = int(seconds // 60)
    secs = seconds % 60
    return f"{minutes}m {secs:.0f}s"


@click.group()
@click.version_option(version="0.1.0", prog_name="cc_scheduler")
@click.option("--config", "-c", "config_file", envvar="CC_DIRECTOR_CONFIG",
              help="Path to config file")
@click.pass_context
def cli(ctx: click.Context, config_file: Optional[str]) -> None:
    """cc_scheduler - manage scheduled jobs for cc_director."""
    ctx.ensure_object(dict)
    ctx.obj["config_file"] = config_file


# ============================================================================
# Job Management Commands
# ============================================================================

@cli.command()
@click.argument("name")
@click.option("--cron", required=True, help="Cron expression (e.g., '0 7 * * 1-5')")
@click.option("--cmd", required=True, help="Command to execute")
@click.option("--working-dir", "-d", help="Working directory for command")
@click.option("--timeout", "-t", default=300, help="Timeout in seconds (default: 300)")
@click.option("--tags", help="Comma-separated tags")
@click.option("--disabled", is_flag=True, help="Create job in disabled state")
@click.pass_context
def add(ctx: click.Context, name: str, cron: str, cmd: str,
        working_dir: Optional[str], timeout: int, tags: Optional[str],
        disabled: bool) -> None:
    """Add a new scheduled job."""
    if not validate_cron(cron):
        click.echo(f"ERROR: Invalid cron expression: {cron}", err=True)
        sys.exit(1)

    db = get_db(ctx.obj.get("config_file"))

    # Check if job already exists
    existing = db.get_job(name)
    if existing:
        click.echo(f"ERROR: Job '{name}' already exists", err=True)
        sys.exit(1)

    # Calculate first run time
    next_run = calculate_next_run(cron) if not disabled else None

    job = Job(
        id=None,
        name=name,
        cron=cron,
        command=cmd,
        working_dir=working_dir,
        enabled=not disabled,
        timeout_seconds=timeout,
        tags=tags,
        created_at=None,
        updated_at=None,
        next_run=next_run,
    )

    job_id = db.add_job(job)
    click.echo(f"Job '{name}' created (ID: {job_id})")
    if next_run:
        click.echo(f"Next run: {format_datetime(next_run)}")
    db.close()


@cli.command("list")
@click.option("--all", "-a", "include_all", is_flag=True, help="Include disabled jobs")
@click.option("--tag", "-t", help="Filter by tag")
@click.pass_context
def list_jobs(ctx: click.Context, include_all: bool, tag: Optional[str]) -> None:
    """List all scheduled jobs."""
    db = get_db(ctx.obj.get("config_file"))
    jobs = db.list_jobs(include_disabled=include_all, tag=tag)
    db.close()

    if not jobs:
        click.echo("No jobs found")
        return

    # Header
    click.echo(f"{'ID':<4} {'NAME':<25} {'CRON':<15} {'ENABLED':<8} {'NEXT RUN':<20} {'TAGS'}")
    click.echo("-" * 90)

    for job in jobs:
        enabled = "yes" if job.enabled else "no"
        next_run = format_datetime(job.next_run)
        tags = job.tags or ""
        click.echo(f"{job.id:<4} {job.name:<25} {job.cron:<15} {enabled:<8} {next_run:<20} {tags}")


@cli.command()
@click.argument("name")
@click.pass_context
def show(ctx: click.Context, name: str) -> None:
    """Show detailed information about a job."""
    db = get_db(ctx.obj.get("config_file"))
    job = db.get_job(name)

    if not job:
        click.echo(f"ERROR: Job '{name}' not found", err=True)
        sys.exit(1)

    click.echo(f"Name:         {job.name}")
    click.echo(f"ID:           {job.id}")
    click.echo(f"Cron:         {job.cron}")
    click.echo(f"Schedule:     {describe_cron(job.cron)}")
    click.echo(f"Command:      {job.command}")
    click.echo(f"Working Dir:  {job.working_dir or '(default)'}")
    click.echo(f"Timeout:      {job.timeout_seconds}s")
    click.echo(f"Enabled:      {'yes' if job.enabled else 'no'}")
    click.echo(f"Tags:         {job.tags or '(none)'}")
    click.echo(f"Created:      {format_datetime(job.created_at)}")
    click.echo(f"Updated:      {format_datetime(job.updated_at)}")
    click.echo(f"Next Run:     {format_datetime(job.next_run)}")

    # Show last run
    last_run = db.get_last_run(name)
    if last_run:
        status = "TIMEOUT" if last_run.timed_out else (
            "OK" if last_run.exit_code == 0 else f"FAILED ({last_run.exit_code})"
        )
        click.echo(f"\nLast Run:     {format_datetime(last_run.started_at)} - {status}")
        click.echo(f"Duration:     {format_duration(last_run.duration_seconds)}")

    db.close()


@cli.command()
@click.argument("name")
@click.option("--cron", help="New cron expression")
@click.option("--cmd", help="New command")
@click.option("--working-dir", "-d", help="New working directory")
@click.option("--timeout", "-t", type=int, help="New timeout in seconds")
@click.option("--tags", help="New tags (replaces existing)")
@click.pass_context
def edit(ctx: click.Context, name: str, cron: Optional[str], cmd: Optional[str],
         working_dir: Optional[str], timeout: Optional[int],
         tags: Optional[str]) -> None:
    """Edit an existing job."""
    db = get_db(ctx.obj.get("config_file"))
    job = db.get_job(name)

    if not job:
        click.echo(f"ERROR: Job '{name}' not found", err=True)
        sys.exit(1)

    if cron is not None:
        if not validate_cron(cron):
            click.echo(f"ERROR: Invalid cron expression: {cron}", err=True)
            sys.exit(1)
        job.cron = cron
        # Recalculate next run
        if job.enabled:
            job.next_run = calculate_next_run(cron)

    if cmd is not None:
        job.command = cmd
    if working_dir is not None:
        job.working_dir = working_dir
    if timeout is not None:
        job.timeout_seconds = timeout
    if tags is not None:
        job.tags = tags if tags else None

    db.update_job(job)
    click.echo(f"Job '{name}' updated")
    db.close()


@cli.command()
@click.argument("name")
@click.pass_context
def enable(ctx: click.Context, name: str) -> None:
    """Enable a job."""
    db = get_db(ctx.obj.get("config_file"))
    job = db.get_job(name)

    if not job:
        click.echo(f"ERROR: Job '{name}' not found", err=True)
        sys.exit(1)

    if job.enabled:
        click.echo(f"Job '{name}' is already enabled")
    else:
        db.set_job_enabled(name, True)
        # Set next run time
        next_run = calculate_next_run(job.cron)
        db.update_next_run(job.id, next_run)
        click.echo(f"Job '{name}' enabled. Next run: {format_datetime(next_run)}")

    db.close()


@cli.command()
@click.argument("name")
@click.pass_context
def disable(ctx: click.Context, name: str) -> None:
    """Disable a job."""
    db = get_db(ctx.obj.get("config_file"))

    if not db.get_job(name):
        click.echo(f"ERROR: Job '{name}' not found", err=True)
        sys.exit(1)

    db.set_job_enabled(name, False)
    click.echo(f"Job '{name}' disabled")
    db.close()


@cli.command()
@click.argument("name")
@click.option("--force", "-f", is_flag=True, help="Skip confirmation")
@click.pass_context
def delete(ctx: click.Context, name: str, force: bool) -> None:
    """Delete a job."""
    db = get_db(ctx.obj.get("config_file"))

    if not db.get_job(name):
        click.echo(f"ERROR: Job '{name}' not found", err=True)
        sys.exit(1)

    if not force:
        if not click.confirm(f"Delete job '{name}'?"):
            click.echo("Cancelled")
            return

    db.delete_job(name)
    click.echo(f"Job '{name}' deleted")
    db.close()


# ============================================================================
# Run History Commands
# ============================================================================

@cli.command()
@click.option("--job", "-j", "job_name", help="Filter by job name")
@click.option("--limit", "-n", default=20, help="Number of runs (default: 20)")
@click.option("--failed", "-f", is_flag=True, help="Only show failures")
@click.option("--today", is_flag=True, help="Only today's runs")
@click.option("--since", type=click.DateTime(), help="Runs since date")
@click.pass_context
def runs(ctx: click.Context, job_name: Optional[str], limit: int,
         failed: bool, today: bool, since: Optional[datetime]) -> None:
    """List recent job runs."""
    db = get_db(ctx.obj.get("config_file"))

    if today:
        since = datetime.now().replace(hour=0, minute=0, second=0, microsecond=0)

    run_list = db.list_runs(
        job_name=job_name,
        limit=limit,
        failed_only=failed,
        since=since,
    )
    db.close()

    if not run_list:
        click.echo("No runs found")
        return

    # Header
    click.echo(f"{'ID':<6} {'JOB':<25} {'STARTED':<20} {'EXIT':<6} {'DURATION':<10} {'STATUS'}")
    click.echo("-" * 85)

    for run in run_list:
        if run.timed_out:
            status = "TIMEOUT"
            exit_str = "-"
        elif run.exit_code is None:
            status = "RUNNING"
            exit_str = "-"
        elif run.exit_code == 0:
            status = "OK"
            exit_str = "0"
        else:
            status = "FAILED"
            exit_str = str(run.exit_code)

        started = format_datetime(run.started_at)
        duration = format_duration(run.duration_seconds)
        click.echo(f"{run.id:<6} {run.job_name:<25} {started:<20} {exit_str:<6} {duration:<10} {status}")


@cli.command("run")
@click.argument("run_id", type=int)
@click.pass_context
def show_run(ctx: click.Context, run_id: int) -> None:
    """Show details of a specific run."""
    db = get_db(ctx.obj.get("config_file"))
    run = db.get_run(run_id)
    db.close()

    if not run:
        click.echo(f"ERROR: Run {run_id} not found", err=True)
        sys.exit(1)

    if run.timed_out:
        status = "TIMEOUT"
    elif run.exit_code is None:
        status = "RUNNING"
    elif run.exit_code == 0:
        status = "OK"
    else:
        status = "FAILED"

    click.echo(f"Run ID:       {run.id}")
    click.echo(f"Job:          {run.job_name} (ID: {run.job_id})")
    click.echo(f"Status:       {status}")
    click.echo(f"Started:      {format_datetime(run.started_at)}")
    click.echo(f"Ended:        {format_datetime(run.ended_at)}")
    click.echo(f"Duration:     {format_duration(run.duration_seconds)}")
    click.echo(f"Exit Code:    {run.exit_code if run.exit_code is not None else '-'}")
    click.echo(f"Timed Out:    {'yes' if run.timed_out else 'no'}")

    if run.stdout:
        click.echo("\n--- STDOUT ---")
        click.echo(run.stdout)

    if run.stderr:
        click.echo("\n--- STDERR ---")
        click.echo(run.stderr)


@cli.command()
@click.argument("job_name")
@click.pass_context
def last(ctx: click.Context, job_name: str) -> None:
    """Show the last run for a job."""
    db = get_db(ctx.obj.get("config_file"))
    job = db.get_job(job_name)

    if not job:
        click.echo(f"ERROR: Job '{job_name}' not found", err=True)
        sys.exit(1)

    run = db.get_last_run(job_name)
    db.close()

    if not run:
        click.echo(f"No runs found for job '{job_name}'")
        return

    # Reuse show_run logic
    ctx.invoke(show_run, run_id=run.id)


# ============================================================================
# Service Control Commands
# ============================================================================

@cli.command()
@click.pass_context
def status(ctx: click.Context) -> None:
    """Check if the scheduler service is running."""
    # Simple check - see if we can connect to DB and count jobs
    db = get_db(ctx.obj.get("config_file"))
    jobs = db.list_jobs(include_disabled=True)
    enabled = [j for j in jobs if j.enabled]
    db.close()

    click.echo(f"Jobs: {len(jobs)} total, {len(enabled)} enabled")
    click.echo("\nNote: Use system tools to check if cc_director_service is running:")
    click.echo("  Windows: sc query cc_director")
    click.echo("  macOS: launchctl list | grep cc.director")


@cli.command()
@click.argument("name")
@click.pass_context
def trigger(ctx: click.Context, name: str) -> None:
    """Trigger a job to run immediately (outside normal schedule)."""
    db = get_db(ctx.obj.get("config_file"))
    job = db.get_job(name)

    if not job:
        click.echo(f"ERROR: Job '{name}' not found", err=True)
        sys.exit(1)

    click.echo(f"Triggering job '{name}'...")
    click.echo(f"Command: {job.command}")
    click.echo("")

    # Execute directly
    result = execute_job(
        command=job.command,
        working_dir=job.working_dir,
        timeout_seconds=job.timeout_seconds,
    )

    # Record the run
    from .database import Run
    run = Run(
        id=None,
        job_id=job.id,
        job_name=job.name,
        started_at=result.started_at,
        ended_at=result.ended_at,
        exit_code=result.exit_code,
        stdout=result.stdout,
        stderr=result.stderr,
        timed_out=result.timed_out,
        duration_seconds=result.duration_seconds,
    )
    run_id = db.create_run(run)
    db.close()

    # Show result
    if result.timed_out:
        click.echo(f"TIMEOUT after {result.duration_seconds:.1f}s")
    elif result.exit_code == 0:
        click.echo(f"OK in {result.duration_seconds:.1f}s")
    else:
        click.echo(f"FAILED with exit code {result.exit_code} in {result.duration_seconds:.1f}s")

    if result.stdout:
        click.echo("\n--- STDOUT ---")
        click.echo(result.stdout)

    if result.stderr:
        click.echo("\n--- STDERR ---")
        click.echo(result.stderr)

    click.echo(f"\nRun recorded with ID: {run_id}")


def main():
    """Entry point for cc_scheduler."""
    cli()


if __name__ == "__main__":
    main()

"""cc_director service - main daemon that executes scheduled jobs."""

import asyncio
import logging
import os
import signal
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
from logging.handlers import TimedRotatingFileHandler
from pathlib import Path
from typing import Optional

import click

from .config import Config
from .cron import calculate_next_run
from .database import Database, Job, Run
from .dispatcher import CommunicationDispatcher, ApprovedFolderWatcher, DispatcherConfig, SQLiteDispatcher
from .executor import execute_job

# Global shutdown flag
_shutdown_requested = False
_running_jobs: set[int] = set()
_running_jobs_lock = threading.Lock()

# Communication dispatcher globals
_comm_dispatcher: Optional[CommunicationDispatcher] = None
_comm_watcher: Optional[ApprovedFolderWatcher] = None
_sqlite_dispatcher: Optional[SQLiteDispatcher] = None
_dispatcher_loop: Optional[asyncio.AbstractEventLoop] = None

# SQLite database path for Communication Manager
def _get_comm_manager_db_path() -> Path:
    """Get Communication Manager database path from config or default location."""
    local = os.environ.get("LOCALAPPDATA", "")
    if local:
        default = Path(local) / "cc-tools" / "data" / "comm_manager" / "content" / "communications.db"
    else:
        default = Path.home() / "cc_communication_manager" / "content" / "communications.db"
    return default

COMM_MANAGER_DB_PATH = _get_comm_manager_db_path()


def setup_logging(log_dir: Path, log_level: str) -> logging.Logger:
    """
    Set up logging with file rotation.

    Args:
        log_dir: Directory for log files
        log_level: Logging level (DEBUG, INFO, WARNING, ERROR)

    Returns:
        Configured logger
    """
    log_dir.mkdir(parents=True, exist_ok=True)
    log_file = log_dir / "cc_director.log"

    logger = logging.getLogger("cc_director")
    logger.setLevel(getattr(logging, log_level.upper(), logging.INFO))

    # File handler with daily rotation, keep 7 days
    file_handler = TimedRotatingFileHandler(
        log_file,
        when="midnight",
        interval=1,
        backupCount=7,
    )
    file_handler.setFormatter(
        logging.Formatter("%(asctime)s [%(levelname)s] %(message)s")
    )
    logger.addHandler(file_handler)

    # Console handler for verbose mode
    console_handler = logging.StreamHandler()
    console_handler.setFormatter(
        logging.Formatter("%(asctime)s [%(levelname)s] %(message)s")
    )
    logger.addHandler(console_handler)

    return logger


def signal_handler(signum: int, frame) -> None:
    """Handle shutdown signals."""
    global _shutdown_requested
    _shutdown_requested = True


def _on_new_approved_item(item_path) -> None:
    """Callback when new item appears in approved/ folder."""
    global _comm_dispatcher, _dispatcher_loop

    if _comm_dispatcher is None or _dispatcher_loop is None:
        return

    # Schedule the dispatch on the async loop
    async def dispatch():
        await _comm_dispatcher.dispatch_item(item_path)

    asyncio.run_coroutine_threadsafe(dispatch(), _dispatcher_loop)


def _start_communication_dispatcher(
    config: Config,
    logger: logging.Logger,
) -> threading.Thread:
    """
    Start the communication dispatcher in a background thread.

    Uses SQLite-based dispatcher that polls the Communication Manager database
    for approved items ready to send.

    Args:
        config: Configuration instance
        logger: Logger instance

    Returns:
        The dispatcher thread
    """
    global _sqlite_dispatcher, _dispatcher_loop

    def run_dispatcher():
        """Run the SQLite dispatcher event loop."""
        global _sqlite_dispatcher, _dispatcher_loop

        _dispatcher_loop = asyncio.new_event_loop()
        asyncio.set_event_loop(_dispatcher_loop)

        # Create SQLite dispatcher
        _sqlite_dispatcher = SQLiteDispatcher(
            db_path=COMM_MANAGER_DB_PATH,
            poll_interval=5.0  # Check every 5 seconds
        )

        logger.info(f"SQLite dispatcher watching: {COMM_MANAGER_DB_PATH}")

        async def run_until_shutdown():
            """Run dispatcher until shutdown requested."""
            try:
                await _sqlite_dispatcher.start()
            except asyncio.CancelledError:
                pass
            finally:
                _sqlite_dispatcher.stop()

        try:
            # Create task for the dispatcher
            task = _dispatcher_loop.create_task(run_until_shutdown())

            # Check for shutdown periodically
            while not _shutdown_requested:
                _dispatcher_loop.run_until_complete(asyncio.sleep(1))

            # Cancel the task on shutdown
            task.cancel()
            _dispatcher_loop.run_until_complete(asyncio.sleep(0.5))

        finally:
            _dispatcher_loop.close()

    thread = threading.Thread(
        target=run_dispatcher,
        daemon=True,
        name="sqlite-dispatcher"
    )
    thread.start()

    logger.info(f"Communication dispatcher started (SQLite mode), watching {COMM_MANAGER_DB_PATH}")
    return thread


def run_job(db: Database, job: Job, logger: logging.Logger) -> None:
    """
    Execute a single job and record the result.

    Args:
        db: Database instance
        job: Job to execute
        logger: Logger instance
    """
    with _running_jobs_lock:
        if job.id in _running_jobs:
            logger.warning(f"Job '{job.name}' is already running, skipping")
            return
        _running_jobs.add(job.id)

    try:
        logger.info(f"Starting job '{job.name}': {job.command}")

        # Create initial run record
        run = Run(
            id=None,
            job_id=job.id,
            job_name=job.name,
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

        # Execute the job
        result = execute_job(
            command=job.command,
            working_dir=job.working_dir,
            timeout_seconds=job.timeout_seconds,
        )

        # Update run record with results
        run.ended_at = result.ended_at
        run.exit_code = result.exit_code
        run.stdout = result.stdout
        run.stderr = result.stderr
        run.timed_out = result.timed_out
        run.duration_seconds = result.duration_seconds
        db.update_run(run)

        # Log result
        if result.timed_out:
            logger.warning(
                f"Job '{job.name}' timed out after {result.duration_seconds:.1f}s"
            )
        elif result.exit_code == 0:
            logger.info(
                f"Job '{job.name}' completed successfully in {result.duration_seconds:.1f}s"
            )
        else:
            logger.error(
                f"Job '{job.name}' failed with exit code {result.exit_code} "
                f"in {result.duration_seconds:.1f}s"
            )
            if result.stderr:
                logger.error(f"  stderr: {result.stderr[:500]}")

        # Calculate next run time
        next_run = calculate_next_run(job.cron)
        db.update_next_run(job.id, next_run)
        logger.info(f"Job '{job.name}' next run scheduled for {next_run}")

    finally:
        with _running_jobs_lock:
            _running_jobs.discard(job.id)


def _start_gateway_server(
    db: Database,
    config: Config,
    running_jobs: set,
    logger: logging.Logger,
) -> threading.Thread:
    """
    Start the gateway web server in a background thread.

    Args:
        db: Database instance
        config: Configuration instance
        running_jobs: Set of currently running job IDs
        logger: Logger instance

    Returns:
        The gateway server thread
    """
    import uvicorn

    from .gateway import create_app
    from .gateway.routes.system import set_start_time

    # Create the FastAPI app
    app = create_app(db, running_jobs)
    app.state.config = config

    # Set service start time for uptime tracking
    set_start_time()

    def run_server():
        """Run uvicorn in a thread."""
        uvicorn_config = uvicorn.Config(
            app,
            host=config.gateway_host,
            port=config.gateway_port,
            log_level="warning",  # Reduce uvicorn noise
            access_log=False,
        )
        server = uvicorn.Server(uvicorn_config)

        # Run the server
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        try:
            loop.run_until_complete(server.serve())
        finally:
            loop.close()

    thread = threading.Thread(target=run_server, daemon=True)
    thread.start()

    logger.info(
        f"Gateway server started at http://{config.gateway_host}:{config.gateway_port}"
    )
    return thread


def initialize_job_schedules(db: Database, logger: logging.Logger) -> None:
    """
    Initialize next_run times for all enabled jobs that don't have one.

    Args:
        db: Database instance
        logger: Logger instance
    """
    jobs = db.list_jobs(include_disabled=False)
    for job in jobs:
        if job.next_run is None:
            next_run = calculate_next_run(job.cron)
            db.update_next_run(job.id, next_run)
            logger.info(f"Initialized schedule for '{job.name}': next run at {next_run}")


def run_scheduler(
    config: Config,
    verbose: bool = False,
    with_gateway: bool = False,
) -> None:
    """
    Main scheduler loop.

    Args:
        config: Configuration instance
        verbose: Enable verbose logging
        with_gateway: Start the web gateway server
    """
    global _shutdown_requested

    # Set up signal handlers
    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)

    # Set up logging
    log_level = "DEBUG" if verbose else config.log_level
    logger = setup_logging(config.get_log_dir(), log_level)
    logger.info("cc_director service starting")

    # Initialize database
    db = Database(str(config.get_db_path()))
    db.create_tables()
    logger.info(f"Database initialized at {config.get_db_path()}")

    # Start gateway server if requested
    gateway_thread = None
    if with_gateway or config.gateway_enabled:
        gateway_thread = _start_gateway_server(
            db, config, _running_jobs, logger
        )

    # Start communication dispatcher
    dispatcher_thread = None
    try:
        dispatcher_thread = _start_communication_dispatcher(config, logger)
    except Exception as e:
        logger.error(f"Failed to start communication dispatcher: {e}")

    # Initialize job schedules
    initialize_job_schedules(db, logger)

    # Thread pool for concurrent job execution
    executor = ThreadPoolExecutor(max_workers=10, thread_name_prefix="job_")

    logger.info(
        f"Scheduler running. Check interval: {config.check_interval}s. "
        "Press Ctrl+C to stop."
    )

    try:
        while not _shutdown_requested:
            # Get due jobs
            due_jobs = db.get_due_jobs()

            for job in due_jobs:
                if _shutdown_requested:
                    break
                # Submit job to thread pool
                executor.submit(run_job, db, job, logger)

            # Sleep until next check (or shutdown)
            sleep_time = config.check_interval
            sleep_start = time.time()
            while not _shutdown_requested and (time.time() - sleep_start) < sleep_time:
                time.sleep(1)

    except KeyboardInterrupt:
        logger.info("Keyboard interrupt received")
    finally:
        logger.info("Shutting down...")

        # Wait for running jobs to complete
        with _running_jobs_lock:
            running_count = len(_running_jobs)
        if running_count > 0:
            logger.info(f"Waiting for {running_count} running jobs to complete...")

        executor.shutdown(wait=True, cancel_futures=False)

        db.close()
        logger.info("cc_director service stopped")


def check_config(config: Config) -> bool:
    """
    Validate configuration.

    Args:
        config: Configuration instance

    Returns:
        True if valid
    """
    print(f"Database path: {config.get_db_path()}")
    print(f"Log directory: {config.get_log_dir()}")
    print(f"Log level: {config.log_level}")
    print(f"Check interval: {config.check_interval}s")
    print(f"Shutdown timeout: {config.shutdown_timeout}s")
    print(f"Gateway enabled: {config.gateway_enabled}")
    print(f"Gateway host: {config.gateway_host}")
    print(f"Gateway port: {config.gateway_port}")

    # Check database
    db_path = config.get_db_path()
    if db_path.exists():
        print(f"Database exists: {db_path}")
        db = Database(str(db_path))
        jobs = db.list_jobs(include_disabled=True)
        print(f"Jobs in database: {len(jobs)}")
        db.close()
    else:
        print(f"Database will be created at: {db_path}")

    return True


@click.group()
@click.version_option(version="0.1.0", prog_name="cc_director_service")
def cli():
    """cc_director service - scheduled job execution daemon."""
    pass


@cli.command()
@click.option("--verbose", "-v", is_flag=True, help="Enable verbose logging")
@click.option("--config", "-c", "config_file", help="Path to config file")
@click.option(
    "--with-gateway", "-g",
    is_flag=True,
    help="Start web gateway server (dashboard and REST API)"
)
def run(verbose: bool, config_file: Optional[str], with_gateway: bool) -> None:
    """Run the scheduler service in foreground."""
    config = Config.load(config_file)
    run_scheduler(config, verbose=verbose, with_gateway=with_gateway)


@cli.command()
@click.option("--config", "-c", "config_file", help="Path to config file")
def check(config_file: Optional[str]) -> None:
    """Check configuration and database status."""
    config = Config.load(config_file)
    check_config(config)


def main():
    """Entry point for cc_director_service."""
    cli()


if __name__ == "__main__":
    main()

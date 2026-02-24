# cc_director Scheduler

Cross-platform job scheduler with cron-style scheduling.

## Installation

```bash
cd scheduler
pip install -e .
```

For development (with test dependencies):

```bash
pip install -e ".[dev]"
```

## Quick Start

### Add a job

```bash
cc_scheduler add "my_job" --cron "0 7 * * 1-5" --cmd "python /path/to/script.py"
```

### List jobs

```bash
cc_scheduler list
```

### Run the service

```bash
cc_director_service run
```

## CLI Reference

### Job Management

```bash
# Add a new job
cc_scheduler add <name> --cron "<expression>" --cmd "<command>" [options]
  --working-dir   Working directory for command
  --timeout       Timeout in seconds (default: 300)
  --tags          Comma-separated tags
  --disabled      Create in disabled state

# List jobs
cc_scheduler list [--all] [--tag <tag>]

# Show job details
cc_scheduler show <name>

# Edit a job
cc_scheduler edit <name> [--cron] [--cmd] [--timeout] [--tags]

# Enable/disable
cc_scheduler enable <name>
cc_scheduler disable <name>

# Delete a job
cc_scheduler delete <name> [--force]
```

### Run History

```bash
# List recent runs
cc_scheduler runs [--job <name>] [--limit <n>] [--failed] [--today]

# Show run details
cc_scheduler run <run_id>

# Show last run for a job
cc_scheduler last <job_name>
```

### Service Control

```bash
# Check status
cc_scheduler status

# Trigger job immediately
cc_scheduler trigger <name>
```

## Configuration

Environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| CC_DIRECTOR_DB | `./cc_director.db` | SQLite database path |
| CC_DIRECTOR_LOG_DIR | `./logs` | Log directory |
| CC_DIRECTOR_LOG_LEVEL | `INFO` | Log level (DEBUG, INFO, WARNING, ERROR) |
| CC_DIRECTOR_CHECK_INTERVAL | `60` | Seconds between schedule checks |
| CC_DIRECTOR_SHUTDOWN_TIMEOUT | `30` | Seconds to wait for jobs on shutdown |

## Cron Expression Format

```
minute hour day month weekday
  *     *    *    *     *
```

Examples:
- `* * * * *` - Every minute
- `0 7 * * 1-5` - 7:00 AM, Monday-Friday
- `*/15 * * * *` - Every 15 minutes
- `0 7,19 * * *` - 7:00 AM and 7:00 PM daily

## Platform Integration

### Windows (NSSM)

```bash
cd platform/windows
install.bat
```

### macOS (launchd)

```bash
cd platform/macos
chmod +x install.sh
./install.sh
```

## Running Tests

```bash
pip install -e ".[dev]"
pytest
```

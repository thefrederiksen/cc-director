# CC Director Gateway & Dashboard

The CC Director Gateway provides a web-based dashboard and REST API for managing scheduled jobs. It runs alongside the scheduler service and provides real-time visibility into job execution.

## Quick Start

Start the scheduler with the gateway enabled:

```bash
cc_director_service run --with-gateway
```

Then open your browser to: **http://localhost:6060/**

## Features

### Web Dashboard

- **Dashboard** - Overview with stats, recent activity, and active jobs
- **Jobs** - List, create, edit, enable/disable, and trigger jobs
- **Runs** - View run history with filtering and search
- **Real-time Updates** - WebSocket connection shows live job status

### REST API

- Full CRUD operations for jobs
- Run history queries
- Health and status endpoints
- OpenAPI documentation at `/api/docs`

### WebSocket

- Live notifications when jobs start/complete
- Automatic dashboard refresh
- Connection status indicator

---

## Starting the Gateway

### Command Line Flag

```bash
cc_director_service run --with-gateway
# or shorthand
cc_director_service run -g
```

### Environment Variable

```bash
set CC_DIRECTOR_GATEWAY_ENABLED=true
cc_director_service run
```

### Configuration File

Create a config file (e.g., `config.ini`):

```ini
gateway_enabled=true
gateway_host=0.0.0.0
gateway_port=6060
```

Then run:

```bash
cc_director_service run --config config.ini
```

---

## Configuration Options

| Environment Variable | Default | Description |
|---------------------|---------|-------------|
| `CC_DIRECTOR_GATEWAY_ENABLED` | `false` | Enable the web gateway |
| `CC_DIRECTOR_GATEWAY_HOST` | `0.0.0.0` | Host to bind to |
| `CC_DIRECTOR_GATEWAY_PORT` | `6060` | Port to listen on |

---

## Dashboard Pages

### Dashboard (/)

The main overview page showing:

- **Stats Cards** - Running jobs, successful/failed runs today
- **Recent Activity** - Last 10 job runs with status
- **Active Jobs** - Enabled jobs with next run times

Data refreshes automatically every 30 seconds and via WebSocket events.

### Jobs (/jobs)

List and manage all scheduled jobs:

- View all jobs with schedule, next run, and status
- Toggle "Show disabled jobs" to include disabled jobs
- **Create Job** - Click "+ New Job" to add a job
- **Actions** - Trigger, Enable/Disable, Delete jobs

### Job Detail (/jobs/{name})

View and edit a specific job:

- **Details** - Name, schedule, command, timeout, tags
- **Edit Form** - Modify schedule, command, working directory
- **Recent Runs** - Last 20 runs for this job
- **Actions** - Trigger Now, Enable/Disable, Delete

### Runs (/runs)

View job execution history:

- **Filters** - By job name, limit, failed only
- **Table** - Job, started time, duration, status, exit code
- Click any row to view run details

### Run Detail (/runs/{id})

View complete run information:

- Job name, status, exit code, timing
- **Standard Output** - Full stdout from the job
- **Standard Error** - Full stderr (if any)

---

## REST API Reference

### Jobs API

#### List Jobs

```http
GET /api/jobs?include_disabled=true&tag=mytag
```

Response:
```json
[
  {
    "id": 1,
    "name": "daily_backup",
    "cron": "0 2 * * *",
    "command": "python backup.py",
    "working_dir": "C:\\Scripts",
    "enabled": true,
    "timeout_seconds": 300,
    "tags": "backup,daily",
    "next_run": "2024-01-16T02:00:00",
    "is_running": false
  }
]
```

#### Get Job

```http
GET /api/jobs/{name}
```

#### Create Job

```http
POST /api/jobs
Content-Type: application/json

{
  "name": "my_job",
  "cron": "0 9 * * *",
  "command": "python script.py",
  "working_dir": "C:\\Projects",
  "enabled": true,
  "timeout_seconds": 300,
  "tags": "daily"
}
```

#### Update Job

```http
PUT /api/jobs/{name}
Content-Type: application/json

{
  "cron": "0 10 * * *",
  "command": "python new_script.py",
  "timeout_seconds": 600
}
```

#### Delete Job

```http
DELETE /api/jobs/{name}
```

#### Enable/Disable Job

```http
POST /api/jobs/{name}/enable
POST /api/jobs/{name}/disable
```

#### Trigger Job

Trigger immediate execution:

```http
POST /api/jobs/{name}/trigger
```

Response:
```json
{
  "message": "Job 'my_job' triggered for immediate execution"
}
```

### Runs API

#### List Runs

```http
GET /api/runs?job_name=my_job&limit=50&failed_only=true
```

Response:
```json
[
  {
    "id": 42,
    "job_id": 1,
    "job_name": "my_job",
    "started_at": "2024-01-15T09:00:00",
    "ended_at": "2024-01-15T09:00:15",
    "exit_code": 0,
    "stdout": "Job completed successfully",
    "stderr": null,
    "timed_out": false,
    "duration_seconds": 15.2,
    "status": "success"
  }
]
```

Status values: `running`, `success`, `failed`, `timeout`

#### Get Run

```http
GET /api/runs/{id}
```

#### Get Runs for Job

```http
GET /api/runs/job/{job_name}?limit=20
```

#### Today's Stats

```http
GET /api/runs/stats/today
```

Response:
```json
{
  "total_runs_today": 45,
  "successful_runs_today": 42,
  "failed_runs_today": 2,
  "timeout_runs_today": 1,
  "running_count": 1
}
```

### System API

#### Health Check

```http
GET /api/health
```

Response:
```json
{
  "status": "healthy",
  "timestamp": "2024-01-15T10:30:00"
}
```

#### Service Status

```http
GET /api/status
```

Response:
```json
{
  "status": "running",
  "timestamp": "2024-01-15T10:30:00",
  "total_jobs": 10,
  "enabled_jobs": 8,
  "running_jobs": 1,
  "database_path": "C:\\cc_director\\cc_director.db",
  "uptime_seconds": 3600.5
}
```

#### Configuration

```http
GET /api/config
```

### OpenAPI Documentation

Interactive API documentation is available at:

- **Swagger UI**: http://localhost:6060/api/docs
- **ReDoc**: http://localhost:6060/api/redoc

---

## WebSocket

Connect to `/ws` for real-time updates.

### Connection

```javascript
const ws = new WebSocket('ws://localhost:6060/ws');

ws.onmessage = function(event) {
    const message = JSON.parse(event.data);
    console.log(message.type, message.job_name);
};
```

### Message Types

| Type | Description |
|------|-------------|
| `job_started` | A job has started executing |
| `job_completed` | A job completed successfully |
| `job_failed` | A job failed (non-zero exit code) |
| `job_timeout` | A job timed out |
| `heartbeat` | Keep-alive message (every 30s) |

### Message Format

```json
{
  "type": "job_completed",
  "timestamp": "2024-01-15T09:00:15",
  "job_name": "daily_backup",
  "run_id": 42,
  "data": {}
}
```

### Keep-Alive

Send ping messages to keep the connection alive:

```javascript
ws.send(JSON.stringify({ type: 'ping' }));
// Server responds with: { type: 'pong', timestamp: '...' }
```

---

## Examples

### Create a Job with curl

```bash
curl -X POST http://localhost:6060/api/jobs \
  -H "Content-Type: application/json" \
  -d "{\"name\": \"test_job\", \"cron\": \"*/5 * * * *\", \"command\": \"echo Hello\", \"timeout_seconds\": 60}"
```

### Trigger a Job

```bash
curl -X POST http://localhost:6060/api/jobs/test_job/trigger
```

### Check Service Health

```bash
curl http://localhost:6060/api/health
```

### List Recent Failed Runs

```bash
curl "http://localhost:6060/api/runs?failed_only=true&limit=10"
```

---

## Architecture

```
+------------------+     +-------------------+
|   Web Browser    |     |   External Tools  |
|   (Dashboard)    |     |   (curl, scripts) |
+--------+---------+     +---------+---------+
         |                         |
         |    HTTP/WebSocket       |
         +------------+------------+
                      |
              +-------v-------+
              |   FastAPI     |
              |   Gateway     |
              |  (port 6060)  |
              +-------+-------+
                      |
              +-------v-------+
              |   Scheduler   |
              |   Service     |
              +-------+-------+
                      |
              +-------v-------+
              |    SQLite     |
              |   Database    |
              +---------------+
```

The gateway runs in a background thread within the scheduler service, sharing:
- Database connection for job/run data
- Running jobs set for real-time status

---

## Security Considerations

By default, the gateway:

- Binds to `0.0.0.0` (all interfaces) - change to `127.0.0.1` for localhost only
- Has no authentication - intended for local/trusted network use
- Uses HTTP (not HTTPS) - use a reverse proxy for production

For production deployments, consider:

1. Setting `gateway_host=127.0.0.1` for localhost only
2. Using a reverse proxy (nginx, Caddy) with HTTPS
3. Adding authentication at the proxy level

---

## Troubleshooting

### Gateway Not Starting

Check the logs for errors:

```bash
cc_director_service run --with-gateway --verbose
```

### Port Already in Use

Change the port:

```bash
set CC_DIRECTOR_GATEWAY_PORT=8080
cc_director_service run --with-gateway
```

### WebSocket Not Connecting

- Ensure you're using `ws://` not `wss://` for HTTP
- Check browser console for connection errors
- Verify the gateway is running on the expected port

### Templates Not Found

Ensure the gateway package is properly installed:

```bash
pip install -e .
```

Or run from the scheduler directory.

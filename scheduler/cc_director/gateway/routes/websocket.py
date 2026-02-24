"""WebSocket endpoint for real-time updates."""

import asyncio
import json
import logging
from datetime import datetime
from typing import Optional

from fastapi import APIRouter, WebSocket, WebSocketDisconnect

logger = logging.getLogger("cc_director.gateway.websocket")

router = APIRouter()


class ConnectionManager:
    """Manages WebSocket connections."""

    def __init__(self):
        self.active_connections: list[WebSocket] = []

    async def connect(self, websocket: WebSocket):
        """Accept a new WebSocket connection."""
        await websocket.accept()
        self.active_connections.append(websocket)
        logger.info(f"WebSocket connected. Total connections: {len(self.active_connections)}")

    def disconnect(self, websocket: WebSocket):
        """Remove a WebSocket connection."""
        if websocket in self.active_connections:
            self.active_connections.remove(websocket)
            logger.info(f"WebSocket disconnected. Total connections: {len(self.active_connections)}")

    async def broadcast(self, message: dict):
        """Broadcast a message to all connected clients."""
        if not self.active_connections:
            return

        message_text = json.dumps(message, default=str)
        disconnected = []

        for connection in self.active_connections:
            try:
                await connection.send_text(message_text)
            except Exception as e:
                logger.warning(f"Failed to send to WebSocket: {e}")
                disconnected.append(connection)

        # Remove disconnected clients
        for conn in disconnected:
            self.disconnect(conn)


# Global connection manager
manager = ConnectionManager()


# Event types for broadcasting
class EventType:
    JOB_STARTED = "job_started"
    JOB_COMPLETED = "job_completed"
    JOB_FAILED = "job_failed"
    JOB_TIMEOUT = "job_timeout"
    JOB_CREATED = "job_created"
    JOB_UPDATED = "job_updated"
    JOB_DELETED = "job_deleted"
    HEARTBEAT = "heartbeat"


async def broadcast_event(
    event_type: str,
    job_name: Optional[str] = None,
    run_id: Optional[int] = None,
    data: Optional[dict] = None,
):
    """
    Broadcast an event to all connected WebSocket clients.

    Args:
        event_type: Type of event (from EventType)
        job_name: Name of the job involved
        run_id: ID of the run involved
        data: Additional data to include
    """
    message = {
        "type": event_type,
        "timestamp": datetime.now().isoformat(),
        "job_name": job_name,
        "run_id": run_id,
        "data": data or {},
    }
    await manager.broadcast(message)


@router.websocket("/ws")
async def websocket_endpoint(websocket: WebSocket):
    """
    WebSocket endpoint for real-time updates.

    Clients connect here to receive live updates about:
    - Job starts and completions
    - Job status changes
    - Periodic heartbeats
    """
    await manager.connect(websocket)

    try:
        while True:
            # Wait for messages from client (keepalive or commands)
            try:
                data = await asyncio.wait_for(
                    websocket.receive_text(),
                    timeout=30.0
                )

                # Handle ping/pong for keepalive
                message = json.loads(data)
                if message.get("type") == "ping":
                    await websocket.send_text(json.dumps({
                        "type": "pong",
                        "timestamp": datetime.now().isoformat(),
                    }))

            except asyncio.TimeoutError:
                # Send heartbeat every 30 seconds
                await websocket.send_text(json.dumps({
                    "type": EventType.HEARTBEAT,
                    "timestamp": datetime.now().isoformat(),
                }))

    except WebSocketDisconnect:
        manager.disconnect(websocket)
    except Exception as e:
        logger.error(f"WebSocket error: {e}")
        manager.disconnect(websocket)

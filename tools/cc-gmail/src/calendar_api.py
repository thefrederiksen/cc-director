"""Google Calendar API wrapper for cc-gmail.

Requires OAuth authentication with calendar scope.
Uses google-api-python-client (already a dependency).
"""

import logging
from datetime import datetime, timedelta, date, timezone
from typing import Optional, List, Dict, Any

from googleapiclient.discovery import build
from google.oauth2.credentials import Credentials

logger = logging.getLogger(__name__)


class CalendarClient:
    """Google Calendar API v3 client."""

    def __init__(self, credentials: Credentials):
        self.service = build("calendar", "v3", credentials=credentials)

    def list_calendars(self) -> List[Dict[str, Any]]:
        """List all calendars the user has access to."""
        result = self.service.calendarList().list().execute()
        calendars = []
        for item in result.get("items", []):
            calendars.append({
                "id": item["id"],
                "name": item.get("summary", ""),
                "primary": item.get("primary", False),
                "access_role": item.get("accessRole", ""),
                "color": item.get("backgroundColor", ""),
            })
        return calendars

    def get_events(
        self, days_ahead: int = 7, calendar_id: str = "primary"
    ) -> List[Dict[str, Any]]:
        """Get upcoming events within a number of days."""
        now = datetime.now(timezone.utc)
        time_min = now.isoformat()
        time_max = (now + timedelta(days=days_ahead)).isoformat()

        result = (
            self.service.events()
            .list(
                calendarId=calendar_id,
                timeMin=time_min,
                timeMax=time_max,
                singleEvents=True,
                orderBy="startTime",
            )
            .execute()
        )

        return [self._format_event(e) for e in result.get("items", [])]

    def get_today(self, calendar_id: str = "primary") -> List[Dict[str, Any]]:
        """Get today's events."""
        today = date.today()
        time_min = datetime(
            today.year, today.month, today.day, tzinfo=timezone.utc
        ).isoformat()
        tomorrow = today + timedelta(days=1)
        time_max = datetime(
            tomorrow.year, tomorrow.month, tomorrow.day, tzinfo=timezone.utc
        ).isoformat()

        result = (
            self.service.events()
            .list(
                calendarId=calendar_id,
                timeMin=time_min,
                timeMax=time_max,
                singleEvents=True,
                orderBy="startTime",
            )
            .execute()
        )

        return [self._format_event(e) for e in result.get("items", [])]

    def get_event(
        self, event_id: str, calendar_id: str = "primary"
    ) -> Dict[str, Any]:
        """Get a single event by ID."""
        event = (
            self.service.events()
            .get(calendarId=calendar_id, eventId=event_id)
            .execute()
        )
        return self._format_event(event)

    def create_event(
        self,
        summary: str,
        start_time: datetime,
        duration_minutes: int = 60,
        location: Optional[str] = None,
        description: Optional[str] = None,
        attendees: Optional[List[str]] = None,
        all_day: bool = False,
        calendar_id: str = "primary",
    ) -> Dict[str, Any]:
        """Create a calendar event."""
        if all_day:
            body: Dict[str, Any] = {
                "summary": summary,
                "start": {"date": start_time.strftime("%Y-%m-%d")},
                "end": {
                    "date": (start_time + timedelta(days=1)).strftime("%Y-%m-%d")
                },
            }
        else:
            end_time = start_time + timedelta(minutes=duration_minutes)
            body = {
                "summary": summary,
                "start": {"dateTime": start_time.isoformat()},
                "end": {"dateTime": end_time.isoformat()},
            }

        if location:
            body["location"] = location
        if description:
            body["description"] = description
        if attendees:
            body["attendees"] = [{"email": email} for email in attendees]

        event = (
            self.service.events()
            .insert(calendarId=calendar_id, body=body)
            .execute()
        )
        return self._format_event(event)

    def delete_event(
        self, event_id: str, calendar_id: str = "primary"
    ) -> None:
        """Delete a calendar event."""
        self.service.events().delete(
            calendarId=calendar_id, eventId=event_id
        ).execute()

    def _format_event(self, event: dict) -> Dict[str, Any]:
        """Parse a Calendar API event into a clean dict."""
        start = event.get("start", {})
        end = event.get("end", {})

        # All-day events use "date", timed events use "dateTime"
        start_str = start.get("dateTime", start.get("date", ""))
        end_str = end.get("dateTime", end.get("date", ""))
        is_all_day = "date" in start and "dateTime" not in start

        attendees = []
        for att in event.get("attendees", []):
            attendees.append(
                {
                    "email": att.get("email", ""),
                    "response": att.get("responseStatus", "needsAction"),
                    "organizer": att.get("organizer", False),
                    "self": att.get("self", False),
                }
            )

        return {
            "id": event.get("id", ""),
            "summary": event.get("summary", "(No title)"),
            "start": start_str,
            "end": end_str,
            "is_all_day": is_all_day,
            "location": event.get("location", ""),
            "description": event.get("description", ""),
            "status": event.get("status", ""),
            "organizer": event.get("organizer", {}).get("email", ""),
            "attendees": attendees,
            "hangout_link": event.get("hangoutLink", ""),
            "html_link": event.get("htmlLink", ""),
            "created": event.get("created", ""),
            "updated": event.get("updated", ""),
        }

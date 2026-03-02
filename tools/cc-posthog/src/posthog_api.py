"""PostHog API client using httpx with HogQL and REST endpoints."""

from typing import Any, Optional

import httpx

from .schema import (
    EventCount,
    EventRecord,
    FunnelStep,
    PageViewRow,
    Recording,
    RecordingEvent,
    StatusInfo,
    TrafficSource,
    VisitorCount,
)
from .time_range import parse_range


class PostHogError(Exception):
    """Raised when a PostHog API call fails."""


class PostHogClient:
    """Client for querying PostHog analytics via HogQL and REST APIs."""

    def __init__(self, api_key: str, project_id: int, host: str = "https://us.posthog.com"):
        self._api_key = api_key
        self._project_id = project_id
        self._host = host.rstrip("/")
        self._client = httpx.Client(
            base_url=self._host,
            headers={
                "Authorization": f"Bearer {api_key}",
                "Content-Type": "application/json",
            },
            timeout=30.0,
        )

    def close(self) -> None:
        """Close the HTTP client."""
        self._client.close()

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()

    # -- Internal helpers --

    def _query_hogql(self, sql: str) -> list[list[Any]]:
        """Execute a HogQL query and return result rows."""
        url = f"/api/projects/{self._project_id}/query/"
        payload = {
            "query": {
                "kind": "HogQLQuery",
                "query": sql,
            }
        }
        resp = self._client.post(url, json=payload)
        if resp.status_code != 200:
            raise PostHogError(
                f"HogQL query failed ({resp.status_code}): {resp.text}"
            )
        data = resp.json()
        return data.get("results", [])

    def _query_structured(self, query: dict) -> dict:
        """Execute a structured PostHog query (e.g. FunnelsQuery)."""
        url = f"/api/projects/{self._project_id}/query/"
        payload = {"query": query}
        resp = self._client.post(url, json=payload)
        if resp.status_code != 200:
            raise PostHogError(
                f"Structured query failed ({resp.status_code}): {resp.text}"
            )
        return resp.json()

    def _rest_get(self, path: str, params: Optional[dict] = None) -> dict:
        """Make a GET request to PostHog REST API."""
        url = f"/api/projects/{self._project_id}/{path}"
        resp = self._client.get(url, params=params or {})
        if resp.status_code != 200:
            raise PostHogError(
                f"REST GET {path} failed ({resp.status_code}): {resp.text}"
            )
        return resp.json()

    # -- Public query methods --

    def get_status(self) -> StatusInfo:
        """Get project status including event and recording counts."""
        # Get project info
        url = f"/api/projects/{self._project_id}/"
        resp = self._client.get(url)
        if resp.status_code != 200:
            raise PostHogError(
                f"Project status failed ({resp.status_code}): {resp.text}"
            )
        project_data = resp.json()

        # Count recent events
        event_rows = self._query_hogql(
            "SELECT count() as cnt FROM events "
            "WHERE timestamp > now() - interval 30 day"
        )
        event_count = event_rows[0][0] if event_rows else 0

        # Count recordings
        rec_rows = self._query_hogql(
            "SELECT count() as cnt FROM session_replay_events "
            "WHERE min_first_timestamp > now() - interval 30 day"
        )
        rec_count = rec_rows[0][0] if rec_rows else 0

        return StatusInfo(
            project_name=project_data.get("name", "Unknown"),
            project_id=self._project_id,
            host=self._host,
            event_count=event_count,
            recording_count=rec_count,
        )

    def get_views(self, last: str = "7d", limit: int = 20) -> list[PageViewRow]:
        """Get page view counts grouped by URL."""
        rng = parse_range(last)
        sql = (
            "SELECT properties.$current_url as page, "
            "count() as views, "
            "count(distinct person_id) as unique_visitors "
            "FROM events "
            "WHERE event = '$pageview' "
            f"AND timestamp > now() - interval {rng['interval']} "
            "GROUP BY page "
            "ORDER BY views DESC "
            f"LIMIT {limit}"
        )
        rows = self._query_hogql(sql)
        return [
            PageViewRow(page=r[0] or "(unknown)", views=r[1], unique_visitors=r[2])
            for r in rows
        ]

    def get_sources(self, last: str = "7d", limit: int = 20) -> list[TrafficSource]:
        """Get traffic sources for the given period."""
        rng = parse_range(last)
        sql = (
            "SELECT coalesce("
            "nullIf(properties.$referring_domain, ''), "
            "nullIf(properties.utm_source, ''), "
            "'(direct)') as source, "
            "count() as cnt "
            "FROM events "
            "WHERE event = '$pageview' "
            f"AND timestamp > now() - interval {rng['interval']} "
            "GROUP BY source "
            "ORDER BY cnt DESC "
            f"LIMIT {limit}"
        )
        rows = self._query_hogql(sql)
        total = sum(r[1] for r in rows) if rows else 1
        return [
            TrafficSource(
                source=r[0],
                count=r[1],
                percentage=round(r[1] / total * 100, 1),
            )
            for r in rows
        ]

    def get_visitors(self, last: str = "7d") -> list[VisitorCount]:
        """Get daily unique visitor counts."""
        rng = parse_range(last)
        sql = (
            "SELECT toDate(timestamp) as day, "
            "count(distinct person_id) as visitors "
            "FROM events "
            "WHERE event = '$pageview' "
            f"AND timestamp > now() - interval {rng['interval']} "
            "GROUP BY day "
            "ORDER BY day"
        )
        rows = self._query_hogql(sql)
        return [
            VisitorCount(date=str(r[0]), visitors=r[1])
            for r in rows
        ]

    def get_pages(self, last: str = "7d", limit: int = 20) -> list[PageViewRow]:
        """Get page rankings by view count (alias for views with path-only URLs)."""
        rng = parse_range(last)
        sql = (
            "SELECT properties.$pathname as page, "
            "count() as views, "
            "count(distinct person_id) as unique_visitors "
            "FROM events "
            "WHERE event = '$pageview' "
            f"AND timestamp > now() - interval {rng['interval']} "
            "GROUP BY page "
            "ORDER BY views DESC "
            f"LIMIT {limit}"
        )
        rows = self._query_hogql(sql)
        return [
            PageViewRow(page=r[0] or "/", views=r[1], unique_visitors=r[2])
            for r in rows
        ]

    def get_funnel(
        self,
        last: str = "30d",
        steps: Optional[list[str]] = None,
    ) -> list[FunnelStep]:
        """Get funnel conversion data using PostHog FunnelsQuery.

        Args:
            last: Time range string (e.g. '30d')
            steps: List of event names for funnel steps. If None, returns empty.
        """
        if not steps:
            return []

        rng = parse_range(last)
        series = [
            {"kind": "EventsNode", "event": event_name}
            for event_name in steps
        ]
        query = {
            "kind": "FunnelsQuery",
            "series": series,
            "dateRange": {"date_from": rng["date_from"]},
            "funnelVizType": "steps",
        }
        data = self._query_structured(query)
        results = data.get("results", [])

        funnel_steps = []
        first_count = None
        for i, step_data in enumerate(results):
            # PostHog returns nested arrays; each step is a list with one dict
            if isinstance(step_data, list) and step_data:
                step_info = step_data[0]
            elif isinstance(step_data, dict):
                step_info = step_data
            else:
                continue

            count = step_info.get("count", 0)
            if first_count is None:
                first_count = count

            prev_count = results[i - 1][0].get("count", count) if i > 0 and isinstance(results[i - 1], list) else (
                results[i - 1].get("count", count) if i > 0 and isinstance(results[i - 1], dict) else count
            )
            drop = prev_count - count
            conv_rate = round(count / first_count * 100, 1) if first_count else 0.0
            drop_rate = round(drop / prev_count * 100, 1) if prev_count else 0.0

            funnel_steps.append(FunnelStep(
                step=f"Step {i + 1}",
                event=steps[i],
                count=count,
                conversion_rate=conv_rate,
                drop_off=drop,
                drop_off_rate=drop_rate,
            ))

        return funnel_steps

    def get_events(
        self,
        last: str = "7d",
        event_name: Optional[str] = None,
        limit: int = 50,
    ) -> list[EventRecord]:
        """Get recent events, optionally filtered by event name."""
        rng = parse_range(last)
        where = (
            f"WHERE timestamp > now() - interval {rng['interval']}"
        )
        if event_name:
            where += f" AND event = '{event_name}'"

        sql = (
            "SELECT timestamp, event, "
            "coalesce(person.properties.email, toString(person_id)) as person, "
            "coalesce(properties.$current_url, '') as url "
            f"FROM events {where} "
            "ORDER BY timestamp DESC "
            f"LIMIT {limit}"
        )
        rows = self._query_hogql(sql)
        return [
            EventRecord(
                timestamp=str(r[0]),
                event=r[1],
                person=r[2] or "",
                url=r[3] or "",
            )
            for r in rows
        ]

    def get_event_counts(self, last: str = "7d", limit: int = 30) -> list[EventCount]:
        """Get event name counts for the period."""
        rng = parse_range(last)
        sql = (
            "SELECT event, count() as cnt "
            "FROM events "
            f"WHERE timestamp > now() - interval {rng['interval']} "
            "GROUP BY event "
            "ORDER BY cnt DESC "
            f"LIMIT {limit}"
        )
        rows = self._query_hogql(sql)
        return [EventCount(event=r[0], count=r[1]) for r in rows]

    def get_recordings(self, last: str = "7d", limit: int = 20) -> list[Recording]:
        """Get session recording summaries via REST API."""
        rng = parse_range(last)
        params = {
            "date_from": rng["date_from"],
            "limit": limit,
        }
        data = self._rest_get("session_recordings", params=params)
        results = data.get("results", [])
        recordings = []
        for rec in results:
            recordings.append(Recording(
                id=rec.get("id", ""),
                start_time=rec.get("start_time", ""),
                duration_seconds=rec.get("recording_duration", 0),
                pages_visited=rec.get("distinct_id_count", 0),
                click_count=rec.get("click_count", 0),
                person=rec.get("person", {}).get("name", "")
                       if rec.get("person") else "",
            ))
        return recordings

    def get_recording(self, recording_id: str) -> list[RecordingEvent]:
        """Get events within a specific session recording."""
        data = self._rest_get(f"session_recordings/{recording_id}")
        snapshots = data.get("result", {}).get("snapshots", [])
        events = []
        for snap in snapshots:
            if snap.get("type") == "custom" or snap.get("data", {}).get("tag"):
                events.append(RecordingEvent(
                    timestamp=snap.get("timestamp", ""),
                    event=snap.get("data", {}).get("tag", "snapshot"),
                    properties=snap.get("data", {}),
                ))
        return events

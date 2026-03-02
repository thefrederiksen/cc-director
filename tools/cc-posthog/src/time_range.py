"""Parse --last flag values into PostHog API formats."""

import re
from datetime import datetime, timedelta, timezone

VALID_UNITS = {"d": "day", "w": "week", "m": "month", "y": "year"}
VALID_RANGES = re.compile(r"^(\d+)([dwmy])$")


def parse_range(value: str) -> dict:
    """Parse a range string like '7d', '30d', '1y' into PostHog formats.

    Returns dict with:
        interval: HogQL interval string, e.g. "7 day"
        date_from: PostHog dateRange format, e.g. "-7d"
        start_iso: ISO 8601 start datetime string
    """
    match = VALID_RANGES.match(value.lower())
    if not match:
        valid = "1d, 7d, 14d, 30d, 90d, 1m, 3m, 6m, 1y"
        raise ValueError(f"Invalid range '{value}'. Examples: {valid}")

    amount = int(match.group(1))
    unit = match.group(2)
    unit_name = VALID_UNITS[unit]

    # HogQL interval
    interval = f"{amount} {unit_name}"

    # PostHog dateRange date_from
    date_from = f"-{amount}{unit}"

    # Compute actual start datetime for display
    now = datetime.now(timezone.utc)
    if unit == "d":
        start = now - timedelta(days=amount)
    elif unit == "w":
        start = now - timedelta(weeks=amount)
    elif unit == "m":
        start = now - timedelta(days=amount * 30)
    elif unit == "y":
        start = now - timedelta(days=amount * 365)

    return {
        "interval": interval,
        "date_from": date_from,
        "start_iso": start.isoformat(),
    }


def validate_range(value: str) -> str:
    """Typer callback to validate --last flag. Returns the value if valid."""
    parse_range(value)
    return value

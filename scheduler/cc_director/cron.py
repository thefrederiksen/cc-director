"""Cron expression parsing and next run calculation."""

from datetime import datetime
from typing import Optional

from croniter import croniter


def validate_cron(expression: str) -> bool:
    """
    Validate a cron expression.

    Args:
        expression: Cron expression (5 fields: minute hour day month weekday)

    Returns:
        True if valid, False otherwise
    """
    try:
        croniter(expression)
        return True
    except (ValueError, KeyError):
        return False


def calculate_next_run(expression: str, from_time: Optional[datetime] = None) -> datetime:
    """
    Calculate the next run time for a cron expression.

    Args:
        expression: Cron expression (5 fields)
        from_time: Base time to calculate from (defaults to now)

    Returns:
        Next run datetime

    Raises:
        ValueError: If the cron expression is invalid
    """
    if from_time is None:
        from_time = datetime.now()

    try:
        cron = croniter(expression, from_time)
        return cron.get_next(datetime)
    except (ValueError, KeyError) as e:
        raise ValueError(f"Invalid cron expression '{expression}': {e}") from e


def get_previous_run(expression: str, from_time: Optional[datetime] = None) -> datetime:
    """
    Calculate the previous run time for a cron expression.

    Args:
        expression: Cron expression (5 fields)
        from_time: Base time to calculate from (defaults to now)

    Returns:
        Previous run datetime
    """
    if from_time is None:
        from_time = datetime.now()

    cron = croniter(expression, from_time)
    return cron.get_prev(datetime)


def describe_cron(expression: str) -> str:
    """
    Get a human-readable description of when a cron job runs.

    Args:
        expression: Cron expression (5 fields)

    Returns:
        Human-readable description
    """
    parts = expression.split()
    if len(parts) != 5:
        return "Invalid expression"

    minute, hour, day, month, weekday = parts

    # Handle common patterns
    if expression == "* * * * *":
        return "Every minute"
    if minute.startswith("*/"):
        interval = minute[2:]
        return f"Every {interval} minutes"
    if hour == "*" and minute != "*":
        return f"Every hour at minute {minute}"
    if weekday == "1-5" and hour != "*" and minute != "*":
        return f"Weekdays at {hour}:{minute.zfill(2)}"
    if weekday == "*" and day == "*" and month == "*":
        return f"Daily at {hour}:{minute.zfill(2)}"

    return expression  # Fall back to raw expression

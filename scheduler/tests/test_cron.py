"""Tests for cron module."""

import pytest
from datetime import datetime

from cc_director.cron import validate_cron, calculate_next_run, describe_cron


class TestValidateCron:
    """Tests for cron expression validation."""

    def test_validate_valid_expression(self):
        """Valid cron expressions should return True."""
        assert validate_cron("* * * * *") is True
        assert validate_cron("0 7 * * 1-5") is True
        assert validate_cron("*/15 * * * *") is True
        assert validate_cron("0 0 1 * *") is True
        assert validate_cron("30 4 * * 0") is True

    def test_validate_invalid_expression(self):
        """Invalid cron expressions should return False."""
        assert validate_cron("") is False
        assert validate_cron("invalid") is False
        assert validate_cron("* * *") is False  # Too few fields
        assert validate_cron("60 * * * *") is False  # Invalid minute
        assert validate_cron("* 25 * * *") is False  # Invalid hour


class TestCalculateNextRun:
    """Tests for next run time calculation."""

    def test_every_minute_next_run(self):
        """Every minute cron should return next minute."""
        base_time = datetime(2026, 2, 20, 10, 30, 0)
        next_run = calculate_next_run("* * * * *", base_time)
        assert next_run == datetime(2026, 2, 20, 10, 31, 0)

    def test_specific_time_next_run(self):
        """Specific time cron should return correct next occurrence."""
        base_time = datetime(2026, 2, 20, 6, 0, 0)  # 6 AM
        next_run = calculate_next_run("0 7 * * *", base_time)  # 7 AM daily
        assert next_run == datetime(2026, 2, 20, 7, 0, 0)

    def test_next_day_rollover(self):
        """Time past today should roll over to tomorrow."""
        base_time = datetime(2026, 2, 20, 8, 0, 0)  # 8 AM
        next_run = calculate_next_run("0 7 * * *", base_time)  # 7 AM daily
        assert next_run == datetime(2026, 2, 21, 7, 0, 0)

    def test_weekday_filter(self):
        """Weekday cron should skip weekends."""
        # Friday
        base_time = datetime(2026, 2, 20, 8, 0, 0)
        next_run = calculate_next_run("0 7 * * 1-5", base_time)  # Weekdays at 7 AM
        # Should be Monday Feb 23
        assert next_run == datetime(2026, 2, 23, 7, 0, 0)

    def test_invalid_expression_raises(self):
        """Invalid cron expression should raise ValueError."""
        with pytest.raises(ValueError):
            calculate_next_run("invalid", datetime.now())


class TestDescribeCron:
    """Tests for human-readable cron descriptions."""

    def test_every_minute(self):
        """Every minute expression."""
        assert describe_cron("* * * * *") == "Every minute"

    def test_interval_minutes(self):
        """Minute interval expression."""
        assert describe_cron("*/15 * * * *") == "Every 15 minutes"

    def test_weekdays(self):
        """Weekday expression."""
        result = describe_cron("0 7 * * 1-5")
        assert "Weekdays" in result or "7:00" in result

    def test_invalid_expression(self):
        """Invalid expression returns itself or 'Invalid'."""
        result = describe_cron("invalid")
        assert "Invalid" in result or "invalid" in result

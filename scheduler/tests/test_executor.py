"""Tests for executor module."""

import pytest
import sys

from cc_director.executor import execute_job


class TestExecuteJob:
    """Tests for job execution."""

    def test_execute_simple_command(self):
        """Should execute a simple command and capture output."""
        if sys.platform == "win32":
            result = execute_job("echo hello")
        else:
            result = execute_job("echo hello")

        assert result.exit_code == 0
        assert "hello" in result.stdout.lower()
        assert result.timed_out is False
        assert result.duration_seconds >= 0

    def test_execute_failing_command(self):
        """Should capture non-zero exit code."""
        if sys.platform == "win32":
            result = execute_job("cmd /c exit 1")
        else:
            result = execute_job("exit 1")

        assert result.exit_code == 1
        assert result.timed_out is False

    def test_execute_with_stderr(self):
        """Should capture stderr output."""
        if sys.platform == "win32":
            result = execute_job("cmd /c echo error 1>&2")
        else:
            result = execute_job("echo error >&2")

        assert "error" in result.stderr.lower() or "error" in result.stdout.lower()

    def test_execute_timeout(self):
        """Should timeout and kill long-running process."""
        if sys.platform == "win32":
            cmd = "ping -n 10 127.0.0.1"
        else:
            cmd = "sleep 10"

        result = execute_job(cmd, timeout_seconds=1)

        assert result.timed_out is True
        assert result.exit_code is None
        assert result.duration_seconds >= 1

    def test_execute_command_not_found(self):
        """Should handle command not found."""
        result = execute_job("nonexistent_command_xyz_12345")

        assert result.exit_code in [1, 127]  # Command not found codes
        assert result.timed_out is False

    def test_execute_with_working_dir(self, tmp_path):
        """Should execute command in specified working directory."""
        if sys.platform == "win32":
            result = execute_job("cd", working_dir=str(tmp_path))
        else:
            result = execute_job("pwd", working_dir=str(tmp_path))

        assert result.exit_code == 0
        # Output should contain the temp path
        assert str(tmp_path).lower().replace("\\", "/") in result.stdout.lower().replace("\\", "/") or \
               tmp_path.name.lower() in result.stdout.lower()

    def test_execute_timestamps(self):
        """Should record start and end timestamps."""
        result = execute_job("echo test")

        assert result.started_at is not None
        assert result.ended_at is not None
        assert result.ended_at >= result.started_at

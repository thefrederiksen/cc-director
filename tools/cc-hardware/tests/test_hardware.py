"""Tests for cc-hardware hardware data collection."""

import pytest
from pathlib import Path

import sys
sys.path.insert(0, str(Path(__file__).parent.parent))

from src.hardware import (
    get_cpu_info,
    get_ram_info,
    get_disk_info,
    get_os_info,
    get_network_info,
    get_battery_info,
    get_gpu_info,
)


class TestCPUInfo:
    """Tests for CPU information."""

    def test_returns_dict(self):
        """CPU info returns a dictionary."""
        info = get_cpu_info()
        assert isinstance(info, dict)

    def test_has_model(self):
        """CPU info includes model name."""
        info = get_cpu_info()
        assert "model" in info

    def test_has_cores(self):
        """CPU info includes core counts."""
        info = get_cpu_info()
        assert "physical_cores" in info
        assert "logical_cores" in info
        assert info["physical_cores"] > 0
        assert info["logical_cores"] > 0

    def test_has_usage(self):
        """CPU info includes usage percentage."""
        info = get_cpu_info()
        assert "usage_percent" in info
        assert 0 <= info["usage_percent"] <= 100


class TestRAMInfo:
    """Tests for RAM information."""

    def test_returns_dict(self):
        """RAM info returns a dictionary."""
        info = get_ram_info()
        assert isinstance(info, dict)

    def test_has_total(self):
        """RAM info includes total memory."""
        info = get_ram_info()
        assert "total_gb" in info
        assert info["total_gb"] > 0

    def test_has_used_and_available(self):
        """RAM info includes used and available."""
        info = get_ram_info()
        assert "used_gb" in info
        assert "available_gb" in info

    def test_used_plus_available_near_total(self):
        """Used + available should be close to total."""
        info = get_ram_info()
        total = info["total_gb"]
        used = info["used_gb"]
        available = info["available_gb"]
        # Allow some tolerance for OS overhead
        assert abs((used + available) - total) < 2


class TestDiskInfo:
    """Tests for disk information."""

    def test_returns_list(self):
        """Disk info returns a list."""
        info = get_disk_info()
        assert isinstance(info, list)
        assert len(info) > 0

    def test_each_disk_has_fields(self):
        """Each disk entry has required fields."""
        info = get_disk_info()
        for disk in info:
            assert "device" in disk
            assert "total_gb" in disk
            assert "used_gb" in disk
            assert "free_gb" in disk


class TestOSInfo:
    """Tests for OS information."""

    def test_returns_dict(self):
        """OS info returns a dictionary."""
        info = get_os_info()
        assert isinstance(info, dict)

    def test_has_system(self):
        """OS info includes system name."""
        info = get_os_info()
        assert "system" in info
        assert info["system"] == "Windows"

    def test_has_hostname(self):
        """OS info includes hostname."""
        info = get_os_info()
        assert "hostname" in info
        assert len(info["hostname"]) > 0


class TestNetworkInfo:
    """Tests for network information."""

    def test_returns_list(self):
        """Network info returns a list."""
        info = get_network_info()
        assert isinstance(info, list)

    def test_has_interface_names(self):
        """Network entries have interface names."""
        info = get_network_info()
        if len(info) > 0:
            assert "name" in info[0]


class TestGPUInfo:
    """Tests for GPU information."""

    def test_returns_list(self):
        """GPU info returns a list (possibly empty)."""
        info = get_gpu_info()
        assert isinstance(info, list)

    def test_nvidia_gpu_has_fields(self):
        """If NVIDIA GPU present, entries have expected fields."""
        info = get_gpu_info()
        if len(info) > 0:
            gpu = info[0]
            assert "name" in gpu
            assert "total_mb" in gpu or "memory_total" in gpu


class TestBatteryInfo:
    """Tests for battery information."""

    def test_returns_dict_or_none(self):
        """Battery info returns dict or None (desktop has no battery)."""
        info = get_battery_info()
        assert info is None or isinstance(info, dict)

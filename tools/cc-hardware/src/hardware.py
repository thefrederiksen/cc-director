"""Hardware query functions."""

import platform
from typing import Any

import psutil


def get_ram_info() -> dict[str, Any]:
    """Get RAM information.

    Returns:
        dict with total, used, available (in GB) and percent used.
    """
    mem = psutil.virtual_memory()
    return {
        "total_gb": round(mem.total / (1024**3), 2),
        "used_gb": round(mem.used / (1024**3), 2),
        "available_gb": round(mem.available / (1024**3), 2),
        "percent": mem.percent,
    }


def get_cpu_info() -> dict[str, Any]:
    """Get CPU information.

    Returns:
        dict with model, physical cores, logical cores, and usage percent.
    """
    # Get CPU model name from platform
    cpu_model = platform.processor() or "Unknown"

    return {
        "model": cpu_model,
        "physical_cores": psutil.cpu_count(logical=False) or 0,
        "logical_cores": psutil.cpu_count(logical=True) or 0,
        "usage_percent": psutil.cpu_percent(interval=0.5),
    }


def get_gpu_info() -> list[dict[str, Any]]:
    """Get GPU information (NVIDIA only).

    Returns:
        list of GPU dicts with name, total/used/free memory.
        Empty list if no NVIDIA GPU or GPUtil not available.
    """
    try:
        import GPUtil
        gpus = GPUtil.getGPUs()
        return [
            {
                "id": gpu.id,
                "name": gpu.name,
                "total_mb": round(gpu.memoryTotal, 0),
                "used_mb": round(gpu.memoryUsed, 0),
                "free_mb": round(gpu.memoryFree, 0),
                "load_percent": round(gpu.load * 100, 1),
                "temperature_c": gpu.temperature,
            }
            for gpu in gpus
        ]
    except ImportError:
        return []
    except (FileNotFoundError, OSError):
        # nvidia-smi not found or not accessible
        return []


def get_disk_info() -> list[dict[str, Any]]:
    """Get disk information for all mounted drives.

    Returns:
        list of drive dicts with device, mountpoint, total/used/free (GB), percent.
    """
    disks = []
    for partition in psutil.disk_partitions():
        try:
            usage = psutil.disk_usage(partition.mountpoint)
            disks.append({
                "device": partition.device,
                "mountpoint": partition.mountpoint,
                "fstype": partition.fstype,
                "total_gb": round(usage.total / (1024**3), 2),
                "used_gb": round(usage.used / (1024**3), 2),
                "free_gb": round(usage.free / (1024**3), 2),
                "percent": usage.percent,
            })
        except (PermissionError, OSError):
            # Skip drives we can't access
            continue
    return disks


def get_os_info() -> dict[str, Any]:
    """Get operating system information.

    Returns:
        dict with system name, version, release, architecture.
    """
    return {
        "system": platform.system(),
        "release": platform.release(),
        "version": platform.version(),
        "architecture": platform.machine(),
        "hostname": platform.node(),
    }


def get_network_info() -> list[dict[str, Any]]:
    """Get network interface information.

    Returns:
        list of interface dicts with name, addresses.
    """
    interfaces = []
    addrs = psutil.net_if_addrs()

    for iface_name, iface_addrs in addrs.items():
        addresses = []
        for addr in iface_addrs:
            if addr.family.name == "AF_INET":
                addresses.append({"type": "IPv4", "address": addr.address})
            elif addr.family.name == "AF_INET6":
                addresses.append({"type": "IPv6", "address": addr.address})

        if addresses:
            interfaces.append({
                "name": iface_name,
                "addresses": addresses,
            })

    return interfaces


def get_battery_info() -> dict[str, Any] | None:
    """Get battery information.

    Returns:
        dict with percent, plugged in status, time remaining.
        None if no battery present.
    """
    battery = psutil.sensors_battery()
    if battery is None:
        return None

    # Time remaining in seconds, -1 or None if unknown/charging
    time_left = battery.secsleft
    if time_left == psutil.POWER_TIME_UNLIMITED or time_left == psutil.POWER_TIME_UNKNOWN:
        time_remaining = None
    elif time_left and time_left > 0:
        hours = time_left // 3600
        minutes = (time_left % 3600) // 60
        time_remaining = f"{hours}h {minutes}m"
    else:
        time_remaining = None

    return {
        "percent": battery.percent,
        "plugged_in": battery.power_plugged,
        "time_remaining": time_remaining,
    }


def get_all_info() -> dict[str, Any]:
    """Get all hardware information.

    Returns:
        dict with all hardware categories.
    """
    return {
        "os": get_os_info(),
        "cpu": get_cpu_info(),
        "ram": get_ram_info(),
        "gpu": get_gpu_info(),
        "disk": get_disk_info(),
        "network": get_network_info(),
        "battery": get_battery_info(),
    }

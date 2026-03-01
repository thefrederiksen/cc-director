# cc-hardware

System hardware information CLI tool.

## Features

- RAM: total, used, available memory
- CPU: model, cores, usage percentage
- GPU: NVIDIA GPU info (memory, load, temperature)
- Disk: per-drive storage info
- OS: system name, version, architecture
- Network: interface names and IP addresses
- Battery: charge level, power status

## Installation

Built executable is installed to `%LOCALAPPDATA%\cc-tools\bin\cc-hardware.exe`

## Usage

```bash
# Show all hardware info
cc-hardware

# Individual components
cc-hardware ram
cc-hardware cpu
cc-hardware gpu
cc-hardware disk
cc-hardware os
cc-hardware network
cc-hardware battery

# JSON output (for scripting)
cc-hardware --json
cc-hardware cpu --json

# Version
cc-hardware --version
```

## Requirements

- Python 3.11+
- NVIDIA drivers for GPU info (optional)

## Dependencies

- psutil - CPU, RAM, Disk, Network, Battery
- GPUtil - NVIDIA GPU info
- typer - CLI framework
- rich - Formatted console output

## Building

```powershell
.\build.ps1
```

Output: `dist\cc-hardware.exe`

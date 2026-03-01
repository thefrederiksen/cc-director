"""Configuration loading for TriSight skills.

Resolution order for cc-click path:
  1. CC_CLICK_PATH environment variable
  2. Default path in cc-tools
"""
import os

# Updated default paths (exe names use dashes per CLI convention)
_DEFAULT_CC_CLICK = os.path.normpath(
    os.path.join(os.path.dirname(__file__), "..", "..", "..", "cc-click", "src", "CcClick", "bin", "Release", "net10.0-windows", "cc-click.exe")
)

_DEFAULT_TRISIGHT_CLI = os.path.normpath(
    os.path.join(os.path.dirname(__file__), "..", "..", "TrisightCli", "bin", "Release", "net10.0-windows10.0.17763.0", "cc-trisight.exe")
)


def get_cc-click_path() -> str:
    """Resolve cc-click.exe path."""
    env = os.environ.get("CC_CLICK_PATH")
    if env:
        return env
    return _DEFAULT_CC_CLICK


def get_trisight_cli_path() -> str:
    """Resolve cc-trisight.exe path."""
    env = os.environ.get("CC_TRISIGHT_CLI_PATH")
    if env:
        return env
    return _DEFAULT_TRISIGHT_CLI


def get_python_path() -> str:
    """Resolve Python interpreter path."""
    env = os.environ.get("CC_PYTHON_PATH")
    if env:
        return env
    return "python"

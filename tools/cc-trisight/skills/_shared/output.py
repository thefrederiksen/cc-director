"""Structured JSON output for trisight skills.

All skills output JSON to stdout for consistent parsing by the orchestrator.
"""
import json
import sys
from typing import Any, Dict


def success(skill: str, data: Dict[str, Any]) -> None:
    """Output success result and exit.

    Args:
        skill: Name of the skill
        data: Result data dictionary
    """
    result = {
        "status": "ok",
        "skill": skill,
        "data": data
    }
    print(json.dumps(result))
    sys.exit(0)


def error(skill: str, message: str) -> None:
    """Output error result and exit.

    Args:
        skill: Name of the skill
        message: Error message
    """
    result = {
        "status": "error",
        "skill": skill,
        "error": message
    }
    print(json.dumps(result))
    sys.exit(1)

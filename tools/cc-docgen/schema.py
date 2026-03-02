"""YAML schema validation for architecture_manifest.yaml.

Validates required fields and structure before diagram generation.
"""

from pathlib import Path
from typing import Any

import yaml


class SchemaError(Exception):
    """Raised when manifest does not match expected schema."""

    pass


REQUIRED_PROJECT_FIELDS = ["name", "description"]
REQUIRED_CONTEXT_FIELDS = ["system"]
REQUIRED_SYSTEM_FIELDS = ["name", "description", "technology"]
REQUIRED_ACTOR_FIELDS = ["id", "name", "type"]
REQUIRED_CONTAINER_FIELDS = ["id", "name", "technology"]


def load_manifest(path: Path) -> dict[str, Any]:
    """Load and validate architecture_manifest.yaml.

    Args:
        path: Path to the manifest file.

    Returns:
        Parsed and validated manifest dictionary.

    Raises:
        FileNotFoundError: If manifest file does not exist.
        SchemaError: If manifest structure is invalid.
    """
    if not path.exists():
        raise FileNotFoundError(f"Manifest file not found: {path}")

    with open(path, "r", encoding="utf-8") as f:
        try:
            manifest = yaml.safe_load(f)
        except yaml.YAMLError as e:
            raise SchemaError(f"Invalid YAML syntax: {e}") from e

    if manifest is None:
        raise SchemaError("Manifest file is empty")

    _validate_structure(manifest)
    return manifest


def _validate_structure(manifest: dict[str, Any]) -> None:
    """Validate manifest structure against schema.

    Args:
        manifest: Parsed YAML dictionary.

    Raises:
        SchemaError: If required fields are missing.
    """
    # Check project section
    if "project" not in manifest:
        raise SchemaError("Missing required section: project")

    project = manifest["project"]
    for field in REQUIRED_PROJECT_FIELDS:
        if field not in project:
            raise SchemaError(f"Missing required field: project.{field}")

    # Check context section
    if "context" not in manifest:
        raise SchemaError("Missing required section: context")

    context = manifest["context"]
    if "system" not in context:
        raise SchemaError("Missing required field: context.system")

    system = context["system"]
    for field in REQUIRED_SYSTEM_FIELDS:
        if field not in system:
            raise SchemaError(f"Missing required field: context.system.{field}")

    # Validate actors if present
    if "actors" in context:
        for i, actor in enumerate(context["actors"]):
            for field in REQUIRED_ACTOR_FIELDS:
                if field not in actor:
                    raise SchemaError(f"Missing required field: context.actors[{i}].{field}")
            if actor["type"] not in ("person", "external_system"):
                raise SchemaError(
                    f"Invalid actor type: {actor['type']}. Must be 'person' or 'external_system'"
                )

    # Validate containers if present
    if "containers" in manifest:
        for i, container in enumerate(manifest["containers"]):
            for field in REQUIRED_CONTAINER_FIELDS:
                if field not in container:
                    raise SchemaError(f"Missing required field: containers[{i}].{field}")


def validate_manifest(manifest: dict[str, Any]) -> bool:
    """Validate manifest structure against schema.

    Args:
        manifest: Parsed YAML dictionary.

    Returns:
        True if valid.

    Raises:
        SchemaError: If required fields are missing.
    """
    _validate_structure(manifest)
    return True


def get_project_info(manifest: dict[str, Any]) -> dict[str, str]:
    """Extract project information from manifest.

    Args:
        manifest: Validated manifest dictionary.

    Returns:
        Dictionary with name, description, and version.
    """
    project = manifest["project"]
    return {
        "name": project["name"],
        "description": project.get("description", ""),
        "version": project.get("version", "1.0.0"),
    }


def get_actors(manifest: dict[str, Any]) -> list[dict[str, Any]]:
    """Extract actors from manifest context.

    Args:
        manifest: Validated manifest dictionary.

    Returns:
        List of actor dictionaries.
    """
    return manifest.get("context", {}).get("actors", [])


def get_containers(manifest: dict[str, Any]) -> list[dict[str, Any]]:
    """Extract containers from manifest.

    Args:
        manifest: Validated manifest dictionary.

    Returns:
        List of container dictionaries.
    """
    return manifest.get("containers", [])


def get_system(manifest: dict[str, Any]) -> dict[str, str]:
    """Extract main system information from manifest.

    Args:
        manifest: Validated manifest dictionary.

    Returns:
        Dictionary with system name, description, and technology.
    """
    return manifest["context"]["system"]

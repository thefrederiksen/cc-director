"""Settings service for cc-director configuration management."""

import sys
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

# Import cc_shared config
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared.config import CCDirectorConfig, get_config_path


def load_config() -> CCDirectorConfig:
    """Load a fresh config from disk."""
    return CCDirectorConfig().load()


def get_all_settings(config: CCDirectorConfig) -> Dict[str, Any]:
    """Get all settings as a flat dictionary.

    Returns:
        Dict with dotted keys like 'screenshots.source_directory'.
    """
    result = {}
    full = config.to_dict()
    _flatten(full, "", result)
    return result


def _flatten(data: Any, prefix: str, result: Dict[str, Any]) -> None:
    """Recursively flatten a nested dict into dotted keys."""
    if isinstance(data, dict):
        for key, value in data.items():
            new_prefix = f"{prefix}.{key}" if prefix else key
            _flatten(value, new_prefix, result)
    elif isinstance(data, list):
        result[prefix] = data
    else:
        result[prefix] = data


def get_section(config: CCDirectorConfig, section: str) -> Optional[Dict[str, Any]]:
    """Get a config section by name.

    Args:
        config: The loaded config.
        section: Section name (e.g. 'screenshots', 'vault', 'llm').

    Returns:
        Dict for that section, or None if not found.
    """
    full = config.to_dict()
    return full.get(section)


def get_value(config: CCDirectorConfig, key: str) -> Tuple[bool, Any]:
    """Get a specific setting value by dotted key.

    Args:
        config: The loaded config.
        key: Dotted key like 'screenshots.source_directory'.

    Returns:
        Tuple of (found, value). found=False if key doesn't exist.
    """
    all_settings = get_all_settings(config)
    if key in all_settings:
        return True, all_settings[key]
    return False, None


def set_value(config: CCDirectorConfig, key: str, value: str) -> bool:
    """Set a specific setting value by dotted key.

    Args:
        config: The loaded config.
        key: Dotted key like 'screenshots.source_directory'.
        value: The string value to set.

    Returns:
        True if the key was found and set, False otherwise.
    """
    parts = key.split(".")
    if len(parts) < 2:
        return False

    # Navigate to the right section/attribute
    section_name = parts[0]
    section = getattr(config, section_name, None)
    if section is None:
        return False

    # For nested keys deeper than 2 levels (e.g. llm.providers.openai.default_model)
    obj = section
    for part in parts[1:-1]:
        obj = getattr(obj, part, None)
        if obj is None:
            return False

    attr_name = parts[-1]
    if not hasattr(obj, attr_name):
        return False

    # Type coercion based on the existing value's type
    current = getattr(obj, attr_name)
    if isinstance(current, bool):
        value = value.lower() in ("true", "1", "yes")
    elif isinstance(current, int):
        value = int(value)
    elif isinstance(current, float):
        value = float(value)

    setattr(obj, attr_name, value)
    config.save()
    return True


def list_keys(config: CCDirectorConfig) -> List[str]:
    """List all available setting keys.

    Returns:
        Sorted list of dotted key strings.
    """
    return sorted(get_all_settings(config).keys())


def get_section_names(config: CCDirectorConfig) -> List[str]:
    """Get the top-level section names.

    Returns:
        List of section names like ['llm', 'photos', 'vault', ...].
    """
    return sorted(config.to_dict().keys())

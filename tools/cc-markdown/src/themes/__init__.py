"""Theme management for cc-markdown."""

import sys
from pathlib import Path

# Available themes with descriptions
THEMES = {
    "boardroom": "Corporate, executive style with serif fonts",
    "terminal": "Technical, monospace with dark-friendly colors",
    "paper": "Minimal, clean, elegant",
    "spark": "Creative, colorful, modern",
    "thesis": "Academic, scholarly with proper citations style",
    "obsidian": "Dark theme with subtle highlights",
    "blueprint": "Technical documentation style",
}


def _get_theme_dir() -> Path:
    """Get the theme directory, handling both package and frozen modes."""
    if getattr(sys, 'frozen', False):
        # Running as compiled executable
        return Path(sys._MEIPASS) / "src" / "themes"
    else:
        # Running as script/package
        return Path(__file__).parent


# Theme directory
THEME_DIR = _get_theme_dir()


def get_theme_css(theme_name: str) -> str:
    """
    Load CSS for a theme.

    Args:
        theme_name: Name of the theme

    Returns:
        CSS content as string

    Raises:
        ValueError: If theme doesn't exist
    """
    if theme_name not in THEMES:
        raise ValueError(f"Unknown theme: {theme_name}. Available: {', '.join(THEMES.keys())}")

    # Get current theme directory (may change between calls in frozen mode)
    theme_dir = _get_theme_dir()

    # Load base CSS
    base_path = theme_dir / "base.css"
    base_css = base_path.read_text(encoding="utf-8") if base_path.exists() else ""

    # Load theme CSS
    theme_path = theme_dir / f"{theme_name}.css"
    if not theme_path.exists():
        raise ValueError(f"Theme file not found: {theme_path}")

    theme_css = theme_path.read_text(encoding="utf-8")

    return f"{base_css}\n\n{theme_css}"


def list_themes() -> dict[str, str]:
    """Return dictionary of theme names and descriptions."""
    return THEMES.copy()

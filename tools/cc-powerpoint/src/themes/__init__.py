"""Theme management for cc-powerpoint presentations.

Imports canonical color/font values from cc_shared.themes and builds
PowerPoint-specific theme objects with format-specific extensions
(font sizes per element type).
"""

from dataclasses import dataclass

# Import canonical themes - handle both package and frozen modes
try:
    from cc_shared.themes import get_theme as get_canonical_theme
except ImportError:
    get_canonical_theme = None


@dataclass(frozen=True)
class ThemeColors:
    """Color scheme for a presentation theme."""
    primary: str
    accent: str
    text: str
    heading: str
    background: str
    code_bg: str


@dataclass(frozen=True)
class ThemeFonts:
    """Font configuration for a presentation theme."""
    heading: str
    body: str
    code: str


@dataclass(frozen=True)
class PresentationTheme:
    """Complete theme definition for PowerPoint generation."""
    name: str
    description: str
    colors: ThemeColors
    fonts: ThemeFonts
    title_font_size: int
    subtitle_font_size: int
    heading_font_size: int
    body_font_size: int
    code_font_size: int


# -- PowerPoint-specific font sizes per theme --

_FONT_SIZES: dict[str, dict[str, int]] = {
    "boardroom": {"title": 40, "subtitle": 24, "heading": 32, "body": 18, "code": 14},
    "paper": {"title": 40, "subtitle": 24, "heading": 32, "body": 18, "code": 14},
    "terminal": {"title": 40, "subtitle": 24, "heading": 32, "body": 18, "code": 14},
    "spark": {"title": 44, "subtitle": 24, "heading": 34, "body": 18, "code": 14},
    "thesis": {"title": 40, "subtitle": 22, "heading": 30, "body": 18, "code": 14},
    "obsidian": {"title": 40, "subtitle": 24, "heading": 32, "body": 18, "code": 14},
    "blueprint": {"title": 40, "subtitle": 24, "heading": 32, "body": 18, "code": 14},
}


def _build_pptx_theme(name: str) -> PresentationTheme:
    """Build a PresentationTheme from canonical theme values + PPTX-specific extensions."""
    ct = get_canonical_theme(name)
    sizes = _FONT_SIZES[name]
    return PresentationTheme(
        name=ct.name,
        description=ct.description,
        colors=ThemeColors(
            primary=ct.colors.primary,
            accent=ct.colors.accent,
            text=ct.colors.text,
            heading=ct.colors.heading,
            background=ct.colors.background,
            code_bg=ct.colors.code_bg,
        ),
        fonts=ThemeFonts(
            heading=ct.fonts.heading,
            body=ct.fonts.body,
            code=ct.fonts.code,
        ),
        title_font_size=sizes["title"],
        subtitle_font_size=sizes["subtitle"],
        heading_font_size=sizes["heading"],
        body_font_size=sizes["body"],
        code_font_size=sizes["code"],
    )


# -- Build themes from canonical source or use hardcoded fallback --

if get_canonical_theme is not None:
    _THEMES: dict[str, PresentationTheme] = {
        name: _build_pptx_theme(name)
        for name in ["boardroom", "paper", "terminal", "spark", "thesis", "obsidian", "blueprint"]
    }
else:
    # Fallback for environments where cc_shared is not available
    _THEMES = {
        "boardroom": PresentationTheme(
            name="boardroom", description="Corporate, executive style with serif fonts",
            colors=ThemeColors(primary="#1A365D", accent="#D69E2E", text="#333333",
                               heading="#1A365D", background="#FFFFFF", code_bg="#F5F5F0"),
            fonts=ThemeFonts(heading="Palatino Linotype", body="Georgia", code="Consolas"),
            title_font_size=40, subtitle_font_size=24, heading_font_size=32, body_font_size=18, code_font_size=14,
        ),
        "paper": PresentationTheme(
            name="paper", description="Minimal, clean, elegant",
            colors=ThemeColors(primary="#1A1A1A", accent="#0066CC", text="#333333",
                               heading="#1A1A1A", background="#FFFFFF", code_bg="#F6F8FA"),
            fonts=ThemeFonts(heading="Segoe UI", body="Segoe UI", code="Consolas"),
            title_font_size=40, subtitle_font_size=24, heading_font_size=32, body_font_size=18, code_font_size=14,
        ),
        "terminal": PresentationTheme(
            name="terminal", description="Technical, monospace with dark-friendly colors",
            colors=ThemeColors(primary="#22C55E", accent="#60A5FA", text="#E0E0E0",
                               heading="#22C55E", background="#0F0F0F", code_bg="#1A1A1A"),
            fonts=ThemeFonts(heading="Consolas", body="Consolas", code="Consolas"),
            title_font_size=40, subtitle_font_size=24, heading_font_size=32, body_font_size=18, code_font_size=14,
        ),
        "spark": PresentationTheme(
            name="spark", description="Creative, colorful, modern",
            colors=ThemeColors(primary="#8B5CF6", accent="#EC4899", text="#333333",
                               heading="#8B5CF6", background="#FFFFFF", code_bg="#FAF5FF"),
            fonts=ThemeFonts(heading="Segoe UI", body="Segoe UI", code="Consolas"),
            title_font_size=44, subtitle_font_size=24, heading_font_size=34, body_font_size=18, code_font_size=14,
        ),
        "thesis": PresentationTheme(
            name="thesis", description="Academic, scholarly with proper citations style",
            colors=ThemeColors(primary="#000000", accent="#800000", text="#333333",
                               heading="#000000", background="#FFFFFF", code_bg="#F5F5F5"),
            fonts=ThemeFonts(heading="Times New Roman", body="Times New Roman", code="Consolas"),
            title_font_size=40, subtitle_font_size=22, heading_font_size=30, body_font_size=18, code_font_size=14,
        ),
        "obsidian": PresentationTheme(
            name="obsidian", description="Dark theme with subtle highlights",
            colors=ThemeColors(primary="#A855F7", accent="#C084FC", text="#D4D4D4",
                               heading="#A855F7", background="#0F0F0F", code_bg="#1E1E1E"),
            fonts=ThemeFonts(heading="Segoe UI", body="Segoe UI", code="Consolas"),
            title_font_size=40, subtitle_font_size=24, heading_font_size=32, body_font_size=18, code_font_size=14,
        ),
        "blueprint": PresentationTheme(
            name="blueprint", description="Technical documentation style",
            colors=ThemeColors(primary="#3B82F6", accent="#1E3A5F", text="#333333",
                               heading="#3B82F6", background="#FFFFFF", code_bg="#EFF6FF"),
            fonts=ThemeFonts(heading="Segoe UI", body="Segoe UI", code="Consolas"),
            title_font_size=40, subtitle_font_size=24, heading_font_size=32, body_font_size=18, code_font_size=14,
        ),
    }


# -- Theme Registry --

THEMES: dict[str, str] = {t.name: t.description for t in _THEMES.values()}


def get_theme(name: str) -> PresentationTheme:
    """Get a theme by name.

    Args:
        name: Theme name (boardroom, paper, terminal, spark, thesis, obsidian, blueprint)

    Returns:
        PresentationTheme instance

    Raises:
        ValueError: If theme name is not recognized
    """
    if name not in _THEMES:
        available = ", ".join(_THEMES.keys())
        raise ValueError(f"Unknown theme: {name}. Available: {available}")
    return _THEMES[name]


def list_themes() -> dict[str, str]:
    """Return dictionary of theme names and descriptions."""
    return THEMES.copy()

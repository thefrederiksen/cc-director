"""Theme management for cc-powerpoint presentations."""

from dataclasses import dataclass


@dataclass(frozen=True)
class ThemeColors:
    """Color scheme for a presentation theme."""
    primary: str        # Main heading/title color
    accent: str         # Accent color for highlights
    text: str           # Body text color
    heading: str        # Heading text color
    background: str     # Slide background color
    code_bg: str        # Code block background color


@dataclass(frozen=True)
class ThemeFonts:
    """Font configuration for a presentation theme."""
    heading: str        # Font for headings/titles
    body: str           # Font for body text
    code: str           # Font for code blocks


@dataclass(frozen=True)
class PresentationTheme:
    """Complete theme definition for PowerPoint generation."""
    name: str
    description: str
    colors: ThemeColors
    fonts: ThemeFonts
    title_font_size: int     # Points - for title slide main title
    subtitle_font_size: int  # Points - for title slide subtitle
    heading_font_size: int   # Points - for slide headings
    body_font_size: int      # Points - for body/bullet text
    code_font_size: int      # Points - for code blocks


# -- Theme Definitions --

BOARDROOM = PresentationTheme(
    name="boardroom",
    description="Corporate, executive style with serif fonts",
    colors=ThemeColors(
        primary="#1A365D",
        accent="#D69E2E",
        text="#333333",
        heading="#1A365D",
        background="#FFFFFF",
        code_bg="#F5F5F0",
    ),
    fonts=ThemeFonts(
        heading="Palatino Linotype",
        body="Georgia",
        code="Consolas",
    ),
    title_font_size=40,
    subtitle_font_size=24,
    heading_font_size=32,
    body_font_size=18,
    code_font_size=14,
)

PAPER = PresentationTheme(
    name="paper",
    description="Minimal, clean, elegant",
    colors=ThemeColors(
        primary="#1A1A1A",
        accent="#0066CC",
        text="#333333",
        heading="#1A1A1A",
        background="#FFFFFF",
        code_bg="#F6F8FA",
    ),
    fonts=ThemeFonts(
        heading="Segoe UI",
        body="Segoe UI",
        code="Consolas",
    ),
    title_font_size=40,
    subtitle_font_size=24,
    heading_font_size=32,
    body_font_size=18,
    code_font_size=14,
)

TERMINAL = PresentationTheme(
    name="terminal",
    description="Technical, monospace with dark-friendly colors",
    colors=ThemeColors(
        primary="#22C55E",
        accent="#60A5FA",
        text="#E0E0E0",
        heading="#22C55E",
        background="#0F0F0F",
        code_bg="#1A1A1A",
    ),
    fonts=ThemeFonts(
        heading="Consolas",
        body="Consolas",
        code="Consolas",
    ),
    title_font_size=40,
    subtitle_font_size=24,
    heading_font_size=32,
    body_font_size=18,
    code_font_size=14,
)

SPARK = PresentationTheme(
    name="spark",
    description="Creative, colorful, modern",
    colors=ThemeColors(
        primary="#8B5CF6",
        accent="#EC4899",
        text="#333333",
        heading="#8B5CF6",
        background="#FFFFFF",
        code_bg="#FAF5FF",
    ),
    fonts=ThemeFonts(
        heading="Segoe UI",
        body="Segoe UI",
        code="Consolas",
    ),
    title_font_size=44,
    subtitle_font_size=24,
    heading_font_size=34,
    body_font_size=18,
    code_font_size=14,
)

THESIS = PresentationTheme(
    name="thesis",
    description="Academic, scholarly with proper citations style",
    colors=ThemeColors(
        primary="#000000",
        accent="#800000",
        text="#333333",
        heading="#000000",
        background="#FFFFFF",
        code_bg="#F5F5F5",
    ),
    fonts=ThemeFonts(
        heading="Times New Roman",
        body="Times New Roman",
        code="Consolas",
    ),
    title_font_size=40,
    subtitle_font_size=22,
    heading_font_size=30,
    body_font_size=18,
    code_font_size=14,
)

OBSIDIAN = PresentationTheme(
    name="obsidian",
    description="Dark theme with subtle highlights",
    colors=ThemeColors(
        primary="#A855F7",
        accent="#C084FC",
        text="#D4D4D4",
        heading="#A855F7",
        background="#0F0F0F",
        code_bg="#1E1E1E",
    ),
    fonts=ThemeFonts(
        heading="Segoe UI",
        body="Segoe UI",
        code="Consolas",
    ),
    title_font_size=40,
    subtitle_font_size=24,
    heading_font_size=32,
    body_font_size=18,
    code_font_size=14,
)

BLUEPRINT = PresentationTheme(
    name="blueprint",
    description="Technical documentation style",
    colors=ThemeColors(
        primary="#3B82F6",
        accent="#1E3A5F",
        text="#333333",
        heading="#3B82F6",
        background="#FFFFFF",
        code_bg="#EFF6FF",
    ),
    fonts=ThemeFonts(
        heading="Segoe UI",
        body="Segoe UI",
        code="Consolas",
    ),
    title_font_size=40,
    subtitle_font_size=24,
    heading_font_size=32,
    body_font_size=18,
    code_font_size=14,
)

# -- Theme Registry --

_THEMES: dict[str, PresentationTheme] = {
    "boardroom": BOARDROOM,
    "paper": PAPER,
    "terminal": TERMINAL,
    "spark": SPARK,
    "thesis": THESIS,
    "obsidian": OBSIDIAN,
    "blueprint": BLUEPRINT,
}

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

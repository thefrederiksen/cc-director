"""Canonical theme definitions for all cc-director document tools.

Single source of truth for theme colors and fonts used by:
cc-pdf, cc-html, cc-word, cc-excel, cc-powerpoint.
"""

from dataclasses import dataclass


@dataclass(frozen=True)
class ThemeColors:
    """Color scheme shared across all document formats."""
    primary: str
    accent: str
    text: str
    heading: str
    background: str
    code_bg: str
    code_text: str
    border: str
    link: str
    blockquote_text: str
    blockquote_border: str
    blockquote_bg: str
    table_header_bg: str
    table_header_text: str
    alt_row_bg: str


@dataclass(frozen=True)
class ThemeFonts:
    """Font families shared across all document formats."""
    heading: str
    body: str
    code: str


@dataclass(frozen=True)
class CanonicalTheme:
    """Complete theme definition used across all document tools."""
    name: str
    description: str
    colors: ThemeColors
    fonts: ThemeFonts


# -- Theme Definitions --
# Color values sourced from cc-markdown CSS files (authoritative).

BOARDROOM = CanonicalTheme(
    name="boardroom",
    description="Corporate, executive style with serif fonts",
    colors=ThemeColors(
        primary="#1A365D",
        accent="#D69E2E",
        text="#2D3748",
        heading="#1A365D",
        background="#FFFFFF",
        code_bg="#EDF2F7",
        code_text="#1A365D",
        border="#CBD5E0",
        link="#2B6CB0",
        blockquote_text="#4A5568",
        blockquote_border="#D69E2E",
        blockquote_bg="#FFFFFF",
        table_header_bg="#1A365D",
        table_header_text="#FFFFFF",
        alt_row_bg="#F7FAFC",
    ),
    fonts=ThemeFonts(
        heading="Palatino Linotype",
        body="Georgia",
        code="Courier New",
    ),
)

PAPER = CanonicalTheme(
    name="paper",
    description="Minimal, clean, elegant",
    colors=ThemeColors(
        primary="#1A1A1A",
        accent="#0066CC",
        text="#1A1A1A",
        heading="#000000",
        background="#FFFFFF",
        code_bg="#F5F5F5",
        code_text="#333333",
        border="#E0E0E0",
        link="#0066CC",
        blockquote_text="#666666",
        blockquote_border="#E0E0E0",
        blockquote_bg="#FFFFFF",
        table_header_bg="#FAFAFA",
        table_header_text="#1A1A1A",
        alt_row_bg="#F9FAFB",
    ),
    fonts=ThemeFonts(
        heading="Segoe UI",
        body="Segoe UI",
        code="Consolas",
    ),
)

TERMINAL = CanonicalTheme(
    name="terminal",
    description="Technical, monospace with dark-friendly colors",
    colors=ThemeColors(
        primary="#22C55E",
        accent="#60A5FA",
        text="#E5E5E5",
        heading="#22C55E",
        background="#0F0F0F",
        code_bg="#0A0A0A",
        code_text="#22C55E",
        border="#404040",
        link="#60A5FA",
        blockquote_text="#A3A3A3",
        blockquote_border="#22C55E",
        blockquote_bg="#0F0F0F",
        table_header_bg="#1A1A1A",
        table_header_text="#22C55E",
        alt_row_bg="#1A1A1A",
    ),
    fonts=ThemeFonts(
        heading="Consolas",
        body="Consolas",
        code="Consolas",
    ),
)

SPARK = CanonicalTheme(
    name="spark",
    description="Creative, colorful, modern",
    colors=ThemeColors(
        primary="#8B5CF6",
        accent="#EC4899",
        text="#374151",
        heading="#1F2937",
        background="#FFFFFF",
        code_bg="#FAF5FF",
        code_text="#7C3AED",
        border="#E5E7EB",
        link="#8B5CF6",
        blockquote_text="#6B7280",
        blockquote_border="#8B5CF6",
        blockquote_bg="#FAF5FF",
        table_header_bg="#8B5CF6",
        table_header_text="#FFFFFF",
        alt_row_bg="#F5F3FF",
    ),
    fonts=ThemeFonts(
        heading="Segoe UI",
        body="Segoe UI",
        code="Consolas",
    ),
)

THESIS = CanonicalTheme(
    name="thesis",
    description="Academic, scholarly with proper citations style",
    colors=ThemeColors(
        primary="#000000",
        accent="#800000",
        text="#333333",
        heading="#000000",
        background="#FFFFFF",
        code_bg="#F8F8F8",
        code_text="#333333",
        border="#CCCCCC",
        link="#800000",
        blockquote_text="#555555",
        blockquote_border="#CCCCCC",
        blockquote_bg="#FFFFFF",
        table_header_bg="#F0F0F0",
        table_header_text="#333333",
        alt_row_bg="#F7FAFC",
    ),
    fonts=ThemeFonts(
        heading="Times New Roman",
        body="Times New Roman",
        code="Courier New",
    ),
)

OBSIDIAN = CanonicalTheme(
    name="obsidian",
    description="Dark theme with subtle highlights",
    colors=ThemeColors(
        primary="#A855F7",
        accent="#C084FC",
        text="#D4D4D4",
        heading="#E5E5E5",
        background="#0F0F0F",
        code_bg="#1E1E1E",
        code_text="#D4D4D4",
        border="#404040",
        link="#A855F7",
        blockquote_text="#A3A3A3",
        blockquote_border="#A855F7",
        blockquote_bg="rgba(168, 85, 247, 0.1)",
        table_header_bg="#262626",
        table_header_text="#A855F7",
        alt_row_bg="#1F2937",
    ),
    fonts=ThemeFonts(
        heading="Segoe UI",
        body="Segoe UI",
        code="Consolas",
    ),
)

BLUEPRINT = CanonicalTheme(
    name="blueprint",
    description="Technical documentation style",
    colors=ThemeColors(
        primary="#3B82F6",
        accent="#F59E0B",
        text="#374151",
        heading="#1E3A5F",
        background="#FFFFFF",
        code_bg="#F1F5F9",
        code_text="#0F172A",
        border="#CBD5E1",
        link="#3B82F6",
        blockquote_text="#64748B",
        blockquote_border="#3B82F6",
        blockquote_bg="#EFF6FF",
        table_header_bg="#1E3A5F",
        table_header_text="#FFFFFF",
        alt_row_bg="#EFF6FF",
    ),
    fonts=ThemeFonts(
        heading="Segoe UI",
        body="Segoe UI",
        code="Consolas",
    ),
)


# -- Theme Registry --

_THEMES: dict[str, CanonicalTheme] = {
    "boardroom": BOARDROOM,
    "paper": PAPER,
    "terminal": TERMINAL,
    "spark": SPARK,
    "thesis": THESIS,
    "obsidian": OBSIDIAN,
    "blueprint": BLUEPRINT,
}

THEMES: dict[str, str] = {t.name: t.description for t in _THEMES.values()}


def get_theme(name: str) -> CanonicalTheme:
    """Get a theme by name.

    Args:
        name: Theme name (boardroom, paper, terminal, spark, thesis, obsidian, blueprint)

    Returns:
        CanonicalTheme instance

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

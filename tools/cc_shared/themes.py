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
    primary_light: str
    shadow_color: str


@dataclass(frozen=True)
class ThemeFonts:
    """Font families shared across all document formats."""
    heading: str
    body: str
    code: str


@dataclass(frozen=True)
class ThemeStyle:
    """Structural style properties beyond colors and fonts."""
    heading_letter_spacing: str
    body_line_height: str
    border_radius: str
    code_border_radius: str
    shadow_sm: str
    shadow_md: str


@dataclass(frozen=True)
class CanonicalTheme:
    """Complete theme definition used across all document tools."""
    name: str
    description: str
    colors: ThemeColors
    fonts: ThemeFonts
    style: ThemeStyle


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
        primary_light="#2A4A7F",
        shadow_color="rgba(26, 54, 93, 0.08)",
    ),
    fonts=ThemeFonts(
        heading="Palatino Linotype",
        body="Georgia",
        code="Courier New",
    ),
    style=ThemeStyle(
        heading_letter_spacing="-0.02em",
        body_line_height="1.7",
        border_radius="4px",
        code_border_radius="6px",
        shadow_sm="0 1px 3px rgba(26, 54, 93, 0.08)",
        shadow_md="0 4px 12px rgba(26, 54, 93, 0.1)",
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
        primary_light="#555555",
        shadow_color="rgba(0, 0, 0, 0.05)",
    ),
    fonts=ThemeFonts(
        heading="Segoe UI",
        body="Segoe UI",
        code="Consolas",
    ),
    style=ThemeStyle(
        heading_letter_spacing="-0.01em",
        body_line_height="1.7",
        border_radius="3px",
        code_border_radius="4px",
        shadow_sm="none",
        shadow_md="none",
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
        primary_light="#4ADE80",
        shadow_color="rgba(34, 197, 94, 0.1)",
    ),
    fonts=ThemeFonts(
        heading="Consolas",
        body="Consolas",
        code="Consolas",
    ),
    style=ThemeStyle(
        heading_letter_spacing="0",
        body_line_height="1.6",
        border_radius="0",
        code_border_radius="0",
        shadow_sm="none",
        shadow_md="none",
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
        primary_light="#A78BFA",
        shadow_color="rgba(139, 92, 246, 0.1)",
    ),
    fonts=ThemeFonts(
        heading="Segoe UI",
        body="Segoe UI",
        code="Consolas",
    ),
    style=ThemeStyle(
        heading_letter_spacing="-0.02em",
        body_line_height="1.7",
        border_radius="12px",
        code_border_radius="12px",
        shadow_sm="0 1px 3px rgba(139, 92, 246, 0.08)",
        shadow_md="0 4px 16px rgba(139, 92, 246, 0.12)",
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
        primary_light="#444444",
        shadow_color="rgba(0, 0, 0, 0.06)",
    ),
    fonts=ThemeFonts(
        heading="Times New Roman",
        body="Times New Roman",
        code="Courier New",
    ),
    style=ThemeStyle(
        heading_letter_spacing="0",
        body_line_height="2.0",
        border_radius="0",
        code_border_radius="2px",
        shadow_sm="none",
        shadow_md="none",
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
        primary_light="#C084FC",
        shadow_color="rgba(168, 85, 247, 0.15)",
    ),
    fonts=ThemeFonts(
        heading="Segoe UI",
        body="Segoe UI",
        code="Consolas",
    ),
    style=ThemeStyle(
        heading_letter_spacing="-0.01em",
        body_line_height="1.7",
        border_radius="8px",
        code_border_radius="8px",
        shadow_sm="0 1px 4px rgba(168, 85, 247, 0.1)",
        shadow_md="0 4px 20px rgba(168, 85, 247, 0.15)",
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
        primary_light="#60A5FA",
        shadow_color="rgba(59, 130, 246, 0.08)",
    ),
    fonts=ThemeFonts(
        heading="Segoe UI",
        body="Segoe UI",
        code="Consolas",
    ),
    style=ThemeStyle(
        heading_letter_spacing="-0.01em",
        body_line_height="1.65",
        border_radius="6px",
        code_border_radius="6px",
        shadow_sm="0 1px 3px rgba(59, 130, 246, 0.08)",
        shadow_md="0 4px 12px rgba(59, 130, 246, 0.1)",
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

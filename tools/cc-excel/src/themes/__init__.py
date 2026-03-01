"""Theme management for cc-excel workbooks."""

from dataclasses import dataclass


@dataclass(frozen=True)
class ExcelColors:
    """Color scheme for Excel formatting."""
    header_bg: str
    header_text: str
    alt_row_bg: str
    border: str
    text: str
    accent: str
    chart_colors: tuple[str, ...]


@dataclass(frozen=True)
class ExcelFonts:
    """Font configuration for Excel."""
    header: str
    body: str
    header_size: int
    body_size: int


@dataclass(frozen=True)
class ExcelTheme:
    """Complete theme for Excel workbook generation."""
    name: str
    description: str
    colors: ExcelColors
    fonts: ExcelFonts
    header_bold: bool
    alt_row_shading: bool
    border_style: str  # "thin", "medium", "none"


# -- Theme Definitions --

BOARDROOM = ExcelTheme(
    name="boardroom",
    description="Corporate, executive style with serif fonts",
    colors=ExcelColors(
        header_bg="#1A365D",
        header_text="#FFFFFF",
        alt_row_bg="#F7FAFC",
        border="#CBD5E0",
        text="#1A202C",
        accent="#D69E2E",
        chart_colors=("#1A365D", "#D69E2E", "#2C7A7B", "#9B2C2C", "#5B21B6", "#C05621"),
    ),
    fonts=ExcelFonts(
        header="Palatino Linotype",
        body="Palatino Linotype",
        header_size=11,
        body_size=10,
    ),
    header_bold=True,
    alt_row_shading=True,
    border_style="medium",
)

PAPER = ExcelTheme(
    name="paper",
    description="Minimal, clean, elegant",
    colors=ExcelColors(
        header_bg="#4A5568",
        header_text="#FFFFFF",
        alt_row_bg="#F9FAFB",
        border="#E2E8F0",
        text="#1A202C",
        accent="#0066CC",
        chart_colors=("#4A5568", "#0066CC", "#38A169", "#E53E3E", "#805AD5", "#DD6B20"),
    ),
    fonts=ExcelFonts(
        header="Segoe UI",
        body="Segoe UI",
        header_size=11,
        body_size=10,
    ),
    header_bold=True,
    alt_row_shading=True,
    border_style="thin",
)

TERMINAL = ExcelTheme(
    name="terminal",
    description="Technical, monospace, dark header",
    colors=ExcelColors(
        header_bg="#1E1E1E",
        header_text="#00FF00",
        alt_row_bg="#2D2D2D",
        border="#4A4A4A",
        text="#D4D4D4",
        accent="#00FF00",
        chart_colors=("#00FF00", "#00BFFF", "#FF6347", "#FFD700", "#DA70D6", "#00FA9A"),
    ),
    fonts=ExcelFonts(
        header="Consolas",
        body="Consolas",
        header_size=11,
        body_size=10,
    ),
    header_bold=True,
    alt_row_shading=True,
    border_style="thin",
)

SPARK = ExcelTheme(
    name="spark",
    description="Creative, vibrant, colorful",
    colors=ExcelColors(
        header_bg="#7C3AED",
        header_text="#FFFFFF",
        alt_row_bg="#F5F3FF",
        border="#C4B5FD",
        text="#1A202C",
        accent="#EC4899",
        chart_colors=("#7C3AED", "#EC4899", "#F59E0B", "#10B981", "#3B82F6", "#EF4444"),
    ),
    fonts=ExcelFonts(
        header="Segoe UI",
        body="Segoe UI",
        header_size=11,
        body_size=10,
    ),
    header_bold=True,
    alt_row_shading=True,
    border_style="medium",
)

THESIS = ExcelTheme(
    name="thesis",
    description="Academic, traditional, scholarly",
    colors=ExcelColors(
        header_bg="#1A202C",
        header_text="#FFFFFF",
        alt_row_bg="#F7FAFC",
        border="#CBD5E0",
        text="#1A202C",
        accent="#2B6CB0",
        chart_colors=("#1A202C", "#2B6CB0", "#276749", "#9B2C2C", "#6B46C1", "#C05621"),
    ),
    fonts=ExcelFonts(
        header="Times New Roman",
        body="Times New Roman",
        header_size=11,
        body_size=10,
    ),
    header_bold=True,
    alt_row_shading=True,
    border_style="thin",
)

OBSIDIAN = ExcelTheme(
    name="obsidian",
    description="Dark, modern, sleek",
    colors=ExcelColors(
        header_bg="#374151",
        header_text="#E5E7EB",
        alt_row_bg="#1F2937",
        border="#4B5563",
        text="#E5E7EB",
        accent="#60A5FA",
        chart_colors=("#60A5FA", "#34D399", "#FBBF24", "#F87171", "#A78BFA", "#FB923C"),
    ),
    fonts=ExcelFonts(
        header="Segoe UI",
        body="Segoe UI",
        header_size=11,
        body_size=10,
    ),
    header_bold=True,
    alt_row_shading=True,
    border_style="thin",
)

BLUEPRINT = ExcelTheme(
    name="blueprint",
    description="Technical documentation, engineering",
    colors=ExcelColors(
        header_bg="#1E40AF",
        header_text="#FFFFFF",
        alt_row_bg="#EFF6FF",
        border="#93C5FD",
        text="#1E293B",
        accent="#F59E0B",
        chart_colors=("#1E40AF", "#F59E0B", "#059669", "#DC2626", "#7C3AED", "#EA580C"),
    ),
    fonts=ExcelFonts(
        header="Consolas",
        body="Consolas",
        header_size=11,
        body_size=10,
    ),
    header_bold=True,
    alt_row_shading=True,
    border_style="medium",
)


# -- Theme Registry --

_THEMES: dict[str, ExcelTheme] = {
    "boardroom": BOARDROOM,
    "paper": PAPER,
    "terminal": TERMINAL,
    "spark": SPARK,
    "thesis": THESIS,
    "obsidian": OBSIDIAN,
    "blueprint": BLUEPRINT,
}

THEMES: dict[str, str] = {t.name: t.description for t in _THEMES.values()}


def get_theme(name: str) -> ExcelTheme:
    """Get a theme by name."""
    if name not in _THEMES:
        available = ", ".join(_THEMES.keys())
        raise ValueError(f"Unknown theme: {name}. Available: {available}")
    return _THEMES[name]


def list_themes() -> dict[str, str]:
    """Return dictionary of theme names and descriptions."""
    return THEMES.copy()

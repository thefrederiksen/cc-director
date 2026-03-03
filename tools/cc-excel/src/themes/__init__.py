"""Theme management for cc-excel workbooks.

Imports canonical color/font values from cc_shared.themes and builds
Excel-specific theme objects with format-specific extensions
(chart palettes, border styles, font sizes).
"""

from dataclasses import dataclass

# Import canonical themes - handle both package and frozen modes
try:
    from cc_shared.themes import get_theme as get_canonical_theme, list_themes as canonical_list_themes
except ImportError:
    get_canonical_theme = None
    canonical_list_themes = None


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


# -- Excel-specific extensions (chart palettes, border styles) --

_CHART_COLORS: dict[str, tuple[str, ...]] = {
    "boardroom": ("#1A365D", "#D69E2E", "#2C7A7B", "#9B2C2C", "#5B21B6", "#C05621"),
    "paper": ("#4A5568", "#0066CC", "#38A169", "#E53E3E", "#805AD5", "#DD6B20"),
    "terminal": ("#00FF00", "#00BFFF", "#FF6347", "#FFD700", "#DA70D6", "#00FA9A"),
    "spark": ("#7C3AED", "#EC4899", "#F59E0B", "#10B981", "#3B82F6", "#EF4444"),
    "thesis": ("#1A202C", "#2B6CB0", "#276749", "#9B2C2C", "#6B46C1", "#C05621"),
    "obsidian": ("#60A5FA", "#34D399", "#FBBF24", "#F87171", "#A78BFA", "#FB923C"),
    "blueprint": ("#1E40AF", "#F59E0B", "#059669", "#DC2626", "#7C3AED", "#EA580C"),
}

_BORDER_STYLES: dict[str, str] = {
    "boardroom": "medium",
    "paper": "thin",
    "terminal": "thin",
    "spark": "medium",
    "thesis": "thin",
    "obsidian": "thin",
    "blueprint": "medium",
}


def _build_excel_theme(name: str) -> ExcelTheme:
    """Build an ExcelTheme from canonical theme values + Excel-specific extensions."""
    ct = get_canonical_theme(name)
    return ExcelTheme(
        name=ct.name,
        description=ct.description,
        colors=ExcelColors(
            header_bg=ct.colors.table_header_bg,
            header_text=ct.colors.table_header_text,
            alt_row_bg=ct.colors.alt_row_bg,
            border=ct.colors.border,
            text=ct.colors.text,
            accent=ct.colors.accent,
            chart_colors=_CHART_COLORS[name],
        ),
        fonts=ExcelFonts(
            header=ct.fonts.heading,
            body=ct.fonts.body,
            header_size=11,
            body_size=10,
        ),
        header_bold=True,
        alt_row_shading=True,
        border_style=_BORDER_STYLES[name],
    )


# -- Build themes from canonical source or use hardcoded fallback --

if get_canonical_theme is not None:
    _THEMES: dict[str, ExcelTheme] = {
        name: _build_excel_theme(name)
        for name in ["boardroom", "paper", "terminal", "spark", "thesis", "obsidian", "blueprint"]
    }
else:
    # Fallback for environments where cc_shared is not available
    _THEMES = {
        "boardroom": ExcelTheme(
            name="boardroom", description="Corporate, executive style with serif fonts",
            colors=ExcelColors(header_bg="#1A365D", header_text="#FFFFFF", alt_row_bg="#F7FAFC",
                               border="#CBD5E0", text="#2D3748", accent="#D69E2E",
                               chart_colors=("#1A365D", "#D69E2E", "#2C7A7B", "#9B2C2C", "#5B21B6", "#C05621")),
            fonts=ExcelFonts(header="Palatino Linotype", body="Georgia", header_size=11, body_size=10),
            header_bold=True, alt_row_shading=True, border_style="medium",
        ),
        "paper": ExcelTheme(
            name="paper", description="Minimal, clean, elegant",
            colors=ExcelColors(header_bg="#FAFAFA", header_text="#1A1A1A", alt_row_bg="#F9FAFB",
                               border="#E0E0E0", text="#1A1A1A", accent="#0066CC",
                               chart_colors=("#4A5568", "#0066CC", "#38A169", "#E53E3E", "#805AD5", "#DD6B20")),
            fonts=ExcelFonts(header="Segoe UI", body="Segoe UI", header_size=11, body_size=10),
            header_bold=True, alt_row_shading=True, border_style="thin",
        ),
        "terminal": ExcelTheme(
            name="terminal", description="Technical, monospace with dark-friendly colors",
            colors=ExcelColors(header_bg="#1A1A1A", header_text="#22C55E", alt_row_bg="#1A1A1A",
                               border="#404040", text="#E5E5E5", accent="#22C55E",
                               chart_colors=("#00FF00", "#00BFFF", "#FF6347", "#FFD700", "#DA70D6", "#00FA9A")),
            fonts=ExcelFonts(header="Consolas", body="Consolas", header_size=11, body_size=10),
            header_bold=True, alt_row_shading=True, border_style="thin",
        ),
        "spark": ExcelTheme(
            name="spark", description="Creative, colorful, modern",
            colors=ExcelColors(header_bg="#8B5CF6", header_text="#FFFFFF", alt_row_bg="#F5F3FF",
                               border="#E5E7EB", text="#374151", accent="#EC4899",
                               chart_colors=("#7C3AED", "#EC4899", "#F59E0B", "#10B981", "#3B82F6", "#EF4444")),
            fonts=ExcelFonts(header="Segoe UI", body="Segoe UI", header_size=11, body_size=10),
            header_bold=True, alt_row_shading=True, border_style="medium",
        ),
        "thesis": ExcelTheme(
            name="thesis", description="Academic, scholarly with proper citations style",
            colors=ExcelColors(header_bg="#F0F0F0", header_text="#333333", alt_row_bg="#F7FAFC",
                               border="#CCCCCC", text="#333333", accent="#800000",
                               chart_colors=("#1A202C", "#2B6CB0", "#276749", "#9B2C2C", "#6B46C1", "#C05621")),
            fonts=ExcelFonts(header="Times New Roman", body="Times New Roman", header_size=11, body_size=10),
            header_bold=True, alt_row_shading=True, border_style="thin",
        ),
        "obsidian": ExcelTheme(
            name="obsidian", description="Dark theme with subtle highlights",
            colors=ExcelColors(header_bg="#262626", header_text="#A855F7", alt_row_bg="#1F2937",
                               border="#404040", text="#D4D4D4", accent="#C084FC",
                               chart_colors=("#60A5FA", "#34D399", "#FBBF24", "#F87171", "#A78BFA", "#FB923C")),
            fonts=ExcelFonts(header="Segoe UI", body="Segoe UI", header_size=11, body_size=10),
            header_bold=True, alt_row_shading=True, border_style="thin",
        ),
        "blueprint": ExcelTheme(
            name="blueprint", description="Technical documentation style",
            colors=ExcelColors(header_bg="#1E3A5F", header_text="#FFFFFF", alt_row_bg="#EFF6FF",
                               border="#CBD5E1", text="#374151", accent="#F59E0B",
                               chart_colors=("#1E40AF", "#F59E0B", "#059669", "#DC2626", "#7C3AED", "#EA580C")),
            fonts=ExcelFonts(header="Segoe UI", body="Segoe UI", header_size=11, body_size=10),
            header_bold=True, alt_row_shading=True, border_style="medium",
        ),
    }


# -- Theme Registry --

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

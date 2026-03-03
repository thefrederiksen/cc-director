"""CSS generation from canonical themes for cc-pdf and cc-html.

Replaces static CSS files with programmatic generation from
the canonical theme definitions in cc_shared.themes.
"""

try:
    from .themes import get_theme, CanonicalTheme
except ImportError:
    from themes import get_theme, CanonicalTheme


# Base structural CSS (from cc-markdown base.css, ~130 lines)
BASE_CSS = """/* Base styles for all themes */

* {
    box-sizing: border-box;
}

html {
    font-size: 16px;
    line-height: 1.6;
}

body {
    margin: 0;
    padding: 0;
}

.markdown-body {
    max-width: 800px;
    margin: 0 auto;
    padding: 2rem;
}

/* Typography */
h1, h2, h3, h4, h5, h6 {
    margin-top: 1.5em;
    margin-bottom: 0.5em;
    font-weight: 600;
    line-height: 1.25;
}

h1 { font-size: 2em; }
h2 { font-size: 1.5em; }
h3 { font-size: 1.25em; }
h4 { font-size: 1em; }
h5 { font-size: 0.875em; }
h6 { font-size: 0.85em; }

p {
    margin: 1em 0;
}

/* Links */
a {
    text-decoration: none;
}

a:hover {
    text-decoration: underline;
}

/* Lists */
ul, ol {
    margin: 1em 0;
    padding-left: 2em;
}

li {
    margin: 0.25em 0;
}

/* Code */
code {
    font-size: 0.9em;
    padding: 0.2em 0.4em;
    border-radius: 3px;
}

pre {
    margin: 1em 0;
    padding: 1em;
    overflow-x: auto;
    border-radius: 6px;
}

pre code {
    padding: 0;
    background: transparent;
}

/* Blockquotes */
blockquote {
    margin: 1em 0;
    padding: 0.5em 1em;
    border-left: 4px solid;
}

blockquote p {
    margin: 0.5em 0;
}

/* Tables */
table {
    width: 100%;
    border-collapse: collapse;
    margin: 1em 0;
}

th, td {
    padding: 0.75em;
    text-align: left;
    border: 1px solid;
}

th {
    font-weight: 600;
}

/* Horizontal rule */
hr {
    border: none;
    height: 1px;
    margin: 2em 0;
}

/* Images */
img {
    max-width: 100%;
    height: auto;
}

/* Task lists */
.task-list-item {
    list-style-type: none;
}

.task-list-item input[type="checkbox"] {
    margin-right: 0.5em;
}
"""


def _generate_theme_css(theme: CanonicalTheme) -> str:
    """Generate theme-specific CSS from canonical theme colors and fonts."""
    c = theme.colors
    f = theme.fonts

    return f"""/* {theme.name.title()} Theme - {theme.description} */

body {{
    font-family: "{f.body}", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    color: {c.text};
    background: {c.background};
}}

h1, h2, h3, h4, h5, h6 {{
    font-family: "{f.heading}", -apple-system, BlinkMacSystemFont, sans-serif;
    color: {c.heading};
}}

a {{
    color: {c.link};
}}

code {{
    font-family: "{f.code}", "JetBrains Mono", "Fira Code", Consolas, monospace;
    background: {c.code_bg};
    color: {c.code_text};
}}

pre {{
    background: {c.code_bg};
    border: 1px solid {c.border};
}}

blockquote {{
    color: {c.blockquote_text};
    border-color: {c.blockquote_border};
    background: {c.blockquote_bg};
}}

th, td {{
    border-color: {c.border};
}}

th {{
    background: {c.table_header_bg};
    color: {c.table_header_text};
}}

hr {{
    background: {c.border};
}}
"""


# Per-theme structural CSS overrides (effects that go beyond color/font)
THEME_EXTRAS: dict[str, str] = {
    "boardroom": """
h1 {
    border-bottom: 3px solid %(accent)s;
    padding-bottom: 0.5em;
}

h2 {
    border-bottom: 1px solid %(border)s;
    padding-bottom: 0.3em;
}

pre {
    border-left: 4px solid %(primary)s;
}

blockquote {
    font-style: italic;
}

hr {
    background: %(accent)s;
    height: 2px;
}
""",
    "paper": "",
    "terminal": """
body {
    font-size: 14px;
}

.markdown-body {
    background: %(background)s;
}

h1, h2, h3, h4, h5, h6 {
    font-weight: 700;
}

h1::before { content: "# "; }
h2::before { content: "## "; }
h3::before { content: "### "; }

code {
    border: 1px solid %(border)s;
}

pre code {
    border: none;
}

/* Terminal-style list markers */
ul {
    list-style-type: none;
}

ul li::before {
    content: "> ";
    color: %(primary)s;
}
""",
    "spark": """
h1 {
    background: linear-gradient(135deg, %(primary)s, %(accent)s);
    -webkit-background-clip: text;
    -webkit-text-fill-color: transparent;
    background-clip: text;
}

a {
    font-weight: 500;
}

a:hover {
    color: %(accent)s;
}

code {
    border-radius: 6px;
}

pre {
    background: linear-gradient(135deg, #faf5ff, #fdf4ff);
    border: 1px solid #e9d5ff;
    border-radius: 12px;
}

blockquote {
    border-radius: 0 8px 8px 0;
}

th {
    background: linear-gradient(135deg, %(primary)s, %(accent)s);
}

hr {
    background: linear-gradient(135deg, %(primary)s, %(accent)s);
    height: 3px;
    border-radius: 2px;
}
""",
    "thesis": """
body {
    font-size: 12pt;
    line-height: 2;
}

.markdown-body {
    max-width: 6.5in;
    text-align: justify;
}

h1, h2, h3, h4, h5, h6 {
    text-align: left;
}

h1 {
    text-align: center;
    margin-top: 2em;
    margin-bottom: 1em;
}

h2 {
    margin-top: 1.5em;
}

code {
    font-size: 10pt;
}

pre {
    font-size: 10pt;
}

blockquote {
    font-style: italic;
    margin: 1.5em 2em;
}

th, td {
    font-size: 11pt;
}

/* Footnote styling */
.footnote {
    font-size: 10pt;
}
""",
    "obsidian": """
.markdown-body {
    background: %(background)s;
}

h1 {
    color: %(primary)s;
}

a:hover {
    color: #c084fc;
}

code {
    border: 1px solid %(border)s;
}

pre code {
    border: none;
}

/* Subtle glow on headings */
h1, h2 {
    text-shadow: 0 0 30px rgba(168, 85, 247, 0.3);
}
""",
    "blueprint": """
h1 {
    border-bottom: 2px solid %(primary)s;
    padding-bottom: 0.5em;
}

h2 {
    color: %(primary)s;
}

a {
    font-weight: 500;
}

code {
    font-size: 0.875em;
}

pre {
    border-left: 4px solid %(primary)s;
}

blockquote {
    border-radius: 0 4px 4px 0;
}

th {
    text-transform: uppercase;
    font-size: 0.875em;
    letter-spacing: 0.05em;
}

/* Note/warning callout styling */
blockquote strong:first-child {
    color: %(primary)s;
}
""",
}


def get_theme_css(theme_name: str) -> str:
    """Get complete CSS for a theme.

    Returns base structural CSS + generated color/font rules + theme extras.

    Args:
        theme_name: Name of the theme

    Returns:
        Complete CSS string

    Raises:
        ValueError: If theme name is not recognized
    """
    theme = get_theme(theme_name)
    c = theme.colors

    # Base structural CSS
    css = BASE_CSS

    # Generated color/font rules
    css += "\n" + _generate_theme_css(theme)

    # Per-theme structural extras
    extras_template = THEME_EXTRAS.get(theme_name, "")
    if extras_template:
        extras = extras_template % {
            "primary": c.primary,
            "accent": c.accent,
            "text": c.text,
            "heading": c.heading,
            "background": c.background,
            "border": c.border,
            "link": c.link,
        }
        css += "\n" + extras

    return css

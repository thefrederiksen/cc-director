"""Word document conversion using python-docx with theme support."""

from pathlib import Path
from typing import Optional

from bs4 import BeautifulSoup
from docx import Document
from docx.shared import Pt, Inches, RGBColor
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.enum.style import WD_STYLE_TYPE

# Import canonical themes - handle both package and frozen modes
try:
    from cc_shared.themes import CanonicalTheme, get_theme
except ImportError:
    try:
        from themes import CanonicalTheme, get_theme
    except ImportError:
        CanonicalTheme = None
        get_theme = None


def _hex_to_rgb(hex_color: str) -> Optional[RGBColor]:
    """Convert hex color string to RGBColor. Returns None for non-hex values."""
    hex_color = hex_color.strip()
    if not hex_color.startswith("#"):
        return None
    hex_color = hex_color.lstrip("#")
    if len(hex_color) == 3:
        hex_color = "".join(c * 2 for c in hex_color)
    if len(hex_color) != 6:
        return None
    try:
        r = int(hex_color[0:2], 16)
        g = int(hex_color[2:4], 16)
        b = int(hex_color[4:6], 16)
        return RGBColor(r, g, b)
    except ValueError:
        return None


def convert_to_word(
    html_content: str,
    output_path: Path,
    theme_name: str = "paper",
) -> None:
    """Convert HTML to Word document with theme styling.

    Maps HTML elements to Word styles:
    - h1-h6 -> Heading 1-6
    - p -> Normal
    - ul/ol -> List styles
    - table -> Table with theme colors
    - pre/code -> Code style with theme code font
    - blockquote -> Quote style

    Args:
        html_content: Complete HTML document string
        output_path: Path for output .docx file
        theme_name: Theme to apply for fonts and colors
    """
    # Get theme
    theme = None
    if get_theme is not None:
        try:
            theme = get_theme(theme_name)
        except ValueError:
            pass

    # Parse HTML
    soup = BeautifulSoup(html_content, "html.parser")

    # Create document
    doc = Document()

    # Apply theme fonts to default style
    if theme:
        style = doc.styles["Normal"]
        style.font.name = theme.fonts.body
        text_color = _hex_to_rgb(theme.colors.text)
        if text_color:
            style.font.color.rgb = text_color

        # Apply heading fonts
        for level in range(1, 7):
            style_name = f"Heading {level}"
            if style_name in [s.name for s in doc.styles]:
                h_style = doc.styles[style_name]
                h_style.font.name = theme.fonts.heading
                heading_color = _hex_to_rgb(theme.colors.heading)
                if heading_color:
                    h_style.font.color.rgb = heading_color

    # Find the main content
    body = soup.find("article") or soup.find("body") or soup

    # Process elements
    _process_element(doc, body, theme)

    # Ensure output directory exists
    output_path.parent.mkdir(parents=True, exist_ok=True)

    # Save
    doc.save(str(output_path))


def _process_element(doc: Document, element, theme: Optional[CanonicalTheme] = None):
    """Recursively process HTML elements."""
    if element.name is None:
        return

    for child in element.children:
        if child.name is None:
            if child.string and child.string.strip():
                pass
            continue

        if child.name in ["h1", "h2", "h3", "h4", "h5", "h6"]:
            level = int(child.name[1])
            text = child.get_text(strip=True)
            heading = doc.add_heading(text, level=level)

            # Apply theme accent underline on h1
            if theme and level == 1:
                primary_color = _hex_to_rgb(theme.colors.primary)
                if primary_color and heading.runs:
                    heading.runs[0].font.color.rgb = primary_color

        elif child.name == "p":
            text = child.get_text(strip=True)
            if text:
                doc.add_paragraph(text)

        elif child.name == "ul":
            _process_list(doc, child, ordered=False)

        elif child.name == "ol":
            _process_list(doc, child, ordered=True)

        elif child.name == "blockquote":
            text = child.get_text(strip=True)
            if text:
                para = doc.add_paragraph(text)
                para.style = "Quote" if "Quote" in [s.name for s in doc.styles] else "Normal"
                para.paragraph_format.left_indent = Inches(0.5)

        elif child.name == "pre":
            code_text = child.get_text()
            para = doc.add_paragraph()
            run = para.add_run(code_text)
            # Use theme code font
            if theme:
                run.font.name = theme.fonts.code
            else:
                run.font.name = "Consolas"
            run.font.size = Pt(9)
            para.paragraph_format.left_indent = Inches(0.25)

        elif child.name == "table":
            _process_table(doc, child, theme)

        elif child.name == "hr":
            para = doc.add_paragraph()
            para.paragraph_format.space_after = Pt(12)

        elif child.name in ["div", "article", "section", "main"]:
            _process_element(doc, child, theme)


def _process_list(doc: Document, list_element, ordered: bool = False):
    """Process ul or ol list."""
    for li in list_element.find_all("li", recursive=False):
        text = li.get_text(strip=True)
        if text:
            style = "List Number" if ordered else "List Bullet"
            try:
                doc.add_paragraph(text, style=style)
            except KeyError:
                para = doc.add_paragraph(text)
                para.paragraph_format.left_indent = Inches(0.5)


def _process_table(doc: Document, table_element, theme: Optional[CanonicalTheme] = None):
    """Process HTML table with theme colors."""
    rows = table_element.find_all("tr")
    if not rows:
        return

    first_row = rows[0]
    cols = first_row.find_all(["th", "td"])
    num_cols = len(cols)

    if num_cols == 0:
        return

    table = doc.add_table(rows=len(rows), cols=num_cols)
    table.style = "Table Grid"

    for row_idx, row in enumerate(rows):
        cells = row.find_all(["th", "td"])
        for col_idx, cell in enumerate(cells):
            if col_idx < num_cols:
                text = cell.get_text(strip=True)
                table.rows[row_idx].cells[col_idx].text = text

                if cell.name == "th":
                    for paragraph in table.rows[row_idx].cells[col_idx].paragraphs:
                        for run in paragraph.runs:
                            run.bold = True
                            # Apply theme header colors
                            if theme:
                                header_text_color = _hex_to_rgb(theme.colors.table_header_text)
                                if header_text_color:
                                    run.font.color.rgb = header_text_color

                    # Apply theme header background
                    if theme:
                        header_bg = _hex_to_rgb(theme.colors.table_header_bg)
                        if header_bg:
                            from docx.oxml.ns import qn
                            from docx.oxml import OxmlElement
                            tc = table.rows[row_idx].cells[col_idx]._tc
                            tcPr = tc.get_or_add_tcPr()
                            shading = OxmlElement("w:shd")
                            shading.set(qn("w:fill"), theme.colors.table_header_bg.lstrip("#"))
                            shading.set(qn("w:val"), "clear")
                            tcPr.append(shading)

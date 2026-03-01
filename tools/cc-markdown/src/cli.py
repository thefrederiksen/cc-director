"""CLI interface for cc-markdown using Typer."""

import sys
from pathlib import Path
from typing import Optional

import typer
from rich.console import Console
from rich.table import Table

# Handle imports for both package and frozen executable modes
try:
    from . import __version__
    from .parser import parse_markdown
    from .html_generator import generate_html, embed_images_as_base64
    from .pdf_converter import convert_to_pdf
    from .word_converter import convert_to_word
    from .themes import THEMES, get_theme_css
except ImportError:
    # Frozen executable mode - use absolute imports
    from src import __version__
    from src.parser import parse_markdown
    from src.html_generator import generate_html, embed_images_as_base64
    from src.pdf_converter import convert_to_pdf
    from src.word_converter import convert_to_word
    from src.themes import THEMES, get_theme_css

app = typer.Typer(
    name="cc-markdown",
    help="Convert Markdown to PDF, Word, and HTML with beautiful themes.",
    add_completion=False,
)
console = Console()


def version_callback(value: bool):
    if value:
        console.print(f"cc-markdown version {__version__}")
        raise typer.Exit()


def themes_callback(value: bool):
    if value:
        table = Table(title="Available Themes")
        table.add_column("Theme", style="cyan")
        table.add_column("Description")

        for name, desc in THEMES.items():
            table.add_row(name, desc)

        console.print(table)
        raise typer.Exit()


@app.command()
def main(
    input_file: Path = typer.Argument(
        ...,
        help="Input Markdown file",
        exists=True,
        readable=True,
    ),
    output: Path = typer.Option(
        ...,
        "--output", "-o",
        help="Output file (format detected from extension: .pdf, .docx, .html)",
    ),
    theme: str = typer.Option(
        "paper",
        "--theme", "-t",
        help="Built-in theme name",
    ),
    css: Optional[Path] = typer.Option(
        None,
        "--css",
        help="Custom CSS file path",
        exists=True,
        readable=True,
    ),
    page_size: str = typer.Option(
        "a4",
        "--page-size",
        help="Page size for PDF (a4, letter)",
    ),
    margin: str = typer.Option(
        "1in",
        "--margin",
        help="Page margin for PDF",
    ),
    version: bool = typer.Option(
        False,
        "--version", "-v",
        callback=version_callback,
        is_eager=True,
        help="Show version and exit",
    ),
    themes_list: bool = typer.Option(
        False,
        "--themes",
        callback=themes_callback,
        is_eager=True,
        help="List available themes and exit",
    ),
):
    """Convert Markdown to PDF, Word, or HTML with beautiful themes."""

    # Validate theme
    if theme not in THEMES and css is None:
        console.print(f"[red]Error:[/red] Unknown theme '{theme}'. Use --themes to list available themes.")
        raise typer.Exit(1)

    # Determine output format
    suffix = output.suffix.lower()
    if suffix not in [".pdf", ".docx", ".html"]:
        console.print(f"[red]Error:[/red] Unsupported output format '{suffix}'. Use .pdf, .docx, or .html")
        raise typer.Exit(1)

    try:
        # Read input
        console.print(f"[blue]Reading:[/blue] {input_file}")
        markdown_content = input_file.read_text(encoding="utf-8")

        # Parse markdown
        console.print("[blue]Parsing:[/blue] Markdown")
        parsed = parse_markdown(markdown_content)

        # Get CSS
        if css:
            console.print(f"[blue]Loading:[/blue] Custom CSS from {css}")
            css_content = css.read_text(encoding="utf-8")
        else:
            console.print(f"[blue]Loading:[/blue] Theme '{theme}'")
            css_content = get_theme_css(theme)

        # Generate HTML
        console.print("[blue]Generating:[/blue] HTML")
        html_content = generate_html(parsed, css_content)

        # Convert to output format
        if suffix == ".html":
            console.print(f"[blue]Writing:[/blue] {output}")
            output.write_text(html_content, encoding="utf-8")

        elif suffix == ".pdf":
            console.print(f"[blue]Converting:[/blue] PDF with {page_size} page size")
            # Embed images as base64 so they work in Chrome headless PDF generation
            html_with_images = embed_images_as_base64(html_content, input_file.parent)
            convert_to_pdf(html_with_images, output, page_size=page_size, margin=margin)

        elif suffix == ".docx":
            console.print(f"[blue]Converting:[/blue] Word document")
            convert_to_word(html_content, output)

        console.print(f"[green]Done:[/green] {output}")

    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Invalid input:[/red] {e}")
        raise typer.Exit(1)
    except RuntimeError as e:
        console.print(f"[red]Conversion error:[/red] {e}")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


if __name__ == "__main__":
    app()

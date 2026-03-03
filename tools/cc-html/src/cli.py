"""CLI interface for cc-html using Typer."""

import sys
from pathlib import Path
from typing import Optional

import typer
from rich.console import Console
from rich.table import Table

# Handle imports for both package and frozen executable modes
try:
    from . import __version__
    from .html_generator import generate_html, embed_images_as_base64
    from .md_converter import convert_html_to_markdown
except ImportError:
    # Frozen executable mode - use absolute imports
    from src import __version__
    from src.html_generator import generate_html, embed_images_as_base64
    from src.md_converter import convert_html_to_markdown

# Import shared modules - handle both package and frozen modes
try:
    from cc_shared.markdown_parser import parse_markdown
    from cc_shared.css_themes import get_theme_css
    from cc_shared.themes import THEMES
except ImportError:
    try:
        sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent / "cc_shared"))
        from cc_shared.markdown_parser import parse_markdown
        from cc_shared.css_themes import get_theme_css
        from cc_shared.themes import THEMES
    except ImportError:
        from markdown_parser import parse_markdown
        from css_themes import get_theme_css
        from themes import THEMES

app = typer.Typer(
    name="cc-html",
    help="Convert between Markdown and HTML with beautiful themes.",
    add_completion=False,
    invoke_without_command=True,
)
console = Console()


def version_callback(value: bool):
    if value:
        console.print(f"cc-html version {__version__}")
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


@app.callback(invoke_without_command=True)
def main_callback(
    ctx: typer.Context,
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
    """Convert between Markdown and HTML with beautiful themes."""
    if ctx.invoked_subcommand is None:
        console.print("Use 'cc-html from-markdown' or 'cc-html to-markdown'. Run --help for details.")
        raise typer.Exit()


@app.command("from-markdown")
def from_markdown(
    input_file: Path = typer.Argument(
        ...,
        help="Input Markdown file",
        exists=True,
        readable=True,
    ),
    output: Path = typer.Option(
        ...,
        "--output", "-o",
        help="Output HTML file",
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
):
    """Convert Markdown to HTML with beautiful themes."""

    # Validate theme
    if theme not in THEMES and css is None:
        console.print(f"[red]Error:[/red] Unknown theme '{theme}'. Use --themes to list available themes.")
        raise typer.Exit(1)

    # Validate output extension
    if output.suffix.lower() != ".html":
        console.print("[red]Error:[/red] Output file must have .html extension")
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

        # Generate HTML with embedded images
        console.print("[blue]Generating:[/blue] HTML")
        html_content = generate_html(parsed, css_content)
        html_content = embed_images_as_base64(html_content, input_file.parent)

        # Write output
        console.print(f"[blue]Writing:[/blue] {output}")
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(html_content, encoding="utf-8")

        console.print(f"[green]Done:[/green] {output}")

    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Invalid input:[/red] {e}")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


@app.command("to-markdown")
def to_markdown(
    input_file: Path = typer.Argument(
        ...,
        help="Input HTML file",
        exists=True,
        readable=True,
    ),
    output: Optional[Path] = typer.Option(
        None,
        "--output", "-o",
        help="Output Markdown file (defaults to input name with .md extension)",
    ),
):
    """Convert HTML to Markdown, extracting embedded images."""

    # Default output path
    if output is None:
        output = input_file.with_suffix(".md")

    # Validate output extension
    if output.suffix.lower() != ".md":
        console.print("[red]Error:[/red] Output file must have .md extension")
        raise typer.Exit(1)

    try:
        console.print(f"[blue]Reading:[/blue] {input_file}")
        html_content = input_file.read_text(encoding="utf-8")

        console.print("[blue]Converting:[/blue] HTML to Markdown")
        markdown = convert_html_to_markdown(
            html_content,
            output_path=output,
            input_dir=input_file.parent,
        )

        console.print(f"[blue]Writing:[/blue] {output}")
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(markdown, encoding="utf-8")

        console.print(f"[green]Done:[/green] {output}")

    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Invalid input:[/red] {e}")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


if __name__ == "__main__":
    app()

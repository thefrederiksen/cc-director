"""CLI interface for cc-powerpoint using Typer."""

import sys
from pathlib import Path

import typer
from rich.console import Console
from rich.table import Table

# Handle imports for both package and frozen executable modes
try:
    from . import __version__
    from .parser import parse_markdown
    from .pptx_generator import generate_pptx
    from .themes import THEMES, get_theme
except ImportError:
    # Frozen executable mode - use absolute imports
    from src import __version__
    from src.parser import parse_markdown
    from src.pptx_generator import generate_pptx
    from src.themes import THEMES, get_theme

app = typer.Typer(
    name="cc-powerpoint",
    help="Convert Markdown to PowerPoint presentations with beautiful themes.",
    add_completion=False,
)
console = Console()


def version_callback(value: bool):
    if value:
        console.print(f"cc-powerpoint version {__version__}")
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
        help="Input Markdown file with --- slide separators",
        exists=True,
        readable=True,
    ),
    output: Path = typer.Option(
        None,
        "--output", "-o",
        help="Output .pptx file path (defaults to input filename with .pptx extension)",
    ),
    theme: str = typer.Option(
        "paper",
        "--theme", "-t",
        help="Built-in theme name",
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
    """Convert Markdown to PowerPoint presentations with beautiful themes."""

    # Validate theme
    if theme not in THEMES:
        console.print(f"[red]Error:[/red] Unknown theme '{theme}'. Use --themes to list available themes.")
        raise typer.Exit(1)

    # Default output path
    if output is None:
        output = input_file.with_suffix(".pptx")

    # Validate output extension
    if output.suffix.lower() != ".pptx":
        console.print(f"[red]Error:[/red] Output file must have .pptx extension, got '{output.suffix}'")
        raise typer.Exit(1)

    try:
        # Read input
        console.print(f"[blue]Reading:[/blue] {input_file}")
        markdown_content = input_file.read_text(encoding="utf-8")

        # Parse markdown into slides
        console.print("[blue]Parsing:[/blue] Markdown slides")
        slides = parse_markdown(markdown_content)

        if not slides:
            console.print("[red]Error:[/red] No slides found. Use --- to separate slides.")
            raise typer.Exit(1)

        console.print(f"[blue]Found:[/blue] {len(slides)} slides")

        # Load theme
        console.print(f"[blue]Theme:[/blue] {theme}")
        presentation_theme = get_theme(theme)

        # Generate PowerPoint
        console.print("[blue]Generating:[/blue] PowerPoint presentation")
        generate_pptx(
            slides=slides,
            theme=presentation_theme,
            output_path=output,
            input_dir=input_file.parent,
        )

        console.print(f"[green]Done:[/green] {output}")

    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Invalid input:[/red] {e}")
        raise typer.Exit(1)
    except RuntimeError as e:
        console.print(f"[red]Generation error:[/red] {e}")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


if __name__ == "__main__":
    app()

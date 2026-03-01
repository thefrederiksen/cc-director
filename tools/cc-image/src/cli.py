"""CLI for cc-image - unified image toolkit."""

from pathlib import Path
from typing import Optional

import requests
import typer
from PIL import UnidentifiedImageError
from rich.console import Console
from rich.table import Table

try:
    from . import __version__
    from .manipulation import image_info, resize, convert
    from .vision import describe, extract_text
    from .generation import generate_to_file
except ImportError:
    from src import __version__
    from src.manipulation import image_info, resize, convert
    from src.vision import describe, extract_text
    from src.generation import generate_to_file

app = typer.Typer(
    name="cc-image",
    help="Unified image toolkit: generate, analyze, OCR, resize, convert.",
    add_completion=False,
)
console = Console()


def version_callback(value: bool):
    if value:
        console.print(f"cc-image version {__version__}")
        raise typer.Exit()


@app.callback()
def main(
    version: bool = typer.Option(
        False, "--version", "-v",
        callback=version_callback,
        is_eager=True,
        help="Show version and exit",
    ),
):
    """Unified image toolkit."""
    pass


@app.command()
def info(
    image: Path = typer.Argument(..., help="Image file", exists=True),
):
    """Show image metadata."""
    data = image_info(image)
    table = Table(title=f"Image Info: {data['path']}")
    table.add_column("Property", style="cyan")
    table.add_column("Value")

    table.add_row("Dimensions", f"{data['width']} x {data['height']}")
    table.add_row("Format", data['format'] or "Unknown")
    table.add_row("Mode", data['mode'])
    table.add_row("Size", f"{data['size_bytes'] / 1024:.1f} KB")

    console.print(table)


@app.command("resize")
def resize_cmd(
    image: Path = typer.Argument(..., help="Input image", exists=True),
    output: Path = typer.Option(..., "-o", "--output", help="Output path"),
    width: Optional[int] = typer.Option(None, "-w", "--width", help="Target width"),
    height: Optional[int] = typer.Option(None, "-h", "--height", help="Target height"),
    quality: int = typer.Option(95, "-q", "--quality", help="JPEG quality (1-100)"),
):
    """Resize an image."""
    if width is None and height is None:
        console.print("[red]Error:[/red] Specify --width or --height")
        raise typer.Exit(1)

    try:
        result = resize(image, output, width=width, height=height, quality=quality)
        info = image_info(result)
        console.print(f"[green]Resized:[/green] {result}")
        console.print(f"[cyan]New size:[/cyan] {info['width']} x {info['height']}")
    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Invalid argument:[/red] {e}")
        raise typer.Exit(1)
    except UnidentifiedImageError:
        console.print(f"[red]Error:[/red] Cannot open image file - unsupported or corrupted format")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


@app.command("convert")
def convert_cmd(
    image: Path = typer.Argument(..., help="Input image", exists=True),
    output: Path = typer.Option(..., "-o", "--output", help="Output path (format from extension)"),
    quality: int = typer.Option(95, "-q", "--quality", help="JPEG quality (1-100)"),
):
    """Convert image format."""
    try:
        result = convert(image, output, quality=quality)
        console.print(f"[green]Converted:[/green] {result}")
    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except UnidentifiedImageError:
        console.print(f"[red]Error:[/red] Cannot open image file - unsupported or corrupted format")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


@app.command("describe")
def describe_cmd(
    image: Path = typer.Argument(..., help="Image to analyze", exists=True),
    engine: str = typer.Option(
        "claude_code",
        "--engine", "-e",
        help="LLM engine: claude_code (default) or openai",
    ),
):
    """Get AI description of an image."""
    try:
        engine_display = "Claude Code" if engine == "claude_code" else "OpenAI"
        console.print(f"[blue]Analyzing image with {engine_display}...[/blue]")
        result = describe(image, engine=engine)
        console.print(f"\n{result}")
    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except RuntimeError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Configuration error:[/red] {e}")
        raise typer.Exit(1)


@app.command("ocr")
def ocr_cmd(
    image: Path = typer.Argument(..., help="Image with text", exists=True),
    engine: str = typer.Option(
        "claude_code",
        "--engine", "-e",
        help="LLM engine: claude_code (default) or openai",
    ),
):
    """Extract text from image (OCR)."""
    try:
        engine_display = "Claude Code" if engine == "claude_code" else "OpenAI"
        console.print(f"[blue]Extracting text with {engine_display}...[/blue]")
        result = extract_text(image, engine=engine)
        console.print(f"\n{result}")
    except FileNotFoundError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except RuntimeError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except ValueError as e:
        console.print(f"[red]Configuration error:[/red] {e}")
        raise typer.Exit(1)


@app.command("generate")
def generate_cmd(
    prompt: str = typer.Argument(..., help="Image description"),
    output: Path = typer.Option(..., "-o", "--output", help="Output path"),
    size: str = typer.Option("1024x1024", "-s", "--size", help="Size: 1024x1024, 1024x1792, 1792x1024"),
    quality: str = typer.Option("standard", "-q", "--quality", help="Quality: standard, hd"),
):
    """Generate image with DALL-E."""
    try:
        console.print(f"[blue]Generating:[/blue] {prompt[:50]}...")
        result = generate_to_file(prompt, output, size=size, quality=quality)
        console.print(f"[green]Generated:[/green] {result}")
    except RuntimeError as e:
        console.print(f"[red]API error:[/red] {e}")
        raise typer.Exit(1)
    except requests.RequestException as e:
        console.print(f"[red]Network error:[/red] {e}")
        raise typer.Exit(1)
    except OSError as e:
        console.print(f"[red]File error:[/red] {e}")
        raise typer.Exit(1)


if __name__ == "__main__":
    app()

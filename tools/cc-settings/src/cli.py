"""CLI interface for cc-settings."""

import json
from typing import Optional

import typer
from rich.console import Console
from rich.table import Table

from . import __version__
from . import settings

app = typer.Typer(
    name="cc-settings",
    help="Manage cc-director configuration and system settings.",
    no_args_is_help=True,
)
console = Console()


def version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-settings v{__version__}")
        raise typer.Exit()


@app.callback()
def main(
    version: bool = typer.Option(
        None, "--version", "-v", callback=version_callback, help="Show version"
    ),
) -> None:
    """Manage cc-director configuration and system settings."""
    pass


@app.command()
def show(
    section: Optional[str] = typer.Argument(
        None, help="Section name to show (e.g. screenshots, vault, llm)"
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """Display current settings."""
    config = settings.load_config()

    if section:
        data = settings.get_section(config, section)
        if data is None:
            sections = settings.get_section_names(config)
            console.print(f"[red]Unknown section: {section}[/red]")
            console.print(f"Available sections: {', '.join(sections)}")
            raise typer.Exit(1)

        if json_output:
            console.print(json.dumps({section: data}, indent=2))
            return

        console.print(f"\n[bold]{section}[/bold]")
        _display_section(data, indent=2)
        console.print()
    else:
        full = config.to_dict()

        if json_output:
            console.print(json.dumps(full, indent=2))
            return

        for name, data in full.items():
            console.print(f"\n[bold]{name}[/bold]")
            _display_section(data, indent=2)
        console.print()


@app.command()
def get(
    key: str = typer.Argument(help="Setting key (e.g. screenshots.source_directory)"),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """Get a specific setting value."""
    config = settings.load_config()
    found, value = settings.get_value(config, key)

    if not found:
        console.print(f"[red]Unknown key: {key}[/red]")
        console.print("Use 'cc-settings list' to see available keys.")
        raise typer.Exit(1)

    if json_output:
        console.print(json.dumps({"key": key, "value": value}, indent=2))
    else:
        console.print(str(value))


@app.command(name="set")
def set_value(
    key: str = typer.Argument(help="Setting key (e.g. screenshots.source_directory)"),
    value: str = typer.Argument(help="New value"),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """Set a configuration value."""
    config = settings.load_config()
    success = settings.set_value(config, key, value)

    if not success:
        console.print(f"[red]Cannot set key: {key}[/red]")
        console.print("Use 'cc-settings list' to see available keys.")
        raise typer.Exit(1)

    if json_output:
        console.print(json.dumps({"key": key, "value": value, "status": "saved"}, indent=2))
    else:
        console.print(f"[green]Set {key} = {value}[/green]")


@app.command(name="list")
def list_keys(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """List all setting keys."""
    config = settings.load_config()
    keys = settings.list_keys(config)
    all_settings = settings.get_all_settings(config)

    if json_output:
        console.print(json.dumps(keys, indent=2))
        return

    table = Table(show_header=True, header_style="bold")
    table.add_column("Key")
    table.add_column("Value")

    for key in keys:
        val = all_settings[key]
        display = _format_value(val)
        table.add_row(key, display)

    console.print(table)


@app.command()
def path(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON"),
) -> None:
    """Show the config file location."""
    from cc_shared.config import get_config_path

    config_path = str(get_config_path())

    if json_output:
        console.print(json.dumps({"config_path": config_path}, indent=2))
    else:
        console.print(config_path)


def _display_section(data, indent=0) -> None:
    """Recursively display a config section."""
    prefix = " " * indent
    if isinstance(data, dict):
        for key, value in data.items():
            if isinstance(value, dict):
                console.print(f"{prefix}[dim]{key}:[/dim]")
                _display_section(value, indent + 2)
            elif isinstance(value, list):
                console.print(f"{prefix}[dim]{key}:[/dim] {_format_value(value)}")
            else:
                console.print(f"{prefix}[dim]{key}:[/dim] {value}")
    else:
        console.print(f"{prefix}{data}")


def _format_value(val) -> str:
    """Format a value for display."""
    if isinstance(val, list):
        if len(val) == 0:
            return "[]"
        return json.dumps(val)
    if isinstance(val, bool):
        return str(val).lower()
    return str(val)


if __name__ == "__main__":
    app()

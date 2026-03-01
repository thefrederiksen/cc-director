"""CLI entry point for cc-personresearch."""

import json
import sys
from pathlib import Path
from typing import Optional

import typer
from rich.console import Console

from src.models import PersonReport
from src.runner import run_search, print_summary, API_SOURCES, BROWSER_SOURCES, TOOL_SOURCES
from src.aggregator import aggregate

app = typer.Typer(
    name="cc-personresearch",
    help="Person research CLI - gather OSINT data from public sources.",
    no_args_is_help=True,
)

console = Console()


@app.command()
def search(
    name: str = typer.Option(..., "--name", "-n", help="Person's full name"),
    email: Optional[str] = typer.Option(None, "--email", "-e", help="Email address"),
    location: Optional[str] = typer.Option(None, "--location", "-l", help="Location hint"),
    output: Optional[str] = typer.Option(None, "--output", "-o", help="Output JSON file path"),
    workspace: Optional[str] = typer.Option(None, "--workspace", "-w",
                                            help="cc-browser workspace for browsing (auto-detected if omitted)"),
    linkedin_workspace: str = typer.Option("linkedin", "--linkedin-workspace",
                                           help="cc-linkedin workspace (default: linkedin)"),
    api_only: bool = typer.Option(False, "--api-only", help="Only run API sources (no browser)"),
    skip: Optional[str] = typer.Option(None, "--skip", help="Comma-separated source names to skip"),
    verbose: bool = typer.Option(False, "--verbose", "-v", help="Show detailed progress"),
) -> None:
    """Search for a person across multiple public data sources."""
    console.print(f"[bold]Researching:[/bold] {name}")
    if email:
        console.print(f"[bold]Email:[/bold] {email}")
    if location:
        console.print(f"[bold]Location:[/bold] {location}")
    console.print()

    skip_list = [s.strip() for s in skip.split(",")] if skip else []

    report = run_search(
        name=name,
        email=email,
        location=location,
        workspace=workspace,
        linkedin_workspace=linkedin_workspace,
        api_only=api_only,
        skip_sources=skip_list,
        verbose=verbose,
    )

    # Post-process
    report = aggregate(report)

    # Print summary
    console.print()
    print_summary(report)

    # Output JSON
    report_json = report.model_dump_json(indent=2)

    if output:
        out_path = Path(output)
        out_path.write_text(report_json, encoding="utf-8")
        console.print(f"\n[bold green]Report saved:[/bold green] {out_path.resolve()}")
    else:
        console.print(f"\n[bold]JSON Report:[/bold]")
        console.print(report_json)


@app.command()
def sources() -> None:
    """List all available data sources."""
    console.print("[bold]API Sources[/bold] (no browser needed):")
    for cls in API_SOURCES:
        src = cls(person_name="test")
        console.print(f"  - {src.name}")

    console.print("\n[bold]Browser Sources[/bold] (require cc-browser):")
    for cls in BROWSER_SOURCES:
        src = cls(person_name="test")
        console.print(f"  - {src.name}")

    console.print("\n[bold]Tool Sources[/bold] (require cc-linkedin):")
    for cls in TOOL_SOURCES:
        src = cls(person_name="test")
        console.print(f"  - {src.name}")


if __name__ == "__main__":
    app()

"""CLI for cc-comm-queue - Communication Manager Queue Tool."""

import json
import logging
import os
import sys
from pathlib import Path
from typing import List, Optional

import typer
from rich.console import Console
from rich.table import Table

__version__ = "0.1.0"

# Handle imports for both package and frozen executable
try:
    from .schema import (
        ContentItem, ContentType, EmailSpecific, LinkedInSpecific,
        Persona, Platform, RedditSpecific, SendTiming, Status, Visibility,
    )
    from .queue_manager import QueueManager
except ImportError:
    # Running as frozen executable
    from schema import (
        ContentItem, ContentType, EmailSpecific, LinkedInSpecific,
        Persona, Platform, RedditSpecific, SendTiming, Status, Visibility,
    )
    from queue_manager import QueueManager

# Configure logging
logging.basicConfig(level=logging.WARNING, format="%(message)s")
logger = logging.getLogger(__name__)

app = typer.Typer(
    name="cc-comm-queue",
    help="CLI tool for adding content to the Communication Manager approval queue.",
    add_completion=False,
)

config_app = typer.Typer(help="Configuration management")
app.add_typer(config_app, name="config")

console = Console()


def get_config():
    """Get configuration using cc_shared."""
    from cc_shared.config import get_config as get_cc_config
    return get_cc_config()


def get_queue_manager() -> QueueManager:
    """Get a QueueManager instance with configured path."""
    config = get_config()
    queue_path = config.comm_manager.get_queue_path()
    return QueueManager(queue_path)


def version_callback(value: bool) -> None:
    """Print version and exit if --version flag is set."""
    if value:
        console.print(f"cc-comm-queue version {__version__}")
        raise typer.Exit()


@app.callback(invoke_without_command=True)
def main(
    ctx: typer.Context,
    version: bool = typer.Option(
        False,
        "--version",
        "-v",
        callback=version_callback,
        is_eager=True,
        help="Show version and exit",
    ),
):
    """CLI tool for adding content to the Communication Manager approval queue."""
    if ctx.invoked_subcommand is None:
        console.print(ctx.get_help())


@app.command()
def add(
    platform: str = typer.Argument(..., help="Platform: linkedin, twitter, reddit, youtube, email, blog"),
    content_type: str = typer.Argument(..., help="Type: post, comment, reply, message, article, email"),
    content: str = typer.Argument(..., help="The actual content text"),
    persona: str = typer.Option("personal", "--persona", "-p", help="Persona: mindzie, center_consulting, personal"),
    destination: Optional[str] = typer.Option(None, "--destination", "-d", help="Where to post (URL)"),
    context_url: Optional[str] = typer.Option(None, "--context-url", "-c", help="What we're responding to (URL)"),
    context_title: Optional[str] = typer.Option(None, "--context-title", help="Title of content we're responding to"),
    tags: Optional[str] = typer.Option(None, "--tags", "-t", help="Comma-separated tags"),
    notes: Optional[str] = typer.Option(None, "--notes", "-n", help="Notes for reviewer"),
    created_by: Optional[str] = typer.Option(None, "--created-by", help="Agent/tool name that created this"),
    # LinkedIn-specific
    linkedin_visibility: str = typer.Option("public", "--linkedin-visibility", help="LinkedIn visibility: public, connections"),
    # Reddit-specific
    reddit_subreddit: Optional[str] = typer.Option(None, "--reddit-subreddit", help="Target subreddit (e.g., r/processimprovement)"),
    reddit_title: Optional[str] = typer.Option(None, "--reddit-title", help="Reddit post title"),
    # Email-specific
    email_to: Optional[str] = typer.Option(None, "--email-to", help="Recipient email address"),
    email_subject: Optional[str] = typer.Option(None, "--email-subject", help="Email subject line"),
    email_attach: Optional[List[str]] = typer.Option(None, "--email-attach", help="Email attachment file path (can be repeated)"),
    # Dispatch fields
    send_timing: str = typer.Option("asap", "--send-timing", "-st", help="When to send: immediate, scheduled, asap, hold"),
    scheduled_for: Optional[str] = typer.Option(None, "--scheduled-for", help="ISO datetime for scheduled send"),
    send_from: Optional[str] = typer.Option(None, "--send-from", "-sf", help="Account: mindzie, personal, consulting"),
    # Media attachments
    media: Optional[List[str]] = typer.Option(None, "--media", "-m", help="Path to media file (can be repeated)"),
    # Output format
    json_output: bool = typer.Option(False, "--json", help="Output as JSON (for agents)"),
):
    """Add content to the pending_review queue."""
    config = get_config()
    qm = get_queue_manager()

    # Parse platform
    try:
        plat = Platform(platform.lower())
    except ValueError:
        if not json_output:
            console.print(f"[red]ERROR:[/red] Invalid platform: {platform}")
            console.print("Valid platforms: linkedin, twitter, reddit, youtube, email, blog")
        else:
            console.print(json.dumps({"success": False, "error": f"Invalid platform: {platform}"}))
        raise typer.Exit(1)

    # Parse content type
    try:
        ctype = ContentType(content_type.lower())
    except ValueError:
        if not json_output:
            console.print(f"[red]ERROR:[/red] Invalid type: {content_type}")
            console.print("Valid types: post, comment, reply, message, article, email")
        else:
            console.print(json.dumps({"success": False, "error": f"Invalid type: {content_type}"}))
        raise typer.Exit(1)

    # Parse persona
    try:
        pers = Persona(persona.lower())
    except ValueError:
        if not json_output:
            console.print(f"[red]ERROR:[/red] Invalid persona: {persona}")
            console.print("Valid personas: mindzie, center_consulting, personal")
        else:
            console.print(json.dumps({"success": False, "error": f"Invalid persona: {persona}"}))
        raise typer.Exit(1)

    # Parse tags
    tag_list = [t.strip() for t in tags.split(",")] if tags else []

    # Get created_by from config if not provided
    actual_created_by = created_by or config.comm_manager.default_created_by

    # Parse send_timing
    try:
        timing = SendTiming(send_timing.lower())
    except ValueError:
        if not json_output:
            console.print(f"[red]ERROR:[/red] Invalid send_timing: {send_timing}")
            console.print("Valid options: immediate, scheduled, asap, hold")
        else:
            console.print(json.dumps({"success": False, "error": f"Invalid send_timing: {send_timing}"}))
        raise typer.Exit(1)

    # Validate scheduled_for if timing is scheduled
    if timing == SendTiming.SCHEDULED and not scheduled_for:
        if not json_output:
            console.print("[red]ERROR:[/red] --scheduled-for required when send_timing is 'scheduled'")
        else:
            console.print(json.dumps({"success": False, "error": "--scheduled-for required when send_timing is 'scheduled'"}))
        raise typer.Exit(1)

    # Validate send_from if provided
    valid_accounts = ["mindzie", "personal", "consulting"]
    if send_from and send_from.lower() not in valid_accounts:
        if not json_output:
            console.print(f"[red]ERROR:[/red] Invalid send_from: {send_from}")
            console.print(f"Valid accounts: {', '.join(valid_accounts)}")
        else:
            console.print(json.dumps({"success": False, "error": f"Invalid send_from: {send_from}"}))
        raise typer.Exit(1)

    # Build the content item
    item = ContentItem(
        platform=plat,
        type=ctype,
        persona=pers,
        content=content,
        created_by=actual_created_by,
        destination_url=destination,
        context_url=context_url,
        context_title=context_title,
        tags=tag_list,
        notes=notes,
        send_timing=timing,
        scheduled_for=scheduled_for,
        send_from=send_from.lower() if send_from else None,
    )

    # Add platform-specific data
    if plat == Platform.LINKEDIN:
        try:
            vis = Visibility(linkedin_visibility.lower())
        except ValueError:
            vis = Visibility.PUBLIC
        item.linkedin_specific = LinkedInSpecific(visibility=vis)

    elif plat == Platform.REDDIT:
        if reddit_subreddit:
            item.reddit_specific = RedditSpecific(
                subreddit=reddit_subreddit,
                title=reddit_title,
            )

    elif plat == Platform.EMAIL:
        if email_to and email_subject:
            # Validate attachment files exist
            attachment_paths = []
            if email_attach:
                for ap in email_attach:
                    p = Path(ap)
                    if not p.exists():
                        if not json_output:
                            console.print(f"[red]ERROR:[/red] Attachment file not found: {ap}")
                        else:
                            console.print(json.dumps({"success": False, "error": f"Attachment file not found: {ap}"}))
                        raise typer.Exit(1)
                    attachment_paths.append(str(p.resolve()))
            item.email_specific = EmailSpecific(
                to=[email_to],
                subject=email_subject,
                attachments=attachment_paths,
            )

    # Parse media files
    media_files = None
    if media:
        media_files = [Path(m) for m in media]
        # Validate all media files exist
        for mf in media_files:
            if not mf.exists():
                if not json_output:
                    console.print(f"[red]ERROR:[/red] Media file not found: {mf}")
                else:
                    console.print(json.dumps({"success": False, "error": f"Media file not found: {mf}"}))
                raise typer.Exit(1)

    # Add to queue
    result = qm.add_content(item, media_files=media_files)

    if json_output:
        console.print(json.dumps({
            "success": result.success,
            "id": result.id,
            "file": result.file,
            "error": result.error,
        }))
    else:
        if result.success:
            console.print(f"[green]OK:[/green] Content added to queue")
            console.print(f"  ID: {result.id}")
            console.print(f"  File: {result.file}")
        else:
            console.print(f"[red]ERROR:[/red] {result.error}")
            raise typer.Exit(1)


@app.command("add-json")
def add_json(
    file: str = typer.Argument(..., help="JSON file path, or '-' for stdin"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Add content from a JSON file or stdin."""
    qm = get_queue_manager()

    try:
        if file == "-":
            data = json.load(sys.stdin)
        else:
            with open(file, "r", encoding="utf-8") as f:
                data = json.load(f)

        # Validate and create item
        item = ContentItem(**data)
        result = qm.add_content(item)

        if json_output:
            console.print(json.dumps({
                "success": result.success,
                "id": result.id,
                "file": result.file,
                "error": result.error,
            }))
        else:
            if result.success:
                console.print(f"[green]OK:[/green] Content added from JSON")
                console.print(f"  ID: {result.id}")
                console.print(f"  File: {result.file}")
            else:
                console.print(f"[red]ERROR:[/red] {result.error}")
                raise typer.Exit(1)

    except json.JSONDecodeError as e:
        if json_output:
            console.print(json.dumps({"success": False, "error": f"Invalid JSON: {e}"}))
        else:
            console.print(f"[red]ERROR:[/red] Invalid JSON: {e}")
        raise typer.Exit(1)
    except Exception as e:
        if json_output:
            console.print(json.dumps({"success": False, "error": str(e)}))
        else:
            console.print(f"[red]ERROR:[/red] {e}")
        raise typer.Exit(1)


@app.command("list")
def list_content(
    status: Optional[str] = typer.Option(None, "-s", "--status", help="Filter by status: pending, approved, rejected, posted"),
    limit: int = typer.Option(20, "-n", help="Max results"),
):
    """List content items in the queue."""
    qm = get_queue_manager()

    # Parse status
    status_filter = None
    if status:
        status_map = {
            "pending": Status.PENDING_REVIEW,
            "pending_review": Status.PENDING_REVIEW,
            "approved": Status.APPROVED,
            "rejected": Status.REJECTED,
            "posted": Status.POSTED,
        }
        status_filter = status_map.get(status.lower())

    items = qm.list_content(status=status_filter)

    if not items:
        console.print("[yellow]No content items found[/yellow]")
        return

    table = Table(title="Content Queue")
    table.add_column("ID", style="dim", width=10)
    table.add_column("Platform", style="cyan")
    table.add_column("Type")
    table.add_column("Persona")
    table.add_column("Status", style="yellow")
    table.add_column("Content", width=40)

    for item in items[:limit]:
        content_preview = item.get("content", "")[:35]
        if len(item.get("content", "")) > 35:
            content_preview += "..."

        status_style = {
            "pending_review": "[yellow]",
            "approved": "[green]",
            "rejected": "[red]",
            "posted": "[dim]",
        }.get(item.get("status", ""), "")
        status_end = status_style.replace("[", "[/") if status_style else ""

        table.add_row(
            item.get("id", "")[:8],
            item.get("platform", "-"),
            item.get("type", "-"),
            item.get("persona", "-"),
            f"{status_style}{item.get('status', '-')}{status_end}",
            content_preview,
        )

    console.print(table)
    console.print(f"\n[dim]Showing {min(len(items), limit)} of {len(items)} items[/dim]")


@app.command("status")
def status_cmd():
    """Show queue status and counts."""
    qm = get_queue_manager()
    stats = qm.get_stats()

    table = Table(title="Queue Status")
    table.add_column("Status", style="cyan")
    table.add_column("Count", justify="right")

    table.add_row("[yellow]Pending Review[/yellow]", str(stats.pending_review))
    table.add_row("[green]Approved[/green]", str(stats.approved))
    table.add_row("[red]Rejected[/red]", str(stats.rejected))
    table.add_row("[dim]Posted[/dim]", str(stats.posted))
    table.add_row("", "")
    table.add_row("[bold]Total[/bold]", str(stats.pending_review + stats.approved + stats.rejected + stats.posted))

    console.print(table)
    console.print(f"\n[dim]Queue path: {qm.queue_path}[/dim]")


@app.command("show")
def show_content(
    content_id: str = typer.Argument(..., help="Content ID (can be partial)"),
):
    """Show details of a specific content item."""
    qm = get_queue_manager()
    item = qm.get_content_by_id(content_id)

    if not item:
        console.print(f"[red]ERROR:[/red] Content not found: {content_id}")
        raise typer.Exit(1)

    # Header
    console.print(f"\n[bold cyan]{item.get('platform', '')} {item.get('type', '')}[/bold cyan]")
    console.print(f"[dim]ID: {item.get('id', '')}[/dim]\n")

    # Details table
    table = Table(show_header=False, box=None)
    table.add_column("Property", style="cyan", width=15)
    table.add_column("Value")

    table.add_row("Status", item.get("status", "-"))
    table.add_row("Persona", f"{item.get('persona', '-')} ({item.get('persona_display', '-')})")
    table.add_row("Created By", item.get("created_by", "-"))
    table.add_row("Created At", item.get("created_at", "-"))

    if item.get("destination_url"):
        table.add_row("Destination", item["destination_url"])
    if item.get("context_url"):
        table.add_row("Context URL", item["context_url"])
    if item.get("tags"):
        table.add_row("Tags", ", ".join(item["tags"]))
    if item.get("notes"):
        table.add_row("Notes", item["notes"])

    console.print(table)

    # Content
    console.print(f"\n[cyan]Content:[/cyan]\n{item.get('content', '')}")

    # File path
    if item.get("_file_path"):
        console.print(f"\n[dim]File: {item['_file_path']}[/dim]")


# =============================================================================
# Config Commands
# =============================================================================

@config_app.command("show")
def config_show():
    """Show current configuration."""
    config = get_config()

    table = Table(title="Communication Manager Configuration")
    table.add_column("Setting", style="cyan")
    table.add_column("Value")

    table.add_row("Queue Path", config.comm_manager.queue_path)
    table.add_row("Default Persona", config.comm_manager.default_persona)
    table.add_row("Default Created By", config.comm_manager.default_created_by)

    console.print(table)


@config_app.command("set")
def config_set(
    key: str = typer.Argument(..., help="Config key: queue_path, default_persona, default_created_by"),
    value: str = typer.Argument(..., help="Config value"),
):
    """Set a configuration value."""
    config_path = Path.home() / ".cc_tools" / "config.json"

    # Load existing config
    if config_path.exists():
        with open(config_path, "r", encoding="utf-8") as f:
            data = json.load(f)
    else:
        data = {}

    # Ensure comm_manager section exists
    if "comm_manager" not in data:
        data["comm_manager"] = {}

    # Set the value
    if key in ["queue_path", "default_persona", "default_created_by"]:
        data["comm_manager"][key] = value
    else:
        console.print(f"[red]ERROR:[/red] Unknown config key: {key}")
        console.print("Valid keys: queue_path, default_persona, default_created_by")
        raise typer.Exit(1)

    # Save config
    config_path.parent.mkdir(parents=True, exist_ok=True)
    with open(config_path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)

    console.print(f"[green]OK:[/green] Set {key} = {value}")


@app.command("migrate")
def migrate_json(
    delete: bool = typer.Option(False, "--delete", help="Delete JSON files after successful migration"),
):
    """Migrate existing JSON files to SQLite database."""
    config = get_config()
    queue_path = Path(config.comm_manager.queue_path)

    console.print(f"[cyan]Migrating JSON files from:[/cyan] {queue_path}")

    try:
        from migrate import migrate_json_to_sqlite
    except ImportError:
        from .migrate import migrate_json_to_sqlite

    stats = migrate_json_to_sqlite(queue_path, backup=True, delete_json=delete)

    if stats["total_migrated"] > 0:
        console.print(f"\n[green]OK:[/green] Migrated {stats['total_migrated']} items to SQLite database")
    else:
        console.print("\n[yellow]No items to migrate[/yellow]")

    if stats["total_errors"] > 0:
        console.print(f"[red]Errors:[/red] {stats['total_errors']} items failed to migrate")
        raise typer.Exit(1)


if __name__ == "__main__":
    app()

"""CLI for cc-vault - Personal Vault from the command line."""

import json
import logging
import sqlite3
import os
import sys
import zipfile

os.environ.setdefault("PYTHONIOENCODING", "utf-8")

# Fix Windows console encoding for non-ASCII characters (e.g. Turkish names)
if sys.stdout.encoding and sys.stdout.encoding.lower() != 'utf-8':
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    sys.stderr.reconfigure(encoding='utf-8', errors='replace')
from datetime import datetime
from pathlib import Path
from types import ModuleType
from typing import Any, Optional, List

import typer
from rich.console import Console
from rich.table import Table
from rich.panel import Panel
from rich.text import Text

# Configure logger
logger = logging.getLogger(__name__)

# Handle imports for both package and frozen executable
try:
    from . import __version__
    from .config import (
        VAULT_PATH, DB_PATH, VECTORS_PATH, DOCUMENTS_PATH,
        get_vault_path, get_config
    )
except ImportError:
    # Running as frozen executable
    import __init__ as pkg
    __version__ = pkg.__version__
    from config import (
        VAULT_PATH, DB_PATH, VECTORS_PATH, DOCUMENTS_PATH,
        get_vault_path, get_config
    )

# Configure logging
logging.basicConfig(level=logging.INFO, format="%(message)s")

app = typer.Typer(
    name="cc-vault",
    help="Personal Vault CLI: manage contacts, tasks, goals, ideas, documents, and more.",
    add_completion=False,
)

# Sub-apps
tasks_app = typer.Typer(help="Task management")
goals_app = typer.Typer(help="Goal tracking")
ideas_app = typer.Typer(help="Idea capture")
contacts_app = typer.Typer(help="Contact management")
docs_app = typer.Typer(help="Document management")
config_app = typer.Typer(help="Configuration")
health_app = typer.Typer(help="Health data")
posts_app = typer.Typer(help="Social media posts")
lists_app = typer.Typer(help="Contact list management")

app.add_typer(tasks_app, name="tasks")
app.add_typer(goals_app, name="goals")
app.add_typer(ideas_app, name="ideas")
app.add_typer(contacts_app, name="contacts")
app.add_typer(docs_app, name="docs")
app.add_typer(config_app, name="config")
app.add_typer(health_app, name="health")
app.add_typer(posts_app, name="posts")
app.add_typer(lists_app, name="lists")

console = Console(force_terminal=True)


def version_callback(value: bool) -> None:
    """Print version and exit if --version flag is set."""
    if value:
        console.print(f"cc-vault version {__version__}")
        raise typer.Exit()


def get_db() -> ModuleType:
    """Get initialized database module."""
    try:
        from . import db
    except ImportError:
        import db
    db.init_db(silent=True)
    return db


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
    """Personal Vault CLI: manage contacts, tasks, goals, ideas, and documents."""
    if ctx.invoked_subcommand is None:
        console.print(ctx.get_help())


# =============================================================================
# Main Commands
# =============================================================================

@app.command()
def init(
    path: Optional[str] = typer.Argument(None, help="Vault path (default: ~/Vault)"),
    force: bool = typer.Option(False, "--force", "-f", help="Reinitialize if exists"),
):
    """Initialize a new vault."""
    try:
        from .config import ensure_directories
    except ImportError:
        from config import ensure_directories

    vault_path = Path(path) if path else VAULT_PATH

    if vault_path.exists() and not force:
        console.print(f"[yellow]Vault already exists at:[/yellow] {vault_path}")
        console.print("Use --force to reinitialize.")
        return

    try:
        ensure_directories()
        db = get_db()
        console.print(f"[green]Vault initialized at:[/green] {vault_path}")
        console.print(f"  Database: {DB_PATH}")
        console.print(f"  Documents: {DOCUMENTS_PATH}")
    except (OSError, sqlite3.Error) as e:
        console.print(f"[red]Error initializing vault:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def stats() -> None:
    """Show vault statistics."""
    db = get_db()

    try:
        stats_data = db.get_vault_stats()

        table = Table(title=f"Vault Statistics")
        table.add_column("Category", style="cyan")
        table.add_column("Count", justify="right")

        table.add_row("Contacts", str(stats_data.get('contacts', 0)))
        table.add_row("Tasks (pending)", str(stats_data.get('tasks_pending', 0)))
        table.add_row("Tasks (completed)", str(stats_data.get('tasks_completed', 0)))
        table.add_row("Goals (active)", str(stats_data.get('goals_active', 0)))
        table.add_row("Ideas", str(stats_data.get('ideas', 0)))
        table.add_row("Documents", str(stats_data.get('documents', 0)))
        table.add_row("Health Entries", str(stats_data.get('health_entries', 0)))
        if stats_data.get('social_posts', 0) > 0:
            table.add_row("Social Posts", str(stats_data.get('social_posts', 0)))
            table.add_row("  - Draft", str(stats_data.get('social_posts_draft', 0)))
            table.add_row("  - Posted", str(stats_data.get('social_posts_posted', 0)))

        console.print(table)
        console.print(f"\n[dim]Vault path: {VAULT_PATH}[/dim]")

    except sqlite3.Error as e:
        console.print(f"[red]Error getting stats:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def ask(
    question: str = typer.Argument(..., help="Question to ask the vault"),
    model: str = typer.Option("gpt-4o", "-m", "--model", help="OpenAI model to use"),
    no_hybrid: bool = typer.Option(False, "--no-hybrid", help="Disable hybrid search"),
) -> None:
    """Ask a question using RAG (Retrieval Augmented Generation)."""
    try:
        from .rag import get_vault_rag
    except ImportError:
        from rag import get_vault_rag

    rag = get_vault_rag()

    console.print(f"[dim]Searching vault...[/dim]")
    result = rag.ask(question, model=model, use_hybrid=not no_hybrid)

    if 'error' in result:
        console.print(f"[red]Error:[/red] {result['error']}")
        raise typer.Exit(1)

    console.print(f"\n[cyan]Answer:[/cyan]\n{result['answer']}")
    console.print(f"\n[dim]Sources: {result['context_used']} items, Mode: {result.get('search_mode', 'unknown')}[/dim]")


@app.command("search")
def search_cmd(
    query: str = typer.Argument(..., help="Search query"),
    n: int = typer.Option(10, "-n", help="Number of results"),
    hybrid: bool = typer.Option(False, "--hybrid", help="Use hybrid search"),
):
    """Search the vault using semantic or hybrid search."""
    try:
        from .rag import get_vault_rag
    except ImportError:
        from rag import get_vault_rag

    rag = get_vault_rag()

    if hybrid:
        results = rag.hybrid_search(query, n_results=n)

        if not results:
            console.print("[yellow]No results found[/yellow]")
            return

        console.print(f"\n[cyan]Hybrid Search Results ({len(results)} chunks):[/cyan]\n")
        for r in results:
            meta = r.get('metadata', {})
            doc_title = meta.get('doc_title', 'Unknown')
            lines = f"lines {meta.get('start_line', '?')}-{meta.get('end_line', '?')}"
            content = r.get('content', '')[:150]
            combined = r.get('combined_score', 0)
            console.print(f"  [{doc_title}, {lines}] score={combined:.3f}")
            console.print(f"    {content}...")
            console.print()
    else:
        results = rag.semantic_search(query, n_results=n)

        for coll, items in results.items():
            if items:
                console.print(f"\n[cyan]{coll.upper()} ({len(items)} results):[/cyan]")
                for item in items[:5]:
                    doc = item.get('document', '')[:100]
                    console.print(f"  [{item['id']}] {doc}...")


@app.command()
def backup(
    destination: Optional[Path] = typer.Argument(None, help="Directory where the backup zip will be saved"),
    list_backups: bool = typer.Option(False, "--list", help="List available backups"),
):
    """Create a full zip backup of the entire vault directory."""
    if list_backups:
        try:
            from .config import BACKUPS_PATH
        except ImportError:
            from config import BACKUPS_PATH

        if not BACKUPS_PATH.exists():
            console.print("[yellow]No backups directory found[/yellow]")
            return

        backups = sorted(BACKUPS_PATH.glob("vault_backup_*.zip"), reverse=True)
        # Also check the destination if different
        if not backups:
            # Check vault path for any zips
            backups = sorted(VAULT_PATH.parent.glob("vault_backup_*.zip"), reverse=True)

        if not backups:
            console.print("[yellow]No backups found[/yellow]")
            return

        table = Table(title="Available Backups")
        table.add_column("File", style="cyan")
        table.add_column("Size", justify="right")
        table.add_column("Date", style="dim")

        for bp in backups:
            size_mb = bp.stat().st_size / (1024 * 1024)
            mod_time = datetime.fromtimestamp(bp.stat().st_mtime).strftime('%Y-%m-%d %H:%M')
            table.add_row(str(bp), f"{size_mb:.1f} MB", mod_time)

        console.print(table)
        return

    if destination is None:
        console.print("[red]Error:[/red] Provide a destination directory, or use --list to see backups")
        raise typer.Exit(1)

    dest = Path(destination)
    if not dest.is_dir():
        console.print(f"[red]Error:[/red] Destination directory does not exist: {dest}")
        raise typer.Exit(1)

    timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
    zip_path = dest / f'vault_backup_{timestamp}.zip'

    file_count = 0
    try:
        with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED) as zf:
            for file in VAULT_PATH.rglob('*'):
                # Skip the backups subdirectory
                try:
                    file.relative_to(VAULT_PATH / 'backups')
                    continue
                except ValueError:
                    pass

                if file.is_file():
                    arcname = file.relative_to(VAULT_PATH)
                    zf.write(file, arcname)
                    file_count += 1

        size_mb = zip_path.stat().st_size / (1024 * 1024)
        console.print(f"[green]Backup created:[/green] {zip_path}")
        console.print(f"  Files: {file_count}")
        console.print(f"  Size:  {size_mb:.1f} MB")
    except OSError as e:
        console.print(f"[red]Error creating backup:[/red] {e}")
        raise typer.Exit(1)


@app.command("repair-vectors")
def repair_vectors():
    """Delete and rebuild the vector index from SQLite chunks."""
    console.print("[dim]Repairing vector index...[/dim]")

    try:
        try:
            from .vectors import VaultVectors
            from .db import get_db as get_db_conn, init_db
        except ImportError:
            from vectors import VaultVectors
            from db import get_db as get_db_conn, init_db

        init_db(silent=True)

        # Step 1: Clear all vec_embeddings
        conn = get_db_conn()
        try:
            conn.execute("DELETE FROM vec_embeddings")
            conn.commit()
            console.print("  Cleared vector embeddings table")
        finally:
            conn.close()

        # Step 2: Re-index all chunks
        vecs = VaultVectors()

        conn = get_db_conn()
        try:
            cursor = conn.execute("""
                SELECT c.id, c.document_id, c.content, c.content_hash,
                       c.start_line, c.end_line, c.chunk_index,
                       d.title as doc_title, d.path as doc_path, d.doc_type
                FROM chunks c
                JOIN documents d ON c.document_id = d.id
                WHERE c.content IS NOT NULL AND LENGTH(TRIM(c.content)) > 0
                ORDER BY c.document_id, c.chunk_index
            """)
            chunks = cursor.fetchall()
        finally:
            conn.close()

        if not chunks:
            console.print("[yellow]No chunks found in database to index[/yellow]")
            return

        # Index in batches
        batch_size = 50
        indexed = 0
        batch = []

        for chunk in chunks:
            chunk_meta = {
                'document_id': chunk['document_id'],
                'doc_title': chunk['doc_title'] or '',
                'doc_path': chunk['doc_path'] or '',
                'doc_type': chunk['doc_type'] or '',
                'start_line': chunk['start_line'] or 0,
                'end_line': chunk['end_line'] or 0,
                'chunk_index': chunk['chunk_index'] or 0
            }
            batch.append({
                'id': f"chunk_{chunk['id']}",
                'content': chunk['content'],
                'metadata': chunk_meta
            })

            if len(batch) >= batch_size:
                vecs.add_chunks_batch(batch)
                indexed += len(batch)
                console.print(f"  Indexed {indexed}/{len(chunks)} chunks...")
                batch = []

        # Final batch
        if batch:
            vecs.add_chunks_batch(batch)
            indexed += len(batch)

        # Update vector IDs in SQLite
        conn = get_db_conn()
        try:
            for chunk in chunks:
                conn.execute(
                    "UPDATE chunks SET vector_id = ? WHERE id = ?",
                    (f"chunk_{chunk['id']}", chunk['id'])
                )
            conn.commit()
        finally:
            conn.close()

        # Log migration hint if old vectors directory exists
        if VECTORS_PATH.exists():
            console.print(f"[dim]NOTE: Old vectors/ directory still exists at {VECTORS_PATH}[/dim]")
            console.print("[dim]It is no longer needed and can be deleted.[/dim]")

        console.print(f"[green]Repair complete:[/green] {indexed} chunks indexed")

    except RuntimeError as e:
        console.print(f"[red]Error during repair:[/red] {e}")
        raise typer.Exit(1)


@app.command()
def restore(
    zip_file: Path = typer.Argument(..., help="Path to vault backup zip file"),
):
    """Restore the vault from a backup zip file."""
    zip_path = Path(zip_file)

    if not zip_path.exists():
        console.print(f"[red]Error:[/red] File not found: {zip_path}")
        raise typer.Exit(1)

    if not zip_path.suffix == '.zip':
        console.print(f"[red]Error:[/red] Not a zip file: {zip_path}")
        raise typer.Exit(1)

    # Validate zip contains vault.db
    try:
        with zipfile.ZipFile(zip_path, 'r') as zf:
            names = zf.namelist()
            if 'vault.db' not in names:
                console.print("[red]Error:[/red] Backup does not contain vault.db - not a valid vault backup")
                raise typer.Exit(1)
    except zipfile.BadZipFile:
        console.print(f"[red]Error:[/red] Corrupt zip file: {zip_path}")
        raise typer.Exit(1)

    # Create safety backup of current vault first
    try:
        from .config import BACKUPS_PATH
    except ImportError:
        from config import BACKUPS_PATH

    BACKUPS_PATH.mkdir(parents=True, exist_ok=True)
    timestamp = datetime.now().strftime('%Y%m%d_%H%M%S')
    safety_path = BACKUPS_PATH / f'vault_backup_pre_restore_{timestamp}.zip'

    console.print("[dim]Creating safety backup of current vault...[/dim]")
    file_count = 0
    try:
        with zipfile.ZipFile(safety_path, 'w', zipfile.ZIP_DEFLATED) as zf:
            for file in VAULT_PATH.rglob('*'):
                try:
                    file.relative_to(VAULT_PATH / 'backups')
                    continue
                except ValueError:
                    pass
                if file.is_file():
                    arcname = file.relative_to(VAULT_PATH)
                    zf.write(file, arcname)
                    file_count += 1
        console.print(f"  Safety backup: {safety_path} ({file_count} files)")
    except OSError as e:
        console.print(f"[red]Error creating safety backup:[/red] {e}")
        raise typer.Exit(1)

    # Extract backup to vault path
    console.print(f"[dim]Restoring from: {zip_path}[/dim]")
    try:
        with zipfile.ZipFile(zip_path, 'r') as zf:
            zf.extractall(VAULT_PATH)
            restored_count = len(zf.namelist())
    except (zipfile.BadZipFile, OSError) as e:
        console.print(f"[red]Error extracting backup:[/red] {e}")
        console.print(f"[yellow]Safety backup available at:[/yellow] {safety_path}")
        raise typer.Exit(1)

    # Run schema migrations
    try:
        try:
            from .db import init_db
        except ImportError:
            from db import init_db
        init_db(silent=True)
    except Exception as e:
        console.print(f"[yellow]Warning:[/yellow] Schema migration issue: {e}")

    console.print(f"[green]Restore complete:[/green] {restored_count} files restored")
    console.print(f"  Safety backup: {safety_path}")
    console.print("[dim]Run 'cc-vault repair-vectors' if vector search doesn't work[/dim]")


# =============================================================================
# Tasks Commands
# =============================================================================

@tasks_app.command("list")
def tasks_list(
    status: str = typer.Option("pending", "-s", "--status", help="Filter by status: pending, completed, all"),
    contact_id: Optional[int] = typer.Option(None, "-c", "--contact", help="Filter by contact"),
    sort: str = typer.Option("priority", "--sort", help="Sort: priority, newest, due"),
    limit: int = typer.Option(20, "-n", help="Max results"),
):
    """List tasks."""
    db = get_db()

    try:
        if status == "all":
            tasks = db.list_tasks(status=None, contact_id=contact_id, limit=limit, sort=sort)
        else:
            tasks = db.list_tasks(status=status, contact_id=contact_id, limit=limit, sort=sort)

        if not tasks:
            console.print(f"[yellow]No {status} tasks found[/yellow]")
            return

        table = Table(title=f"Tasks ({status})")
        table.add_column("ID", style="dim")
        table.add_column("Task", style="cyan")
        table.add_column("Due", style="yellow")
        table.add_column("Priority")
        table.add_column("Contact")

        for task in tasks:
            # Convert numeric priority to label
            priority_num = task.get('priority', 3)
            if isinstance(priority_num, int):
                if priority_num <= 2:
                    priority_label = 'high'
                elif priority_num >= 4:
                    priority_label = 'low'
                else:
                    priority_label = 'medium'
            else:
                priority_label = str(priority_num)

            priority_style = {'high': '[red]', 'low': '[dim]'}.get(priority_label, '')
            priority_end = '[/red]' if priority_label == 'high' else '[/dim]' if priority_label == 'low' else ''

            table.add_row(
                str(task['id']),
                task['title'][:50],
                task.get('due_date', '-')[:10] if task.get('due_date') else '-',
                f"{priority_style}{priority_label}{priority_end}",
                task.get('contact_name', '-') or '-',
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error listing tasks:[/red] {e}")
        raise typer.Exit(1)


@tasks_app.command("add")
def tasks_add(
    title: str = typer.Argument(..., help="Task title"),
    due: Optional[str] = typer.Option(None, "-d", "--due", help="Due date (YYYY-MM-DD)"),
    priority: str = typer.Option("medium", "-p", "--priority", help="Priority: low, medium, high (or 1-5)"),
    contact_id: Optional[int] = typer.Option(None, "-c", "--contact", help="Link to contact"),
    goal_id: Optional[int] = typer.Option(None, "-g", "--goal", help="Link to goal"),
    description: Optional[str] = typer.Option(None, "-n", "--notes", help="Task description"),
):
    """Add a new task."""
    db = get_db()

    # Convert priority string to int (1-5)
    priority_map = {'high': 1, 'medium': 3, 'low': 5}
    if priority.lower() in priority_map:
        priority_int = priority_map[priority.lower()]
    else:
        try:
            priority_int = int(priority)
        except ValueError:
            console.print(f"[red]Error:[/red] Invalid priority. Use low, medium, high, or 1-5")
            raise typer.Exit(1)

    try:
        task_id = db.add_task(
            title=title,
            due_date=due,
            priority=priority_int,
            description=description,
            contact_id=contact_id,
            goal_id=goal_id,
        )
        console.print(f"[green]Task added:[/green] #{task_id} - {title}")

    except sqlite3.Error as e:
        console.print(f"[red]Error adding task:[/red] {e}")
        raise typer.Exit(1)


@tasks_app.command("done")
def tasks_done(
    task_id: int = typer.Argument(..., help="Task ID to complete"),
):
    """Mark a task as completed."""
    db = get_db()

    try:
        db.complete_task(task_id)
        console.print(f"[green]Task #{task_id} marked as completed[/green]")

    except sqlite3.Error as e:
        console.print(f"[red]Error completing task:[/red] {e}")
        raise typer.Exit(1)


@tasks_app.command("cancel")
def tasks_cancel(
    task_id: int = typer.Argument(..., help="Task ID to cancel"),
):
    """Cancel a task."""
    db = get_db()

    try:
        db.update_task(task_id, status='cancelled')
        console.print(f"[yellow]Task #{task_id} cancelled[/yellow]")

    except sqlite3.Error as e:
        console.print(f"[red]Error cancelling task:[/red] {e}")
        raise typer.Exit(1)


@tasks_app.command("show")
def tasks_show(
    task_id: int = typer.Argument(..., help="Task ID"),
):
    """Show full details of a task."""
    db = get_db()

    try:
        task = db.get_task(task_id)

        if not task:
            console.print(f"[red]Task #{task_id} not found[/red]")
            raise typer.Exit(1)

        # Convert numeric priority to label
        priority_num = task.get('priority', 3)
        if isinstance(priority_num, int):
            if priority_num <= 2:
                priority_label = 'high'
            elif priority_num >= 4:
                priority_label = 'low'
            else:
                priority_label = 'medium'
        else:
            priority_label = str(priority_num)

        # Build detail lines
        lines = []
        lines.append(f"[bold]Status:[/bold] {task.get('status', 'pending')}")
        lines.append(f"[bold]Priority:[/bold] {priority_label} ({priority_num})")
        if task.get('due_date'):
            lines.append(f"[bold]Due:[/bold] {task['due_date'][:10]}")
        if task.get('context'):
            lines.append(f"[bold]Context:[/bold] {task['context']}")
        if task.get('contact_name'):
            contact_info = task['contact_name']
            if task.get('contact_email'):
                contact_info += f" ({task['contact_email']})"
            lines.append(f"[bold]Contact:[/bold] {contact_info}")
        if task.get('goal_title'):
            lines.append(f"[bold]Goal:[/bold] {task['goal_title']}")
        lines.append(f"[bold]Created:[/bold] {task.get('created_at', '-')}")
        if task.get('completed_at'):
            lines.append(f"[bold]Completed:[/bold] {task['completed_at']}")
        if task.get('description'):
            lines.append("")
            lines.append("[bold]Description:[/bold]")
            lines.append(task['description'])

        console.print(Panel("\n".join(lines), title=f"Task #{task_id}: {task['title']}"))

    except sqlite3.Error as e:
        console.print(f"[red]Error showing task:[/red] {e}")
        raise typer.Exit(1)


@tasks_app.command("update")
def tasks_update(
    task_id: int = typer.Argument(..., help="Task ID"),
    title: Optional[str] = typer.Option(None, "--title", help="New title"),
    description: Optional[str] = typer.Option(None, "-d", "--description", help="New description"),
    priority: Optional[str] = typer.Option(None, "-p", "--priority", help="Priority: low, medium, high (or 1-5)"),
    due: Optional[str] = typer.Option(None, "--due", help="Due date (YYYY-MM-DD)"),
    context: Optional[str] = typer.Option(None, "--context", help="Context tag"),
    contact_id: Optional[int] = typer.Option(None, "-c", "--contact", help="Link to contact ID (0 to unlink)"),
    goal_id: Optional[int] = typer.Option(None, "-g", "--goal", help="Link to goal ID (0 to unlink)"),
):
    """Update a task's details."""
    db = get_db()

    # Convert priority string to int if provided
    priority_int = None
    if priority is not None:
        priority_map = {'high': 1, 'medium': 3, 'low': 5}
        if priority.lower() in priority_map:
            priority_int = priority_map[priority.lower()]
        else:
            try:
                priority_int = int(priority)
            except ValueError:
                console.print(f"[red]Error:[/red] Invalid priority. Use low, medium, high, or 1-5")
                raise typer.Exit(1)

    try:
        success = db.update_task(
            task_id,
            title=title,
            description=description,
            priority=priority_int,
            due_date=due,
            context=context,
            contact_id=contact_id,
            goal_id=goal_id,
        )
        if success:
            console.print(f"[green]Task #{task_id} updated[/green]")
        else:
            console.print(f"[red]Task #{task_id} not found[/red]")
            raise typer.Exit(1)

    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error updating task:[/red] {e}")
        raise typer.Exit(1)


@tasks_app.command("search")
def tasks_search(
    query: str = typer.Argument(..., help="Search query"),
):
    """Search tasks by title, description, or context."""
    db = get_db()

    try:
        tasks = db.search_tasks(query)

        if not tasks:
            console.print(f"[yellow]No tasks found matching '{query}'[/yellow]")
            return

        console.print(f"[bold]Search results for '{query}':[/bold] {len(tasks)} found\n")

        table = Table()
        table.add_column("ID", style="dim")
        table.add_column("Task", style="cyan")
        table.add_column("Status")
        table.add_column("Due", style="yellow")
        table.add_column("Priority")
        table.add_column("Contact")

        for task in tasks:
            # Convert numeric priority to label
            priority_num = task.get('priority', 3)
            if isinstance(priority_num, int):
                if priority_num <= 2:
                    priority_label = 'high'
                elif priority_num >= 4:
                    priority_label = 'low'
                else:
                    priority_label = 'medium'
            else:
                priority_label = str(priority_num)

            priority_style = {'high': '[red]', 'low': '[dim]'}.get(priority_label, '')
            priority_end = '[/red]' if priority_label == 'high' else '[/dim]' if priority_label == 'low' else ''

            table.add_row(
                str(task['id']),
                task['title'][:50],
                task.get('status', 'pending'),
                task.get('due_date', '-')[:10] if task.get('due_date') else '-',
                f"{priority_style}{priority_label}{priority_end}",
                task.get('contact_name', '-') or '-',
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error searching tasks:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Goals Commands
# =============================================================================

@goals_app.command("list")
def goals_list(
    status: str = typer.Option("active", "-s", "--status", help="Filter: active, achieved, paused, all"),
    category: Optional[str] = typer.Option(None, "-c", "--category", help="Filter by category"),
    timeframe: Optional[str] = typer.Option(None, "-t", "--timeframe", help="Filter by timeframe"),
):
    """List goals."""
    db = get_db()

    try:
        if status == "all":
            goals = db.list_goals(status=None, category=category, timeframe=timeframe, include_achieved=True)
        else:
            goals = db.list_goals(status=status, category=category, timeframe=timeframe)

        if not goals:
            console.print(f"[yellow]No {status} goals found[/yellow]")
            return

        table = Table(title=f"Goals ({status})")
        table.add_column("ID", style="dim")
        table.add_column("Goal", style="cyan")
        table.add_column("Target", style="yellow")
        table.add_column("Progress")
        table.add_column("Status")

        for goal in goals:
            progress = goal.get('progress', 0) or 0
            progress_bar = f"[{'=' * int(progress / 10)}{' ' * (10 - int(progress / 10))}] {progress}%"

            table.add_row(
                str(goal['id']),
                goal['title'][:40],
                goal.get('target_date', '-')[:10] if goal.get('target_date') else '-',
                progress_bar,
                goal.get('status', 'active'),
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error listing goals:[/red] {e}")
        raise typer.Exit(1)


@goals_app.command("add")
def goals_add(
    title: str = typer.Argument(..., help="Goal title"),
    target: Optional[str] = typer.Option(None, "-t", "--target", help="Target date (YYYY-MM-DD)"),
    description: Optional[str] = typer.Option(None, "-d", "--description", help="Goal description"),
    category: Optional[str] = typer.Option(None, "-c", "--category", help="Goal category"),
):
    """Add a new goal."""
    db = get_db()

    try:
        goal_id = db.add_goal(
            title=title,
            target_date=target,
            description=description,
            category=category,
        )
        console.print(f"[green]Goal added:[/green] #{goal_id} - {title}")

    except sqlite3.Error as e:
        console.print(f"[red]Error adding goal:[/red] {e}")
        raise typer.Exit(1)


@goals_app.command("achieve")
def goals_achieve(
    goal_id: int = typer.Argument(..., help="Goal ID to mark as achieved"),
):
    """Mark a goal as achieved."""
    db = get_db()

    try:
        db.achieve_goal(goal_id)
        console.print(f"[green]Goal #{goal_id} marked as achieved![/green]")

    except sqlite3.Error as e:
        console.print(f"[red]Error updating goal:[/red] {e}")
        raise typer.Exit(1)


@goals_app.command("pause")
def goals_pause(
    goal_id: int = typer.Argument(..., help="Goal ID to pause"),
):
    """Pause a goal."""
    db = get_db()

    try:
        db.pause_goal(goal_id)
        console.print(f"[yellow]Goal #{goal_id} paused[/yellow]")

    except sqlite3.Error as e:
        console.print(f"[red]Error pausing goal:[/red] {e}")
        raise typer.Exit(1)


@goals_app.command("resume")
def goals_resume(
    goal_id: int = typer.Argument(..., help="Goal ID to resume"),
):
    """Resume a paused goal."""
    db = get_db()

    try:
        db.resume_goal(goal_id)
        console.print(f"[green]Goal #{goal_id} resumed[/green]")

    except sqlite3.Error as e:
        console.print(f"[red]Error resuming goal:[/red] {e}")
        raise typer.Exit(1)


@goals_app.command("update")
def goals_update(
    goal_id: int = typer.Argument(..., help="Goal ID"),
    title: Optional[str] = typer.Option(None, "--title", help="New title"),
    description: Optional[str] = typer.Option(None, "-d", "--description", help="New description"),
    category: Optional[str] = typer.Option(None, "-c", "--category", help="New category"),
    target: Optional[str] = typer.Option(None, "-t", "--target", help="New target date (YYYY-MM-DD)"),
):
    """Update a goal's details."""
    db = get_db()

    try:
        db.update_goal(
            goal_id,
            title=title,
            description=description,
            category=category,
            target_date=target,
        )
        console.print(f"[green]Goal #{goal_id} updated[/green]")

    except sqlite3.Error as e:
        console.print(f"[red]Error updating goal:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Ideas Commands
# =============================================================================

@ideas_app.command("list")
def ideas_list(
    status: str = typer.Option("new", "-s", "--status", help="Filter: new, actionable, archived, all"),
    domain: Optional[str] = typer.Option(None, "-d", "--domain", help="Filter by domain"),
    limit: int = typer.Option(20, "-n", help="Max results"),
):
    """List ideas."""
    db = get_db()

    try:
        if status == "all":
            ideas = db.list_ideas(status=None, domain=domain, limit=limit)
        else:
            ideas = db.list_ideas(status=status, domain=domain, limit=limit)

        if not ideas:
            console.print(f"[yellow]No {status} ideas found[/yellow]")
            return

        table = Table(title=f"Ideas ({status})")
        table.add_column("ID", style="dim")
        table.add_column("Idea", style="cyan")
        table.add_column("Domain")
        table.add_column("Created")
        table.add_column("Status")

        for idea in ideas:
            table.add_row(
                str(idea['id']),
                idea['content'][:50],
                idea.get('domain', '-') or '-',
                idea.get('created_at', '')[:10],
                idea.get('status', 'new'),
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error listing ideas:[/red] {e}")
        raise typer.Exit(1)


@ideas_app.command("add")
def ideas_add(
    content: str = typer.Argument(..., help="Idea content"),
    tags: Optional[str] = typer.Option(None, "-t", "--tags", help="Tags (comma-separated)"),
    domain: Optional[str] = typer.Option(None, "-d", "--domain", help="Domain/category"),
    goal_id: Optional[int] = typer.Option(None, "-g", "--goal", help="Link to goal"),
):
    """Add a new idea."""
    db = get_db()

    try:
        idea_id = db.add_idea(
            content=content,
            tags=tags,
            domain=domain,
            goal_id=goal_id,
        )
        console.print(f"[green]Idea added:[/green] #{idea_id}")

    except sqlite3.Error as e:
        console.print(f"[red]Error adding idea:[/red] {e}")
        raise typer.Exit(1)


@ideas_app.command("actionable")
def ideas_actionable(
    idea_id: int = typer.Argument(..., help="Idea ID"),
):
    """Mark an idea as actionable."""
    db = get_db()

    try:
        db.update_idea_status(idea_id, 'actionable')
        console.print(f"[green]Idea #{idea_id} marked as actionable[/green]")

    except sqlite3.Error as e:
        console.print(f"[red]Error updating idea:[/red] {e}")
        raise typer.Exit(1)


@ideas_app.command("archive")
def ideas_archive(
    idea_id: int = typer.Argument(..., help="Idea ID"),
):
    """Archive an idea."""
    db = get_db()

    try:
        db.update_idea_status(idea_id, 'archived')
        console.print(f"[yellow]Idea #{idea_id} archived[/yellow]")

    except sqlite3.Error as e:
        console.print(f"[red]Error archiving idea:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Social Posts Commands
# =============================================================================

@posts_app.command("list")
def posts_list(
    platform: Optional[str] = typer.Option(None, "-p", "--platform", help="Filter: linkedin, twitter, reddit, other"),
    status: Optional[str] = typer.Option(None, "-s", "--status", help="Filter: draft, scheduled, posted"),
    limit: int = typer.Option(50, "-n", help="Max results"),
):
    """List social media posts."""
    db = get_db()

    try:
        posts = db.list_social_posts(platform=platform, status=status, limit=limit)

        if not posts:
            console.print("[yellow]No posts found[/yellow]")
            return

        table = Table(title="Social Posts")
        table.add_column("ID", style="dim")
        table.add_column("Platform", style="cyan")
        table.add_column("Status")
        table.add_column("Content")
        table.add_column("Audience")
        table.add_column("Created")

        platform_colors = {'linkedin': 'blue', 'twitter': 'cyan', 'reddit': 'red', 'other': 'white'}
        status_colors = {'draft': 'yellow', 'scheduled': 'magenta', 'posted': 'green'}

        for post in posts:
            platform_val = post.get('platform', 'other')
            status_val = post.get('status', 'draft')
            content = post.get('content', '')[:40]
            if len(post.get('content', '')) > 40:
                content += '...'

            table.add_row(
                str(post['id']),
                f"[{platform_colors.get(platform_val, 'white')}]{platform_val}[/]",
                f"[{status_colors.get(status_val, 'white')}]{status_val}[/]",
                content.replace('\n', ' '),
                post.get('audience', '-') or '-',
                post.get('created_at', '')[:10],
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error listing posts:[/red] {e}")
        raise typer.Exit(1)


@posts_app.command("add")
def posts_add(
    content: str = typer.Argument(..., help="Post content"),
    platform: str = typer.Option("linkedin", "-p", "--platform", help="Platform: linkedin, twitter, reddit, other"),
    audience: Optional[str] = typer.Option(None, "-a", "--audience", help="Target audience"),
    tags: Optional[str] = typer.Option(None, "-t", "--tags", help="Tags (comma-separated)"),
    goal_id: Optional[int] = typer.Option(None, "-g", "--goal", help="Link to goal ID"),
    status: str = typer.Option("draft", "-s", "--status", help="Status: draft, scheduled, posted"),
):
    """Add a new social media post."""
    db = get_db()

    try:
        post_id = db.add_social_post(
            platform=platform,
            content=content,
            status=status,
            audience=audience,
            tags=tags,
            goal_id=goal_id,
        )
        console.print(f"[green]Post added:[/green] #{post_id} ({platform}, {status})")

    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error adding post:[/red] {e}")
        raise typer.Exit(1)


@posts_app.command("show")
def posts_show(
    post_id: int = typer.Argument(..., help="Post ID"),
):
    """Show details of a social post."""
    db = get_db()

    try:
        post = db.get_social_post(post_id)
        if not post:
            console.print(f"[red]Post #{post_id} not found[/red]")
            raise typer.Exit(1)

        platform_names = {'linkedin': 'LinkedIn', 'twitter': 'Twitter/X', 'reddit': 'Reddit', 'other': 'Other'}
        title = f"{platform_names.get(post['platform'], 'Unknown')} Post #{post['id']}"

        lines = []
        lines.append(f"[bold]Status:[/bold] {post['status']}")
        if post.get('audience'):
            lines.append(f"[bold]Audience:[/bold] {post['audience']}")
        if post.get('tags'):
            lines.append(f"[bold]Tags:[/bold] {post['tags']}")
        if post.get('goal_title'):
            lines.append(f"[bold]Goal:[/bold] {post['goal_title']}")
        if post.get('url'):
            lines.append(f"[bold]URL:[/bold] {post['url']}")
        if post.get('posted_at'):
            lines.append(f"[bold]Posted:[/bold] {post['posted_at']}")
        lines.append(f"[bold]Created:[/bold] {post['created_at']}")
        lines.append("")
        lines.append("[bold]Content:[/bold]")
        lines.append(post['content'])

        console.print(Panel("\n".join(lines), title=title))

    except sqlite3.Error as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@posts_app.command("posted")
def posts_posted(
    post_id: int = typer.Argument(..., help="Post ID"),
    url: Optional[str] = typer.Option(None, "-u", "--url", help="URL of live post"),
):
    """Mark a post as posted."""
    db = get_db()

    try:
        success = db.mark_social_post_posted(post_id, url)
        if success:
            console.print(f"[green]Post #{post_id} marked as posted[/green]")
            if url:
                console.print(f"URL: {url}")
        else:
            console.print(f"[red]Post #{post_id} not found[/red]")
            raise typer.Exit(1)

    except sqlite3.Error as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)


@posts_app.command("search")
def posts_search(
    query: str = typer.Argument(..., help="Search query"),
):
    """Search social posts by content, tags, or audience."""
    db = get_db()

    try:
        posts = db.search_social_posts(query)

        if not posts:
            console.print(f"[yellow]No posts found matching '{query}'[/yellow]")
            return

        console.print(f"[bold]Search results for '{query}':[/bold] {len(posts)} found\n")

        table = Table()
        table.add_column("ID", style="dim")
        table.add_column("Platform", style="cyan")
        table.add_column("Status")
        table.add_column("Content")
        table.add_column("Created")

        for post in posts:
            content = post.get('content', '')[:50]
            if len(post.get('content', '')) > 50:
                content += '...'

            table.add_row(
                str(post['id']),
                post.get('platform', 'other'),
                post.get('status', 'draft'),
                content.replace('\n', ' '),
                post.get('created_at', '')[:10],
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error searching posts:[/red] {e}")
        raise typer.Exit(1)


@posts_app.command("update")
def posts_update(
    post_id: int = typer.Argument(..., help="Post ID"),
    content: Optional[str] = typer.Option(None, "--content", help="New content"),
    audience: Optional[str] = typer.Option(None, "-a", "--audience", help="Target audience"),
    tags: Optional[str] = typer.Option(None, "-t", "--tags", help="Tags (comma-separated)"),
    status: Optional[str] = typer.Option(None, "-s", "--status", help="Status: draft, scheduled, posted"),
):
    """Update a social post."""
    db = get_db()

    try:
        success = db.update_social_post(
            post_id=post_id,
            content=content,
            status=status,
            audience=audience,
            tags=tags,
        )
        if success:
            console.print(f"[green]Post #{post_id} updated[/green]")
        else:
            console.print(f"[red]Post #{post_id} not found[/red]")
            raise typer.Exit(1)

    except ValueError as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error updating post:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Contacts Commands
# =============================================================================

@contacts_app.command("list")
def contacts_list(
    account: Optional[str] = typer.Option(None, "-a", "--account", help="Filter by account: consulting, personal, both"),
    category: Optional[str] = typer.Option(None, "-c", "--category", help="Filter by category"),
    relationship: Optional[str] = typer.Option(None, "-r", "--relationship", help="Filter by relationship"),
    has: Optional[List[str]] = typer.Option(None, "--has", help="Only show contacts where field is non-empty (repeatable)"),
    missing: Optional[List[str]] = typer.Option(None, "--missing", help="Only show contacts where field is empty/null (repeatable)"),
    format: str = typer.Option("text", "--format", "-f", help="Output format: text, json"),
):
    """List contacts."""
    db = get_db()

    try:
        contacts = db.list_contacts(
            account=account,
            category=category,
            relationship=relationship,
            has_fields=has,
            missing_fields=missing,
        )

        if not contacts:
            console.print("[yellow]No contacts found[/yellow]")
            return

        if format == "json":
            print(json.dumps(contacts, indent=2, default=str))
            return

        table = Table(title="Contacts")
        table.add_column("ID", style="dim")
        table.add_column("Name", style="cyan")
        table.add_column("Email")
        table.add_column("Company")
        table.add_column("Last Contact")

        for c in contacts:
            table.add_row(
                str(c['id']),
                c['name'],
                c.get('email', '-') or '-',
                c.get('company', '-') or '-',
                c.get('last_contact', '-')[:10] if c.get('last_contact') else '-',
            )

        console.print(table)

    except ValueError as e:
        console.print(f"[red]Invalid filter:[/red] {e}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error listing contacts:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("add")
def contacts_add(
    name: str = typer.Argument(..., help="Contact name"),
    email: str = typer.Option(..., "-e", "--email", help="Email address (required)"),
    account: str = typer.Option("personal", "-a", "--account", help="Account: consulting, personal, both"),
    phone: Optional[str] = typer.Option(None, "-p", "--phone", help="Phone number"),
    company: Optional[str] = typer.Option(None, "-c", "--company", help="Company"),
    role: Optional[str] = typer.Option(None, "-r", "--role", help="Role/title"),
):
    """Add a new contact."""
    db = get_db()

    try:
        contact_id = db.add_contact(
            email=email,
            name=name,
            account=account,
            phone=phone,
            company=company,
            role=role,
        )
        console.print(f"[green]Contact added:[/green] #{contact_id} - {name}")

    except sqlite3.Error as e:
        console.print(f"[red]Error adding contact:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("show")
def contacts_show(
    contact_id: int = typer.Argument(..., help="Contact ID"),
):
    """Show contact details."""
    db = get_db()

    try:
        contact = db.get_contact_by_id(contact_id)

        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        # Header
        display_name = contact.get('name') or "(no name)"
        console.print(f"\n[bold cyan]{display_name}[/bold cyan]")
        console.print(f"[dim]Contact #{contact_id}[/dim]\n")

        # Basic info
        table = Table(show_header=False, box=None)
        table.add_column("Property", style="cyan", width=20)
        table.add_column("Value")

        basic_fields = [
            ('email', 'Email'),
            ('phone', 'Phone'),
            ('company', 'Company'),
            ('title', 'Title'),
            ('location', 'Location'),
            ('account', 'Account'),
            ('category', 'Category'),
            ('relationship', 'Relationship'),
            ('priority', 'Priority'),
            ('lead_source', 'Lead Source'),
            ('lead_status', 'Lead Status'),
        ]
        for field, label in basic_fields:
            val = contact.get(field)
            if val:
                table.add_row(label, str(val))

        # Social links
        social_fields = [
            ('linkedin', 'LinkedIn'),
            ('twitter', 'Twitter'),
            ('github', 'GitHub'),
            ('website', 'Website'),
            ('whatsapp', 'WhatsApp'),
            ('instagram', 'Instagram'),
            ('facebook', 'Facebook'),
            ('telegram', 'Telegram'),
            ('signal', 'Signal'),
            ('skype', 'Skype'),
        ]
        for field, label in social_fields:
            val = contact.get(field)
            if val:
                table.add_row(label, str(val))

        # Personal details
        personal_fields = [
            ('birthday', 'Birthday'),
            ('spouse_name', 'Spouse'),
            ('children', 'Children'),
            ('pets', 'Pets'),
            ('hobbies', 'Hobbies'),
            ('address', 'Address'),
            ('timezone', 'Timezone'),
        ]
        for field, label in personal_fields:
            val = contact.get(field)
            if val:
                table.add_row(label, str(val))

        # Contact preferences
        pref_fields = [
            ('best_contact_method', 'Best Contact'),
            ('best_time', 'Best Time'),
            ('response_speed', 'Response Speed'),
            ('contact_frequency', 'Frequency'),
            ('style', 'Style'),
            ('greeting', 'Greeting'),
            ('signoff', 'Sign-off'),
        ]
        for field, label in pref_fields:
            val = contact.get(field)
            if val and val != 'casual' and val != 'normal':
                table.add_row(label, str(val))

        # Dates
        if contact.get('first_contact'):
            table.add_row("First Contact", contact['first_contact'][:10])
        if contact.get('last_contact'):
            table.add_row("Last Contact", contact['last_contact'][:10])

        # Context/notes
        if contact.get('context'):
            table.add_row("Notes", contact['context'])

        console.print(table)

        # Tags
        tags = db.get_tags(contact_id)
        if tags:
            console.print(f"\n[cyan]Tags:[/cyan] {', '.join(tags)}")

        # Memories
        memories = db.get_memories(contact_id)
        if memories:
            console.print("\n[cyan]Memories:[/cyan]")
            for m in memories:
                cat = f"[dim][{m.get('category', '')}][/dim] " if m.get('category') else ""
                console.print(f"  {cat}{m.get('fact', '')}")
                if m.get('detail'):
                    console.print(f"    [dim]{m['detail']}[/dim]")

        # Recent interactions (if contact has email)
        if contact.get('email'):
            interactions = db.get_interactions(contact['email'], limit=5)
            if interactions:
                console.print("\n[cyan]Recent Interactions:[/cyan]")
                for i in interactions:
                    console.print(f"  [{i.get('interaction_date', '')[:10]}] {i.get('type', '')} - {i.get('summary', '')[:50]}")

    except sqlite3.Error as e:
        console.print(f"[red]Error showing contact:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("memory")
def contacts_memory(
    contact_id: int = typer.Argument(..., help="Contact ID"),
    fact: str = typer.Argument(..., help="Memory/fact to remember"),
    category: str = typer.Option("general", "-c", "--category", help="Memory category"),
    detail: Optional[str] = typer.Option(None, "-d", "--detail", help="Additional detail"),
):
    """Add a memory/fact about a contact."""
    db = get_db()

    try:
        contact = db.get_contact_by_id(contact_id)
        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        db.add_memory(
            contact_id=contact_id,
            category=category,
            fact=fact,
            detail=detail,
        )
        display_name = contact.get('name') or f"#{contact_id}"
        console.print(f"[green]Memory added for {display_name}[/green]")

    except sqlite3.Error as e:
        console.print(f"[red]Error adding memory:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("update")
def contacts_update(
    contact_id: int = typer.Argument(..., help="Contact ID"),
    name: Optional[str] = typer.Option(None, "--name", help="Name"),
    email: Optional[str] = typer.Option(None, "-e", "--email", help="Email address"),
    phone: Optional[str] = typer.Option(None, "-p", "--phone", help="Phone number"),
    company: Optional[str] = typer.Option(None, "-c", "--company", help="Company"),
    title: Optional[str] = typer.Option(None, "-t", "--title", help="Title/role"),
    location: Optional[str] = typer.Option(None, "-l", "--location", help="Location"),
    linkedin: Optional[str] = typer.Option(None, "--linkedin", help="LinkedIn URL"),
    account: Optional[str] = typer.Option(None, "-a", "--account", help="Account: consulting, personal, both"),
    category: Optional[str] = typer.Option(None, "--category", help="Category"),
    relationship: Optional[str] = typer.Option(None, "-r", "--relationship", help="Relationship"),
    notes: Optional[str] = typer.Option(None, "--notes", help="Notes"),
    lead_source: Optional[str] = typer.Option(None, "--lead-source", help="Lead source"),
    lead_status: Optional[str] = typer.Option(None, "--lead-status", help="Lead status"),
):
    """Update a contact."""
    db = get_db()

    try:
        contact = db.get_contact_by_id(contact_id)
        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        db.update_contact(
            contact_id,
            name=name,
            email=email,
            phone=phone,
            company=company,
            title=title,
            location=location,
            linkedin=linkedin,
            account=account,
            category=category,
            relationship=relationship,
            context=notes,
            lead_source=lead_source,
            lead_status=lead_status,
        )
        display_name = name or contact.get('name') or f"#{contact_id}"
        console.print(f"[green]Contact {display_name} updated[/green]")

    except sqlite3.Error as e:
        console.print(f"[red]Error updating contact:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("delete")
def contacts_delete(
    contact_id: int = typer.Argument(..., help="Contact ID"),
    force: bool = typer.Option(False, "--force", "-f", help="Skip confirmation"),
):
    """Delete a contact."""
    db = get_db()

    try:
        contact = db.get_contact_by_id(contact_id)
        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        display_name = contact.get('name') or contact.get('email') or f"#{contact_id}"
        if not force:
            confirm = typer.confirm(f"Delete contact {display_name} (#{contact_id})?")
            if not confirm:
                console.print("Cancelled.")
                return

        deleted = db.delete_contact(contact_id)
        if deleted:
            console.print(f"[green]Contact {display_name} (#{contact_id}) deleted[/green]")
        else:
            console.print(f"[red]Failed to delete contact #{contact_id}[/red]")
            raise typer.Exit(1)

    except sqlite3.Error as e:
        console.print(f"[red]Error deleting contact:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("enrich")
def contacts_enrich(
    contact_id: int = typer.Argument(..., help="Contact ID to enrich"),
    data: str = typer.Argument(..., help="JSON string with profile data from cc-linkedin enrich"),
):
    """Enrich a contact with data from LinkedIn profile extraction."""
    db = get_db()

    try:
        contact = db.get_contact_by_id(contact_id)
        if not contact:
            console.print(f"[red]Contact #{contact_id} not found[/red]")
            raise typer.Exit(1)

        profile = json.loads(data)

        if not profile.get("profile_exists", False):
            db.add_memory(
                contact_id=contact_id,
                category="linkedin",
                fact="LinkedIn profile not found or unavailable",
                source="cc-linkedin enrich",
            )
            console.print(f"[yellow]Contact #{contact_id}: profile not found, memory added[/yellow]")
            return

        # Update vault contact fields from profile data
        update_fields = {}
        if profile.get("name") and not contact.get("name"):
            update_fields["name"] = profile["name"]
        elif profile.get("name") and contact.get("name") == "":
            update_fields["name"] = profile["name"]

        if profile.get("current_company") and not contact.get("company"):
            update_fields["company"] = profile["current_company"]

        if profile.get("current_title") and not contact.get("title"):
            update_fields["title"] = profile["current_title"]

        if profile.get("location") and not contact.get("location"):
            update_fields["location"] = profile["location"]

        if update_fields:
            db.update_contact(contact_id, **update_fields)

        # Store headline as context/notes if empty
        if profile.get("headline") and not contact.get("context"):
            db.update_contact(contact_id, context=profile["headline"])

        # Store extra info as memories
        if profile.get("about"):
            db.add_memory(
                contact_id=contact_id,
                category="about",
                fact=profile["about"],
                source="cc-linkedin enrich",
            )

        if profile.get("education"):
            db.add_memory(
                contact_id=contact_id,
                category="education",
                fact=profile["education"],
                source="cc-linkedin enrich",
            )

        if profile.get("pronouns"):
            db.add_memory(
                contact_id=contact_id,
                category="personal",
                fact=f"Pronouns: {profile['pronouns']}",
                source="cc-linkedin enrich",
            )

        if profile.get("connections"):
            db.add_memory(
                contact_id=contact_id,
                category="linkedin",
                fact=f"Connections: {profile['connections']}",
                source="cc-linkedin enrich",
            )

        display_name = update_fields.get("name") or contact.get("name") or f"#{contact_id}"
        console.print(f"[green]Enriched: {display_name}[/green]")

    except json.JSONDecodeError as e:
        console.print(f"[red]Invalid JSON data:[/red] {e}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error enriching contact:[/red] {e}")
        raise typer.Exit(1)


@contacts_app.command("search")
def contacts_search(
    name: str = typer.Argument(..., help="Name to search for"),
    threshold: int = typer.Option(50, "--threshold", "-t", help="Minimum match score (0-100)"),
    n: int = typer.Option(10, "-n", help="Max results"),
    exact: bool = typer.Option(False, "--exact", help="Use exact matching (LIKE) instead of fuzzy"),
):
    """Search contacts by name using fuzzy/phonetic matching."""
    db = get_db()

    try:
        if exact:
            # Use existing exact search
            results = db.search_contacts(name)
            if not results:
                console.print(f"[yellow]No contacts matching:[/yellow] {name}")
                return

            table = Table(title=f"Search Results for '{name}' (exact)")
            table.add_column("ID", style="dim")
            table.add_column("Name", style="cyan")
            table.add_column("Email")
            table.add_column("Company")
            table.add_column("Match", style="green")

            for c in results[:n]:
                table.add_row(
                    str(c['id']),
                    c['name'],
                    c.get('email', '-') or '-',
                    c.get('company', '-') or '-',
                    "exact",
                )

            console.print(table)
        else:
            # Use fuzzy search
            results = db.fuzzy_search_contacts(name, threshold=threshold, limit=n)

            if not results:
                console.print(f"[yellow]No contacts matching:[/yellow] {name} (threshold={threshold})")
                console.print("[dim]Try lowering the threshold with --threshold 30[/dim]")
                return

            table = Table(title=f"Search Results for '{name}'")
            table.add_column("ID", style="dim")
            table.add_column("Name", style="cyan")
            table.add_column("Email")
            table.add_column("Company")
            table.add_column("Score", justify="right")
            table.add_column("Match", style="green")

            for c in results:
                score = c.get('match_score', 0)
                match_type = c.get('match_type', 'fuzzy')

                # Color score based on confidence
                if score >= 80:
                    score_style = "[green]"
                    score_end = "[/green]"
                elif score >= 60:
                    score_style = "[yellow]"
                    score_end = "[/yellow]"
                else:
                    score_style = "[dim]"
                    score_end = "[/dim]"

                table.add_row(
                    str(c['id']),
                    c['name'],
                    c.get('email', '-') or '-',
                    c.get('company', '-') or '-',
                    f"{score_style}{score:.0f}{score_end}",
                    match_type,
                )

            console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error searching contacts:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Documents Commands
# =============================================================================

@docs_app.command("list")
def docs_list(
    doc_type: Optional[str] = typer.Option(None, "-t", "--type", help="Filter by type: note, journal, transcript, research"),
    limit: int = typer.Option(20, "-n", help="Max results"),
):
    """List documents."""
    db = get_db()

    try:
        docs = db.list_documents(doc_type=doc_type, limit=limit)

        if not docs:
            console.print("[yellow]No documents found[/yellow]")
            return

        table = Table(title="Documents")
        table.add_column("ID", style="dim")
        table.add_column("Title", style="cyan")
        table.add_column("Type")
        table.add_column("Date")
        table.add_column("Tags")

        for doc in docs:
            table.add_row(
                str(doc['id']),
                doc.get('title', '')[:40],
                doc.get('doc_type', '-'),
                doc.get('created_at', '')[:10],
                doc.get('tags', '-') or '-',
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error listing documents:[/red] {e}")
        raise typer.Exit(1)


@docs_app.command("add")
def docs_add(
    path: Path = typer.Argument(..., help="File path to import"),
    doc_type: str = typer.Option("research", "-t", "--type", help="Document type"),
    title: Optional[str] = typer.Option(None, "--title", help="Document title"),
    tags: Optional[str] = typer.Option(None, "--tags", help="Comma-separated tags"),
):
    """Import a document into the vault."""
    try:
        try:
            from .importer import import_document
        except ImportError:
            from importer import import_document

        result = import_document(str(path), doc_type=doc_type, title=title, tags=tags)

        if result.get('success'):
            console.print(f"[green]Document imported:[/green] #{result['document_id']}")
            console.print(f"  Path: {result.get('path')}")
            if result.get('chunk_count'):
                console.print(f"  Chunks: {result['chunk_count']}")
        else:
            console.print(f"[red]Error:[/red] {result.get('error')}")
            raise typer.Exit(1)

    except (OSError, sqlite3.Error, ValueError) as e:
        console.print(f"[red]Error importing document:[/red] {e}")
        raise typer.Exit(1)


@docs_app.command("show")
def docs_show(
    doc_id: int = typer.Argument(..., help="Document ID"),
):
    """Show document details."""
    db = get_db()

    try:
        doc = db.get_document(doc_id)

        if not doc:
            console.print(f"[red]Document #{doc_id} not found[/red]")
            raise typer.Exit(1)

        # Header
        console.print(f"\n[bold cyan]{doc.get('title', 'Untitled')}[/bold cyan]")
        console.print(f"[dim]Document #{doc_id}[/dim]\n")

        # Details
        table = Table(show_header=False, box=None)
        table.add_column("Property", style="cyan", width=15)
        table.add_column("Value")

        table.add_row("Type", doc.get('doc_type', '-'))
        table.add_row("Path", doc.get('path', '-'))
        table.add_row("Created", doc.get('created_at', '-')[:19] if doc.get('created_at') else '-')
        if doc.get('tags'):
            table.add_row("Tags", doc['tags'])
        if doc.get('source'):
            table.add_row("Source", doc['source'])

        console.print(table)

        # Content preview
        if doc.get('path'):
            full_path = DOCUMENTS_PATH / doc['path']
            if full_path.exists():
                content = full_path.read_text(encoding='utf-8')[:500]
                console.print(f"\n[cyan]Preview:[/cyan]\n{content}...")

    except (OSError, sqlite3.Error) as e:
        console.print(f"[red]Error showing document:[/red] {e}")
        raise typer.Exit(1)


@docs_app.command("search")
def docs_search(
    query: str = typer.Argument(..., help="Search query"),
):
    """Search documents using full-text search."""
    db = get_db()

    try:
        results = db.search_documents(query)

        if not results:
            console.print(f"[yellow]No documents matching:[/yellow] {query}")
            return

        console.print(f"\n[cyan]Search Results ({len(results)}):[/cyan]\n")

        for doc in results:
            console.print(f"  #{doc['id']} [cyan]{doc.get('title', 'Untitled')}[/cyan]")
            console.print(f"    Type: {doc.get('doc_type', '-')} | Path: {doc.get('path', '-')[:50]}")
            console.print()

    except sqlite3.Error as e:
        console.print(f"[red]Error searching documents:[/red] {e}")
        raise typer.Exit(1)


def _resolve_docs_to_index(
    docs: List[dict],
) -> tuple[List[tuple[dict, str]], List[dict]]:
    """Read document files from disk, separating indexable from missing/empty."""
    to_index = []
    missing = []

    for doc in docs:
        doc_path = doc.get('path', '')
        if not doc_path:
            missing.append(doc)
            continue

        full_path = DOCUMENTS_PATH / doc_path
        if not full_path.exists():
            missing.append(doc)
            continue

        content = full_path.read_text(encoding='utf-8', errors='replace')
        if not content.strip():
            missing.append(doc)
            continue

        to_index.append((doc, content))

    return to_index, missing


def _print_dry_run(
    docs_to_index: List[tuple[dict, str]],
    missing_files: List[dict],
) -> None:
    """Print dry-run report of what would be indexed."""
    console.print("\n[dim]-- Dry run, no changes made --[/dim]\n")
    for doc, content in docs_to_index[:20]:
        console.print(f"  #{doc['id']} {doc.get('title', 'Untitled')} ({len(content)} chars)")
    if len(docs_to_index) > 20:
        console.print(f"  ... and {len(docs_to_index) - 20} more")
    if missing_files:
        console.print(f"\n[yellow]Files missing or empty:[/yellow]")
        for doc in missing_files[:10]:
            console.print(f"  #{doc['id']} {doc.get('title', 'Untitled')} -> {doc.get('path', 'no path')}")
        if len(missing_files) > 10:
            console.print(f"  ... and {len(missing_files) - 10} more")


@docs_app.command("reindex")
def docs_reindex(
    doc_id: Optional[int] = typer.Argument(None, help="Specific document ID to reindex"),
    all_docs: bool = typer.Option(False, "--all", help="Reindex all documents (re-chunk everything)"),
    unchunked: bool = typer.Option(False, "--unchunked", help="Only index documents that have no chunks"),
    dry_run: bool = typer.Option(False, "--dry-run", help="Show what would be indexed without doing it"),
):
    """Chunk and index documents that are missing from the search index."""
    try:
        try:
            from .vectors import VaultVectors
            from .db import get_db as get_db_conn, init_db, get_document
        except ImportError:
            from vectors import VaultVectors
            from db import get_db as get_db_conn, init_db, get_document

        init_db(silent=True)
        conn = get_db_conn()

        # Determine which documents to process
        if doc_id is not None:
            doc = get_document(doc_id)
            if not doc:
                console.print(f"[red]Document #{doc_id} not found[/red]")
                raise typer.Exit(1)
            docs = [doc]
        elif all_docs:
            cursor = conn.execute(
                "SELECT id, title, path, doc_type FROM documents ORDER BY id"
            )
            docs = [dict(row) for row in cursor.fetchall()]
        else:
            cursor = conn.execute("""
                SELECT id, title, path, doc_type FROM documents d
                WHERE NOT EXISTS (SELECT 1 FROM chunks c WHERE c.document_id = d.id)
                ORDER BY id
            """)
            docs = [dict(row) for row in cursor.fetchall()]

        conn.close()

        if not docs:
            console.print("[green]All documents are already indexed.[/green]")
            return

        docs_to_index, missing_files = _resolve_docs_to_index(docs)

        console.print(f"\n[cyan]Documents to index:[/cyan] {len(docs_to_index)}")
        if missing_files:
            console.print(f"[yellow]Skipping {len(missing_files)} docs (file missing or empty)[/yellow]")

        if dry_run:
            _print_dry_run(docs_to_index, missing_files)
            return

        # Index documents
        vecs = VaultVectors()
        indexed = 0
        errors = 0

        for i, (doc, content) in enumerate(docs_to_index, 1):
            try:
                chunk_ids = vecs.index_document_chunks(
                    document_id=doc['id'],
                    content=content,
                    metadata={
                        'doc_title': doc.get('title', ''),
                        'doc_type': doc.get('doc_type', ''),
                        'doc_path': doc.get('path', ''),
                    }
                )

                ts_conn = get_db_conn()
                try:
                    ts_conn.execute(
                        "UPDATE documents SET indexed_at = CURRENT_TIMESTAMP WHERE id = ?",
                        (doc['id'],)
                    )
                    ts_conn.commit()
                finally:
                    ts_conn.close()

                indexed += 1
                if i % 10 == 0 or i == len(docs_to_index):
                    console.print(f"  Indexed {i}/{len(docs_to_index)} ({len(chunk_ids)} chunks for #{doc['id']})")

            except (OSError, sqlite3.Error, ValueError, RuntimeError) as e:
                errors += 1
                console.print(f"  [red]Error indexing #{doc['id']} {doc.get('title', '')}:[/red] {e}")

        console.print(f"\n[green]Done.[/green] Indexed: {indexed} | Errors: {errors} | Skipped: {len(missing_files)}")

    except (OSError, sqlite3.Error) as e:
        console.print(f"[red]Error during reindex:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Config Commands
# =============================================================================

@config_app.command("show")
def config_show():
    """Show current configuration."""
    import os
    config = get_config()

    table = Table(title="Vault Configuration")
    table.add_column("Setting", style="cyan")
    table.add_column("Value")

    table.add_row("Vault Path", str(config.vault_path))
    table.add_row("Database", str(config.db_path))
    table.add_row("Vector DB", str(config.vectors_path))
    table.add_row("Documents", str(config.documents_path))
    table.add_row("OpenAI API Key", "Set" if os.environ.get("OPENAI_API_KEY") else "[red]Not Set[/red]")

    console.print(table)


@config_app.command("set")
def config_set(
    key: str = typer.Argument(..., help="Config key to set"),
    value: str = typer.Argument(..., help="Config value"),
):
    """Set a configuration value."""
    try:
        from .config import save_config
    except ImportError:
        from config import save_config

    try:
        save_config(key, value)
        console.print(f"[green]Config updated:[/green] {key} = {value}")
    except (OSError, ValueError) as e:
        console.print(f"[red]Error setting config:[/red] {e}")
        raise typer.Exit(1)


# =============================================================================
# Health Commands
# =============================================================================

@health_app.command("list")
def health_list(
    category: Optional[str] = typer.Option(None, "-c", "--category", help="Filter by category"),
    days: int = typer.Option(30, "-d", "--days", help="Days to show"),
):
    """List health entries."""
    db = get_db()

    try:
        entries = db.list_health_entries(category=category, days=days)

        if not entries:
            console.print("[yellow]No health entries found[/yellow]")
            return

        table = Table(title="Health Entries")
        table.add_column("ID", style="dim")
        table.add_column("Date", style="cyan")
        table.add_column("Category")
        table.add_column("Summary")

        for e in entries:
            table.add_row(
                str(e['id']),
                e.get('entry_date', '-'),
                e.get('category', '-'),
                (e.get('summary', '-') or '-')[:50],
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error listing health entries:[/red] {e}")
        raise typer.Exit(1)


@health_app.command("insights")
def health_insights(
    query: str = typer.Option("recent health trends", "-q", "--query", help="Health query"),
    days: int = typer.Option(30, "-d", "--days", help="Days to analyze"),
):
    """Get AI-powered health insights."""
    try:
        try:
            from .rag import get_vault_rag
        except ImportError:
            from rag import get_vault_rag
        rag = get_vault_rag()

        console.print(f"[dim]Analyzing health data...[/dim]")
        result = rag.health_insights(query, days=days)

        if 'error' in result:
            console.print(f"[red]Error:[/red] {result['error']}")
            raise typer.Exit(1)

        console.print(f"\n[cyan]Health Insights:[/cyan]\n{result['insights']}")
        console.print(f"\n[dim]Based on {result['data_points']} data points[/dim]")

    except ImportError as e:
        console.print(f"[red]Error:[/red] RAG not available: {e}")
        raise typer.Exit(1)


# =============================================================================
# Graph Commands (Entity Links)
# =============================================================================

graph_app = typer.Typer(help="Graph statistics and traversal")
app.add_typer(graph_app, name="graph")


def _get_valid_entity_types() -> List[str]:
    """Get valid entity types from config."""
    try:
        from .config import ENTITY_TYPES
    except ImportError:
        from config import ENTITY_TYPES
    return ENTITY_TYPES


@app.command("link")
def create_link(
    source_type: str = typer.Argument(..., help="Source entity type (contact, task, goal, idea, document)"),
    source_id: int = typer.Argument(..., help="Source entity ID"),
    target_type: str = typer.Argument(..., help="Target entity type"),
    target_id: int = typer.Argument(..., help="Target entity ID"),
    rel: str = typer.Option(..., "--rel", "-r", help="Relationship type (e.g., works_on, mentions, supports)"),
    strength: int = typer.Option(3, "--strength", "-s", help="Relationship strength (1-5)"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Create a link between two entities."""
    db = get_db()

    valid_types = _get_valid_entity_types()
    if source_type not in valid_types:
        console.print(f"[red]Error:[/red] Invalid source_type '{source_type}'. Must be: {', '.join(valid_types)}")
        raise typer.Exit(1)
    if target_type not in valid_types:
        console.print(f"[red]Error:[/red] Invalid target_type '{target_type}'. Must be: {', '.join(valid_types)}")
        raise typer.Exit(1)
    if strength < 1 or strength > 5:
        console.print(f"[red]Error:[/red] Strength must be between 1 and 5")
        raise typer.Exit(1)

    try:
        link_id = db.add_entity_link(
            source_type=source_type,
            source_id=source_id,
            target_type=target_type,
            target_id=target_id,
            relationship=rel,
            strength=strength,
        )

        if json_output:
            result = {
                "success": True,
                "link_id": link_id,
                "source": {"type": source_type, "id": source_id},
                "target": {"type": target_type, "id": target_id},
                "relationship": rel,
                "strength": strength,
            }
            console.print(json.dumps(result, indent=2))
        else:
            console.print(f"[green]Linked[/green] {source_type}:{source_id} -> {target_type}:{target_id} ({rel}, strength={strength})")

    except (sqlite3.Error, ValueError) as e:
        console.print(f"[red]Error creating link:[/red] {e}")
        raise typer.Exit(1)


@app.command("unlink")
def remove_link(
    source_type: str = typer.Argument(..., help="Source entity type"),
    source_id: int = typer.Argument(..., help="Source entity ID"),
    target_type: str = typer.Argument(..., help="Target entity type"),
    target_id: int = typer.Argument(..., help="Target entity ID"),
    rel: Optional[str] = typer.Option(None, "--rel", "-r", help="Specific relationship to remove (optional)"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Remove a link between two entities."""
    db = get_db()

    try:
        removed = db.remove_entity_link(
            source_type=source_type,
            source_id=source_id,
            target_type=target_type,
            target_id=target_id,
            relationship=rel,
        )

        if json_output:
            result = {
                "success": removed,
                "source": {"type": source_type, "id": source_id},
                "target": {"type": target_type, "id": target_id},
                "relationship": rel,
            }
            console.print(json.dumps(result, indent=2))
        else:
            if removed:
                console.print(f"[green]Unlinked[/green] {source_type}:{source_id} -> {target_type}:{target_id}")
            else:
                console.print(f"[yellow]No link found[/yellow] {source_type}:{source_id} -> {target_type}:{target_id}")

    except sqlite3.Error as e:
        console.print(f"[red]Error removing link:[/red] {e}")
        raise typer.Exit(1)


@app.command("links")
def get_links(
    entity_type: str = typer.Argument(..., help="Entity type (contact, task, goal, idea, document)"),
    entity_id: int = typer.Argument(..., help="Entity ID"),
    depth: int = typer.Option(1, "--depth", "-d", help="Depth of traversal (1=direct only)"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Get all links for an entity."""
    try:
        try:
            from .graph import get_vault_graph
        except ImportError:
            from graph import get_vault_graph

        graph = get_vault_graph()
        result = graph.get_links(entity_type, entity_id, depth=depth)

        if 'error' in result:
            console.print(f"[red]Error:[/red] {result['error']}")
            raise typer.Exit(1)

        if json_output:
            console.print(json.dumps(result, indent=2))
        else:
            entity = result.get('entity', {})
            console.print(f"\n[bold cyan]{entity.get('type', '')}:{entity.get('id', '')}[/bold cyan] - {entity.get('label', '')}")

            links = result.get('links', [])
            if not links:
                console.print("[dim]No links found[/dim]")
            else:
                table = Table(title=f"Links ({len(links)} total)")
                table.add_column("Type", style="cyan")
                table.add_column("ID", style="dim")
                table.add_column("Label")
                table.add_column("Relationship")
                table.add_column("Dir")
                table.add_column("Strength", justify="right")

                for link in links:
                    direction = "<-" if link.get('direction') == 'incoming' else "->"
                    via = f" (via {link.get('via')})" if link.get('via') else ""
                    table.add_row(
                        link.get('type', ''),
                        str(link.get('id', '')),
                        (link.get('label', '')[:40] + via),
                        link.get('relationship', '-') or '-',
                        direction,
                        str(link.get('strength', '')),
                    )

                console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error getting links:[/red] {e}")
        raise typer.Exit(1)


@app.command("context")
def get_context(
    entity_type: str = typer.Argument(..., help="Entity type (contact, task, goal, idea, document)"),
    entity_id: int = typer.Argument(..., help="Entity ID"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Get entity with full linked context (for agents)."""
    try:
        try:
            from .graph import get_vault_graph
        except ImportError:
            from graph import get_vault_graph

        graph = get_vault_graph()
        result = graph.get_context(entity_type, entity_id)

        if 'error' in result:
            console.print(f"[red]Error:[/red] {result['error']}")
            raise typer.Exit(1)

        if json_output:
            console.print(json.dumps(result, indent=2))
        else:
            entity = result.get('entity', {})
            details = entity.get('details', {})

            # Header
            console.print(f"\n[bold cyan]{entity.get('type', '')}:{entity.get('id', '')}[/bold cyan]")

            # Entity details
            table = Table(show_header=False, box=None)
            table.add_column("Property", style="cyan", width=15)
            table.add_column("Value")

            for key, value in details.items():
                if value and key != 'label':
                    table.add_row(key, str(value)[:60])

            console.print(table)

            # Linked items
            linked = result.get('linked', [])
            if linked:
                console.print(f"\n[cyan]Linked Items ({len(linked)}):[/cyan]")

                for item in linked:
                    direction = "<-" if item.get('direction') == 'incoming' else "->"
                    rel = item.get('relationship') or ''
                    item_details = item.get('details', {})
                    label = item_details.get('label', '') or item_details.get('name', '') or item_details.get('title', '')

                    console.print(f"  {direction} [{item.get('type')}:{item.get('id')}] {label[:50]}")
                    if rel:
                        console.print(f"     [dim]relationship: {rel}, strength: {item.get('strength', 1)}[/dim]")
            else:
                console.print("\n[dim]No linked items[/dim]")

    except sqlite3.Error as e:
        console.print(f"[red]Error getting context:[/red] {e}")
        raise typer.Exit(1)


@graph_app.command("stats")
def graph_stats(
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Show graph statistics."""
    db = get_db()

    try:
        stats = db.get_graph_stats()

        if json_output:
            console.print(json.dumps(stats, indent=2))
        else:
            # Entity counts
            table = Table(title="Graph Statistics")
            table.add_column("Entity Type", style="cyan")
            table.add_column("Count", justify="right")

            entities = stats.get('entities', {})
            for entity_type, count in entities.items():
                table.add_row(entity_type, str(count))

            table.add_row("", "")
            table.add_row("[bold]Total Links[/bold]", f"[bold]{stats.get('total_links', 0)}[/bold]")

            console.print(table)

            # Most connected
            most_connected = stats.get('most_connected', [])
            if most_connected:
                console.print("\n[cyan]Most Connected Entities:[/cyan]")
                for item in most_connected[:5]:
                    console.print(f"  {item.get('type')}:{item.get('id')} - {item.get('name', '')} ({item.get('links', 0)} links)")

    except sqlite3.Error as e:
        console.print(f"[red]Error getting stats:[/red] {e}")
        raise typer.Exit(1)


@graph_app.command("path")
def graph_path(
    from_type: str = typer.Argument(..., help="Source entity type"),
    from_id: int = typer.Argument(..., help="Source entity ID"),
    to_type: str = typer.Argument(..., help="Target entity type"),
    to_id: int = typer.Argument(..., help="Target entity ID"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Find path between two entities."""
    try:
        try:
            from .graph import get_vault_graph
        except ImportError:
            from graph import get_vault_graph

        graph = get_vault_graph()
        path = graph.find_path(from_type, from_id, to_type, to_id)

        if path is None:
            result = {"found": False, "path": None}
            if json_output:
                console.print(json.dumps(result, indent=2))
            else:
                console.print(f"[yellow]No path found[/yellow] between {from_type}:{from_id} and {to_type}:{to_id}")
            return

        if json_output:
            result = {"found": True, "path": path, "length": len(path)}
            console.print(json.dumps(result, indent=2))
        else:
            console.print(f"\n[cyan]Path ({len(path)} steps):[/cyan]\n")

            for i, node in enumerate(path):
                prefix = "  " if i == 0 else "  -> "
                rel = f" ({node.get('relationship')})" if node.get('relationship') else ""
                console.print(f"{prefix}[{node.get('type')}:{node.get('id')}] {node.get('label', '')}{rel}")

    except sqlite3.Error as e:
        console.print(f"[red]Error finding path:[/red] {e}")
        raise typer.Exit(1)


@graph_app.command("sync-fk")
def graph_sync_fk(
    dry_run: bool = typer.Option(False, "--dry-run", help="Preview without making changes"),
    json_output: bool = typer.Option(False, "--json", help="Output as JSON"),
):
    """Populate entity_links from FK relationships in the schema."""
    db = get_db()

    try:
        stats = db.populate_links_from_fk(dry_run=dry_run)

        if json_output:
            console.print(json.dumps(stats, indent=2))
        else:
            mode = "[yellow]DRY RUN[/yellow] - " if dry_run else ""
            console.print(f"\n{mode}[cyan]FK Relationship Sync[/cyan]\n")

            # Show per-relationship stats
            table = Table(title="Relationships Processed")
            table.add_column("Relationship", style="cyan")
            table.add_column("Found", justify="right")
            table.add_column("Created", justify="right", style="green")
            table.add_column("Skipped", justify="right", style="yellow")

            for rel_name, rel_stats in stats.get("relationships", {}).items():
                table.add_row(
                    rel_name,
                    str(rel_stats.get("found", 0)),
                    str(rel_stats.get("created", 0)),
                    str(rel_stats.get("skipped", 0))
                )

            console.print(table)

            # Summary
            console.print(f"\n[bold]Total links created:[/bold] {stats.get('total_created', 0)}")
            if stats.get("total_skipped", 0) > 0:
                console.print(f"[yellow]Total skipped:[/yellow] {stats.get('total_skipped', 0)}")

            # Errors
            errors = stats.get("errors", [])
            if errors:
                console.print(f"\n[red]Errors ({len(errors)}):[/red]")
                for err in errors[:10]:
                    console.print(f"  - {err}")
                if len(errors) > 10:
                    console.print(f"  ... and {len(errors) - 10} more")

            if dry_run:
                console.print("\n[yellow]Run without --dry-run to create links[/yellow]")

    except sqlite3.Error as e:
        console.print(f"[red]Error syncing FK relationships:[/red] {e}")
        raise typer.Exit(1)


# ===========================================
# LISTS COMMANDS
# ===========================================


@lists_app.callback(invoke_without_command=True)
def lists_default(ctx: typer.Context):
    """List all contact lists."""
    if ctx.invoked_subcommand is not None:
        return

    db = get_db()
    try:
        lists = db.list_lists()

        if not lists:
            console.print("[yellow]No lists found. Create one with: cc-vault lists create \"List Name\"[/yellow]")
            return

        table = Table(title="Contact Lists")
        table.add_column("ID", style="dim")
        table.add_column("Name", style="cyan")
        table.add_column("Type")
        table.add_column("Members", justify="right")
        table.add_column("Description")
        table.add_column("Created", style="dim")

        for lst in lists:
            table.add_row(
                str(lst['id']),
                lst['name'],
                lst.get('list_type', 'general') or 'general',
                str(lst.get('member_count', 0)),
                (lst.get('description') or '-')[:40],
                lst.get('created_at', '-')[:10] if lst.get('created_at') else '-',
            )

        console.print(table)

    except sqlite3.Error as e:
        console.print(f"[red]Error listing lists:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("create")
def lists_create(
    name: str = typer.Argument(..., help="List name"),
    description: Optional[str] = typer.Option(None, "--description", "-d", help="List description"),
    list_type: str = typer.Option("general", "--type", "-t", help="List type (e.g. general, mailing, outreach)"),
):
    """Create a new contact list."""
    db = get_db()
    try:
        list_id = db.create_list(name=name, description=description, list_type=list_type)
        console.print(f"[green]List created:[/green] #{list_id} - {name}")
    except sqlite3.IntegrityError:
        console.print(f"[red]List already exists:[/red] {name}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error creating list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("show")
def lists_show(
    name: str = typer.Argument(..., help="List name"),
):
    """Show members of a contact list."""
    db = get_db()
    try:
        lst = db.get_list(name)
        if not lst:
            console.print(f"[red]List not found:[/red] {name}")
            raise typer.Exit(1)

        members = db.get_list_members(name)

        console.print(f"\n[bold cyan]{lst['name']}[/bold cyan]")
        if lst.get('description'):
            console.print(f"[dim]{lst['description']}[/dim]")
        console.print(f"Type: {lst.get('list_type', 'general')}  |  Members: {len(members)}\n")

        if not members:
            console.print("[yellow]No members in this list[/yellow]")
            return

        table = Table(title=f"Members of \"{name}\"")
        table.add_column("ID", style="dim")
        table.add_column("Name", style="cyan")
        table.add_column("Email")
        table.add_column("Company")
        table.add_column("Location")
        table.add_column("Added", style="dim")

        for m in members:
            table.add_row(
                str(m['id']),
                m['name'],
                m.get('email', '-') or '-',
                m.get('company', '-') or '-',
                m.get('location', '-') or '-',
                m.get('list_added_at', '-')[:10] if m.get('list_added_at') else '-',
            )

        console.print(table)

    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error showing list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("delete")
def lists_delete(
    name: str = typer.Argument(..., help="List name"),
    yes: bool = typer.Option(False, "--yes", "-y", help="Skip confirmation"),
):
    """Delete a contact list."""
    db = get_db()
    try:
        lst = db.get_list(name)
        if not lst:
            console.print(f"[red]List not found:[/red] {name}")
            raise typer.Exit(1)

        if not yes:
            members = db.get_list_members(name)
            confirm = typer.confirm(f"Delete list \"{name}\" with {len(members)} members?")
            if not confirm:
                console.print("[yellow]Cancelled[/yellow]")
                raise typer.Exit()

        deleted = db.delete_list(name)
        if deleted:
            console.print(f"[green]List deleted:[/green] {name}")
        else:
            console.print(f"[red]Failed to delete list:[/red] {name}")
            raise typer.Exit(1)

    except sqlite3.Error as e:
        console.print(f"[red]Error deleting list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("rename")
def lists_rename(
    name: str = typer.Argument(..., help="Current list name"),
    new_name: str = typer.Argument(..., help="New list name"),
):
    """Rename a contact list."""
    db = get_db()
    try:
        db.rename_list(name, new_name)
        console.print(f"[green]List renamed:[/green] \"{name}\" -> \"{new_name}\"")
    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.IntegrityError:
        console.print(f"[red]List already exists:[/red] {new_name}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error renaming list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("update")
def lists_update(
    name: str = typer.Argument(..., help="List name"),
    description: Optional[str] = typer.Option(None, "--description", "-d", help="New description"),
    list_type: Optional[str] = typer.Option(None, "--type", "-t", help="New list type"),
):
    """Update a list's description or type."""
    if description is None and list_type is None:
        console.print("[red]Provide --description or --type (or both)[/red]")
        raise typer.Exit(1)

    db = get_db()
    try:
        db.update_list(name, description=description, list_type=list_type)
        console.print(f"[green]List updated:[/green] {name}")
    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error updating list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("copy")
def lists_copy(
    source: str = typer.Argument(..., help="Source list name"),
    dest: str = typer.Argument(..., help="Destination list name"),
):
    """Duplicate a list with all its members."""
    db = get_db()
    try:
        count = db.copy_list(source, dest)
        console.print(f"[green]List copied:[/green] \"{source}\" -> \"{dest}\" ({count} members)")
    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.IntegrityError:
        console.print(f"[red]List already exists:[/red] {dest}")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error copying list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("add")
def lists_add(
    name: str = typer.Argument(..., help="List name"),
    contact_id: Optional[int] = typer.Option(None, "--contact-id", "-c", help="Single contact ID to add"),
    query: Optional[str] = typer.Option(None, "--query", "-q", help="SQL WHERE clause to match contacts"),
):
    """Add contacts to a list (by ID or query)."""
    if not contact_id and not query:
        console.print("[red]Provide --contact-id or --query[/red]")
        raise typer.Exit(1)

    db = get_db()
    try:
        if contact_id:
            added = db.add_list_member(name, contact_id)
            if added:
                console.print(f"[green]Added contact #{contact_id} to \"{name}\"[/green]")
            else:
                console.print(f"[yellow]Contact #{contact_id} is already in \"{name}\"[/yellow]")
        elif query:
            count = db.add_list_members_by_query(name, query)
            console.print(f"[green]Added {count} contacts to \"{name}\"[/green]")

    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error adding to list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("remove")
def lists_remove(
    name: str = typer.Argument(..., help="List name"),
    contact_id: Optional[int] = typer.Option(None, "--contact-id", "-c", help="Contact ID to remove"),
    query: Optional[str] = typer.Option(None, "--query", "-q", help="SQL WHERE clause to match contacts"),
):
    """Remove contacts from a list (by ID or query)."""
    if not contact_id and not query:
        console.print("[red]Provide --contact-id or --query[/red]")
        raise typer.Exit(1)

    db = get_db()
    try:
        if contact_id:
            removed = db.remove_list_member(name, contact_id)
            if removed:
                console.print(f"[green]Removed contact #{contact_id} from \"{name}\"[/green]")
            else:
                console.print(f"[yellow]Contact #{contact_id} was not in \"{name}\"[/yellow]")
        elif query:
            count = db.remove_list_members_by_query(name, query)
            console.print(f"[green]Removed {count} contacts from \"{name}\"[/green]")

    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error removing from list:[/red] {e}")
        raise typer.Exit(1)


@lists_app.command("export")
def lists_export(
    name: str = typer.Argument(..., help="List name"),
    format: str = typer.Option("json", "--format", "-f", help="Export format: json or csv"),
):
    """Export list members as JSON or CSV."""
    db = get_db()
    try:
        output = db.export_list(name, format=format)
        if not output:
            console.print(f"[yellow]List \"{name}\" is empty[/yellow]")
            return
        console.print(output)

    except ValueError as e:
        console.print(f"[red]{e}[/red]")
        raise typer.Exit(1)
    except sqlite3.Error as e:
        console.print(f"[red]Error exporting list:[/red] {e}")
        raise typer.Exit(1)


if __name__ == "__main__":
    app()

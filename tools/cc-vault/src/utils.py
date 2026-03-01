"""Utility functions for cc-vault."""

from datetime import datetime
from typing import Optional, List, Dict, Any
import re


def format_timestamp(timestamp: Optional[str]) -> str:
    """Format ISO timestamp to readable format."""
    if not timestamp:
        return "Unknown"
    try:
        dt = datetime.fromisoformat(timestamp.replace('Z', '+00:00'))
        return dt.strftime("%Y-%m-%d %H:%M:%S")
    except (ValueError, TypeError):
        return timestamp[:19] if timestamp else "Unknown"


def format_date(date_str: Optional[str]) -> str:
    """Format date string to YYYY-MM-DD."""
    if not date_str:
        return "-"
    return date_str[:10] if len(date_str) >= 10 else date_str


def sanitize_text(text: str) -> str:
    """Remove non-ASCII characters to avoid encoding issues."""
    if not text:
        return ""

    # Replace common Unicode characters with ASCII equivalents
    replacements = {
        '\u2013': '-',   # en-dash
        '\u2014': '-',   # em-dash
        '\u2018': "'",   # left single quote
        '\u2019': "'",   # right single quote
        '\u201c': '"',   # left double quote
        '\u201d': '"',   # right double quote
        '\u2026': '...', # ellipsis
        '\u00a0': ' ',   # non-breaking space
        '\u00b7': '*',   # middle dot
        '\u2022': '*',   # bullet
        '\u2192': '->',  # rightwards arrow
        '\r\n': '\n',    # Windows line ending
        '\r': '\n',      # Old Mac line ending
    }

    for unicode_char, ascii_char in replacements.items():
        text = text.replace(unicode_char, ascii_char)

    # Remove any remaining non-ASCII characters
    return text.encode('ascii', 'ignore').decode('ascii')


def truncate(text: str, max_length: int = 80) -> str:
    """Truncate text to max length with ellipsis."""
    if not text:
        return ""
    text = sanitize_text(text)
    if len(text) <= max_length:
        return text
    return text[:max_length - 3] + "..."


def format_size(size_bytes: int) -> str:
    """Format byte size to human readable format."""
    for unit in ["B", "KB", "MB", "GB"]:
        if size_bytes < 1024:
            return f"{size_bytes:.1f} {unit}"
        size_bytes /= 1024
    return f"{size_bytes:.1f} TB"


def format_progress_bar(percent: int, width: int = 10) -> str:
    """Format a simple text progress bar."""
    if percent < 0:
        percent = 0
    if percent > 100:
        percent = 100
    filled = int(width * percent / 100)
    empty = width - filled
    return f"[{'=' * filled}{' ' * empty}] {percent}%"


def format_tasks_table(tasks: List[Dict[str, Any]]) -> List[List[str]]:
    """Format tasks for table display."""
    rows = []
    for task in tasks:
        priority = task.get('priority', 'medium')
        rows.append([
            str(task['id']),
            truncate(task['title'], 50),
            format_date(task.get('due_date')),
            priority,
            task.get('contact_name', '-') or '-',
        ])
    return rows


def format_goals_table(goals: List[Dict[str, Any]]) -> List[List[str]]:
    """Format goals for table display."""
    rows = []
    for goal in goals:
        progress = goal.get('progress', 0) or 0
        rows.append([
            str(goal['id']),
            truncate(goal['title'], 40),
            format_date(goal.get('target_date')),
            format_progress_bar(progress),
            goal.get('status', 'active'),
        ])
    return rows


def format_ideas_table(ideas: List[Dict[str, Any]]) -> List[List[str]]:
    """Format ideas for table display."""
    rows = []
    for idea in ideas:
        rows.append([
            str(idea['id']),
            truncate(idea['content'], 50),
            idea.get('category', '-') or '-',
            format_date(idea.get('created_at')),
            idea.get('status', 'new'),
        ])
    return rows


def format_contacts_table(contacts: List[Dict[str, Any]]) -> List[List[str]]:
    """Format contacts for table display."""
    rows = []
    for c in contacts:
        rows.append([
            str(c['id']),
            c['name'],
            c.get('email', '-') or '-',
            c.get('company', '-') or '-',
            format_date(c.get('last_contact')),
        ])
    return rows


def format_documents_table(docs: List[Dict[str, Any]]) -> List[List[str]]:
    """Format documents for table display."""
    rows = []
    for doc in docs:
        rows.append([
            str(doc['id']),
            truncate(doc.get('title', ''), 40),
            doc.get('doc_type', '-'),
            format_date(doc.get('created_at')),
            doc.get('tags', '-') or '-',
        ])
    return rows


def clean_filename(name: str) -> str:
    """Clean a string for use as a filename."""
    # Remove or replace invalid characters
    name = re.sub(r'[<>:"/\\|?*]', '', name)
    # Replace spaces with underscores
    name = name.replace(' ', '_')
    # Remove leading/trailing whitespace and dots
    name = name.strip('. ')
    # Limit length
    if len(name) > 200:
        name = name[:200]
    return name.lower()


def parse_tags(tags_str: Optional[str]) -> List[str]:
    """Parse comma-separated tags string into list."""
    if not tags_str:
        return []
    return [tag.strip() for tag in tags_str.split(',') if tag.strip()]


def format_tags(tags: List[str]) -> str:
    """Format tags list into comma-separated string."""
    return ', '.join(tags) if tags else ''


def relative_time(timestamp: str) -> str:
    """Convert timestamp to relative time (e.g., '2 days ago')."""
    try:
        dt = datetime.fromisoformat(timestamp.replace('Z', '+00:00'))
        now = datetime.now(dt.tzinfo) if dt.tzinfo else datetime.now()
        diff = now - dt

        if diff.days > 365:
            years = diff.days // 365
            return f"{years} year{'s' if years > 1 else ''} ago"
        elif diff.days > 30:
            months = diff.days // 30
            return f"{months} month{'s' if months > 1 else ''} ago"
        elif diff.days > 0:
            return f"{diff.days} day{'s' if diff.days > 1 else ''} ago"
        elif diff.seconds > 3600:
            hours = diff.seconds // 3600
            return f"{hours} hour{'s' if hours > 1 else ''} ago"
        elif diff.seconds > 60:
            minutes = diff.seconds // 60
            return f"{minutes} minute{'s' if minutes > 1 else ''} ago"
        else:
            return "just now"
    except (ValueError, TypeError):
        return timestamp[:19] if timestamp else "unknown"

"""
Contact enrichment from Gmail emails.

Reads vault contacts, searches Gmail for recent emails to/from each contact,
uses Claude Haiku to extract personal info, and updates empty vault fields
plus adds facts as memories.

Usage:
    python enrich_contacts.py                      # Enrich all contacts
    python enrich_contacts.py --contact-id 42      # Single contact
    python enrich_contacts.py --account personal   # Gmail account
    python enrich_contacts.py --limit 10           # First N contacts
    python enrich_contacts.py --dry-run            # Preview only
    python enrich_contacts.py --reset              # Clear state, start over
"""

import argparse
import json
import subprocess
import sys
import time
from datetime import date
from pathlib import Path
from typing import Optional

# -- Add tool source directories to path for imports --
TOOLS_DIR = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(TOOLS_DIR / "cc-vault" / "src"))
sys.path.insert(0, str(TOOLS_DIR / "cc-gmail" / "src"))
sys.path.insert(0, str(TOOLS_DIR))

from db import (
    init_db,
    get_db,
    get_contact_by_id,
    update_contact,
    add_memory,
    get_memories,
)
from auth import load_credentials, resolve_account
from gmail_api import GmailClient

# -- Constants --
STATE_FILE = Path(__file__).resolve().parent / "enrich_state.json"
MAX_EMAILS = 10
MAX_BODY_CHARS = 4000
LLM_DELAY_SECONDS = 1.0
TODAY = date.today().isoformat()

# Fields we can extract and populate in the vault
ENRICHABLE_FIELDS = [
    "company", "title", "location", "hobbies", "spouse_name",
    "children", "pets", "phone", "linkedin", "twitter", "website",
    "birthday", "timezone", "context", "relationship",
    "instagram", "facebook", "github",
]


def load_state() -> dict:
    """Load the enrichment state file tracking processed contacts."""
    if STATE_FILE.exists():
        return json.loads(STATE_FILE.read_text())
    return {"processed": [], "errors": [], "last_run": None}


def save_state(state: dict):
    """Save enrichment state to disk."""
    state["last_run"] = TODAY
    STATE_FILE.write_text(json.dumps(state, indent=2))


def fetch_emails(gmail: GmailClient, email: str) -> list[dict]:
    """
    Search Gmail for the last N emails sent to or received from this contact.
    Returns list of dicts with 'from', 'to', 'subject', 'date', 'body'.
    """
    query = f"to:{email} OR from:{email}"
    results = gmail.search(query, max_results=MAX_EMAILS)

    if not results:
        return []

    emails = []
    for msg_stub in results:
        details = gmail.get_message_details(msg_stub["id"])
        headers = details.get("headers", {})
        body = details.get("body", "") or details.get("snippet", "")

        emails.append({
            "from": headers.get("from", ""),
            "to": headers.get("to", ""),
            "subject": headers.get("subject", ""),
            "date": headers.get("date", ""),
            "body": body,
        })

    return emails


def build_prompt(contact: dict, emails: list[dict]) -> str:
    """
    Build the LLM extraction prompt from contact data and email content.
    Truncates combined email bodies to MAX_BODY_CHARS.
    """
    # Summarize current contact state
    current_fields = {}
    for field in ENRICHABLE_FIELDS:
        val = contact.get(field)
        if val:
            current_fields[field] = val

    # Build email content block, truncating to fit
    email_blocks = []
    total_chars = 0
    for em in emails:
        body = em["body"] or ""
        # Truncate individual emails if needed
        remaining = MAX_BODY_CHARS - total_chars
        if remaining <= 0:
            break
        if len(body) > remaining:
            body = body[:remaining] + "..."

        block = (
            f"From: {em['from']}\n"
            f"To: {em['to']}\n"
            f"Subject: {em['subject']}\n"
            f"Date: {em['date']}\n"
            f"Body:\n{body}"
        )
        email_blocks.append(block)
        total_chars += len(body)

    emails_text = "\n---\n".join(email_blocks)

    prompt = f"""You are analyzing emails to extract personal information about a contact.

CONTACT: {contact.get('name', 'Unknown')} <{contact.get('email', '')}>

CURRENT KNOWN INFO (do not repeat these -- only extract NEW information):
{json.dumps(current_fields, indent=2) if current_fields else "None"}

EMAILS:
{emails_text}

Extract any personal or professional information about this contact from the emails above.
Return a JSON object with these rules:
- Only include fields where you found clear evidence in the emails
- Do NOT guess or infer -- only include facts clearly stated or strongly implied
- Do NOT include fields that are already known (listed above)
- For "facts", include interesting personal details that don't fit standard fields

JSON format:
{{
  "company": "their employer or company name",
  "title": "their job title",
  "location": "their city/state/country",
  "hobbies": "comma-separated hobbies or interests",
  "spouse_name": "spouse/partner name",
  "children": "info about children",
  "pets": "info about pets",
  "phone": "phone number",
  "linkedin": "LinkedIn URL",
  "twitter": "Twitter/X handle or URL",
  "website": "personal or company website URL",
  "birthday": "YYYY-MM-DD format if found",
  "timezone": "e.g. America/New_York",
  "context": "brief description of who this person is and how you know them",
  "relationship": "e.g. colleague, friend, vendor, client",
  "instagram": "Instagram handle",
  "facebook": "Facebook URL",
  "github": "GitHub username or URL",
  "facts": [
    {{"category": "work|family|interests|travel|health|education|other", "fact": "specific fact about this person"}}
  ]
}}

Return ONLY valid JSON, nothing else. If you found nothing new, return {{}}.
"""
    return prompt


def call_llm(prompt: str) -> Optional[dict]:
    """
    Call Claude CLI in non-interactive mode with Haiku for fast extraction.
    Returns parsed JSON dict, or None on failure.
    """
    result = subprocess.run(
        ["claude", "-p", "--model", "haiku", "--output-format", "json"],
        input=prompt,
        capture_output=True,
        text=True,
        timeout=60,
    )

    if result.returncode != 0:
        print(f"  [!] LLM call failed (exit {result.returncode}): {result.stderr[:200]}")
        return None

    # Parse the JSON output from Claude CLI
    # The --output-format json wraps the response, extract the result text
    try:
        outer = json.loads(result.stdout)
        # Claude CLI json format has a "result" field with the text
        text = outer.get("result", result.stdout)
    except (json.JSONDecodeError, AttributeError):
        text = result.stdout

    # Find JSON in the response (may have markdown fences or extra text)
    text = text.strip()
    if text.startswith("```"):
        # Strip markdown code fences
        lines = text.split("\n")
        # Remove first and last lines if they are fences
        if lines[0].startswith("```"):
            lines = lines[1:]
        if lines and lines[-1].strip() == "```":
            lines = lines[:-1]
        text = "\n".join(lines)

    try:
        return json.loads(text)
    except json.JSONDecodeError:
        # Try to find JSON object in the text
        start = text.find("{")
        end = text.rfind("}") + 1
        if start >= 0 and end > start:
            try:
                return json.loads(text[start:end])
            except json.JSONDecodeError:
                pass
        print(f"  [!] Failed to parse LLM JSON response")
        return None


def apply_enrichment(contact_id: int, contact: dict, extracted: dict, dry_run: bool) -> dict:
    """
    Apply extracted data to vault contact fields and memories.
    Only fills empty fields -- never overwrites existing data.
    Returns summary of what was updated.
    """
    summary = {"fields_updated": [], "memories_added": 0, "skipped_fields": []}

    # Separate facts from contact fields
    facts = extracted.pop("facts", [])

    # Update empty contact fields
    updates = {}
    for field in ENRICHABLE_FIELDS:
        new_value = extracted.get(field)
        if not new_value:
            continue

        current_value = contact.get(field)
        if current_value:
            summary["skipped_fields"].append(field)
            continue

        updates[field] = new_value
        summary["fields_updated"].append(field)

    if updates and not dry_run:
        update_contact(contact_id, **updates)

    # Add facts as memories
    if facts and isinstance(facts, list):
        # Get existing memories to avoid duplicates
        existing = get_memories(contact_id)
        existing_facts = {m["fact"].lower() for m in existing}

        for fact_item in facts:
            if not isinstance(fact_item, dict):
                continue
            fact_text = fact_item.get("fact", "")
            category = fact_item.get("category", "other")
            if not fact_text:
                continue
            if fact_text.lower() in existing_facts:
                continue

            if not dry_run:
                add_memory(
                    contact_id=contact_id,
                    category=category,
                    fact=fact_text,
                    source="gmail-enrichment",
                    source_date=TODAY,
                    confidence="inferred",
                )
            summary["memories_added"] += 1

    return summary


def enrich_contact(gmail: GmailClient, contact: dict, dry_run: bool) -> Optional[dict]:
    """
    Run the full enrichment pipeline for a single contact.
    Returns summary dict or None if skipped.
    """
    contact_id = contact["id"]
    email = contact["email"]
    name = contact.get("name", "Unknown")

    print(f"\n  [{contact_id}] {name} <{email}>")

    # Step 1: Fetch emails
    emails = fetch_emails(gmail, email)
    if not emails:
        print(f"    No emails found, skipping")
        return None

    print(f"    Found {len(emails)} emails")

    # Step 2: Build prompt and call LLM
    prompt = build_prompt(contact, emails)
    extracted = call_llm(prompt)
    if not extracted:
        print(f"    LLM extraction returned nothing")
        return None

    # Step 3: Apply to vault
    summary = apply_enrichment(contact_id, contact, extracted, dry_run)

    # Report
    prefix = "[DRY RUN] " if dry_run else ""
    if summary["fields_updated"]:
        print(f"    {prefix}Updated fields: {', '.join(summary['fields_updated'])}")
    if summary["memories_added"]:
        print(f"    {prefix}Added {summary['memories_added']} memories")
    if summary["skipped_fields"]:
        print(f"    Skipped (already set): {', '.join(summary['skipped_fields'])}")
    if not summary["fields_updated"] and not summary["memories_added"]:
        print(f"    Nothing new extracted")

    return summary


def main():
    parser = argparse.ArgumentParser(description="Enrich vault contacts from Gmail emails")
    parser.add_argument("--contact-id", type=int, help="Enrich a single contact by ID")
    parser.add_argument("--account", default="personal", help="Gmail account name (default: personal)")
    parser.add_argument("--limit", type=int, help="Process only first N contacts")
    parser.add_argument("--dry-run", action="store_true", help="Preview only, don't write to vault")
    parser.add_argument("--reset", action="store_true", help="Clear state file and start over")
    args = parser.parse_args()

    # Init vault DB
    init_db(silent=True)

    # Handle reset
    if args.reset:
        if STATE_FILE.exists():
            STATE_FILE.unlink()
            print("State file cleared.")
        else:
            print("No state file to clear.")
        if not args.contact_id and not args.limit:
            return

    # Load state
    state = load_state()

    # Connect to Gmail
    print(f"Connecting to Gmail account: {args.account}")
    account_name = resolve_account(args.account)
    creds = load_credentials(account_name)
    if not creds:
        print(f"ERROR: Gmail account '{account_name}' not authenticated.")
        print(f"Run: cc-gmail auth --account {account_name}")
        sys.exit(1)
    gmail = GmailClient(creds)

    # Get contacts to process -- only Gmail-extracted contacts (lead_source='sent_items')
    if args.contact_id:
        contact = get_contact_by_id(args.contact_id)
        if not contact:
            print(f"ERROR: Contact #{args.contact_id} not found in vault")
            sys.exit(1)
        contacts = [contact]
    else:
        conn = get_db()
        cursor = conn.cursor()
        cursor.execute(
            "SELECT * FROM contacts WHERE lead_source = 'sent_items' ORDER BY name"
        )
        contacts = [dict(row) for row in cursor.fetchall()]
        conn.close()

    total = len(contacts)
    print(f"Total contacts in vault: {total}")

    # Filter out already processed (unless single contact or reset)
    if not args.contact_id:
        processed_ids = set(state.get("processed", []))
        contacts = [c for c in contacts if c["id"] not in processed_ids]
        skipped = total - len(contacts)
        if skipped > 0:
            print(f"Already processed: {skipped} (use --reset to redo)")

    # Apply limit
    if args.limit and len(contacts) > args.limit:
        contacts = contacts[:args.limit]

    print(f"Contacts to process: {len(contacts)}")
    if args.dry_run:
        print("MODE: Dry run (no changes will be written)")

    # Process each contact
    enriched = 0
    errors = 0
    for i, contact in enumerate(contacts):
        try:
            summary = enrich_contact(gmail, contact, args.dry_run)
            if summary:
                enriched += 1

            # Track as processed (even if nothing was extracted)
            if not args.dry_run and not args.contact_id:
                state["processed"].append(contact["id"])
                save_state(state)

        except Exception as ex:
            print(f"    ERROR: {ex}")
            errors += 1
            if not args.contact_id:
                state["errors"].append({"id": contact["id"], "error": str(ex)})
                save_state(state)

        # Rate limit between LLM calls
        if i < len(contacts) - 1:
            time.sleep(LLM_DELAY_SECONDS)

    # Final summary
    print(f"\n{'=' * 50}")
    print(f"DONE: {enriched} enriched, {len(contacts) - enriched - errors} unchanged, {errors} errors")
    print(f"Total processed this session: {len(contacts)}")
    if not args.dry_run:
        print(f"State saved to: {STATE_FILE}")


if __name__ == "__main__":
    main()

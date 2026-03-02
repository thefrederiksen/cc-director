"""
LinkedIn Contact Enrichment Script

Visits LinkedIn profiles for vault contacts that have a LinkedIn URL but no name,
extracts profile data via cc-linkedin, and updates the vault via cc-vault.

Usage:
    python scripts/enrich-contacts.py                  # Run enrichment
    python scripts/enrich-contacts.py --dry-run        # Preview without changes
    python scripts/enrich-contacts.py --max 10         # Limit to 10 contacts
    python scripts/enrich-contacts.py --reset          # Reset state and start over

Rate limits:
    - 45-90 seconds between profiles (random jitter)
    - 30 profiles per session, then 15-30 min cool-down
    - 150 profiles per day max
"""

import argparse
import json
import logging
import random
import sqlite3
import subprocess
import sys
import time
from datetime import datetime, date
from pathlib import Path

# State and log files
SCRIPT_DIR = Path(__file__).parent
STATE_FILE = SCRIPT_DIR / "enrich-state.json"
LOG_FILE = SCRIPT_DIR / "enrich-contacts.log"

# Vault database
VAULT_DB = Path.home() / "AppData" / "Local" / "cc-director" / "vault" / "vault.db"

# Rate limiting
DELAY_MIN = 45
DELAY_MAX = 90
SESSION_LIMIT = 30
DAILY_LIMIT = 150
COOLDOWN_MIN = 15 * 60  # 15 minutes in seconds
COOLDOWN_MAX = 30 * 60  # 30 minutes in seconds

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(message)s",
    handlers=[
        logging.FileHandler(LOG_FILE, encoding="utf-8"),
        logging.StreamHandler(sys.stdout),
    ],
)
log = logging.getLogger(__name__)


def load_state() -> dict:
    """Load enrichment state from file."""
    if STATE_FILE.exists():
        with open(STATE_FILE, "r") as f:
            return json.load(f)
    return {
        "processed_ids": [],
        "failed_ids": [],
        "not_found_ids": [],
        "today": None,
        "today_count": 0,
    }


def save_state(state: dict):
    """Save enrichment state to file."""
    with open(STATE_FILE, "w") as f:
        json.dump(state, f, indent=2)


def get_blank_contacts() -> list:
    """Query vault for contacts with LinkedIn URLs but no name."""
    db = sqlite3.connect(str(VAULT_DB))
    db.row_factory = sqlite3.Row
    rows = db.execute("""
        SELECT id, linkedin
        FROM contacts
        WHERE (name IS NULL OR name = '')
        AND linkedin IS NOT NULL AND linkedin != ''
        ORDER BY id
    """).fetchall()
    db.close()
    return [{"id": r["id"], "linkedin": r["linkedin"]} for r in rows]


def extract_username(linkedin_url: str) -> str:
    """Extract username from LinkedIn URL."""
    # Handle https://linkedin.com/in/username or https://www.linkedin.com/in/username
    import re
    match = re.search(r"/in/([a-zA-Z0-9_-]+)", linkedin_url)
    if match:
        return match.group(1)
    return ""


def run_linkedin_enrich(username: str) -> dict:
    """Call cc-linkedin enrich and return parsed JSON."""
    try:
        result = subprocess.run(
            ["cc-linkedin", "enrich", username],
            capture_output=True,
            text=True,
            timeout=60,
            encoding="utf-8",
            errors="replace",
        )
        if result.returncode != 0:
            log.error("cc-linkedin enrich failed for %s: %s", username, result.stderr.strip())
            return {"profile_exists": False, "error": result.stderr.strip()}

        output = result.stdout.strip()
        if not output:
            return {"profile_exists": False, "error": "Empty output"}

        return json.loads(output)

    except subprocess.TimeoutExpired:
        log.error("cc-linkedin enrich timed out for %s", username)
        return {"profile_exists": False, "error": "Timeout"}
    except json.JSONDecodeError as e:
        log.error("Invalid JSON from cc-linkedin for %s: %s", username, e)
        return {"profile_exists": False, "error": str(e)}


def run_vault_enrich(contact_id: int, profile_data: dict) -> bool:
    """Call cc-vault contacts enrich with the profile data."""
    try:
        data_json = json.dumps(profile_data, ensure_ascii=False)
        result = subprocess.run(
            ["cc-vault", "contacts", "enrich", str(contact_id), data_json],
            capture_output=True,
            text=True,
            timeout=30,
            encoding="utf-8",
            errors="replace",
        )
        if result.returncode != 0:
            log.error("cc-vault enrich failed for #%d: %s", contact_id, result.stderr.strip())
            return False
        log.info("  -> %s", result.stdout.strip())
        return True

    except subprocess.TimeoutExpired:
        log.error("cc-vault enrich timed out for #%d", contact_id)
        return False


def main():
    parser = argparse.ArgumentParser(description="Enrich vault contacts from LinkedIn profiles")
    parser.add_argument("--dry-run", action="store_true", help="Preview without making changes")
    parser.add_argument("--max", type=int, default=0, help="Max contacts to process (0 = use daily limit)")
    parser.add_argument("--reset", action="store_true", help="Reset state and start over")
    parser.add_argument("--delay-min", type=int, default=DELAY_MIN, help=f"Min delay between profiles (default: {DELAY_MIN}s)")
    parser.add_argument("--delay-max", type=int, default=DELAY_MAX, help=f"Max delay between profiles (default: {DELAY_MAX}s)")
    args = parser.parse_args()

    if args.reset:
        if STATE_FILE.exists():
            STATE_FILE.unlink()
        log.info("State reset")

    state = load_state()
    today = date.today().isoformat()

    # Reset daily counter if new day
    if state.get("today") != today:
        state["today"] = today
        state["today_count"] = 0

    # Check daily limit
    remaining_today = DAILY_LIMIT - state["today_count"]
    if remaining_today <= 0:
        log.info("Daily limit reached (%d profiles). Try again tomorrow.", DAILY_LIMIT)
        return

    # Get contacts to process
    all_blank = get_blank_contacts()
    processed_set = set(state["processed_ids"] + state["failed_ids"] + state["not_found_ids"])
    pending = [c for c in all_blank if c["id"] not in processed_set]

    log.info("Total blank contacts: %d", len(all_blank))
    log.info("Already processed: %d", len(processed_set))
    log.info("Remaining: %d", len(pending))
    log.info("Today's count: %d / %d", state["today_count"], DAILY_LIMIT)

    if not pending:
        log.info("All contacts have been processed.")
        return

    # Determine how many to process this run
    max_this_run = args.max if args.max > 0 else remaining_today
    max_this_run = min(max_this_run, remaining_today, len(pending))

    log.info("Will process %d contacts this run", max_this_run)

    if args.dry_run:
        log.info("DRY RUN - showing first %d contacts:", min(max_this_run, 10))
        for c in pending[:min(max_this_run, 10)]:
            username = extract_username(c["linkedin"])
            log.info("  #%d -> %s", c["id"], username)
        return

    session_count = 0
    processed_this_run = 0

    for contact in pending[:max_this_run]:
        username = extract_username(contact["linkedin"])
        if not username:
            log.warning("Skipping #%d: could not extract username from %s", contact["id"], contact["linkedin"])
            state["failed_ids"].append(contact["id"])
            save_state(state)
            continue

        log.info("[%d/%d] Processing #%d: %s", processed_this_run + 1, max_this_run, contact["id"], username)

        # Session cool-down
        if session_count >= SESSION_LIMIT:
            cooldown = random.randint(COOLDOWN_MIN, COOLDOWN_MAX)
            log.info("Session limit reached (%d). Cooling down for %d minutes...", SESSION_LIMIT, cooldown // 60)
            time.sleep(cooldown)
            session_count = 0

        # Extract profile data
        profile_data = run_linkedin_enrich(username)

        if not profile_data.get("profile_exists", False):
            log.info("  Profile not found for %s", username)
            # Still update vault to record the attempt
            run_vault_enrich(contact["id"], profile_data)
            state["not_found_ids"].append(contact["id"])
        else:
            name = profile_data.get("name") or "(no name extracted)"
            log.info("  Found: %s", name)
            success = run_vault_enrich(contact["id"], profile_data)
            if success:
                state["processed_ids"].append(contact["id"])
            else:
                state["failed_ids"].append(contact["id"])

        state["today_count"] += 1
        session_count += 1
        processed_this_run += 1
        save_state(state)

        # Random delay before next profile
        if processed_this_run < max_this_run:
            delay = random.randint(args.delay_min, args.delay_max)
            log.info("  Waiting %d seconds...", delay)
            time.sleep(delay)

    log.info("Done. Processed %d contacts this run.", processed_this_run)
    log.info("Total processed: %d, Not found: %d, Failed: %d",
             len(state["processed_ids"]), len(state["not_found_ids"]), len(state["failed_ids"]))


if __name__ == "__main__":
    main()

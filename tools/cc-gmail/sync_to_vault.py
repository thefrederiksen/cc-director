"""Sync extracted Gmail sent contacts into cc-vault.

Reads sent_contacts.json, checks each against cc-vault contacts search,
and adds missing ones.
"""

import json
import subprocess
import re
import sys


def clean_email(email):
    """Clean up malformed email addresses."""
    # Remove wrapping quotes and escaped quotes
    email = email.strip('"').replace('\\"', '')
    # Extract email from "name <email>" format that leaked into the email field
    match = re.search(r'<([^>]+)>', email)
    if match:
        return match.group(1).strip().lower()
    # Remove leading ? or special chars
    email = email.lstrip('?')
    # Must contain @
    if '@' not in email:
        return None
    return email.strip().lower()


def is_real_contact(email):
    """Filter out automated/system addresses."""
    skip = [
        'noreply', 'no-reply', 'notifications@', 'mailer-daemon',
        'donotreply', 'do-not-reply', 'unsubscribe', '@rts.kijiji',
        'inbound.postmarkapp.com', '@reply.', '@bounce.',
        'calendar-notification', '@docs.google.com',
        'forwarding-noreply@google.com', '@e.', '@em.',
        '@mail.', '@email.', '@info.', '@news.',
    ]
    return not any(p in email for p in skip)


def vault_search(email):
    """Search vault for a contact by email."""
    try:
        result = subprocess.run(
            ['cc-vault', 'contacts', 'search', email],
            capture_output=True, text=True, timeout=10,
            encoding='utf-8', errors='replace',
        )
        # If search finds matches it prints them
        output = result.stdout + result.stderr
        # Check if the email appears in the output (case insensitive)
        return email.lower() in output.lower()
    except Exception:
        return False


def vault_add(name, email):
    """Add a contact to the vault."""
    display_name = name if name else email.split('@')[0]
    cmd = ['cc-vault', 'contacts', 'add', display_name, '--email', email]
    try:
        result = subprocess.run(
            cmd, capture_output=True, text=True, timeout=10,
            encoding='utf-8', errors='replace',
        )
        return result.returncode == 0
    except Exception:
        return False


def main():
    with open('sent_contacts.json', 'r', encoding='utf-8') as f:
        contacts = json.load(f)

    print(f"Loaded {len(contacts)} raw contacts from sent_contacts.json")

    # Clean and filter
    cleaned = []
    for c in contacts:
        email = clean_email(c['email'])
        if not email:
            continue
        if not is_real_contact(email):
            continue
        name = c.get('name', '').strip()
        # Don't use email-as-name
        if name and '@' in name:
            name = ''
        cleaned.append({'email': email, 'name': name})

    # Deduplicate by email
    seen = {}
    for c in cleaned:
        if c['email'] not in seen or (c['name'] and not seen[c['email']]):
            seen[c['email']] = c['name']

    unique = [{'email': e, 'name': n} for e, n in sorted(seen.items())]
    print(f"After cleaning/filtering: {len(unique)} real contacts")

    # Check vault add help to understand required args
    result = subprocess.run(
        ['cc-vault', 'contacts', 'add', '--help'],
        capture_output=True, text=True, timeout=10,
        encoding='utf-8', errors='replace',
    )
    print(f"\ncc-vault contacts add help:\n{result.stdout}")

    # Search vault for each and add missing
    added = 0
    skipped = 0
    already_exists = 0
    failed = 0

    for i, c in enumerate(unique):
        email = c['email']
        name = c['name']

        # Check if already in vault
        if vault_search(email):
            already_exists += 1
            continue

        # Add to vault
        if vault_add(name, email):
            added += 1
            label = f"{name} <{email}>" if name else email
            print(f"  [+] Added: {label}")
        else:
            failed += 1
            print(f"  [-] Failed: {email}")

        # Progress
        if (i + 1) % 50 == 0:
            print(f"  ... progress: {i+1}/{len(unique)}")

    print(f"\n--- Summary ---")
    print(f"Total unique contacts: {len(unique)}")
    print(f"Already in vault:      {already_exists}")
    print(f"Added:                 {added}")
    print(f"Failed:                {failed}")


if __name__ == '__main__':
    main()

"""Vault integration for music recommendations.

Shells out to cc-vault CLI to query personal music preferences.
Per CLAUDE.md: always use cc-vault CLI, never query vault directly.
"""

import subprocess
import re
from typing import Optional


def get_recommendations(mood: Optional[str] = None) -> list[str]:
    """Query vault for music preferences and return suggestions.

    Args:
        mood: Optional mood/genre hint to focus recommendations.

    Returns:
        List of search suggestions (artist names, genre + mood combos, etc.)
    """
    query = "music preferences, favorite artists, genres, playlists"
    if mood:
        query = f"music preferences for {mood} mood, favorite {mood} artists and genres"

    result = subprocess.run(
        ["cc-vault", "ask", query],
        capture_output=True,
        text=True,
        timeout=30,
    )

    if result.returncode != 0:
        return []

    output = result.stdout.strip()
    if not output:
        return []

    return _parse_vault_response(output, mood)


def _parse_vault_response(response: str, mood: Optional[str] = None) -> list[str]:
    """Parse vault response into actionable search suggestions.

    Looks for artist names, genres, and specific recommendations.
    """
    suggestions = []

    # Extract items from bullet points or lists
    lines = response.split("\n")
    for line in lines:
        line = line.strip()
        # Skip empty lines and headers
        if not line or line.startswith("#"):
            continue

        # Clean bullet points
        cleaned = re.sub(r"^[-*+]\s*", "", line)
        cleaned = re.sub(r"^\d+\.\s*", "", cleaned)
        cleaned = cleaned.strip()

        if not cleaned or len(cleaned) < 3:
            continue

        # Skip generic statements, keep names/genres
        if any(skip in cleaned.lower() for skip in [
            "no music", "not found", "no data", "no preferences",
            "i don't", "i couldn't", "unable to",
        ]):
            continue

        # If it looks like an artist or genre, add it
        if len(cleaned) < 100:
            suggestions.append(cleaned)

    # If mood specified, prefix suggestions with mood
    if mood and suggestions:
        mood_suggestions = [f"{mood} {s}" for s in suggestions[:3]]
        return mood_suggestions + suggestions[:5]

    return suggestions[:10]

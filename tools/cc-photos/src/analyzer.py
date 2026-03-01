"""AI-powered image analysis for generating descriptions."""

import json
import re
import sqlite3
import sys
from pathlib import Path
from typing import List, Optional, Tuple

from rich.progress import Progress, TaskID

from . import database as db


# Try to import LLM providers
try:
    from cc_shared import get_llm_provider, LLMProvider
except ImportError:
    cc_shared_path = Path(__file__).parent.parent.parent.parent / "cc_shared"
    if cc_shared_path.exists():
        sys.path.insert(0, str(cc_shared_path.parent))
        try:
            from cc_shared import get_llm_provider, LLMProvider
        except ImportError:
            get_llm_provider = None
            LLMProvider = None
    else:
        get_llm_provider = None
        LLMProvider = None


ANALYSIS_PROMPT = """Analyze this image and provide:
1. A concise description (2-3 sentences) of what the image shows
2. Key subjects/objects visible
3. Setting or location if identifiable
4. Any notable details or text visible

Be factual and concise. Output only the description, no formatting or headers."""


def analyze_image(
    image_path: Path,
    provider_name: Optional[str] = None,
) -> Tuple[str, List[str], str, str]:
    """Analyze an image using AI.

    Args:
        image_path: Path to the image file
        provider_name: Optional provider name to use

    Returns:
        Tuple of (description, keywords, provider_name, model)
    """
    if get_llm_provider is None:
        raise ImportError("LLM providers not available. Install cc_shared.")

    provider = get_llm_provider(provider_name)

    # Get description
    description = provider.describe_image(image_path, ANALYSIS_PROMPT)

    # Extract keywords from description
    keywords = extract_keywords(description)

    return description, keywords, provider.name, ""


def extract_keywords(text: str) -> List[str]:
    """Extract keywords from description text."""
    stop_words = {
        "a", "an", "the", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "must", "shall", "can", "this", "that",
        "these", "those", "i", "you", "he", "she", "it", "we", "they",
        "what", "which", "who", "when", "where", "why", "how", "all",
        "each", "every", "both", "few", "more", "most", "other", "some",
        "such", "no", "nor", "not", "only", "own", "same", "so", "than",
        "too", "very", "just", "also", "now", "here", "there", "then",
        "once", "and", "but", "or", "if", "because", "as", "until",
        "while", "of", "at", "by", "for", "with", "about", "against",
        "between", "into", "through", "during", "before", "after",
        "above", "below", "to", "from", "up", "down", "in", "out",
        "on", "off", "over", "under", "again", "further", "then",
        "image", "shows", "appears", "seems", "looks", "visible",
        "see", "seen", "show", "showing", "appear", "appearing",
    }

    words = re.findall(r'\b[a-zA-Z]{3,}\b', text.lower())
    keywords = []

    for word in words:
        if word not in stop_words and word not in keywords:
            keywords.append(word)
            if len(keywords) >= 10:
                break

    return keywords


def analyze_photos(
    limit: Optional[int] = None,
    provider_name: Optional[str] = None,
    progress: Optional[Progress] = None,
    task_id: Optional[TaskID] = None,
) -> Tuple[int, int, List[str]]:
    """Analyze unanalyzed photos.

    Args:
        limit: Maximum number of photos to analyze
        provider_name: Optional provider name to use
        progress: Optional Rich progress instance
        task_id: Optional task ID for progress updates

    Returns:
        Tuple of (analyzed_count, error_count, errors)
    """
    photos = db.get_unanalyzed_photos(limit=limit)

    if progress and task_id is not None:
        progress.update(task_id, total=len(photos))

    analyzed = 0
    errors = []

    for photo in photos:
        try:
            image_path = Path(photo['file_path'])

            if not image_path.exists():
                errors.append(f"File not found: {photo['file_path']}")
                if progress and task_id is not None:
                    progress.advance(task_id)
                continue

            description, keywords, prov_name, model = analyze_image(
                image_path, provider_name=provider_name
            )

            # Store analysis
            keywords_json = json.dumps(keywords) if keywords else None
            db.add_analysis(
                photo_id=photo['id'],
                description=description,
                keywords=keywords_json,
                provider=prov_name,
                model=model,
            )

            analyzed += 1

        except (OSError, ValueError, RuntimeError, sqlite3.Error) as e:
            errors.append(f"{photo['file_path']}: {e}")

        if progress and task_id is not None:
            progress.advance(task_id)

    return analyzed, len(errors), errors

"""HTML document generator with CSS embedding."""

import base64
import mimetypes
from pathlib import Path
from typing import Optional

from bs4 import BeautifulSoup

from .parser import ParsedMarkdown


HTML_TEMPLATE = """<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>{title}</title>
    <style>
{css}
    </style>
</head>
<body>
    <article class="markdown-body">
{content}
    </article>
</body>
</html>
"""


def generate_html(parsed: ParsedMarkdown, css: str) -> str:
    """
    Generate standalone HTML document with embedded CSS.

    Args:
        parsed: ParsedMarkdown object with HTML content
        css: CSS stylesheet content

    Returns:
        Complete HTML document as string
    """
    title = parsed.title or "Document"

    # Indent CSS for cleaner output
    css_indented = "\n".join(f"        {line}" for line in css.split("\n"))

    # Indent content for cleaner output
    content_indented = "\n".join(f"        {line}" for line in parsed.html.split("\n"))

    return HTML_TEMPLATE.format(
        title=title,
        css=css_indented,
        content=content_indented,
    )


def embed_images_as_base64(html_content: str, base_path: Optional[Path] = None) -> str:
    """
    Embed all images in HTML as base64 data URIs.

    Args:
        html_content: HTML string with img tags
        base_path: Base path to resolve relative image paths from

    Returns:
        HTML with images embedded as base64 data URIs
    """
    if base_path is None:
        return html_content

    soup = BeautifulSoup(html_content, 'html.parser')

    for img in soup.find_all('img'):
        src = img.get('src', '')
        if not src or src.startswith('data:'):
            continue

        # Resolve the image path relative to the base path
        if src.startswith(('http://', 'https://')):
            # Skip remote URLs
            continue
        elif src.startswith('file:///'):
            # Handle file:// URLs
            img_path = Path(src[8:])
        else:
            # Relative path - resolve from base directory
            img_path = (base_path / src).resolve()

        if not img_path.exists():
            continue

        # Read and encode the image
        try:
            with open(img_path, 'rb') as f:
                img_data = f.read()

            # Determine MIME type
            mime_type, _ = mimetypes.guess_type(str(img_path))
            if not mime_type:
                mime_type = 'image/png'

            # Create data URI
            b64_data = base64.b64encode(img_data).decode('utf-8')
            data_uri = f"data:{mime_type};base64,{b64_data}"

            img['src'] = data_uri
        except Exception:
            # If we can't read the image, leave the original src
            pass

    return str(soup)

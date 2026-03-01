"""PDF conversion using Chrome headless."""

import os
import subprocess
import tempfile
from pathlib import Path
from typing import Optional


# Page size dimensions
PAGE_SIZES = {
    "a4": {"width": "8.27in", "height": "11.69in"},
    "letter": {"width": "8.5in", "height": "11in"},
}


def find_chrome() -> Optional[str]:
    """Find Chrome executable on the system."""
    chrome_paths = [
        # Windows paths
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        rf"C:\Users\{os.getenv('USERNAME', '')}\AppData\Local\Google\Chrome\Application\chrome.exe",
        # Linux paths
        "/usr/bin/google-chrome",
        "/usr/bin/google-chrome-stable",
        "/usr/bin/chromium",
        "/usr/bin/chromium-browser",
        # macOS paths
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        "/Applications/Chromium.app/Contents/MacOS/Chromium",
    ]

    for path in chrome_paths:
        if os.path.exists(path):
            return path
    return None


def convert_to_pdf(
    html_content: str,
    output_path: Path,
    page_size: str = "a4",
    margin: str = "1in",
) -> None:
    """
    Convert HTML to PDF using Chrome headless.

    Args:
        html_content: Complete HTML document string
        output_path: Path for output PDF file
        page_size: Page size ('a4' or 'letter')
        margin: Page margin (e.g., '1in', '2cm')

    Raises:
        RuntimeError: If Chrome is not found or conversion fails
    """
    # Validate page size
    page_size = page_size.lower()
    if page_size not in PAGE_SIZES:
        raise ValueError(f"Unknown page size: {page_size}. Use 'a4' or 'letter'.")

    # Find Chrome
    chrome_exe = find_chrome()
    if not chrome_exe:
        raise RuntimeError(
            "Chrome not found. Please install Google Chrome for PDF conversion.\n"
            "Download from: https://www.google.com/chrome/"
        )

    # Ensure output directory exists
    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)

    # Write HTML to temp file
    with tempfile.NamedTemporaryFile(
        mode='w', suffix='.html', delete=False, encoding='utf-8'
    ) as f:
        f.write(html_content)
        temp_html = f.name

    try:
        abs_html_path = os.path.abspath(temp_html)
        abs_output_path = os.path.abspath(str(output_path))

        # Convert to file URI (Chrome requires this format)
        unix_path = abs_html_path.replace('\\', '/')
        file_uri = f"file:///{unix_path}"

        # Build Chrome command
        cmd = [
            chrome_exe,
            '--headless',
            '--disable-gpu',
            '--disable-software-rasterizer',
            '--disable-dev-shm-usage',
            '--no-sandbox',
            '--no-pdf-header-footer',
            '--allow-file-access-from-files',
            f'--print-to-pdf={abs_output_path}',
            '--virtual-time-budget=3000',
            file_uri
        ]

        # Run Chrome
        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=60,
        )

        if result.returncode != 0 or not os.path.exists(abs_output_path):
            error_msg = result.stderr if result.stderr else "Unknown error"
            raise RuntimeError(f"PDF conversion failed: {error_msg}")

    finally:
        # Clean up temp file
        if os.path.exists(temp_html):
            os.unlink(temp_html)

"""
GitHub API helper for fetching release information.
"""

import json
import urllib.request
import urllib.error
from typing import Optional


GITHUB_API_BASE = "https://api.github.com"
GITHUB_RAW_BASE = "https://raw.githubusercontent.com"
REPO_OWNER = "cc-director"
REPO_NAME = "cc-tools"


def get_latest_release() -> Optional[dict]:
    """
    Fetch the latest release from GitHub.

    Returns:
        Release info dict or None if no releases found.
    """
    url = f"{GITHUB_API_BASE}/repos/{REPO_OWNER}/{REPO_NAME}/releases/latest"

    try:
        request = urllib.request.Request(
            url,
            headers={
                "Accept": "application/vnd.github.v3+json",
                "User-Agent": "cc-tools-setup"
            }
        )
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        if e.code == 404:
            return None
        raise


def get_release_assets(release: dict) -> dict:
    """
    Get asset download URLs from a release.

    Args:
        release: Release info dict from GitHub API

    Returns:
        Dict mapping asset names to download URLs
    """
    assets = {}
    for asset in release.get("assets", []):
        name = asset.get("name", "")
        url = asset.get("browser_download_url", "")
        if name and url:
            assets[name] = url
    return assets


def download_file(url: str, dest_path: str, show_progress: bool = True) -> bool:
    """
    Download a file from URL to destination path.

    Args:
        url: URL to download from
        dest_path: Local path to save file
        show_progress: Whether to show download progress

    Returns:
        True if successful, False otherwise
    """
    try:
        request = urllib.request.Request(
            url,
            headers={"User-Agent": "cc-tools-setup"}
        )

        with urllib.request.urlopen(request, timeout=300) as response:
            total_size = int(response.headers.get("Content-Length", 0))
            downloaded = 0
            block_size = 8192

            with open(dest_path, "wb") as f:
                while True:
                    chunk = response.read(block_size)
                    if not chunk:
                        break
                    f.write(chunk)
                    downloaded += len(chunk)

                    if show_progress and total_size > 0:
                        percent = (downloaded / total_size) * 100
                        mb_downloaded = downloaded / (1024 * 1024)
                        mb_total = total_size / (1024 * 1024)
                        print(f"\r  Progress: {percent:.1f}% ({mb_downloaded:.1f}/{mb_total:.1f} MB)", end="")

            if show_progress:
                print()  # New line after progress

        return True

    except urllib.error.URLError as e:
        print(f"  Download failed: {e}")
        return False
    except OSError as e:
        print(f"  Download failed: {e}")
        return False


def download_raw_file(path: str, dest_path: str, branch: str = "main") -> bool:
    """
    Download a raw file from the repository.

    Args:
        path: Path within the repository (e.g., "skills/cc-tools/SKILL.md")
        dest_path: Local path to save file
        branch: Git branch to download from

    Returns:
        True if successful, False otherwise
    """
    url = f"{GITHUB_RAW_BASE}/{REPO_OWNER}/{REPO_NAME}/{branch}/{path}"
    return download_file(url, dest_path, show_progress=False)

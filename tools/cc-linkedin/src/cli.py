"""cc-linkedin CLI - LinkedIn interactions via browser automation."""

import typer
from rich.console import Console
from rich.table import Table
from rich import print as rprint
from typing import Optional
import json
import os
import re
import time
from pathlib import Path

from urllib.parse import quote

try:
    from .browser_client import BrowserClient, BrowserError, WorkspaceError
    from .linkedin_selectors import LinkedInURLs, LinkedIn
    from .delays import jittered_sleep
except ImportError:
    from browser_client import BrowserClient, BrowserError, WorkspaceError
    from linkedin_selectors import LinkedInURLs, LinkedIn
    from delays import jittered_sleep

app = typer.Typer(
    name="cc-linkedin",
    help="LinkedIn CLI via browser automation",
    no_args_is_help=True,
)

console = Console()


def get_config_dir() -> Path:
    """Get cc-linkedin config directory."""
    try:
        from cc_storage import CcStorage
        return CcStorage.tool_config("linkedin")
    except ImportError:
        import sys
        _tools_dir = str(Path(__file__).resolve().parent.parent.parent)
        if _tools_dir not in sys.path:
            sys.path.insert(0, _tools_dir)
        from cc_storage import CcStorage
        return CcStorage.tool_config("linkedin")


def load_default_workspace() -> str:
    """Load default workspace from config.json.

    Returns:
        Default workspace name from config, or 'linkedin' if not configured.

    Raises:
        typer.Exit: If config file is invalid.
    """
    config_file = get_config_dir() / "config.json"

    if not config_file.exists():
        console.print(
            f"[yellow]WARNING:[/yellow] Config file not found: {config_file}\n"
            "Using default workspace: linkedin\n"
            "Create config.json with: {\"default_workspace\": \"linkedin\"}"
        )
        return "linkedin"

    try:
        with open(config_file, "r") as f:
            data = json.load(f)
        return data.get("default_workspace", "linkedin")
    except json.JSONDecodeError as e:
        console.print(f"[red]ERROR:[/red] Invalid JSON in {config_file}: {e}")
        raise typer.Exit(1)
    except IOError as e:
        console.print(f"[red]ERROR:[/red] Cannot read {config_file}: {e}")
        raise typer.Exit(1)


# Global options stored in context
class Config:
    workspace: str = ""
    format: str = "text"
    delay: float = 1.0
    verbose: bool = False


config = Config()


def get_client() -> BrowserClient:
    """Get browser client instance for configured workspace."""
    try:
        return BrowserClient(workspace=config.workspace)
    except WorkspaceError as e:
        console.print(f"[red]ERROR:[/red] {e}")
        raise typer.Exit(1)


def output(data: dict, message: str = "") -> None:
    """Output data in configured format."""
    if config.format == "json":
        # Use print() directly to avoid Rich console encoding issues on Windows
        print(json.dumps(data, ensure_ascii=False))
    elif config.format == "markdown":
        # Convert to markdown representation
        console.print(f"```\n{json.dumps(data, indent=2)}\n```")
    else:
        if message:
            console.print(message)
        elif config.verbose and data:
            console.print(data)


def error(msg: str) -> None:
    """Print error message."""
    console.print(f"[red]ERROR:[/red] {msg}")


def success(msg: str) -> None:
    """Print success message."""
    console.print(f"[green]OK:[/green] {msg}")


def warn(msg: str) -> None:
    """Print warning message."""
    console.print(f"[yellow]WARNING:[/yellow] {msg}")


def note(msg: str) -> None:
    """Print info message."""
    console.print(f"[blue]INFO:[/blue] {msg}")


def find_element_ref(snapshot_text: str, keywords: list[str], element_type: str = "button") -> Optional[str]:
    """Find element ref matching keywords in snapshot.

    Args:
        snapshot_text: The snapshot text to search
        keywords: List of keywords to match (any match)
        element_type: Element type to look for (default: button)

    Returns:
        Element ref string if found, None otherwise
    """
    lines = snapshot_text.split('\n')
    for line in lines:
        line_lower = line.lower()
        if element_type.lower() in line_lower:
            if any(kw.lower() in line_lower for kw in keywords):
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    return match.group(1)
    return None


def find_element_ref_near_text(snapshot_text: str, near_text: str, keywords: list[str], search_range: int = 10) -> Optional[str]:
    """Find element ref near specific text in snapshot.

    Args:
        snapshot_text: The snapshot text to search
        near_text: Text to find first
        keywords: Keywords the element should match
        search_range: Number of lines to search around the text

    Returns:
        Element ref string if found, None otherwise
    """
    lines = snapshot_text.split('\n')
    for i, line in enumerate(lines):
        if near_text.lower() in line.lower():
            # Search nearby lines for element
            for j in range(max(0, i - 5), min(len(lines), i + search_range)):
                line_lower = lines[j].lower()
                if any(kw.lower() in line_lower for kw in keywords):
                    match = re.search(r'\[ref=(\w+)\]', lines[j])
                    if match:
                        return match.group(1)
    return None


@app.callback()
def main(
    workspace: Optional[str] = typer.Option(None, "--workspace", "-w", help="cc-browser workspace name or alias"),
    format: str = typer.Option("text", help="Output format: text, json, markdown"),
    delay: float = typer.Option(1.0, help="Delay between actions (seconds)"),
    verbose: bool = typer.Option(False, "--verbose", "-v", help="Verbose output"),
):
    """LinkedIn CLI via browser automation.

    Requires cc-browser daemon to be running.
    Start it with: cc-browser daemon --workspace linkedin
    """
    # Load default workspace from config if not specified
    if workspace is None:
        workspace = load_default_workspace()

    config.workspace = workspace
    config.format = format
    config.delay = delay
    config.verbose = verbose


# =============================================================================
# Status Commands
# =============================================================================

@app.command()
def status():
    """Check cc-browser daemon and LinkedIn login status."""
    try:
        client = get_client()

        # Check daemon status
        result = client.status()
        console.print("[green]cc-browser daemon:[/green] running")

        browser_status = result.get("browser", "unknown")
        console.print(f"[green]Browser:[/green] {browser_status}")

        # Check if on LinkedIn
        try:
            info = client.info()
            url = info.get("url", "")
            if "linkedin.com" in url:
                console.print(f"[green]Current page:[/green] {url}")
            else:
                console.print(f"[yellow]Current page:[/yellow] {url} (not on LinkedIn)")
        except BrowserError:
            console.print("[yellow]Browser:[/yellow] no page loaded")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def whoami():
    """Show current logged-in LinkedIn user."""
    try:
        client = get_client()

        # Navigate to LinkedIn if not already there
        info = client.info()
        url = info.get("url", "")
        if "linkedin.com" not in url:
            client.navigate(LinkedInURLs.home())
            jittered_sleep(3)

        # Get user info via JavaScript
        js_code = """
        (() => {
            // Try to find user name from the feed identity module
            const identityModule = document.querySelector('.feed-identity-module');
            if (identityModule) {
                const nameEl = identityModule.querySelector('.feed-identity-module__actor-meta a');
                if (nameEl) {
                    const name = nameEl.textContent?.trim() || '';
                    const href = nameEl.getAttribute('href') || '';
                    const usernameMatch = href.match(/\\/in\\/([^/]+)/);
                    const username = usernameMatch ? usernameMatch[1] : '';
                    return JSON.stringify({ name, username });
                }
            }

            // Try navigation profile photo
            const navPhoto = document.querySelector('.global-nav__me-photo');
            if (navPhoto) {
                const altText = navPhoto.getAttribute('alt') || '';
                return JSON.stringify({ name: altText, username: '' });
            }

            // Check if sign in button exists (not logged in)
            const signIn = document.querySelector('a[data-tracking-control-name*="signin"]');
            if (signIn) {
                return JSON.stringify({ error: 'not_logged_in' });
            }

            return JSON.stringify({ error: 'unknown' });
        })()
        """

        result = client.evaluate(js_code)
        result_str = result.get("result", "{}")
        user_data = json.loads(result_str) if isinstance(result_str, str) else result_str

        if user_data.get("error") == "not_logged_in":
            warn("Not logged in")
            console.print("Tip: Log into LinkedIn in the browser first")
        elif user_data.get("error"):
            warn("Could not detect user. Make sure you're logged in.")
        else:
            name = user_data.get("name", "Unknown")
            username = user_data.get("username", "")
            console.print(f"Logged in as: [green]{name}[/green]")
            if username:
                console.print(f"Profile: linkedin.com/in/{username}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def me():
    """View your own LinkedIn profile summary."""
    try:
        client = get_client()

        # Navigate to own profile
        info = client.info()
        url = info.get("url", "")

        # First get username from home page if needed
        if "linkedin.com" not in url:
            client.navigate(LinkedInURLs.home())
            jittered_sleep(3)

        # Click on "View profile" or navigate via identity module
        js_get_profile = """
        (() => {
            const identityModule = document.querySelector('.feed-identity-module');
            if (identityModule) {
                const link = identityModule.querySelector('a[href*="/in/"]');
                if (link) {
                    return link.getAttribute('href');
                }
            }
            return '';
        })()
        """

        result = client.evaluate(js_get_profile)
        profile_url = result.get("result", "")

        if profile_url:
            if not profile_url.startswith("http"):
                profile_url = f"https://www.linkedin.com{profile_url}"
            client.navigate(profile_url)
            jittered_sleep(3)

        # Extract profile info
        js_profile = """
        (() => {
            const name = document.querySelector('h1.text-heading-xlarge')?.textContent?.trim() || '';
            const headline = document.querySelector('div.text-body-medium')?.textContent?.trim() || '';
            const location = document.querySelector('span.text-body-small.inline')?.textContent?.trim() || '';

            // Get connections count
            let connections = '';
            const connectionsEl = document.querySelector('span.t-bold');
            if (connectionsEl && connectionsEl.textContent?.includes('connection')) {
                connections = connectionsEl.textContent.trim();
            } else {
                const allBold = document.querySelectorAll('span.t-bold');
                for (const el of allBold) {
                    if (el.nextSibling?.textContent?.includes('connection')) {
                        connections = el.textContent.trim();
                        break;
                    }
                }
            }

            return JSON.stringify({
                name,
                headline,
                location,
                connections
            });
        })()
        """

        result = client.evaluate(js_profile)
        profile_data = json.loads(result.get("result", "{}"))

        console.print(f"\n[bold]{profile_data.get('name', 'Unknown')}[/bold]")
        if profile_data.get("headline"):
            console.print(f"{profile_data['headline']}")
        if profile_data.get("location"):
            console.print(f"Location: {profile_data['location']}")
        if profile_data.get("connections"):
            console.print(f"Connections: {profile_data['connections']}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Feed Commands
# =============================================================================

@app.command()
def feed(
    limit: int = typer.Option(10, help="Number of posts to show"),
):
    """View LinkedIn home feed."""
    try:
        client = get_client()

        # Navigate to feed
        client.navigate(LinkedInURLs.home())
        jittered_sleep(3)

        # Scroll to load more content
        for _ in range(2):
            client.scroll("down")
            jittered_sleep(1)

        # Extract feed items
        js_feed = """
        (() => {
            const posts = document.querySelectorAll('div.feed-shared-update-v2');
            const data = [];

            for (const post of posts) {
                try {
                    // Get author info
                    const authorEl = post.querySelector('.update-components-actor__name span[aria-hidden="true"]');
                    const author = authorEl?.textContent?.trim() || 'Unknown';

                    const headlineEl = post.querySelector('.update-components-actor__description');
                    const authorHeadline = headlineEl?.textContent?.trim() || '';

                    // Get post content
                    const contentEl = post.querySelector('.feed-shared-text');
                    let content = contentEl?.textContent?.trim() || '';
                    if (content.length > 200) {
                        content = content.substring(0, 200) + '...';
                    }

                    // Get time
                    const timeEl = post.querySelector('.update-components-actor__sub-description');
                    const timeAgo = timeEl?.textContent?.trim()?.split(' ').slice(0, 2).join(' ') || '';

                    // Get engagement counts
                    const reactionsEl = post.querySelector('span.social-details-social-counts__reactions-count');
                    const likes = reactionsEl?.textContent?.trim() || '0';

                    const commentsEl = post.querySelector('button[aria-label*="comment"] span');
                    const comments = commentsEl?.textContent?.trim()?.replace(/[^0-9]/g, '') || '0';

                    // Get post ID from data attribute or URL
                    const urnAttr = post.getAttribute('data-urn') || '';
                    const id = urnAttr.split(':').pop() || '';

                    if (content || author !== 'Unknown') {
                        data.push({
                            id,
                            author,
                            authorHeadline,
                            content,
                            likes,
                            comments,
                            timeAgo
                        });
                    }
                } catch (e) {
                    // Skip problematic posts
                }
            }

            return JSON.stringify(data);
        })()
        """

        result = client.evaluate(js_feed)
        posts_data = json.loads(result.get("result", "[]"))

        console.print(f"\n[bold]LinkedIn Feed[/bold]\n")

        if posts_data:
            if config.format == "json":
                console.print_json(json.dumps(posts_data[:limit]))
            else:
                for i, p in enumerate(posts_data[:limit], 1):
                    console.print(f"[cyan]{i}. {p['author']}[/cyan]")
                    if p.get("authorHeadline"):
                        console.print(f"   [dim]{p['authorHeadline'][:60]}[/dim]")
                    if p.get("content"):
                        console.print(f"   {p['content']}")
                    console.print(f"   [dim]{p.get('timeAgo', '')} | {p.get('likes', '0')} likes | {p.get('comments', '0')} comments[/dim]")
                    console.print()
        else:
            console.print("No posts found in feed.")
            if config.verbose:
                snapshot = client.snapshot()
                console.print(snapshot.get("snapshot", ""))

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def create(
    content: str = typer.Argument(..., help="Post content text"),
    image: Optional[str] = typer.Option(None, "--image", "-i", help="Path to image file to attach"),
):
    """Create a new LinkedIn post."""
    try:
        # Use JS evaluation for reliable clicking (cc-browser clicks fail on modals)
        from cdp_helper import create_post_via_js

        # Get port from browser client config
        client = get_client()
        port = client.port

        if image:
            note(f"Creating post with image: {image}")
        else:
            note("Creating post via JS evaluation...")
        result = create_post_via_js(port, content, image)

        if result.get("success"):
            success("Post created successfully")
        else:
            error(f"Failed to create post: {result.get('error', 'Unknown error')}")
            raise typer.Exit(1)

    except ImportError:
        # Fallback to old method if Playwright not available
        warn("Playwright not available, using legacy method")
        _create_post_legacy(content, image)

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


def _create_post_legacy(content: str, image: Optional[str] = None):
    """Legacy create post method using cc-browser (may fail on visibility modal)."""
    warn("Legacy method - visibility modal clicking may fail")
    success("Use Playwright-based method instead")


def _attach_image_to_post(client: BrowserClient, image_path: str) -> None:
    """Attach an image to the post being created."""
    from pathlib import Path

    image_file = Path(image_path)
    if not image_file.exists():
        warn(f"Image file not found: {image_path}")
        return

    # Get snapshot to find media/image button
    snapshot = client.snapshot()
    snapshot_text = snapshot.get("snapshot", "")

    # Look for media/photo/image button
    media_ref = find_element_ref(snapshot_text, ["media", "photo", "image", "add media", "add a photo"], "button")

    if not media_ref:
        # Try finding via icon or aria-label
        lines = snapshot_text.split('\n')
        for line in lines:
            line_lower = line.lower()
            if ('photo' in line_lower or 'image' in line_lower or 'media' in line_lower) and 'button' in line_lower:
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    media_ref = match.group(1)
                    break

    if media_ref:
        client.click(media_ref)
        jittered_sleep(1)

        # Get snapshot to find file input
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        # Find file input element ref
        file_input_ref = None
        lines = snapshot_text.split('\n')
        for line in lines:
            line_lower = line.lower()
            if 'file' in line_lower and ('input' in line_lower or 'upload' in line_lower):
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    file_input_ref = match.group(1)
                    break

        if file_input_ref:
            # Use the browser client's upload method
            try:
                client.upload(file_input_ref, str(image_file.absolute()))
                jittered_sleep(2)
                console.print(f"[dim]Image attached: {image_file.name}[/dim]")
            except Exception as e:
                warn(f"Could not attach image: {e}")
        else:
            # Try using JavaScript to find and trigger the file input
            js_find_input = """
            (() => {
                const fileInputs = document.querySelectorAll('input[type="file"]');
                if (fileInputs.length > 0) {
                    // Return info about first file input
                    const input = fileInputs[0];
                    return JSON.stringify({
                        found: true,
                        id: input.id || '',
                        name: input.name || '',
                        accept: input.accept || ''
                    });
                }
                return JSON.stringify({ found: false });
            })()
            """

            result = client.evaluate(js_find_input)
            input_info = json.loads(result.get("result", "{}"))

            if input_info.get("found"):
                console.print(f"[yellow]File input found but cannot auto-upload.[/yellow]")
                console.print(f"[yellow]Please manually select: {image_file.absolute()}[/yellow]")
                jittered_sleep(5)  # Give user time to manually select
            else:
                warn("Could not find file input for image upload")
    else:
        warn("Could not find media button to attach image")


@app.command()
def post(
    url: str = typer.Argument(..., help="Post URL or activity ID"),
):
    """View a specific LinkedIn post."""
    try:
        client = get_client()

        # Navigate to post
        if url.startswith("http"):
            post_url = url
        else:
            post_url = LinkedInURLs.post(url)

        client.navigate(post_url)
        jittered_sleep(3)

        # Extract post content
        js_post = """
        (() => {
            // Get author
            const authorEl = document.querySelector('.update-components-actor__name span[aria-hidden="true"]');
            const author = authorEl?.textContent?.trim() || 'Unknown';

            const headlineEl = document.querySelector('.update-components-actor__description');
            const authorHeadline = headlineEl?.textContent?.trim() || '';

            // Get content
            const contentEl = document.querySelector('.feed-shared-text');
            const content = contentEl?.textContent?.trim() || '';

            // Get time
            const timeEl = document.querySelector('.update-components-actor__sub-description');
            const timeAgo = timeEl?.textContent?.trim() || '';

            // Get engagement
            const reactionsEl = document.querySelector('span.social-details-social-counts__reactions-count');
            const likes = reactionsEl?.textContent?.trim() || '0';

            const commentsEl = document.querySelector('button[aria-label*="comment"] span');
            const comments = commentsEl?.textContent?.trim()?.replace(/[^0-9]/g, '') || '0';

            return JSON.stringify({
                author,
                authorHeadline,
                content,
                timeAgo,
                likes,
                comments
            });
        })()
        """

        result = client.evaluate(js_post)
        post_data = json.loads(result.get("result", "{}"))

        console.print(f"\n[bold]{post_data.get('author', 'Unknown')}[/bold]")
        if post_data.get("authorHeadline"):
            console.print(f"[dim]{post_data['authorHeadline']}[/dim]")
        console.print()

        if post_data.get("content"):
            console.print(post_data["content"])
        else:
            console.print("[dim](No text content - may be image/video post)[/dim]")

        console.print()
        console.print(f"[dim]{post_data.get('timeAgo', '')} | {post_data.get('likes', '0')} reactions | {post_data.get('comments', '0')} comments[/dim]")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def like(
    url: str = typer.Argument(..., help="Post URL to like"),
):
    """Like a LinkedIn post."""
    try:
        client = get_client()

        # Navigate to post
        if url.startswith("http"):
            client.navigate(url)
        else:
            client.navigate(LinkedInURLs.post(url))
        jittered_sleep(3)

        # Get snapshot to find like button
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        if config.verbose:
            console.print(snapshot_text)

        like_ref = find_element_ref(snapshot_text, ["like"], "button")

        if like_ref:
            client.click(like_ref)
            jittered_sleep(1)
            success("Post liked")
        else:
            error("Could not find like button")
            raise typer.Exit(1)

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def comment(
    url: str = typer.Argument(..., help="Post URL to comment on"),
    text: str = typer.Option(..., "--text", "-t", help="Comment text"),
):
    """Comment on a LinkedIn post."""
    try:
        client = get_client()

        # Navigate to post
        if url.startswith("http"):
            client.navigate(url)
        else:
            client.navigate(LinkedInURLs.post(url))
        jittered_sleep(3)

        # Click comment button to open comment box
        snapshot = client.snapshot()
        comment_btn_ref = find_element_ref(snapshot.get("snapshot", ""), ["comment"], "button")

        if comment_btn_ref:
            client.click(comment_btn_ref)
            jittered_sleep(1)

        # Get new snapshot to find comment input
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        if config.verbose:
            console.print(snapshot_text)

        input_ref = find_element_ref(snapshot_text, ["textbox", "comment"])
        if config.verbose and input_ref:
            console.print(f"Found input: {input_ref}")

        if not input_ref:
            error("Could not find comment input")
            raise typer.Exit(1)

        # Type comment
        client.click(input_ref)
        jittered_sleep(0.5)
        client.type(input_ref, text)
        jittered_sleep(0.5)

        # Find and click post/submit button
        snapshot = client.snapshot()
        submit_ref = find_element_ref(snapshot.get("snapshot", ""), ["post", "submit"], "button")

        if submit_ref:
            client.click(submit_ref)
            jittered_sleep(1)
            success("Comment posted")
        else:
            warn("Could not find submit button. Comment may not have been posted.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Profile Commands
# =============================================================================

@app.command()
def profile(
    username: str = typer.Argument(..., help="LinkedIn username (vanity URL slug)"),
):
    """View someone's LinkedIn profile."""
    try:
        client = get_client()

        # Navigate to profile
        client.navigate(LinkedInURLs.profile(username))
        jittered_sleep(3)

        # Extract profile info including related profiles for spider effect
        js_profile = """
        (() => {
            const name = document.querySelector('h1.text-heading-xlarge')?.textContent?.trim() || '';
            const headline = document.querySelector('div.text-body-medium')?.textContent?.trim() || '';
            const location = document.querySelector('span.text-body-small.inline')?.textContent?.trim() || '';

            // Get connections/followers
            let connections = '';
            const spans = document.querySelectorAll('span');
            for (const span of spans) {
                const text = span.textContent || '';
                if (text.includes('connection') || text.includes('follower')) {
                    const num = span.querySelector('span.t-bold')?.textContent?.trim() ||
                               span.previousElementSibling?.textContent?.trim();
                    if (num) {
                        connections = num + ' ' + (text.includes('connection') ? 'connections' : 'followers');
                        break;
                    }
                }
            }

            // Get about section
            const about = document.querySelector('section.pv-about-section div.pv-shared-text-with-see-more span')?.textContent?.trim() || '';

            // Spider effect: Extract "People also viewed" and similar profiles
            const relatedProfiles = [];
            const seen = new Set();

            // Find all profile links on the page (sidebar, recommendations, etc.)
            const profileLinks = document.querySelectorAll('a[href*="/in/"]');
            for (const link of profileLinks) {
                const href = link.getAttribute('href') || '';
                const match = href.match(/\\/in\\/([a-zA-Z0-9\\-]+)/);
                if (match && match[1]) {
                    const username = match[1];
                    if (!seen.has(username) && username.length > 2) {
                        seen.add(username);
                        // Try to get name from link text or nearby elements
                        const linkName = link.textContent?.trim() || '';
                        if (linkName && linkName.length > 2 && linkName.length < 100) {
                            relatedProfiles.push({
                                username: username,
                                name: linkName.split('\\n')[0].trim()
                            });
                        } else {
                            relatedProfiles.push({ username: username });
                        }
                    }
                }
            }

            return JSON.stringify({
                name,
                headline,
                location,
                connections,
                about: about.substring(0, 300),
                related_profiles: relatedProfiles.slice(0, 20)
            });
        })()
        """

        result = client.evaluate(js_profile)
        profile_data = json.loads(result.get("result", "{}"))

        if config.format == "json":
            print(json.dumps(profile_data, ensure_ascii=False))
        else:
            console.print(f"\n[bold]{profile_data.get('name', 'Unknown')}[/bold]")
            if profile_data.get("headline"):
                console.print(f"{profile_data['headline']}")
            if profile_data.get("location"):
                console.print(f"Location: {profile_data['location']}")
            if profile_data.get("connections"):
                console.print(f"{profile_data['connections']}")
            if profile_data.get("about"):
                console.print(f"\nAbout:\n{profile_data['about']}...")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


def scroll_until_loaded(client: BrowserClient, selector: str, target_count: int, max_stale_scrolls: int = 3) -> int:
    """Scroll page until target element count is reached or no new elements load.

    Args:
        client: BrowserClient instance
        selector: CSS selector to count elements
        target_count: Desired number of elements
        max_stale_scrolls: Consecutive scrolls with no new elements before stopping

    Returns:
        Final element count
    """
    count_js = f"document.querySelectorAll('{selector}').length"
    prev_count = int(client.evaluate(count_js).get("result", "0"))
    stale = 0
    scroll_num = 0
    max_scrolls = 50  # Hard ceiling to prevent infinite loops

    while prev_count < target_count and stale < max_stale_scrolls and scroll_num < max_scrolls:
        client.scroll("down")
        jittered_sleep(2)
        scroll_num += 1

        current_count = int(client.evaluate(count_js).get("result", "0"))
        if current_count <= prev_count:
            stale += 1
        else:
            stale = 0
        prev_count = current_count

        # Brief extra pause every 10 scrolls to reduce detection risk
        if scroll_num % 10 == 0:
            jittered_sleep(3)

        if config.verbose:
            console.print(f"  Loaded {current_count} / {target_count} ...", end="\r")

    if config.verbose:
        console.print(f"  Loaded {prev_count} elements after {scroll_num} scrolls.")

    return prev_count


def _load_existing_connections(filepath: str) -> tuple[list[dict], set[str]]:
    """Load connections from an existing JSON file for resume/append mode.

    Args:
        filepath: Path to existing JSON file.

    Returns:
        Tuple of (connections list, set of usernames already captured).

    Raises:
        typer.Exit: If file cannot be read or parsed.
    """
    try:
        with open(filepath, "r", encoding="utf-8") as f:
            existing_data = json.load(f)
        if isinstance(existing_data, dict) and "connections" in existing_data:
            conns = existing_data["connections"]
        elif isinstance(existing_data, list):
            conns = existing_data
        else:
            conns = []
        usernames = {c["username"] for c in conns if c.get("username")}
        console.print(f"Loaded {len(usernames)} existing connections from {filepath}")
        return conns, usernames
    except (json.JSONDecodeError, IOError) as e:
        error(f"Cannot read append file {filepath}: {e}")
        raise typer.Exit(1)


def _write_connections_json(
    filepath: str,
    connections_data: list[dict],
    search_filter: str = "",
    limit: Optional[int] = None,
) -> None:
    """Write connections data to a JSON file with metadata wrapper.

    Args:
        filepath: Output file path.
        connections_data: List of connection dicts to write.
        search_filter: Search keyword used, if any.
        limit: Limit value used, if relevant.
    """
    from datetime import datetime

    export_data: dict = {
        "exported_at": datetime.now().isoformat(),
        "search_filter": search_filter or None,
        "total": len(connections_data),
        "connections": connections_data,
    }
    if limit is not None:
        export_data["limit"] = limit
    with open(filepath, "w", encoding="utf-8") as f:
        json.dump(export_data, f, indent=2, ensure_ascii=False)


def _filter_connections_search(client: BrowserClient, search_term: str) -> None:
    """Type a search term into the connections page search bar.

    Tries snapshot-based ref typing first, then JS injection as alternate approach.

    Args:
        client: BrowserClient instance.
        search_term: Keyword to type into the search input.
    """
    search_js = """
    (() => {
        const selectors = [
            'input#mn-connections-search-input',
            'input[placeholder*="search" i]',
            'input[placeholder*="Search"]',
            'input[aria-label*="search" i]',
            'input[aria-label*="Search"]'
        ];
        for (const sel of selectors) {
            const el = document.querySelector(sel);
            if (el) {
                el.scrollIntoView();
                el.focus();
                el.click();
                return 'found';
            }
        }
        return 'not_found';
    })()
    """
    find_result = client.evaluate(search_js)
    if find_result.get("result") == "not_found":
        warn("Could not find search input on connections page. Proceeding without filter.")
        return

    jittered_sleep(1)
    snapshot = client.snapshot()
    snap_text = snapshot.get("snapshot", "")
    input_ref = find_element_ref(snap_text, ["search", "connections"], "textbox")
    if not input_ref:
        input_ref = find_element_ref(snap_text, ["search", "connections"], "input")

    if input_ref:
        client.type(input_ref, search_term)
        jittered_sleep(1)
        client.press("Enter")
        jittered_sleep(3)
    else:
        # Snapshot ref not found -- type via JS directly as alternate approach
        # chr(39) is single quote, stripped to prevent JS string injection
        safe_term = search_term.replace(chr(39), "")
        type_js = f"""
        (() => {{
            const selectors = [
                'input#mn-connections-search-input',
                'input[placeholder*="search" i]',
                'input[placeholder*="Search"]'
            ];
            for (const sel of selectors) {{
                const el = document.querySelector(sel);
                if (el) {{
                    el.value = '{safe_term}';
                    el.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    el.dispatchEvent(new KeyboardEvent('keydown', {{ key: 'Enter', code: 'Enter', bubbles: true }}));
                    return 'typed';
                }}
            }}
            return 'not_found';
        }})()
        """
        client.evaluate(type_js)
        jittered_sleep(3)


# CSS selector used to count connection cards for scroll-loading
_CONNECTIONS_CARD_SELECTOR = 'main a[href*="/in/"]'

# JavaScript to extract connection cards from the DOM
_JS_EXTRACT_CONNECTIONS = """
(() => {
    const data = [];
    const seen = new Set();

    // Current LinkedIn layout (2025+): main > main contains links with /in/ hrefs.
    // Each connection is an <a> wrapping paragraphs (name link + headline).
    const mainEl = document.querySelector('main main') || document.querySelector('main');
    if (!mainEl) return JSON.stringify([]);

    const profileLinks = mainEl.querySelectorAll('a[href*="/in/"]');

    for (const link of profileLinks) {
        const href = link.getAttribute('href') || '';
        const usernameMatch = href.match(/\\/in\\/([^/?]+)/);
        if (!usernameMatch) continue;

        const username = usernameMatch[1];
        if (seen.has(username)) continue;

        // Each connection link contains paragraphs: first has the name, second has headline
        const paragraphs = link.querySelectorAll('p');

        // Skip links that don't have connection info (e.g. profile picture links)
        if (paragraphs.length < 1) continue;

        // The name link is nested: <p><a href="/in/...">Name</a></p>
        const nameLink = link.querySelector('a[href*="/in/' + username + '"]');
        let name = '';
        if (nameLink && nameLink !== link) {
            name = nameLink.textContent?.trim() || '';
        }
        if (!name && paragraphs.length > 0) {
            name = paragraphs[0].textContent?.trim() || '';
        }

        // Headline is the second paragraph
        let headline = '';
        if (paragraphs.length >= 2) {
            headline = paragraphs[1].textContent?.trim() || '';
        }

        // Skip non-connection entries (navigation links, etc.)
        if (!name || name.length < 2 || name.length > 100) continue;
        // Skip if name looks like a button label
        if (['Message', 'Connect', 'Follow', 'Search with filters'].includes(name)) continue;

        seen.add(username);
        data.push({ username, name, headline });
    }

    // Fallback: try legacy mn-connection-card selector
    if (data.length === 0) {
        const cards = document.querySelectorAll('li.mn-connection-card');
        for (const card of cards) {
            const nameEl = card.querySelector('.mn-connection-card__name');
            const name = nameEl?.textContent?.trim() || '';
            const linkEl = card.querySelector('a[href*="/in/"]');
            const href = linkEl?.getAttribute('href') || '';
            const usernameMatch = href.match(/\\/in\\/([^/]+)/);
            const username = usernameMatch ? usernameMatch[1] : '';
            const occupationEl = card.querySelector('.mn-connection-card__occupation');
            const headline = occupationEl?.textContent?.trim() || '';
            if (name) {
                data.push({ username, name, headline });
            }
        }
    }

    return JSON.stringify(data);
})()
"""


@app.command()
def connections(
    limit: int = typer.Option(20, help="Number of connections to show"),
    search: str = typer.Option("", "--search", "-s", help="Filter connections by keyword"),
    output_file: str = typer.Option("", "--output", "-o", help="Write JSON output to file"),
    append_file: str = typer.Option("", "--append", "-a", help="Resume: append to existing JSON file, skip duplicates"),
):
    """List your LinkedIn connections."""
    if output_file and append_file:
        error("Cannot use both --output and --append. Use --append to resume into an existing file, or --output for a new file.")
        raise typer.Exit(1)

    existing_connections: list[dict] = []
    existing_usernames: set[str] = set()
    if append_file and os.path.exists(append_file):
        existing_connections, existing_usernames = _load_existing_connections(append_file)

    try:
        client = get_client()
        client.navigate(LinkedInURLs.connections())
        jittered_sleep(3)

        if search:
            _filter_connections_search(client, search)

        scroll_until_loaded(client, _CONNECTIONS_CARD_SELECTOR, limit)

        result = client.evaluate(_JS_EXTRACT_CONNECTIONS)
        connections_data = json.loads(result.get("result", "[]"))[:limit]

        # Append/resume mode -- merge with existing, dedup by username
        if append_file:
            new_connections = [c for c in connections_data if c.get("username") not in existing_usernames]
            merged = existing_connections + new_connections
            _write_connections_json(append_file, merged, search_filter=search)
            console.print(f"Found {len(new_connections)} new ({len(existing_usernames)} already captured)")
            console.print(f"Wrote {len(merged)} total connections to {append_file}")
            return

        if output_file:
            _write_connections_json(output_file, connections_data, search_filter=search, limit=limit)
            console.print(f"Wrote {len(connections_data)} connections to {output_file}")
            return

        # Console output
        console.print(f"\n[bold]Your Connections[/bold] ({len(connections_data)} shown)\n")
        if connections_data:
            if config.format == "json":
                print(json.dumps(connections_data, ensure_ascii=False))
            else:
                table = Table(show_header=True, header_style="bold", box=None)
                table.add_column("#", width=3)
                table.add_column("Name", width=25)
                table.add_column("Headline", width=45, no_wrap=True, overflow="ellipsis")
                for i, c in enumerate(connections_data, 1):
                    table.add_row(str(i), c["name"], c.get("headline", ""))
                console.print(table)
        else:
            console.print("No connections found.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def connect(
    username: str = typer.Argument(..., help="LinkedIn username to connect with"),
    note: str = typer.Option("", "--note", "-n", help="Optional connection note"),
):
    """Send a connection request."""
    try:
        client = get_client()

        # Navigate to profile
        client.navigate(LinkedInURLs.profile(username))
        jittered_sleep(3)

        # Get snapshot to find connect button
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        if config.verbose:
            console.print(snapshot_text)

        # Find connect button (exclude message buttons)
        connect_ref = _find_connect_button(snapshot_text)

        if not connect_ref:
            # Check if already connected or pending
            if find_element_ref(snapshot_text, ["message"], "button"):
                warn("Already connected (Message button found)")
                return
            if "pending" in snapshot_text.lower():
                warn("Connection request already pending")
                return

            error("Could not find Connect button")
            raise typer.Exit(1)

        client.click(connect_ref)
        jittered_sleep(1)

        # If note provided, add it
        if note:
            _add_connection_note(client, note)

        # Click send/done button
        jittered_sleep(0.5)
        snapshot = client.snapshot()
        send_ref = find_element_ref(snapshot.get("snapshot", ""), ["send", "done"], "button")

        if send_ref:
            client.click(send_ref)
            jittered_sleep(1)
            success(f"Connection request sent to {username}")
        else:
            warn("Could not find send button. Request may not have been sent.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


def _find_connect_button(snapshot_text: str) -> Optional[str]:
    """Find connect button, excluding message buttons."""
    lines = snapshot_text.split('\n')
    for line in lines:
        line_lower = line.lower()
        if 'connect' in line_lower and 'button' in line_lower:
            if 'message' not in line_lower:
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    return match.group(1)
    return None


def _add_connection_note(client: BrowserClient, note: str) -> None:
    """Add a note to connection request."""
    snapshot = client.snapshot()
    add_note_ref = find_element_ref(snapshot.get("snapshot", ""), ["add a note"])

    if add_note_ref:
        client.click(add_note_ref)
        jittered_sleep(0.5)

        # Find text area and type note
        snapshot = client.snapshot()
        note_ref = find_element_ref(snapshot.get("snapshot", ""), ["textbox", "textarea"])
        if note_ref:
            client.type(note_ref, note)


# =============================================================================
# Messaging Commands
# =============================================================================

@app.command()
def messages(
    limit: int = typer.Option(10, help="Number of conversations to show"),
    unread_only: bool = typer.Option(False, "--unread", help="Show only unread messages"),
):
    """View recent LinkedIn messages."""
    try:
        client = get_client()

        # Navigate to messaging
        client.navigate(LinkedInURLs.messaging())
        jittered_sleep(3)

        # Extract message threads
        js_messages = """
        (() => {
            const threads = document.querySelectorAll('li.msg-conversation-listitem');
            const data = [];

            for (const thread of threads) {
                const nameEl = thread.querySelector('.msg-conversation-card__participant-names');
                const name = nameEl?.textContent?.trim() || 'Unknown';

                const previewEl = thread.querySelector('.msg-conversation-card__message-snippet');
                const preview = previewEl?.textContent?.trim() || '';

                const timeEl = thread.querySelector('.msg-conversation-card__time-stamp');
                const timeAgo = timeEl?.textContent?.trim() || '';

                const unread = thread.classList.contains('msg-conversation-card--unread');

                data.push({
                    participant: name,
                    preview: preview.substring(0, 100),
                    timeAgo,
                    unread
                });
            }

            return JSON.stringify(data);
        })()
        """

        result = client.evaluate(js_messages)
        messages_data = json.loads(result.get("result", "[]"))

        # Filter if unread_only
        if unread_only:
            messages_data = [m for m in messages_data if m.get("unread")]

        console.print(f"\n[bold]Messages[/bold]")
        if unread_only:
            console.print("[dim](unread only)[/dim]")
        console.print()

        if messages_data:
            if config.format == "json":
                console.print_json(json.dumps(messages_data[:limit]))
            else:
                for i, m in enumerate(messages_data[:limit], 1):
                    unread_marker = "[bold]*[/bold] " if m.get("unread") else "  "
                    console.print(f"{unread_marker}[cyan]{i}. {m['participant']}[/cyan] [dim]{m.get('timeAgo', '')}[/dim]")
                    if m.get("preview"):
                        console.print(f"   {m['preview']}")
                    console.print()
        else:
            if unread_only:
                console.print("No unread messages.")
            else:
                console.print("No messages found.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def message(
    username: str = typer.Argument(..., help="LinkedIn username to message"),
    text: str = typer.Option(..., "--text", "-t", help="Message text"),
):
    """Send a message to a LinkedIn connection."""
    try:
        client = get_client()

        # Navigate to profile first
        client.navigate(LinkedInURLs.profile(username))
        jittered_sleep(3)

        # Find and click Message button
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        if config.verbose:
            console.print(snapshot_text)

        message_ref = find_element_ref(snapshot_text, ["message"], "button")

        if not message_ref:
            error("Could not find Message button. You may not be connected.")
            raise typer.Exit(1)

        client.click(message_ref)
        jittered_sleep(2)

        # Find message input
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        input_ref = find_element_ref(snapshot_text, ["textbox", "message input"])
        if not input_ref:
            input_ref = find_element_ref(snapshot_text, ["contenteditable", "write a message"])

        if not input_ref:
            error("Could not find message input")
            raise typer.Exit(1)

        # Type message
        client.click(input_ref)
        jittered_sleep(0.3)
        client.type(input_ref, text)
        jittered_sleep(0.5)

        # Find and click send button
        snapshot = client.snapshot()
        send_ref = find_element_ref(snapshot.get("snapshot", ""), ["send"], "button")

        if send_ref:
            client.click(send_ref)
            jittered_sleep(1)
            success(f"Message sent to {username}")
        else:
            # Try pressing Enter as alternative
            client.press("Enter")
            jittered_sleep(1)
            success(f"Message sent to {username}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Search Commands
# =============================================================================

@app.command()
def search(
    query: str = typer.Argument(..., help="Search query"),
    search_type: str = typer.Option("people", "--type", "-t", help="Search type: people, posts, companies, jobs"),
    limit: int = typer.Option(10, help="Number of results to show"),
    network: str = typer.Option("", "--network", "-n", help="Connection degree: 1st, 2nd, 3rd (people only)"),
):
    """Search LinkedIn."""
    if network and search_type != "people":
        warn(f"--network filter only applies to people search. Ignoring for type '{search_type}'.")
        network = ""

    try:
        client = get_client()

        # Navigate to search results
        client.navigate(LinkedInURLs.search(query, search_type, network=network))
        jittered_sleep(3)

        # Scroll to load results
        client.scroll("down")
        jittered_sleep(1)

        if search_type == "people":
            js_search = """
            (() => {
                const data = [];
                const seen = new Set();

                // LinkedIn 2024+ structure: main > main > ul > a[href*="/in/"] > li
                // The profile link CONTAINS the listitem, not vice versa
                const profileLinks = document.querySelectorAll('main a[href*="/in/"]');

                for (const link of profileLinks) {
                    const href = link.getAttribute('href') || '';
                    const usernameMatch = href.match(/\\/in\\/([^?/]+)/);
                    if (!usernameMatch) continue;

                    const username = usernameMatch[1];
                    if (seen.has(username)) continue;

                    // The link should contain a listitem with the person's info
                    const listitem = link.querySelector('li') || link;

                    // Get all paragraphs inside the result
                    const paragraphs = listitem.querySelectorAll('p');
                    if (paragraphs.length < 2) continue; // Need at least name paragraph + headline

                    let name = '';
                    let headline = '';
                    let location = '';

                    for (let i = 0; i < paragraphs.length; i++) {
                        const p = paragraphs[i];
                        const text = p.textContent?.trim() || '';
                        if (!text || text.length < 2) continue;

                        // First paragraph with name usually contains bullet and degree info
                        if (!name && (text.includes('1st') || text.includes('2nd') || text.includes('3rd') || i === 0)) {
                            // Look for a nested link with just the name
                            const nameLink = p.querySelector('a[href*="/in/' + username + '"]');
                            if (nameLink) {
                                name = nameLink.textContent?.trim() || '';
                            } else {
                                // Extract name from start of text (before bullet)
                                const parts = text.split(/[•·]/);
                                if (parts[0]) {
                                    name = parts[0].trim();
                                }
                            }
                            continue;
                        }

                        // Skip action buttons and premium upsells
                        if (text === 'Connect' || text === 'Message' || text === 'Follow' ||
                            text.includes('Premium') || text.includes('Cancel anytime')) continue;

                        // Skip mutual connections text
                        if (text.includes('mutual connection')) continue;

                        // Skip "Past:" and "Current:" descriptions (they're after headline/location)
                        if (text.startsWith('Past:') || text.startsWith('Current:') || text.startsWith('Summary:')) continue;

                        // Assign headline then location
                        if (!headline) {
                            headline = text.substring(0, 150);
                        } else if (!location && text.length < 80 && !text.includes('|')) {
                            location = text.substring(0, 60);
                            break;
                        }
                    }

                    if (name && name.length > 1) {
                        seen.add(username);
                        data.push({
                            name: name.substring(0, 50),
                            username,
                            headline,
                            location,
                            type: 'person'
                        });
                    }
                }

                // Fallback to old structure if no results
                if (data.length === 0) {
                    const results = document.querySelectorAll('li.reusable-search__result-container');
                    for (const result of results) {
                        const nameEl = result.querySelector('.entity-result__title-text a span[aria-hidden="true"]');
                        const name = nameEl?.textContent?.trim() || '';
                        const linkEl = result.querySelector('.entity-result__title-text a');
                        const href = linkEl?.getAttribute('href') || '';
                        const usernameMatch = href.match(/\\/in\\/([^?/]+)/);
                        const username = usernameMatch ? usernameMatch[1] : '';
                        const headlineEl = result.querySelector('.entity-result__primary-subtitle');
                        const headline = headlineEl?.textContent?.trim() || '';
                        const locationEl = result.querySelector('.entity-result__secondary-subtitle');
                        const location = locationEl?.textContent?.trim() || '';
                        if (name) {
                            data.push({ name, username, headline, location, type: 'person' });
                        }
                    }
                }

                return JSON.stringify(data);
            })()
            """
        else:
            js_search = """
            (() => {
                const data = [];

                // Try new structure first
                const mainContent = document.querySelector('main') || document;
                const listItems = mainContent.querySelectorAll('li');

                for (const item of listItems) {
                    const titleEl = item.querySelector('a span[aria-hidden="true"], .entity-result__title-text');
                    const title = titleEl?.textContent?.trim() || '';
                    const subtitleEl = item.querySelector('p, .entity-result__primary-subtitle');
                    const subtitle = subtitleEl?.textContent?.trim() || '';
                    if (title && title.length > 2 && title.length < 200) {
                        data.push({ title, subtitle, type: '""" + search_type + """' });
                    }
                }

                // Fallback to old structure
                if (data.length === 0) {
                    const results = document.querySelectorAll('li.reusable-search__result-container');
                    for (const result of results) {
                        const titleEl = result.querySelector('.entity-result__title-text');
                        const title = titleEl?.textContent?.trim() || '';
                        const subtitleEl = result.querySelector('.entity-result__primary-subtitle');
                        const subtitle = subtitleEl?.textContent?.trim() || '';
                        if (title) {
                            data.push({ title, subtitle, type: '""" + search_type + """' });
                        }
                    }
                }

                return JSON.stringify(data);
            })()
            """

        result = client.evaluate(js_search)
        results_data = json.loads(result.get("result", "[]"))

        console.print(f"\n[bold]Search Results for '{query}'[/bold] ({search_type})\n")

        if results_data:
            if config.format == "json":
                console.print_json(json.dumps(results_data[:limit]))
            else:
                if search_type == "people":
                    table = Table(show_header=True, header_style="bold", box=None)
                    table.add_column("#", width=3)
                    table.add_column("Name", width=25)
                    table.add_column("Headline", width=35, no_wrap=True, overflow="ellipsis")
                    table.add_column("Location", width=20, no_wrap=True, overflow="ellipsis")

                    for i, r in enumerate(results_data[:limit], 1):
                        table.add_row(str(i), r.get("name", ""), r.get("headline", ""), r.get("location", ""))
                    console.print(table)
                else:
                    for i, r in enumerate(results_data[:limit], 1):
                        console.print(f"{i}. {r.get('title', '')}")
                        if r.get("subtitle"):
                            console.print(f"   [dim]{r['subtitle']}[/dim]")
                        console.print()
        else:
            console.print("No results found.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Notifications Commands
# =============================================================================

@app.command()
def notifications(
    limit: int = typer.Option(10, help="Number of notifications to show"),
    unread_only: bool = typer.Option(False, "--unread", help="Show only unread"),
):
    """View LinkedIn notifications."""
    try:
        client = get_client()

        # Navigate to notifications
        client.navigate(LinkedInURLs.notifications())
        jittered_sleep(3)

        # Extract notifications
        js_notifications = """
        (() => {
            const items = document.querySelectorAll('.nt-card, .notification-card');
            const data = [];

            for (const item of items) {
                const textEl = item.querySelector('.nt-card__text, .notification-card__text');
                const text = textEl?.textContent?.trim() || '';

                const timeEl = item.querySelector('.nt-card__time, .notification-card__time, time');
                const timeAgo = timeEl?.textContent?.trim() || '';

                const unread = item.classList.contains('nt-card--unread') ||
                              item.classList.contains('notification-card--unread') ||
                              item.querySelector('.notification-badge') !== null;

                if (text) {
                    data.push({ text: text.substring(0, 200), timeAgo, unread });
                }
            }

            return JSON.stringify(data);
        })()
        """

        result = client.evaluate(js_notifications)
        notifications_data = json.loads(result.get("result", "[]"))

        # Filter if unread_only
        if unread_only:
            notifications_data = [n for n in notifications_data if n.get("unread")]

        console.print(f"\n[bold]Notifications[/bold]\n")

        if notifications_data:
            if config.format == "json":
                console.print_json(json.dumps(notifications_data[:limit]))
            else:
                for i, n in enumerate(notifications_data[:limit], 1):
                    unread_marker = "[bold]*[/bold] " if n.get("unread") else "  "
                    console.print(f"{unread_marker}[cyan]{i}.[/cyan] {n['text']}")
                    if n.get("timeAgo"):
                        console.print(f"   [dim]{n['timeAgo']}[/dim]")
                    console.print()
        else:
            console.print("No notifications found.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Invitations Commands
# =============================================================================

@app.command()
def invitations(
    limit: int = typer.Option(10, help="Number of invitations to show"),
):
    """View pending connection invitations."""
    try:
        client = get_client()

        # Navigate to invitations
        client.navigate(LinkedInURLs.invitations())
        jittered_sleep(3)

        # Extract invitations
        js_invitations = """
        (() => {
            const cards = document.querySelectorAll('li.invitation-card, .mn-invitation-list li');
            const data = [];

            for (const card of cards) {
                const nameEl = card.querySelector('.invitation-card__name, .mn-person-info__name');
                const name = nameEl?.textContent?.trim() || '';

                const headlineEl = card.querySelector('.invitation-card__occupation, .mn-person-info__occupation');
                const headline = headlineEl?.textContent?.trim() || '';

                const timeEl = card.querySelector('.time-badge, .invitation-card__time');
                const timeAgo = timeEl?.textContent?.trim() || '';

                const mutualEl = card.querySelector('.member-insights__count, .mn-person-info__mutual');
                const mutual = mutualEl?.textContent?.trim() || '';

                if (name) {
                    data.push({ name, headline, timeAgo, mutual });
                }
            }

            return JSON.stringify(data);
        })()
        """

        result = client.evaluate(js_invitations)
        invitations_data = json.loads(result.get("result", "[]"))

        console.print(f"\n[bold]Pending Invitations[/bold] ({len(invitations_data)} found)\n")

        if invitations_data:
            if config.format == "json":
                console.print_json(json.dumps(invitations_data[:limit]))
            else:
                table = Table(show_header=True, header_style="bold", box=None)
                table.add_column("#", width=3)
                table.add_column("Name", width=25)
                table.add_column("Headline", width=40, no_wrap=True, overflow="ellipsis")
                table.add_column("Time", width=10)

                for i, inv in enumerate(invitations_data[:limit], 1):
                    table.add_row(
                        str(i),
                        inv["name"],
                        inv.get("headline", ""),
                        inv.get("timeAgo", "")
                    )
                console.print(table)
        else:
            console.print("No pending invitations.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def accept(
    name: str = typer.Argument(..., help="Name (or partial) of person to accept"),
):
    """Accept a connection invitation."""
    try:
        client = get_client()

        # Navigate to invitations
        client.navigate(LinkedInURLs.invitations())
        jittered_sleep(3)

        # Get snapshot to find the invitation
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        if config.verbose:
            console.print(snapshot_text)

        # Check if name exists in snapshot
        if name.lower() not in snapshot_text.lower():
            error(f"Could not find invitation from '{name}'")
            raise typer.Exit(1)

        # Find accept button near the name
        accept_ref = find_element_ref_near_text(snapshot_text, name, ["accept", "button"])

        if not accept_ref:
            error("Could not find Accept button")
            raise typer.Exit(1)

        client.click(accept_ref)
        jittered_sleep(1)
        success(f"Accepted invitation from {name}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def ignore(
    name: str = typer.Argument(..., help="Name (or partial) of person to ignore"),
):
    """Ignore a connection invitation."""
    try:
        client = get_client()

        # Navigate to invitations
        client.navigate(LinkedInURLs.invitations())
        jittered_sleep(3)

        # Get snapshot to find the invitation
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        if config.verbose:
            console.print(snapshot_text)

        # Check if name exists in snapshot
        if name.lower() not in snapshot_text.lower():
            error(f"Could not find invitation from '{name}'")
            raise typer.Exit(1)

        # Find ignore button near the name
        ignore_ref = find_element_ref_near_text(snapshot_text, name, ["ignore", "button"])

        if not ignore_ref:
            error("Could not find Ignore button")
            raise typer.Exit(1)

        client.click(ignore_ref)
        jittered_sleep(1)
        success(f"Ignored invitation from {name}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Additional Post Actions
# =============================================================================

@app.command()
def repost(
    url: str = typer.Argument(..., help="Post URL to repost"),
    with_thoughts: str = typer.Option("", "--thoughts", "-t", help="Add your thoughts"),
):
    """Repost content to your feed."""
    try:
        client = get_client()

        # Navigate to post
        if url.startswith("http"):
            client.navigate(url)
        else:
            client.navigate(LinkedInURLs.post(url))
        jittered_sleep(3)

        # Get snapshot to find repost button
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        if config.verbose:
            console.print(snapshot_text)

        repost_ref = find_element_ref(snapshot_text, ["repost"], "button")

        if not repost_ref:
            error("Could not find Repost button")
            raise typer.Exit(1)

        client.click(repost_ref)
        jittered_sleep(1)

        # Get new snapshot for repost options
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        if with_thoughts:
            _repost_with_thoughts(client, snapshot_text, with_thoughts)
        else:
            _repost_instant(client, snapshot_text)

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


def _repost_with_thoughts(client: BrowserClient, snapshot_text: str, thoughts: str) -> None:
    """Handle repost with thoughts flow."""
    thoughts_ref = find_element_ref(snapshot_text, ["thoughts", "quote"])

    if thoughts_ref:
        client.click(thoughts_ref)
        jittered_sleep(1)

        # Find text input and type thoughts
        snapshot = client.snapshot()
        input_ref = find_element_ref(snapshot.get("snapshot", ""), ["textbox", "editor"])

        if input_ref:
            client.click(input_ref)
            jittered_sleep(0.3)
            client.type(input_ref, thoughts)

        # Click post button
        jittered_sleep(0.5)
        snapshot = client.snapshot()
        post_ref = find_element_ref(snapshot.get("snapshot", ""), ["post"], "button")

        if post_ref:
            client.click(post_ref)

        jittered_sleep(1)
        success("Reposted with your thoughts")
    else:
        warn("Could not find 'repost with thoughts' option")


def _repost_instant(client: BrowserClient, snapshot_text: str) -> None:
    """Handle instant repost flow."""
    # Look for instant repost option (not the one with thoughts)
    lines = snapshot_text.split('\n')
    instant_ref = None
    for line in lines:
        line_lower = line.lower()
        if ('instant' in line_lower or 'repost' in line_lower) and 'button' in line_lower:
            if 'thoughts' not in line_lower:
                match = re.search(r'\[ref=(\w+)\]', line)
                if match:
                    instant_ref = match.group(1)
                    break

    if instant_ref:
        client.click(instant_ref)
        jittered_sleep(1)
        success("Reposted")
    else:
        warn("Could not find instant repost option")


@app.command()
def save(
    url: str = typer.Argument(..., help="Post URL to save"),
):
    """Save a post for later."""
    try:
        client = get_client()

        # Navigate to post
        if url.startswith("http"):
            client.navigate(url)
        else:
            client.navigate(LinkedInURLs.post(url))
        jittered_sleep(3)

        # Get snapshot to find save/more button
        snapshot = client.snapshot()
        snapshot_text = snapshot.get("snapshot", "")

        if config.verbose:
            console.print(snapshot_text)

        # First try to find direct save button
        save_ref = find_element_ref(snapshot_text, ["save"], "button")

        if not save_ref:
            # Try finding the more/overflow menu
            more_ref = find_element_ref(snapshot_text, ["more", "..."], "button")
            if more_ref:
                client.click(more_ref)
                jittered_sleep(0.5)

            # Get new snapshot and look for save
            snapshot = client.snapshot()
            save_ref = find_element_ref(snapshot.get("snapshot", ""), ["save"])

        if save_ref:
            client.click(save_ref)
            jittered_sleep(1)
            success("Post saved")
        else:
            error("Could not find Save option")
            raise typer.Exit(1)

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Company Commands
# =============================================================================

@app.command()
def company(
    company_id: str = typer.Argument(..., help="Company name or LinkedIn company slug"),
):
    """View company page information."""
    try:
        client = get_client()

        # Navigate to company page
        client.navigate(LinkedInURLs.company(company_id))
        jittered_sleep(3)

        # Extract company info
        js_company = """
        (() => {
            const name = document.querySelector('h1.org-top-card-summary__title, h1.top-card-layout__title')?.textContent?.trim() || '';

            const taglineEl = document.querySelector('.org-top-card-summary__tagline, .top-card-layout__headline');
            const tagline = taglineEl?.textContent?.trim() || '';

            // Get info items (industry, size, etc)
            const infoItems = document.querySelectorAll('.org-top-card-summary-info-list__info-item, .top-card-layout__entity-info-item');
            let industry = '';
            let size = '';
            let headquarters = '';

            for (const item of infoItems) {
                const text = item.textContent?.trim() || '';
                if (text.includes('employees')) {
                    size = text;
                } else if (!industry) {
                    industry = text;
                }
            }

            // Get about/overview
            const aboutEl = document.querySelector('.org-page-details-module__card-spacing p, .org-about-us-organization-description__text');
            const about = aboutEl?.textContent?.trim()?.substring(0, 500) || '';

            // Get followers
            const followersEl = document.querySelector('[data-test-id="about-us__followers"], .org-top-card-summary__follower-count');
            const followers = followersEl?.textContent?.trim() || '';

            // Get website
            const websiteEl = document.querySelector('a[data-test-id="about-us__website"], .link-without-visited-state');
            const website = websiteEl?.getAttribute('href') || '';

            return JSON.stringify({
                name,
                tagline,
                industry,
                size,
                followers,
                website,
                about
            });
        })()
        """

        result = client.evaluate(js_company)
        company_data = json.loads(result.get("result", "{}"))

        if config.format == "json":
            console.print_json(json.dumps(company_data))
        else:
            console.print(f"\n[bold]{company_data.get('name', 'Unknown')}[/bold]")
            if company_data.get("tagline"):
                console.print(f"{company_data['tagline']}")
            console.print()

            if company_data.get("industry"):
                console.print(f"Industry: {company_data['industry']}")
            if company_data.get("size"):
                console.print(f"Size: {company_data['size']}")
            if company_data.get("followers"):
                console.print(f"Followers: {company_data['followers']}")
            if company_data.get("website"):
                console.print(f"Website: {company_data['website']}")

            if company_data.get("about"):
                console.print(f"\nAbout:\n{company_data['about']}...")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Jobs Commands
# =============================================================================

@app.command()
def jobs(
    query: str = typer.Argument(..., help="Job search query"),
    location: str = typer.Option("", "--location", "-l", help="Location filter"),
    limit: int = typer.Option(10, help="Number of results to show"),
):
    """Search for jobs on LinkedIn."""
    try:
        client = get_client()

        # Build search URL
        search_url = LinkedInURLs.search(query, "jobs")
        if location:
            search_url += f"&location={quote(location)}"

        client.navigate(search_url)
        jittered_sleep(3)

        # Scroll to load results
        client.scroll("down")
        jittered_sleep(1)

        # Extract job listings
        js_jobs = """
        (() => {
            const cards = document.querySelectorAll('.job-card-container, .jobs-search-results__list-item');
            const data = [];

            for (const card of cards) {
                const titleEl = card.querySelector('.job-card-list__title, .job-card-container__link');
                const title = titleEl?.textContent?.trim() || '';

                const companyEl = card.querySelector('.job-card-container__company-name, .job-card-container__primary-description');
                const company = companyEl?.textContent?.trim() || '';

                const locationEl = card.querySelector('.job-card-container__metadata-item, .job-card-container__metadata-wrapper');
                const location = locationEl?.textContent?.trim() || '';

                const timeEl = card.querySelector('time, .job-card-container__footer-item');
                const posted = timeEl?.textContent?.trim() || '';

                const linkEl = card.querySelector('a[href*="/jobs/view/"]');
                const url = linkEl?.getAttribute('href') || '';

                if (title) {
                    data.push({ title, company, location, posted, url });
                }
            }

            return JSON.stringify(data);
        })()
        """

        result = client.evaluate(js_jobs)
        jobs_data = json.loads(result.get("result", "[]"))

        console.print(f"\n[bold]Jobs: '{query}'[/bold]")
        if location:
            console.print(f"Location: {location}")
        console.print()

        if jobs_data:
            if config.format == "json":
                console.print_json(json.dumps(jobs_data[:limit]))
            else:
                for i, job in enumerate(jobs_data[:limit], 1):
                    console.print(f"[cyan]{i}. {job['title']}[/cyan]")
                    console.print(f"   {job.get('company', '')} - {job.get('location', '')}")
                    if job.get("posted"):
                        console.print(f"   [dim]{job['posted']}[/dim]")
                    console.print()
        else:
            console.print("No jobs found.")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


# =============================================================================
# Navigation & Utility Commands
# =============================================================================

@app.command()
def goto(url: str = typer.Argument(..., help="URL to navigate to")):
    """Navigate to a LinkedIn URL."""
    try:
        client = get_client()

        # Ensure URL is a LinkedIn URL
        if not url.startswith("http"):
            if url.startswith("in/"):
                url = f"{LinkedInURLs.BASE}/{url}"
            else:
                url = f"{LinkedInURLs.BASE}/in/{url}"

        client.navigate(url)
        success(f"Navigated to {url}")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def snapshot():
    """Get current page snapshot (for debugging)."""
    try:
        client = get_client()
        result = client.snapshot()
        console.print(result.get("snapshot", "No snapshot available"))

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


@app.command()
def screenshot(
    output: str = typer.Option("linkedin_screenshot.png", help="Output filename"),
):
    """Take a screenshot of the current page."""
    try:
        client = get_client()
        result = client.screenshot()

        # Save base64 image
        import base64
        image_data = result.get("screenshot", "")
        if image_data:
            with open(output, "wb") as f:
                f.write(base64.b64decode(image_data))
            success(f"Screenshot saved to {output}")
        else:
            error("No screenshot data received")

    except BrowserError as e:
        error(str(e))
        raise typer.Exit(1)


if __name__ == "__main__":
    app()

"""Simple CDP helper for clicking buttons - no Playwright needed."""
import json
import httpx
import time
from pathlib import Path

try:
    from .delays import jittered_sleep
except ImportError:
    from delays import jittered_sleep


def click_element_by_text(port: int, text: str, tag: str = "button") -> dict:
    """Click an element by its text content using CDP."""
    base_url = f"http://localhost:{port}"

    # Get the websocket URL for CDP
    try:
        resp = httpx.get(f"{base_url}/json/version", timeout=5)
        ws_url = resp.json().get("webSocketDebuggerUrl")
    except Exception as e:
        return {"success": False, "error": f"Cannot connect to browser: {e}"}

    # Use cc-browser's /evaluate endpoint instead of raw CDP
    js_code = f'''
    (function() {{
        const elements = document.querySelectorAll('{tag}');
        for (const el of elements) {{
            if (el.textContent.trim() === '{text}') {{
                el.click();
                return 'clicked';
            }}
        }}
        return 'not found';
    }})()
    '''

    try:
        resp = httpx.post(f"{base_url}/evaluate", json={"js": js_code}, timeout=10)
        result = resp.json()
        if result.get("result") == "clicked":
            return {"success": True, "message": f"Clicked {tag} with text '{text}'"}
        else:
            return {"success": False, "error": f"{tag} with text '{text}' not found"}
    except Exception as e:
        return {"success": False, "error": f"Evaluate failed: {e}"}


def create_post_via_js(port: int, content: str, image_path: str = None) -> dict:
    """Create a LinkedIn post using JavaScript evaluation.

    Args:
        port: cc-browser port
        content: Post text content
        image_path: Optional path to image or video file to attach
    """
    base_url = f"http://localhost:{port}"

    # Step 1: Navigate to feed
    try:
        httpx.post(f"{base_url}/navigate", json={"url": "https://www.linkedin.com/feed/"}, timeout=30)
    except Exception as e:
        return {"success": False, "error": f"Navigate failed: {e}"}

    jittered_sleep(2)

    # Step 2: Click "Start a post"
    result = click_element_by_text(port, "Start a post", "button")
    if not result.get("success"):
        # Try clicking the placeholder text area instead
        js_click_start = '''
        (function() {
            const el = document.querySelector('[data-placeholder*="Start a post"]');
            if (el) { el.click(); return 'clicked'; }
            return 'not found';
        })()
        '''
        try:
            resp = httpx.post(f"{base_url}/evaluate", json={"js": js_click_start}, timeout=10)
            if resp.json().get("result") != "clicked":
                return {"success": False, "error": "Could not find Start a post element"}
        except Exception as e:
            return {"success": False, "error": f"Click start post failed: {e}"}

    jittered_sleep(2)

    # Step 3: Type content
    escaped_content = content.replace("'", "\\'").replace("\n", "\\n")
    js_type = f'''
    (function() {{
        const editor = document.querySelector('[role="textbox"][contenteditable="true"]');
        if (editor) {{
            editor.focus();
            editor.textContent = '{escaped_content}';
            editor.dispatchEvent(new Event('input', {{ bubbles: true }}));
            return 'typed';
        }}
        return 'editor not found';
    }})()
    '''
    try:
        resp = httpx.post(f"{base_url}/evaluate", json={"js": js_type}, timeout=10)
        if resp.json().get("result") != "typed":
            return {"success": False, "error": "Could not type into editor"}
    except Exception as e:
        return {"success": False, "error": f"Type failed: {e}"}

    jittered_sleep(1)

    # Step 3.5: Attach image/video if provided
    if image_path:
        image_file = Path(image_path)
        if not image_file.exists():
            return {"success": False, "error": f"Image file not found: {image_path}"}

        # Click the media button (camera/image icon)
        js_click_media = '''
        (function() {
            // Look for media button - it's usually a button with aria-label containing "media" or "photo"
            const buttons = document.querySelectorAll('button');
            for (const btn of buttons) {
                const label = (btn.getAttribute('aria-label') || '').toLowerCase();
                const text = btn.textContent.toLowerCase();
                if (label.includes('media') || label.includes('photo') || label.includes('image') ||
                    text.includes('media') || text.includes('photo')) {
                    btn.click();
                    return 'clicked media';
                }
            }
            // Try looking for the media icon by class
            const mediaBtn = document.querySelector('[data-test-icon="image-medium"]');
            if (mediaBtn) {
                mediaBtn.closest('button').click();
                return 'clicked media icon';
            }
            return 'media button not found';
        })()
        '''
        try:
            resp = httpx.post(f"{base_url}/evaluate", json={"js": js_click_media}, timeout=10)
            result = resp.json().get("result", "")
            if "not found" in result:
                return {"success": False, "error": "Could not find media button"}
        except Exception as e:
            return {"success": False, "error": f"Click media button failed: {e}"}

        jittered_sleep(2)

        # Find the file input and upload the image
        # LinkedIn uses a hidden file input for uploads
        js_find_input = '''
        (function() {
            const inputs = document.querySelectorAll('input[type="file"]');
            for (const input of inputs) {
                if (input.accept && (input.accept.includes('image') || input.accept.includes('video'))) {
                    return input.id || 'found';
                }
            }
            // Return info about any file inputs
            if (inputs.length > 0) return 'file_input_exists';
            return 'no file input';
        })()
        '''
        try:
            resp = httpx.post(f"{base_url}/evaluate", json={"js": js_find_input}, timeout=10)
            input_result = resp.json().get("result", "")
        except Exception as e:
            return {"success": False, "error": f"Find file input failed: {e}"}

        # Use cc-browser's upload endpoint with the file input
        # We need to find the actual input element reference
        js_get_input_ref = '''
        (function() {
            const inputs = document.querySelectorAll('input[type="file"]');
            for (const input of inputs) {
                if (input.accept && (input.accept.includes('image') || input.accept.includes('video'))) {
                    // Set a data attribute we can reference
                    input.setAttribute('data-cc-upload', 'true');
                    return 'marked';
                }
            }
            // Mark any file input
            if (inputs.length > 0) {
                inputs[0].setAttribute('data-cc-upload', 'true');
                return 'marked first';
            }
            return 'no input';
        })()
        '''
        try:
            resp = httpx.post(f"{base_url}/evaluate", json={"js": js_get_input_ref}, timeout=10)
        except Exception as e:
            return {"success": False, "error": f"Mark file input failed: {e}"}

        # Use cc-browser's upload endpoint with element selector
        try:
            # Convert to absolute path with forward slashes for the browser
            abs_path = str(image_file.resolve()).replace("\\", "/")
            upload_resp = httpx.post(
                f"{base_url}/upload",
                json={"element": 'input[data-cc-upload="true"]', "paths": [abs_path]},
                timeout=30
            )
            if upload_resp.status_code != 200:
                return {"success": False, "error": f"Upload failed: {upload_resp.text}"}
        except Exception as e:
            return {"success": False, "error": f"Upload request failed: {e}"}

        # Wait for upload to process
        jittered_sleep(3)

        # LinkedIn shows an image editor - click "Next" to proceed
        result = click_element_by_text(port, "Next", "button")
        if result.get("success"):
            jittered_sleep(2)
        # If no "Next" button, maybe we're already past the editor

    # Step 4: Click Post button
    result = click_element_by_text(port, "Post", "button")
    if not result.get("success"):
        return {"success": False, "error": "Could not find Post button"}

    jittered_sleep(3)

    # Step 5: Handle visibility modal if it appears
    for _ in range(10):
        result = click_element_by_text(port, "Done", "button")
        if result.get("success"):
            jittered_sleep(2)
            # Click Post again
            click_element_by_text(port, "Post", "button")
            jittered_sleep(3)
            break
        jittered_sleep(0.5)

    return {"success": True, "message": "Post created"}

"""
LinkedIn Message Paste PoC - Tests different text input methods.

Launches Chromium with the existing cc-browser LinkedIn profile (reuses cookies/session).
Tests which text input approaches actually work on LinkedIn's contenteditable message editor.

IMPORTANT: Close cc-browser's LinkedIn connection first -- can't share a profile dir.
  cc-browser connections close linkedin

Usage:
  python tools/cc-browser/tests/linkedin-paste-test.py [--contact "Name"]
"""

import os
import sys
import time
import argparse
from pathlib import Path
from playwright.sync_api import sync_playwright, TimeoutError as PwTimeout

PROFILE_DIR = os.path.join(os.environ["LOCALAPPDATA"], "cc-director", "connections", "linkedin")
SCREENSHOTS_DIR = Path(__file__).parent / "screenshots"
MESSAGING_URL = "https://www.linkedin.com/messaging/"

# Short test message
TEST_TEXT = "Hello, this is a test message from the paste PoC. Please ignore."


def take_screenshot(page, name):
    """Save screenshot with given name."""
    SCREENSHOTS_DIR.mkdir(exist_ok=True)
    path = SCREENSHOTS_DIR / f"{name}.png"
    page.screenshot(path=str(path))
    print(f"  [screenshot] {path}")
    return path


def get_editor_text(page, selector):
    """Get visible text from the contenteditable editor."""
    try:
        return page.evaluate(f"""() => {{
            const el = document.querySelector('{selector}');
            return el ? el.innerText.trim() : '';
        }}""")
    except Exception:
        return ""


def clear_editor(page, selector):
    """Clear the contenteditable editor."""
    page.evaluate(f"""() => {{
        const el = document.querySelector('{selector}');
        if (el) {{
            el.focus();
            const sel = window.getSelection();
            const range = document.createRange();
            range.selectNodeContents(el);
            sel.removeAllRanges();
            sel.addRange(range);
            document.execCommand('delete', false);
        }}
    }}""")
    time.sleep(0.5)


def test_method(page, selector, method_name, test_fn):
    """Run a test method and report results."""
    print(f"\n--- Method {method_name} ---")

    # Clear editor first
    clear_editor(page, selector)
    before = get_editor_text(page, selector)
    if before:
        print(f"  [!] Editor not empty after clear: '{before[:50]}'")

    # Run the test
    try:
        test_fn()
        time.sleep(1)  # Wait for React to process
    except Exception as e:
        print(f"  [FAIL] Exception: {e}")
        take_screenshot(page, f"fail-{method_name}")
        return False

    # Check result
    after = get_editor_text(page, selector)
    take_screenshot(page, f"result-{method_name}")

    if TEST_TEXT in after:
        print(f"  [OK] Text appeared in editor! ({len(after)} chars)")
        return True
    elif after and after != before:
        print(f"  [PARTIAL] Some text appeared: '{after[:80]}'")
        return False
    else:
        print(f"  [FAIL] No text appeared in editor")
        return False


def main():
    parser = argparse.ArgumentParser(description="Test LinkedIn message paste methods")
    parser.add_argument("--contact", default=None, help="Contact name to message (opens existing thread)")
    args = parser.parse_args()

    if not os.path.isdir(PROFILE_DIR):
        print(f"[ERROR] LinkedIn profile dir not found: {PROFILE_DIR}")
        sys.exit(1)

    print(f"[+] Using profile: {PROFILE_DIR}")
    print(f"[+] Test text: '{TEST_TEXT[:50]}...'")

    results = {}

    with sync_playwright() as p:
        # Launch with existing profile - channel="chrome" uses installed Chrome
        browser = p.chromium.launch_persistent_context(
            user_data_dir=PROFILE_DIR,
            headless=False,
            channel="chrome",
            args=[
                "--disable-blink-features=AutomationControlled",
                "--no-first-run",
                "--no-default-browser-check",
            ],
            viewport={"width": 1280, "height": 900},
        )

        page = browser.pages[0] if browser.pages else browser.new_page()

        # Navigate to messaging
        print(f"[+] Navigating to {MESSAGING_URL}")
        page.goto(MESSAGING_URL, wait_until="domcontentloaded", timeout=30000)
        time.sleep(3)

        take_screenshot(page, "01-messaging-page")

        # Check if logged in
        if "login" in page.url.lower() or "signin" in page.url.lower():
            print("[ERROR] Not logged in! Close cc-browser linkedin connection and re-run.")
            browser.close()
            sys.exit(1)

        print("[+] Logged in to LinkedIn messaging")

        # If contact specified, search for and open that conversation
        if args.contact:
            print(f"[+] Looking for conversation with: {args.contact}")
            # Click on search or find the contact in the conversation list
            try:
                # Try clicking on a conversation that matches
                conv = page.locator(f"text={args.contact}").first
                conv.click(timeout=5000)
                time.sleep(2)
            except PwTimeout:
                print(f"  [!] Could not find conversation with '{args.contact}', using first available")

        # Find the message editor - LinkedIn uses contenteditable div
        # Common selectors for LinkedIn message editor
        editor_selectors = [
            'div.msg-form__contenteditable[contenteditable="true"]',
            'div[role="textbox"][contenteditable="true"]',
            'div.msg-form__msg-content-container div[contenteditable="true"]',
        ]

        selector = None
        for sel in editor_selectors:
            try:
                el = page.wait_for_selector(sel, timeout=3000)
                if el:
                    selector = sel
                    print(f"[+] Found editor: {sel}")
                    break
            except PwTimeout:
                continue

        if not selector:
            print("[ERROR] Could not find message editor. Open a conversation first.")
            take_screenshot(page, "error-no-editor")
            # Try clicking on the first conversation
            try:
                first_conv = page.locator(".msg-conversation-listitem__link").first
                first_conv.click(timeout=5000)
                time.sleep(2)
                for sel in editor_selectors:
                    try:
                        el = page.wait_for_selector(sel, timeout=3000)
                        if el:
                            selector = sel
                            print(f"[+] Found editor after clicking conversation: {sel}")
                            break
                    except PwTimeout:
                        continue
            except PwTimeout:
                pass

        if not selector:
            print("[ERROR] Still no editor found. Aborting.")
            take_screenshot(page, "error-final")
            browser.close()
            sys.exit(1)

        take_screenshot(page, "02-editor-found")

        # =====================================================================
        # Method A: page.fill() - Playwright's built-in fill
        # =====================================================================
        def test_a():
            page.fill(selector, TEST_TEXT)

        results["A-fill"] = test_method(page, selector, "A-fill", test_a)

        # =====================================================================
        # Method B: page.type() - Playwright's character-by-character typing
        # =====================================================================
        def test_b():
            page.click(selector)
            page.type(selector, TEST_TEXT, delay=5)

        results["B-type"] = test_method(page, selector, "B-type", test_b)

        # =====================================================================
        # Method C: CDP Input.insertText
        # =====================================================================
        def test_c():
            page.click(selector)
            cdp = browser.new_cdp_session(page)
            cdp.send("Input.insertText", {"text": TEST_TEXT})
            cdp.detach()

        results["C-cdp-insertText"] = test_method(page, selector, "C-cdp-insertText", test_c)

        # =====================================================================
        # Method D: Synthetic ClipboardEvent (paste) in page context
        # =====================================================================
        def test_d():
            page.evaluate(f"""() => {{
                const el = document.querySelector('{selector}');
                el.focus();
                const text = {repr(TEST_TEXT)};
                const html = text.split('\\n').map(l => l || '<br>').join('<br>');
                const dt = new DataTransfer();
                dt.setData('text/plain', text);
                dt.setData('text/html', html);
                const evt = new ClipboardEvent('paste', {{
                    bubbles: true,
                    cancelable: true,
                    clipboardData: dt,
                }});
                el.dispatchEvent(evt);
            }}""")

        results["D-clipboardEvent"] = test_method(page, selector, "D-clipboardEvent", test_d)

        # =====================================================================
        # Method E: document.execCommand('insertText')
        # =====================================================================
        def test_e():
            page.evaluate(f"""() => {{
                const el = document.querySelector('{selector}');
                el.focus();
                const text = {repr(TEST_TEXT)};
                const lines = text.split('\\n');
                for (let i = 0; i < lines.length; i++) {{
                    if (i > 0) document.execCommand('insertLineBreak', false);
                    if (lines[i]) document.execCommand('insertText', false, lines[i]);
                }}
            }}""")

        results["E-execCommand"] = test_method(page, selector, "E-execCommand", test_e)

        # =====================================================================
        # Method F: Keyboard-level input via CDP Input.dispatchKeyEvent
        # (simulates actual keyboard presses at OS level)
        # =====================================================================
        def test_f():
            page.click(selector)
            cdp = browser.new_cdp_session(page)
            # Use keyDown/keyUp with text parameter for each character
            for ch in TEST_TEXT:
                if ch == '\n':
                    cdp.send("Input.dispatchKeyEvent", {
                        "type": "keyDown", "key": "Enter",
                        "code": "Enter", "windowsVirtualKeyCode": 13
                    })
                    cdp.send("Input.dispatchKeyEvent", {
                        "type": "keyUp", "key": "Enter",
                        "code": "Enter", "windowsVirtualKeyCode": 13
                    })
                else:
                    cdp.send("Input.dispatchKeyEvent", {
                        "type": "keyDown", "text": ch, "key": ch,
                        "windowsVirtualKeyCode": ord(ch.upper())
                    })
                    cdp.send("Input.dispatchKeyEvent", {
                        "type": "keyUp", "key": ch,
                        "windowsVirtualKeyCode": ord(ch.upper())
                    })
            cdp.detach()

        results["F-cdp-keyEvents"] = test_method(page, selector, "F-cdp-keyEvents", test_f)

        # =====================================================================
        # Summary
        # =====================================================================
        print("\n" + "=" * 60)
        print("RESULTS SUMMARY")
        print("=" * 60)
        for method, success in results.items():
            status = "OK" if success else "FAIL"
            print(f"  [{status}] {method}")
        print("=" * 60)

        winners = [m for m, s in results.items() if s]
        if winners:
            print(f"\nWinning method(s): {', '.join(winners)}")
        else:
            print("\nNo method worked! Check screenshots for clues.")

        print("\nPress Enter to close browser...")
        input()
        browser.close()


if __name__ == "__main__":
    main()

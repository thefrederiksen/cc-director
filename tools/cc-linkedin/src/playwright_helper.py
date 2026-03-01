"""Playwright helper for reliable button clicking on LinkedIn."""
import asyncio
import os
import sys


async def _create_post_async(port: int, content: str) -> dict:
    """Create a LinkedIn post using Playwright for reliable clicking."""

    # Set PLAYWRIGHT_BROWSERS_PATH to prevent Playwright from looking for browsers
    # We only use connect_over_cdp which doesn't need local browsers
    os.environ["PLAYWRIGHT_BROWSERS_PATH"] = "0"

    from playwright.async_api import async_playwright

    async with async_playwright() as p:
        try:
            browser = await p.chromium.connect_over_cdp(f"http://localhost:{port}")
            context = browser.contexts[0]
            page = context.pages[0]

            # Navigate to feed if not there
            if "linkedin.com/feed" not in page.url:
                await page.goto("https://www.linkedin.com/feed/")
                await asyncio.sleep(2)

            # Click "Start a post"
            start_post = page.locator("button:has-text('Start a post')")
            if await start_post.count() == 0:
                start_post = page.locator("[data-placeholder*='Start a post']")

            if await start_post.count() > 0:
                await start_post.first.click()
                await asyncio.sleep(2)
            else:
                return {"success": False, "error": "Could not find 'Start a post'"}

            # Type content
            editor = page.locator("[role='textbox'][contenteditable='true']")
            if await editor.count() > 0:
                await editor.first.fill(content)
                await asyncio.sleep(1)
            else:
                return {"success": False, "error": "Could not find editor"}

            # Click Post button
            post_btn = page.locator("button.share-actions__primary-action")
            if await post_btn.count() == 0:
                post_btn = page.get_by_role("button", name="Post", exact=True)

            if await post_btn.count() > 0:
                await post_btn.first.click()
                await asyncio.sleep(3)
            else:
                return {"success": False, "error": "Could not find Post button"}

            # Check for visibility modal (Done button)
            done_btn = page.get_by_role("button", name="Done")
            for _ in range(10):
                if await done_btn.count() > 0:
                    await done_btn.click()
                    await asyncio.sleep(2)

                    # Click Post again if needed
                    post_btn = page.get_by_role("button", name="Post", exact=True)
                    if await post_btn.count() > 0:
                        await post_btn.first.click()
                        await asyncio.sleep(3)
                    break
                await asyncio.sleep(0.5)

            return {"success": True, "message": "Post created"}

        except Exception as e:
            return {"success": False, "error": str(e)}


def create_post(port: int, content: str) -> dict:
    """Synchronous wrapper for creating a post."""
    return asyncio.run(_create_post_async(port, content))

"""Test clicking LinkedIn modal button with Playwright directly."""
import asyncio
from playwright.async_api import async_playwright

async def main() -> None:
    async with async_playwright() as p:
        # Connect to existing browser on port 9223
        browser = await p.chromium.connect_over_cdp("http://localhost:9223")

        # Get the first context and page
        contexts = browser.contexts
        if not contexts:
            print("ERROR: No browser contexts found")
            return

        context = contexts[0]
        pages = context.pages
        if not pages:
            print("ERROR: No pages found")
            return

        page = pages[0]
        print(f"Connected to page: {page.url}")

        # Try to find and click the Done button
        try:
            # Wait for Done button and click it
            done_btn = page.get_by_role("button", name="Done")
            if await done_btn.count() > 0:
                print("Found Done button, clicking...")
                await done_btn.click(force=True)
                print("Clicked Done button")
                await asyncio.sleep(2)
            else:
                print("Done button not found by role")

                # Try by text
                done_btn = page.locator("button:has-text('Done')")
                count = await done_btn.count()
                print(f"Found {count} buttons with 'Done' text")
                if count > 0:
                    await done_btn.first.click(force=True)
                    print("Clicked first Done button")
                    await asyncio.sleep(2)
        except Exception as e:
            print(f"Error clicking Done: {e}")

        # Now try to click Post button
        try:
            post_btn = page.get_by_role("button", name="Post", exact=True)
            if await post_btn.count() > 0:
                print("Found Post button, clicking...")
                await post_btn.click(force=True)
                print("Clicked Post button")
            else:
                print("Post button not found")
        except Exception as e:
            print(f"Error clicking Post: {e}")

        print("Done")

if __name__ == "__main__":
    asyncio.run(main())

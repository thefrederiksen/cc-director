"""Full post flow using Playwright directly."""
import asyncio
from playwright.async_api import async_playwright

async def main() -> None:
    async with async_playwright() as p:
        # Connect to existing browser on port 9223
        browser = await p.chromium.connect_over_cdp("http://localhost:9223")

        context = browser.contexts[0]
        page = context.pages[0]
        print(f"Connected to page: {page.url}")

        # Click "Start a post" to open composer
        print("Looking for Start a post button...")
        start_post = page.locator("button:has-text('Start a post')")
        if await start_post.count() == 0:
            # Try the input field instead
            start_post = page.locator("[data-placeholder*='Start a post']")

        if await start_post.count() > 0:
            print("Clicking Start a post...")
            await start_post.first.click()
            await asyncio.sleep(2)
        else:
            print("Could not find Start a post element")
            return

        # Type the post content
        print("Typing post content...")
        # The editor is a contenteditable div
        editor = page.locator("[role='textbox'][contenteditable='true']")
        if await editor.count() > 0:
            await editor.first.fill("Playwright test post - will delete")
            await asyncio.sleep(1)
        else:
            print("Could not find editor")
            return

        # Click Post button
        print("Clicking Post button...")
        post_btn = page.locator("button.share-actions__primary-action")
        if await post_btn.count() == 0:
            post_btn = page.get_by_role("button", name="Post", exact=True)

        if await post_btn.count() > 0:
            await post_btn.first.click()
            print("Clicked Post, waiting for modal...")
            await asyncio.sleep(3)
        else:
            print("Could not find Post button")
            return

        # Now handle the visibility modal
        print("Looking for visibility modal...")

        # Check if Done button appears (visibility modal)
        done_btn = page.get_by_role("button", name="Done")
        for attempt in range(10):
            count = await done_btn.count()
            print(f"Attempt {attempt+1}: Done button count = {count}")
            if count > 0:
                print("Found Done button, clicking...")
                await done_btn.click()
                print("SUCCESS: Clicked Done button!")
                await asyncio.sleep(2)
                break
            await asyncio.sleep(1)
        else:
            print("Done button never appeared")
            return

        # Check if we need to click Post again
        post_btn = page.get_by_role("button", name="Post", exact=True)
        count = await post_btn.count()
        print(f"Post buttons after Done: {count}")
        if count > 0:
            print("Clicking Post to submit...")
            await post_btn.first.click()
            await asyncio.sleep(3)

        print("DONE - check if post was created")

if __name__ == "__main__":
    asyncio.run(main())

# cc-linkedin Feature Implementation Plan

**Date:** 2026-02-27
**Target files:**
- `src/cc-linkedin/src/cli.py` (main implementation)
- `src/cc-linkedin/src/linkedin_selectors.py` (URL builder)

---

## Feature 1 (Must-Have): Dynamic Scrolling for Connections

**Problem:** Connections command hardcodes 2 scrolls (cli.py line 896), yielding only ~20-30 results regardless of `--limit`.

**Approach:** Replace the fixed `range(2)` loop with a dynamic scroll loop that keeps scrolling until `--limit` is reached or no new cards load.

**Implementation:**

1. **Add a `scroll_until_loaded()` helper function** (new function in cli.py, around line 880):
   ```
   def scroll_until_loaded(client, selector, target_count, max_stale_scrolls=3):
   ```
   - Parameters:
     - `client` - BrowserClient instance
     - `selector` - CSS selector to count elements (`li.mn-connection-card`)
     - `target_count` - desired number of elements (from `--limit`)
     - `max_stale_scrolls` - number of consecutive scrolls with no new elements before giving up (default 3)
   - Logic:
     1. Run JS: `document.querySelectorAll(selector).length` to get current count
     2. If count >= target_count, return
     3. Scroll down with `client.scroll("down")`
     4. Wait with jittered delay (see Feature 4)
     5. Re-count elements
     6. If count did not increase, increment stale counter
     7. If count increased, reset stale counter
     8. If stale counter >= max_stale_scrolls, stop (we've hit the end)
     9. Loop back to step 2

2. **Replace lines 895-898** in the `connections` command:
   ```python
   # OLD:
   for _ in range(2):
       client.scroll("down")
       time.sleep(1)

   # NEW:
   scroll_until_loaded(client, "li.mn-connection-card", limit)
   ```

3. **Console feedback during scrolling:**
   - Print progress: `"Loaded 45 / 200 connections..."` after each scroll batch
   - Use `console.print()` with `end="\r"` for in-place updates (or Rich Status)

**Edge cases:**
- LinkedIn may cap visible connections at some number regardless of scrolling
- Network latency: use 2-second base wait after each scroll to let DOM update
- If `--limit` is very large (500+), add a brief pause every 10 scrolls to reduce detection risk

**Estimated changes:** ~30 lines new helper, ~5 lines modified in connections command

---

## Feature 2 (Must-Have): Connections --search TEXT

**Problem:** No way to filter connections by keyword. User must manually search the connections page.

**Approach:** Add a `--search` option that types into LinkedIn's connections page search bar before extracting results.

**Implementation:**

1. **Add `--search` parameter** to the connections command (cli.py line 884):
   ```python
   def connections(
       limit: int = typer.Option(20, help="Number of connections to show"),
       search: str = typer.Option("", "--search", "-s", help="Search/filter connections by keyword"),
   ):
   ```

2. **After navigation, before scrolling** (between lines 893-895), add search logic:
   ```python
   if search:
       # Step 1: Click the search input to ensure focus/expand
       search_input = client.find("input#mn-connections-search-input")
       # Fallback selector: input[placeholder*="Search"], input[aria-label*="Search"]
       if not search_input:
           search_input = client.find("input[placeholder*='search' i]")

       client.click(search_input ref)
       time.sleep(jittered(1))  # Wait for potential typeahead expansion

       # Step 2: Type the search term
       client.type(search, ref=search_input_ref)
       time.sleep(jittered(2))  # Wait for filtered results to load

       # Step 3: Press Enter if needed (some versions require it)
       client.press("Enter")
       time.sleep(jittered(2))  # Wait for results to update
   ```

3. **Selector discovery strategy** (plan for both simple and typeahead):
   - Try known selector: `input#mn-connections-search-input`
   - Fallback: `input[placeholder*="search" i]` on the connections page
   - If neither found, try clicking the search icon/area first, then look for the input
   - Log a clear error if search input cannot be found

4. **Search interacts with dynamic scrolling:**
   - After search filters results, the dynamic scroll (Feature 1) still applies
   - Filtered results may be fewer, so scroll loop will hit stale quickly -- that's expected

**Estimated changes:** ~25 lines in connections command

---

## Feature 3 (Must-Have): Search --network Filter

**Problem:** `cc-linkedin search` can't filter by connection degree (1st/2nd/3rd).

**Approach:** Add `--network` option and modify `LinkedInURLs.search()` to append the `&network` URL parameter.

**Implementation:**

1. **Modify `LinkedInURLs.search()`** in linkedin_selectors.py (lines 112-130):
   ```python
   @staticmethod
   def search(query: str, search_type: str = "all", network: str = "") -> str:
       # ... existing URL building ...

       if network:
           network_map = {
               "1st": "F",
               "2nd": "S",
               "3rd": "O",
           }
           code = network_map.get(network, "")
           if code:
               url += f'&network=["{code}"]'

       return url
   ```

2. **Add `--network` option** to the search command (cli.py line 1193):
   ```python
   def search(
       query: str = typer.Argument(..., help="Search query"),
       search_type: str = typer.Option("people", "--type", "-t", help="Search type: people, posts, companies, jobs"),
       limit: int = typer.Option(10, help="Number of results to show"),
       network: str = typer.Option("", "--network", "-n", help="Connection degree: 1st, 2nd, 3rd"),
   ):
   ```

3. **Pass network to URL builder** (cli.py line 1201):
   ```python
   url = LinkedInURLs.search(query, search_type, network=network)
   ```

4. **Validation:** If `--network` is used with `--type` other than `people`, print warning:
   ```
   WARNING: --network filter only applies to people search. Ignoring for type '{search_type}'.
   ```

**Note:** Network filter is single-value only (1st OR 2nd OR 3rd, not combinations).

**Estimated changes:** ~15 lines in linkedin_selectors.py, ~10 lines in cli.py

---

## Feature 4 (Must-Have): Random Jitter on All Delays

**Problem:** All `time.sleep()` calls use fixed durations. LinkedIn detects regular timing patterns.

**Approach:** Create a `jittered_sleep()` helper with tiered jitter based on base duration, then replace all `time.sleep()` calls.

**Implementation:**

1. **Add jitter helper** at top of cli.py (around line 30, after imports):
   ```python
   import random

   def jittered_sleep(base: float) -> None:
       """Sleep with random jitter. Tiered by base duration to avoid breaking fast loops."""
       if base < 1.0:
           jitter = random.uniform(0, 0.5)
       elif base < 3.0:
           jitter = random.uniform(0, 1.5)
       else:
           jitter = random.uniform(0, 2.0)
       time.sleep(base + jitter)
   ```

   Tier logic:
   | Base sleep | Jitter range | Effective range | Use case |
   |-----------|-------------|----------------|----------|
   | < 1.0s    | 0 - 0.5s   | 0.3s - 1.5s   | Modal loops, quick checks |
   | 1.0 - 2.9s | 0 - 1.5s | 1.0s - 4.4s   | Scroll waits, click waits |
   | >= 3.0s   | 0 - 2.0s   | 3.0s - 5.0s   | Navigation, page loads |

2. **Replace all `time.sleep()` calls** across both files:
   - **cli.py:** ~58 calls -> `jittered_sleep(N)`
   - **cdp_helper.py:** ~10 calls -> `jittered_sleep(N)`
   - Simple find-and-replace: `time.sleep(` -> `jittered_sleep(`

3. **Import `random`** at top of both files (if not already imported)

4. **Share the helper:**
   - Define `jittered_sleep()` in a small utility module (e.g., `src/utils.py`) OR
   - Define it in cli.py and import in cdp_helper.py
   - Simplest: define in cli.py, import in cdp_helper.py via `from cli import jittered_sleep`

**Estimated changes:** ~10 lines new helper, ~68 line modifications (mechanical replacement)

---

## Feature 5 (Nice-to-Have): --output FILE on Connections

**Problem:** Console output includes Rich formatting characters that pollute piped output.

**Approach:** Add `--output` option that writes clean JSON to a file with metadata wrapper.

**Implementation:**

1. **Add `--output` parameter** to connections command:
   ```python
   def connections(
       limit: int = typer.Option(20, help="Number of connections to show"),
       search: str = typer.Option("", "--search", "-s", help="Search/filter connections by keyword"),
       output: str = typer.Option("", "--output", "-o", help="Write JSON output to file"),
   ):
   ```

2. **After data extraction, before display** (around line 935):
   ```python
   if output:
       from datetime import datetime
       export_data = {
           "exported_at": datetime.now().isoformat(),
           "search_filter": search or None,
           "total": len(connections_data),
           "limit": limit,
           "connections": connections_data[:limit],
       }
       with open(output, "w", encoding="utf-8") as f:
           json.dump(export_data, f, indent=2, ensure_ascii=False)
       console.print(f"Wrote {len(connections_data[:limit])} connections to {output}")
       return
   ```

3. **JSON structure written to file:**
   ```json
   {
     "exported_at": "2026-02-27T14:30:00.000000",
     "search_filter": "Toronto",
     "total": 87,
     "limit": 100,
     "connections": [
       {"username": "jdoe", "name": "Jane Doe", "headline": "CEO at Acme"},
       ...
     ]
   }
   ```

4. **When `--output` is used, still print summary to console** (count written) but skip the table/JSON console output.

**Estimated changes:** ~20 lines in connections command

---

## Feature 6 (Nice-to-Have): --append FILE / Resume Mode

**Problem:** Long extraction sessions may fail partway. No way to resume without re-fetching everything.

**Approach:** Add `--append` option that reads an existing JSON file, builds a set of already-captured usernames, and skips them during extraction.

**Implementation:**

1. **Add `--append` parameter** to connections command:
   ```python
   def connections(
       limit: int = typer.Option(20, help="Number of connections to show"),
       search: str = typer.Option("", "--search", "-s", help="Search/filter connections by keyword"),
       output: str = typer.Option("", "--output", "-o", help="Write JSON output to file"),
       append: str = typer.Option("", "--append", "-a", help="Resume: append to existing JSON file, skip duplicates"),
   ):
   ```

2. **At start of command, load existing data:**
   ```python
   existing_usernames = set()
   existing_connections = []

   if append:
       if os.path.exists(append):
           with open(append, "r", encoding="utf-8") as f:
               existing_data = json.load(f)
           # Support both wrapped format and plain array
           if isinstance(existing_data, dict) and "connections" in existing_data:
               existing_connections = existing_data["connections"]
           elif isinstance(existing_data, list):
               existing_connections = existing_data
           existing_usernames = {c["username"] for c in existing_connections if c.get("username")}
           console.print(f"Loaded {len(existing_usernames)} existing connections from {append}")
   ```

3. **After extraction, filter out duplicates:**
   ```python
   new_connections = [c for c in connections_data if c.get("username") not in existing_usernames]
   console.print(f"Found {len(new_connections)} new connections ({len(connections_data)} total on page, {len(existing_usernames)} already captured)")
   ```

4. **Merge and write:**
   ```python
   if append:
       merged = existing_connections + new_connections
       export_data = {
           "exported_at": datetime.now().isoformat(),
           "search_filter": search or None,
           "total": len(merged),
           "connections": merged,
       }
       with open(append, "w", encoding="utf-8") as f:
           json.dump(export_data, f, indent=2, ensure_ascii=False)
       console.print(f"Wrote {len(merged)} total connections to {append} ({len(new_connections)} new)")
       return
   ```

5. **Dedup key:** `username` field (confirmed reliable for this use case)

6. **--append implies --output behavior:** When `--append` is set, file writing is automatic. No separate `--output` needed.

7. **Mutual exclusivity:** If both `--output` and `--append` are provided, error:
   ```
   ERROR: Cannot use both --output and --append. Use --append to resume into an existing file, or --output to write a new file.
   ```

**Estimated changes:** ~35 lines in connections command

---

## Implementation Order

Recommended sequence (dependencies noted):

| Order | Feature | Depends on | Reason |
|-------|---------|-----------|--------|
| 1 | **Feature 4** - Jitter | None | Foundation. All other features use delays. Do this first so new code uses jitter from the start. |
| 2 | **Feature 1** - Dynamic scrolling | Feature 4 | Core fix. Uses jittered delays. Unlocks the real value of --limit. |
| 3 | **Feature 3** - Search --network | None | Small, isolated change in URL builder. Quick win. |
| 4 | **Feature 2** - Connections --search | Feature 1, 4 | Builds on dynamic scrolling. Search + scroll together. |
| 5 | **Feature 5** - --output FILE | Feature 2 | Writes output from connections (including search results). |
| 6 | **Feature 6** - --append / resume | Feature 5 | Extends --output with merge/dedup logic. |

---

## Files Modified Summary

| File | Changes |
|------|---------|
| `src/cli.py` | Features 1-6: new helper functions, modified connections command, modified search command, jitter replacement (~68 sleep calls) |
| `src/linkedin_selectors.py` | Feature 3: add `network` param to `LinkedInURLs.search()` |
| `src/cdp_helper.py` | Feature 4: replace ~10 sleep calls with jittered_sleep |

**No new files required.** All changes fit within existing modules.

---

## Testing Approach

Each feature should be tested manually against a live LinkedIn session:

1. **Feature 4 (Jitter):** Run any command, observe varying delays in verbose mode
2. **Feature 1 (Dynamic scroll):** `cc-linkedin connections --limit 100 -v` -- verify it loads >30 results
3. **Feature 3 (Network):** `cc-linkedin search "engineer" --type people --network 1st` -- verify URL has `&network=["F"]`
4. **Feature 2 (Search):** `cc-linkedin connections --search "Toronto" --limit 50`
5. **Feature 5 (Output):** `cc-linkedin connections --output test.json` -- verify clean JSON file
6. **Feature 6 (Append):** Run twice with `--append out.json` -- verify dedup on second run

---

## Risk Notes

- **Rate limiting:** Dynamic scrolling (Feature 1) does many more page interactions. The jitter (Feature 4) mitigates this, but scrolling 20+ times in one session is more aggressive than 2 scrolls. Consider adding a configurable `--scroll-delay` override for cautious users.
- **Selector stability:** LinkedIn periodically changes DOM structure. The `li.mn-connection-card` selector and search input selectors may break. Code should log clear errors when selectors return 0 results.
- **Large datasets:** Scrolling to load 500+ connections means keeping all those DOM nodes in memory in the browser. Performance may degrade. The scroll helper should have a hard ceiling (e.g., 50 scroll attempts max) to prevent infinite loops.

---
name: update-docs
description: Update public documentation in docs/public/ when features or tools change. Triggers on "/update-docs", "update docs", "update documentation".
---

# Update Documentation

Keep public-facing documentation (`docs/public/`) in sync with code changes. This skill determines what changed, identifies which doc pages need updating, writes or updates the markdown files, and updates the manifest.

## Quick Reference

| Action | Description |
|--------|-------------|
| Detect changes | Read git diff to determine what changed |
| Identify doc pages | Match changes to existing or new doc pages |
| Write/update docs | Create or edit markdown files in docs/public/ |
| Update manifest | Add new pages to docs/public/index.json if needed |

## Workflow

### STEP 1: Determine What Changed

Use Bash to gather context:

```bash
git diff --cached --name-only
git diff --name-only
git status
```

Also check for a user-provided description of what changed. If the user says "I added cc-newtools" or "I changed the session manager", use that as primary context.

### STEP 2: Read Current Documentation State

Use the Read tool to read `docs/public/index.json` to understand:
- What categories exist
- What pages exist
- What file paths are referenced

### STEP 3: Classify the Change

Determine which type of documentation update is needed:

| Change Type | Doc Action |
|-------------|-----------|
| New tool added (cc-*) | Add entry to tools/overview.md, optionally create dedicated tool page |
| New feature in existing tool | Update the relevant tool section in tools/overview.md |
| New skill added | Update tools/overview.md or create a new page |
| Architecture change | Update getting-started/introduction.md if it affects the overview |
| Installation change | Update getting-started/installation.md |
| Workflow change | Update getting-started/quick-start.md |
| New category of docs needed | Create new directory and pages, update index.json |

### STEP 4: Read Source Material

For tool changes, read the tool's README or help output to get accurate command syntax and options. Key source files:

- `docs/CC_TOOLS.md` -- master reference for all tool documentation
- Tool README files in `tools/` subdirectories
- Help output from the tool itself (`tool-name --help`)

Do NOT guess at command syntax or options. Read the actual source.

### STEP 5: Write Documentation Updates

For each file that needs updating:

1. Use the Read tool to read the current content
2. Use the Edit tool to make targeted changes (preferred) or Write tool for new files
3. Follow these writing guidelines:

**Tone:** Clear, direct, practical. Write for someone who wants to get things done, not learn theory.

**Structure:**
- Start with what the tool/feature does (one sentence)
- Show the most common usage immediately (code block)
- List options in a table
- Add notes or warnings at the end

**Code examples:** Always show real, working commands. Include the most common use case first.

**No filler:** Skip "In this section you will learn..." phrasing. Get to the point.

### STEP 6: Update index.json (if needed)

If new pages were created, add them to `docs/public/index.json`:

1. Read the current index.json
2. Add the new page entry to the appropriate category
3. If a new category is needed, add the category with its pages
4. Write the updated index.json

Validate that:
- Every `file` path in index.json points to an actual file in docs/public/
- Every file in docs/public/ (except index.json) has an entry in index.json
- Category and page IDs are lowercase kebab-case
- Page titles are clear and concise

### STEP 7: Report Changes

Present a summary to the user:

```
Documentation Update Complete

Files updated:
- docs/public/tools/overview.md (added cc-newtool section)

Files created:
- (none)

Manifest changes:
- (none)

Status: DONE
```

## Examples

### Example 1: New Tool Added

**User:** /update-docs -- I added cc-calendar tool

**Agent:**
1. Reads git diff to find cc-calendar related changes
2. Reads docs/CC_TOOLS.md for the tool's documentation
3. Reads docs/public/tools/overview.md
4. Adds cc-calendar entry to the Quick Reference table
5. Adds a new section for cc-calendar with usage examples
6. Reports what was changed

### Example 2: Feature Added to Existing Tool

**User:** /update-docs -- added chart support to cc-excel

**Agent:**
1. Reads git diff to understand the chart feature
2. Reads docs/public/tools/overview.md
3. Updates the cc-excel section to include chart commands
4. Reports what was changed

### Example 3: Called by /review-code

When `/review-code` detects missing documentation and suggests running `/update-docs`:

1. Uses the review output to understand what's missing
2. Follows the same workflow above
3. After completion, user can re-run `/review-code` to verify

## Notes

- This skill only modifies files in `docs/public/`
- It never modifies internal docs like `docs/CodingStyle.md` or `docs/VisualStyle.md`
- The manifest at `docs/public/index.json` is consumed by the project website at runtime
- All documentation is served via raw.githubusercontent.com -- no build step needed

---

**Skill Version:** 1.0
**Last Updated:** 2026-03-01

---
name: enter-issue
description: Create a GitHub issue from screenshots, images, text descriptions, or any combination. Use this when the user wants to file a new issue or report a bug.
disable-model-invocation: true
argument-hint: [title or description]
---

# Create GitHub Issue

Create a new GitHub issue based on the user's input. The input may include screenshots, images, text descriptions, error logs, or any combination.

## Input: $ARGUMENTS

## Instructions

1. **Gather context from the user's input:**
   - Read any text description or title provided in `$ARGUMENTS`
   - If the user has provided screenshot paths or image paths, read them using the Read tool to understand their content
   - Common screenshot locations to check if referenced:
     - `C:\Users\sfrederiksen\Pictures\Screenshots`
     - `D:\Personal\OneDrive\Pictures\Screenshots`
   - If no title is provided, derive one from the description/images

2. **Collect additional information if needed:**
   - If the description is vague, use AskUserQuestion to clarify:
     - What is the expected behavior?
     - What is the actual behavior?
     - Steps to reproduce?
     - Priority/severity?
   - If the user provided enough information, proceed without asking

3. **Upload images to the issue:**
   - If images/screenshots were provided, they need to be included in the issue
   - Use `gh issue create` which supports image references in markdown
   - For local images, upload them by referencing in the issue body — GitHub CLI handles image uploads when you use the web editor, so instead embed image paths as attachments
   - Strategy: Create the issue first, then use `gh issue edit` to add images if needed, OR reference images inline

4. **Determine labels:**
   - Check available labels: `gh label list`
   - Apply appropriate labels (bug, enhancement, documentation, etc.)
   - If no matching labels exist, create them if appropriate

5. **Create the issue using GitHub CLI:**
   ```
   gh issue create --title "TITLE" --body "BODY" --label "LABELS"
   ```
   - Title: Clear, concise, imperative form (e.g., "Fix crash when opening settings")
   - Body format:
     ```markdown
     ## Description
     [Clear description of the issue]

     ## Screenshots
     [Any attached images]

     ## Steps to Reproduce (if bug)
     1. Step one
     2. Step two

     ## Expected Behavior
     [What should happen]

     ## Actual Behavior
     [What actually happens]

     ## Additional Context
     [Any extra info, logs, environment details]
     ```

6. **Report back:**
   - Show the created issue URL
   - Show the issue number
   - Summarize what was created

## Important Notes
- Always use `gh` CLI for GitHub operations
- Use a HEREDOC for the body to preserve formatting:
  ```bash
  gh issue create --title "Title" --body "$(cat <<'EOF'
  ## Description
  ...
  EOF
  )"
  ```
- If images were provided, mention that they should be manually attached via the GitHub web UI if CLI upload is not possible, and provide the issue URL for easy access

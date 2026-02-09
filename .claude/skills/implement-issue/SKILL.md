---
name: implement-issue
description: Implement a GitHub issue end-to-end and create a PR. Reads the issue, creates a branch, implements the solution, runs tests, and opens a pull request.
disable-model-invocation: true
argument-hint: [issue-number]
---

# Implement GitHub Issue & Create PR

Implement a GitHub issue from start to finish: understand the requirements, write the code, run tests, and create a pull request.

## Input: Issue #$ARGUMENTS

## Instructions

### Phase 1: Understand the Issue

1. **Fetch the full issue:**
   ```bash
   gh issue view $0 --json title,body,labels,assignees,comments,state,milestone
   ```

2. **Download any attached images:**
   - Parse the issue body and comments for image URLs
   - Download and view them using the Read tool to understand visual requirements/bugs

3. **Analyze requirements:**
   - What needs to change?
   - What are the acceptance criteria?
   - Are there any constraints mentioned?
   - Which parts of the codebase are likely affected?

4. **Explore the codebase** to understand the relevant code:
   - Use Glob and Grep to find related files
   - Read the relevant source files
   - Understand existing patterns and architecture

### Phase 2: Plan the Implementation

1. **Create a brief implementation plan** covering:
   - Files to modify/create
   - Approach and key decisions
   - Test strategy

2. **Present the plan to the user** using AskUserQuestion:
   - Summarize what you'll do
   - Ask if they want to proceed or adjust the approach
   - If the implementation is straightforward, you may skip explicit approval

### Phase 3: Create a Branch

1. **Ensure clean working state:**
   ```bash
   git status
   ```

2. **Create a feature branch from main:**
   ```bash
   git checkout main
   git pull origin main
   git checkout -b issue-$0-SHORT_DESCRIPTION
   ```
   - Branch naming: `issue-NUMBER-short-kebab-description`
   - Example: `issue-42-fix-session-crash`

### Phase 4: Implement the Solution

1. **Write the code:**
   - Follow existing code patterns and conventions
   - Keep changes focused on the issue — no unrelated cleanups
   - Write clean, readable code

2. **Add or update tests** as appropriate:
   - Unit tests for new logic
   - Update existing tests if behavior changes

3. **Build and verify:**
   ```bash
   dotnet build
   ```

4. **Run tests:**
   ```bash
   dotnet test
   ```
   - Fix any failures before proceeding
   - If tests fail and the fix isn't straightforward, inform the user

### Phase 5: Commit Changes

1. **Stage relevant files:**
   ```bash
   git add <specific-files>
   ```

2. **Create a descriptive commit:**
   - Reference the issue number in the commit message
   - Follow conventional commit style
   ```bash
   git commit -m "$(cat <<'EOF'
   fix: description of what was fixed

   Closes #ISSUE_NUMBER

   Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
   EOF
   )"
   ```

### Phase 6: Push and Create PR

1. **Push the branch:**
   ```bash
   git push -u origin HEAD
   ```

2. **Create the pull request:**
   ```bash
   gh pr create --title "TITLE" --body "$(cat <<'EOF'
   ## Summary
   Brief description of what this PR does.

   Closes #ISSUE_NUMBER

   ## Changes
   - Change 1
   - Change 2

   ## Test Plan
   - [ ] Tests pass (`dotnet test`)
   - [ ] Build succeeds (`dotnet build`)
   - [ ] Manual verification steps if applicable

   🤖 Generated with [Claude Code](https://claude.com/claude-code)
   EOF
   )"
   ```

3. **Report the result:**
   - Show the PR URL
   - Show a summary of changes made
   - Confirm the issue will be auto-closed when merged

## Important Notes
- Always use `gh` CLI for GitHub operations
- Never force-push or modify existing commits
- Keep the PR focused on the issue — one issue per PR
- Run `dotnet build` and `dotnet test` before creating the PR
- Reference the issue number with `Closes #N` in the PR body for auto-closing
- If the implementation requires significant architectural decisions, discuss with the user first
- The commit message should NOT be created unless the user explicitly asks to commit
- Ask user for confirmation before pushing and creating the PR

---
name: commit
description: Create a git commit following the project's commit standards. Triggers on "/commit" or when user asks to commit changes.
---

# Commit Skill

Commit workflow: review code, list files, get approval, then drive ALL THE WAY to a merged
pull request on origin/main and park the checkout back on main.

## Trunk-based development - "done" means merged to main

This repo (like every repo here) does trunk-based development. There are three states of
work and only one counts:

1. **Committed / pushed to a side branch** - still IN PROGRESS. A backup, not a delivery.
2. **An open pull request** - still IN PROGRESS.
3. **Merged to origin/main** - DONE. Tracked, visible to every agent, carries its pull
   request record so it can be QA'd or reverted later.

So a commit request is NOT finished at `git push`. Committing still requires the user's
explicit ask (per CLAUDE.md) - but ONCE the user approves the commit, that authorizes driving
it the rest of the way: push, open a pull request, merge on green, delete the branch, and park
the checkout back on main. "I committed and pushed" is an unfinished job. Merge first, QA
after - main is GitHub, not a public release.

The four supporting rules (see the TRUNK DEVELOPMENT section of the global CLAUDE.md):
- A branch lives less than a day - merge the day it is born.
- Soft cap of 3 open pull requests at once - finish one before opening a fourth.
- One checkout per concurrent activity - never build two workstreams from one tree; a
  concurrent agent uses its own worktree off origin/main.
- Never commit directly on main. If the checkout is on main, create a branch first.

## One remote build request per coherent batch

Local commits are checkpoints; a push requests shared GitHub validation. Complete all known code,
tests, proof, and applicable local checks before the first push. Do not push intermediate progress
or a proof-only follow-up when both can be sent together. If remote checks expose a defect, gather
and locally validate the complete known correction batch before pushing once more.

## Triggers

Invoke with /commit or when user asks to commit changes.

## Scope

Default: Only files Claude modified this session.
With "all" argument: All uncommitted changes.
With specific files: Only those files.

## Workflow

STEP 1: Determine scope and gather changes

Run git status to see all changes.
Run git log --oneline -5 to see recent commit message style.

If user specified "all", include everything.
If user specified files, use those.
Otherwise, use only files modified during this Claude Code session.

STEP 2: Run code review

Use the Skill tool to invoke the review-code skill.

Wait for the review to complete.

Read the REVIEW_STATUS line from the output.

If REVIEW_STATUS is FAIL:
- Show the blocking issues found
- Tell the user they have three options:
  1. Reply "fix issues" for help resolving them
  2. Fix manually and run /commit again
  3. Reply "commit anyway" to bypass (not recommended)
- STOP and wait for user response

If REVIEW_STATUS is PASS:
- Note any warnings
- Continue to Step 3

STEP 3: Present commit plan

Show the user:
- List of files to commit (status and path)
- Code review result summary
- Proposed commit message

Commit message format:
  [type]: [Short description]

  [Optional body explaining why]

Type prefixes: feat, fix, refactor, docs, style, test, chore

Do NOT include "Co-Authored-By" lines in commit messages.

Ask: Approve this commit? Reply "yes" to commit and push.

STEP 4: Wait for approval

DO NOT PROCEED until user says yes, ok, approved, go ahead, or proceed.

If user says no or requests changes, update the plan and re-present.

STEP 5: Put the work on a branch (never commit directly on main)

Check the current branch: git branch --show-current.

If the checkout is already on a short-lived feature branch, commit there.

If the checkout is on main, create a branch FIRST - never commit on main:
- If no other session shares this checkout: git checkout -b <type>/<short-desc>
- If this checkout is shared with other live sessions (a fleet checkout), do NOT move its
  HEAD. Create an isolated worktree off origin/main and work there:
  git worktree add ../<repo>-wt-<short-desc> -b <type>/<short-desc> origin/main
  then cd into it. (See the "Shared checkout" rules in the global CLAUDE.md.)

STEP 6: Commit, then send one remote build request

Stage the specific files using git add with each filename.
Never use git add . or git add -A.

Commit using HEREDOC format:
git commit -m "$(cat <<'EOF'
[message here]
EOF
)"

Run git status to verify commit succeeded.

After every known file and local check is complete, push once with: git push -u origin HEAD.

If push fails due to remote changes, run git pull --rebase then git push.

STEP 7: Open the pull request, merge on green, and park back on main

The job is not done until the work is MERGED to origin/main. Once the user approved the
commit (Step 4), drive it home:

1. Open the pull request: gh pr create --fill (or with a title/body matching recent PRs).
2. Wait for checks to go green: gh pr checks <number> --watch.
   - If checks fail, gather every known correction, validate the whole correction batch locally,
     and push the NEW commits together (never amend, never --no-verify), then re-watch. Report the
     failure to the user if it is not quick and mechanical.
3. Merge with squash once green: gh pr merge <number> --squash --delete-branch.
   (delete_branch_on_merge is ON, but --delete-branch is explicit and harmless.)
4. Park the checkout back on main:
   git checkout main && git pull
   If you worked in a worktree, remove it: git worktree remove ../<repo>-wt-<short-desc>.
5. Verify the resting state: git status --short is empty, git branch --show-current is main,
   and the feature branch is gone (git branch --list <type>/<short-desc> prints nothing).

STEP 8: Report completion

Tell the user:
- Pull request number and merge commit on main
- Number of files changed
- That the branch was deleted and the checkout is parked back on main, clean
- Any exception (e.g. left open because checks are still running) WITH the reason

## Handling bypass requests

If user says "commit anyway" with blocking issues:
- Warn about the risks (runtime errors, security, UI issues)
- Ask them to type "I understand the risks"
- If confirmed, add a note about bypassed review in the commit message
- Proceed with commit

## Git rules (from CLAUDE.md)

NEVER do these:
- Update git config
- Run push --force, reset --hard, checkout ., clean -f
- Skip hooks with --no-verify
- Force push to main/master
- Use git add . or git add -A
- Amend commits unless explicitly requested

ALWAYS do these:
- Use HEREDOC for commit messages
- Drive an approved commit all the way to merged-on-main, then park the checkout on main
- Create NEW commit after hook failures, never amend

---

**Skill Version:** 2.0 - trunk-based development (2026-07-11)
**Changed from 1.0:** The workflow no longer stops at `git push`. Once the user approves the
commit it drives all the way to a merged pull request on origin/main and parks the checkout
back on main: branch-if-on-main (or isolated worktree on a shared checkout), push, open the
pull request, merge on green (squash + delete branch), park on main, verify clean. Added the
trunk-based-development framing (merged-to-main is the only "done") aligned with the global
CLAUDE.md TRUNK DEVELOPMENT section and the /repo-hygiene skill.
**Adapted from:** sampleWeb commit skill

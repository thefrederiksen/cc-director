---
name: campaign
description: Run email outreach campaigns using vault contact lists. Drafts personalized emails per contact and queues them for approval. Triggers on "/campaign", "run campaign", "email campaign", "outreach campaign".
---

# Campaign Skill -- Email Outreach via Vault Lists

Draft personalized emails for an entire vault contact list and queue them for approval in Communication Manager.

## Triggers

Invoke with /campaign or when user asks to run an email outreach campaign.

## Workflow

### STEP 1: Gather campaign parameters

Ask the user for these parameters (use AskUserQuestion or conversational prompts):

1. **Campaign name** -- e.g., "Q1 Process Mining Outreach"
2. **Vault list name** -- e.g., "Q1 Prospects" (must exist in cc-vault)
3. **Email purpose/brief** -- the value proposition, CTA, and key message
4. **Send-from account** -- mindzie, personal, or consulting
5. **Subject line direction** -- base subject line to personalize per contact

If the user provides some of these upfront (e.g., in the /campaign invocation), skip asking for those.

### STEP 2: Validate the list

Run:
```bash
cc-vault lists export "<list-name>" -f json
```

Parse the JSON output. Report:
- Total contacts in list
- How many have email addresses (will be drafted)
- How many will be skipped (no email address)

If the list is empty or does not exist, tell the user and stop.

Ask the user to confirm before proceeding: "Ready to draft [N] emails for campaign '[name]'?"

### STEP 3: Generate campaign ID

Create a slug from the campaign name + today's date:
- Lowercase the name
- Replace spaces with hyphens
- Remove special characters
- Append date: `-YYYY-MM-DD`

Example: "Q1 Process Mining Outreach" -> `q1-process-mining-outreach-2026-03-03`

Tell the user the campaign ID.

### STEP 4: Fetch rich contact data and draft emails

For each contact that has an email address:

1. Fetch full contact data:
   ```bash
   cc-vault contacts show <contact-id>
   ```

2. Show progress inline:
   ```
   [1/43] Drafting for John Smith (Acme Corp)...
   ```

3. Draft a personalized email using:
   - The campaign brief as the core message
   - Contact's company, title, and context for tailored value prop
   - Relationship history and memories for personal touches
   - Contact's style preference (formal/casual/friendly) if stored
   - Their preferred greeting and signoff if stored
   - Keep it genuine and conversational -- no template feel

4. Queue the email via cc-comm-queue:
   ```bash
   cc-comm-queue add email email "<email-body>" \
     --email-to "<contact-email>" \
     --email-subject "<personalized-subject>" \
     --send-from <account> \
     --campaign-id "<campaign-id>" \
     --tags "campaign,<campaign-slug>" \
     --notes "Campaign: <campaign-name> | List: <list-name> | Contact: <contact-name>" \
     --created-by "campaign-skill"
   ```

Important:
- Use --json flag on cc-comm-queue add to get structured output for reliable parsing
- If a queue add fails, log the error and continue with the next contact -- do not stop the entire campaign
- Track successes, failures, and skips

### STEP 5: Summary report

After all emails are drafted, show:

```
CAMPAIGN COMPLETE: <campaign-name>
Campaign ID: <campaign-id>
---
Drafted: <N> emails
Skipped: <N> contacts (no email)
Failed:  <N> errors
---
Next steps:
  1. Review emails: cc-comm-queue list --campaign-id "<campaign-id>"
  2. Open Communication Manager to approve/edit/reject individual emails
  3. Approved emails will be sent by the dispatcher service
```

If there were failures, list the contact names that failed with brief error reasons.

## Rules

- NEVER send emails directly. All emails go through cc-comm-queue for approval.
- Always use cc-vault CLI for contact data -- never query databases directly.
- Always use cc-comm-queue CLI for queuing -- never write to the database directly.
- If cc-vault or cc-comm-queue fails with a tool error (crash, missing module), STOP and report per CLAUDE.md rules.
- Do not draft emails for contacts without email addresses -- skip them silently and count them.
- Each email must be genuinely personalized, not just "Hi {name}" template substitution.

## Arguments

Optional arguments can be passed after /campaign:
- List name: `/campaign "Q1 Prospects"` -- pre-fills the vault list
- Full spec: The user may provide all parameters in natural language

---

**Skill Version:** 1.0
**Last Updated:** 2026-03-03

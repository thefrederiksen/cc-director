# LinkedIn Contact Enrichment Plan

## Problem

We have 3,178 contacts in the vault that were imported from `pini_contacts.db` (a LinkedIn prospect database) on 2026-02-18. These contacts have:

- No name (empty string)
- No email
- No company
- A LinkedIn profile URL (e.g. `https://linkedin.com/in/aakarsh-rana`)
- Account: consulting, relationship: prospect, lead_source: linkedin

The original database has been deleted, so we cannot recover the data from the source. The only way to get real information is to visit each LinkedIn profile and extract what we can.

## Proof of Concept (Completed)

We successfully tested this with contact #3097:

1. Navigated to `https://linkedin.com/in/aakarsh-rana` via cc-linkedin
2. Extracted: Name (Aakarsh Rana), Company (ZS), Title (Decision Analytics Associate Intern), Location (Delhi, India)
3. Updated the vault contact with all extracted info
4. Added a memory note about origin

## What Data We Can Extract

The cc-linkedin `profile` command runs JavaScript on the profile page. Currently it extracts:

| Field | LinkedIn Source | Vault Column |
|-------|---------------|--------------|
| Full name | `h1.text-heading-xlarge` | `name` |
| Headline | `div.text-body-medium` | stored in `context` as notes |
| Location | `span.text-body-small.inline` | `location` |
| Connections/Followers | sidebar span | stored in memory |
| About section | about section | stored in memory |

With additional JavaScript extraction we can also get:

| Field | LinkedIn Source | Vault Column |
|-------|---------------|--------------|
| Current company | Experience section (first entry) | `company` |
| Current title | Experience section (first entry) | `title` |
| Profile photo URL | `img.pv-top-card-profile-picture` | stored in memory |
| Education | Education section | stored in memory |
| Website | Contact info section | `website` |
| Twitter/X | Contact info section | `twitter` |
| Industry | Profile header area | stored in memory |
| Pronouns | Next to name | stored in memory |

## Architecture

### New cc-linkedin command: `enrich`

```
cc-linkedin enrich <linkedin-url-or-username> --format json
```

Returns a JSON object with all extractable profile data:

```json
{
  "name": "Aakarsh Rana",
  "headline": "DAA intern @ZS | Former Instrumentation intern @HPCL",
  "location": "Delhi, India",
  "current_company": "ZS",
  "current_title": "Decision Analytics Associate Intern",
  "about": "...",
  "education": "Netaji Subhas University of Technology (NSUT), Delhi",
  "website": null,
  "twitter": null,
  "pronouns": "He/Him",
  "connections": "500+",
  "profile_exists": true
}
```

If the profile doesn't exist (404, "page not found"), returns:

```json
{
  "profile_exists": false
}
```

### New cc-vault command: `contacts enrich`

```
cc-vault contacts enrich <contact-id> --data '<json from cc-linkedin>'
```

Takes the JSON output from `cc-linkedin enrich` and updates the vault contact:

- Sets `name`, `company`, `title`, `location` from the JSON
- Stores headline, about, education, pronouns as memories
- Marks the contact as `reviewed = 1`
- Sets `last_contact` to today (we "visited" them)

### Orchestration script: `scripts/enrich-contacts.py`

A Python script that:

1. Queries the vault for all contacts where `name = ''` and `linkedin IS NOT NULL`
2. For each contact:
   a. Extracts the username from the LinkedIn URL
   b. Calls `cc-linkedin enrich <username> --format json`
   c. Parses the JSON response
   d. If profile exists: calls `cc-vault contacts update` with extracted data
   e. If profile doesn't exist: marks contact with a memory "LinkedIn profile not found"
   f. Logs progress to `scripts/enrich-contacts.log`
   g. Waits a random delay before the next one
3. Tracks progress in a simple state file so it can resume if interrupted

## Rate Limiting Strategy

LinkedIn will flag automated browsing. We must be careful:

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| Delay between profiles | 45-90 seconds (random) | Mimics human browsing |
| Max profiles per session | 30 | Stay under radar per session |
| Cool-down between sessions | 15-30 minutes | Let things settle |
| Max profiles per day | 150 | Conservative daily limit |
| Total time to complete | ~22 days at 150/day | 3,178 / 150 |

The script should:
- Use random jitter on all delays (not fixed intervals)
- Stop after 30 profiles and wait 15-30 minutes
- Stop after 150 profiles for the day
- Log every action with timestamps
- Save state so it can resume tomorrow

## Handling Edge Cases

| Case | Action |
|------|--------|
| Profile doesn't exist (deleted/deactivated) | Add memory "Profile not found as of YYYY-MM-DD", skip |
| Profile is private / limited view | Extract whatever is visible, add memory "Limited profile" |
| Name is in non-Latin script | Store as-is (UTF-8 is fixed now) |
| LinkedIn blocks/rate limits | Stop immediately, log the error, resume later |
| Page fails to load | Retry once after 30s, then skip and log |
| Contact already has a name | Skip (already enriched) |
| Duplicate LinkedIn URLs | Process once, skip duplicates |

## Implementation Steps

### Step 1: Add `enrich` command to cc-linkedin

- Add new JavaScript extraction that gets all available profile data
- Return structured JSON
- Handle missing/private profiles gracefully
- Rebuild cc-linkedin

### Step 2: Add `contacts enrich` command to cc-vault

- Accept JSON blob and contact ID
- Map fields to vault columns
- Store extra info as memories
- Rebuild cc-vault

### Step 3: Create orchestration script

- Query vault for blank contacts
- Loop with rate limiting
- State file for resume capability
- Logging

### Step 4: Test run (10 contacts)

- Run on 10 contacts manually
- Verify data quality
- Check LinkedIn doesn't flag anything
- Adjust delays if needed

### Step 5: Production run

- Run in batches of 150/day
- Monitor for LinkedIn warnings
- ~22 days to complete all 3,178

## Vault Fields Mapping

```
LinkedIn Profile Data    ->    Vault Contact Field
------------------------------------------------------
Name                     ->    name
Headline                 ->    context (notes)
Location                 ->    location
Current Company          ->    company
Current Title            ->    title
Website                  ->    website
Twitter/X                ->    twitter
About                    ->    memory (category: "about")
Education                ->    memory (category: "education")
Pronouns                 ->    memory (category: "personal")
Connections count        ->    memory (category: "linkedin")
Profile URL              ->    linkedin (already set)
```

## Prerequisites

- [x] cc-linkedin build fixed (ModuleNotFoundError resolved)
- [x] cc-vault contacts update works without email
- [x] cc-vault contacts memory works without email
- [x] cc-vault contacts show displays all fields
- [x] Unicode handling fixed for non-ASCII names
- [ ] cc-linkedin `enrich` command implemented
- [ ] cc-vault `contacts enrich` command implemented
- [ ] Orchestration script created
- [ ] Test run on 10 contacts
- [ ] Production run started

## Files to Create/Modify

| File | Action |
|------|--------|
| `tools/cc-linkedin/src/cli.py` | Add `enrich` command |
| `tools/cc-vault/src/cli.py` | Add `contacts enrich` command |
| `tools/cc-vault/src/db.py` | Add `enrich_contact()` helper |
| `scripts/enrich-contacts.py` | New orchestration script |
| `scripts/enrich-state.json` | Auto-created state file |
| `scripts/enrich-contacts.log` | Auto-created log file |

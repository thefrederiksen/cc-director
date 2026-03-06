# Creating Browser Navigation Skills

Step-by-step process for adding a new website to the cc-browser skill library.

## Phase 1 - Setup

1. Create connection: `cc-browser connections add {name} --url https://{domain}`
2. Open browser: `cc-browser connections open {name}`
3. Log in manually if needed

## Phase 2 - Reconnaissance

1. Visit each major section of the site
2. Note URL patterns (copy from address bar)
3. Identify auth indicators (what appears when logged in vs not)
4. Note any bot detection warnings or rate limiting
5. Check for cookie consent banners or modals that block interaction

## Phase 3 - Selector Discovery

1. For each page section: `cc-browser snapshot --interactive --connection {name}`
2. Test selectors: `cc-browser evaluate --connection {name} --fn "document.querySelector('{sel}')?.textContent"`
3. Stability preference order:
   - `data-testid` attributes (most stable)
   - `aria-label` attributes
   - Semantic HTML elements (nav, main, header)
   - Class names (least stable, change with deploys)
4. For data extraction, prefer JavaScript over CSS when structure is complex

## Phase 4 - Workflow Writing

1. Manually perform each action (search, post, message) in the browser
2. Record each step with cc-browser commands
3. Test each workflow end-to-end
4. Note required delays and retry patterns
5. Document what success/failure looks like for each workflow

## Phase 5 - Documentation

1. Copy `TEMPLATE.skill.md` to `{name}.skill.md`
2. Fill in all sections
3. Remove sections that don't apply (e.g., Timing if no bot detection)
4. Add site-specific gotchas

## Phase 6 - Registration

1. Update `manifest.json` -- add entry under `skills`:
   ```json
   "{name}": {
     "file": "{name}.skill.md",
     "site": "{domain.com}",
     "version": "YYYY.MM.DD"
   }
   ```

2. Create Claude Code skill wrapper at `.claude/skills/{name}/skill.md`:
   ```markdown
   ---
   name: {name}
   description: Interact with {Site Name} via browser. Triggers on "/{name}", "{name}", ...
   ---
   # {Site Name} Connection

   Load the navigation skill, then carry out the user's request using cc-browser commands.

   ## Setup
   Ensure browser is open: `cc-browser connections open {name}`

   ## Skill Instructions
   Read the full navigation skill before acting:
   $( cc-browser skills show --connection {name} )

   ## Rules
   - ALWAYS follow the timing/delay requirements from the skill
   - ALWAYS check authentication state first
   - Use `cc-browser snapshot --interactive` to discover current element refs
   - If a selector fails, try the skill's alternative selectors or use snapshot discovery
   - Use `cc-browser skills learn {name} "description"` to persist any new patterns you discover

   $ARGUMENTS
   ```

3. Build and deploy: run `tools/cc-browser/build.ps1`
4. Test: `cc-browser skills show --connection {name}`

## Skill Name Mapping

If a connection name differs from its skill name (e.g., `my-work-linkedin` should use the `linkedin` skill), set the `skillName` field on the connection:

```
cc-browser connections add my-work-linkedin --url https://linkedin.com --skill-name linkedin
```

This makes `resolveSkill()` look up the `linkedin` managed skill instead of `my-work-linkedin`.

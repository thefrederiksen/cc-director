---
name: cencon-generate
description: Analyze .NET source code and generate CenCon documentation (architecture_manifest.yaml, security_profile.yaml, INDEX.md). Triggers on "/cencon-generate".
---

# CenCon Generate Skill

Automatically analyze .NET source code (the ground truth) and generate CenCon documentation files.

## Triggers

- `/cencon-generate` - Full generation
- `/cencon-generate --diff` - Show changes since last generation
- `/cencon-generate --dry-run` - Preview without writing files

## Output Files

All files are created in `docs/cencon/`:
- `architecture_manifest.yaml` - C4 Level 1 & 2 model (machine-readable)
- `security_profile.yaml` - OWASP Desktop + custom security rules
- `INDEX.md` - Human-readable summary with data flow diagrams

## Workflow

### STEP 1: Discover Repository Context

1. Use Glob to find *.sln files in the repository root
2. Use Glob to find all *.csproj files
3. Read existing documentation for context:
   - README.md (if exists)
   - docs/CodingStyle.md (if exists)
   - CLAUDE.md (if exists)
4. Read existing CenCon files (if any) for comparison:
   - docs/cencon/architecture_manifest.yaml
   - docs/cencon/security_profile.yaml
   - docs/cencon/INDEX.md

### STEP 2: Discover Containers (Projects)

For each .csproj file found:

1. Read the .csproj file
2. Extract key properties:
   - TargetFramework (e.g., net10.0, net8.0)
   - OutputType (WinExe, Library, Exe)
   - UseWPF (true/false)
   - UseWindowsForms (true/false)
   - ImplicitUsings
3. Extract ProjectReferences to build dependency graph
4. Extract PackageReferences for external dependencies

Classify each project into container types:
- `wpf_ui` - OutputType=WinExe + UseWPF=true
- `core_services` - OutputType=Library, no UI
- `cli` - OutputType=Exe, no UI
- `tests` - Project name ends with .Tests or contains test packages

Build a dependency graph from ProjectReferences.

### STEP 3: Discover Components (Classes/Namespaces)

For each non-test container:

1. List all directories under the project folder (excluding bin, obj, Properties)
2. Each top-level directory represents a component group
3. For each directory, use Grep to find:
   - Public classes: `public (class|interface|enum|record|struct) \w+`
   - With attributes: `\[.*\]` before class declarations
4. Read the primary class file in each directory
5. Extract XML documentation summaries (`/// <summary>`)
6. Identify design patterns by looking for:
   - Strategy: Interface with multiple implementations (ISessionBackend)
   - Observer: Events and event handlers
   - State Machine: Enum + switch/state transitions
   - Producer-Consumer: BlockingCollection, ConcurrentQueue
   - Repository: Load/Save/Get methods

Record component information:
- name: Class/interface name
- description: From XML doc or inferred from code
- file: Primary file path
- files: List of related files if multiple

### STEP 4: Identify External Systems

Search for patterns indicating external system interactions:

1. HttpClient usage -> External API
2. Process.Start("git") or Process.StartInfo containing "git" -> Git VCS
3. Process.Start("claude") -> Claude Code CLI
4. Process.Start with other executables -> External tool
5. NamedPipeServerStream -> IPC (this is a boundary, not an actor)
6. [DllImport] attributes -> Native Windows APIs
7. HttpClient with OpenAI/Anthropic patterns -> AI APIs

For each external system found:
- id: snake_case identifier
- name: Human-readable name
- description: What it does
- type: external_system or person
- relationship: How the main system uses it

Developer (person) is always added as the primary actor.

### STEP 5: Extract Security Information

Search for security-relevant patterns in all .cs files:

1. **Credential patterns**:
   - `password\s*=`, `apikey`, `secret`, `token\s*=`
   - ConnectionStrings with sensitive data

2. **User input handling**:
   - Console.ReadLine, TextBox.Text
   - Command line args (args[])

3. **Process execution**:
   - Process.Start, ProcessStartInfo
   - Arguments construction

4. **File path construction**:
   - Path.Combine with user input
   - String concatenation for paths

5. **Network access**:
   - HttpClient, TcpClient, WebSocket
   - NamedPipeServerStream (IPC)

6. **Crypto and secrets**:
   - Encryption/decryption patterns
   - DPAPI usage

For each pattern found, determine:
- Is this a potential vulnerability?
- Is there proper validation/sanitization?
- What mitigation is in place?

Generate custom security rules based on findings.

### STEP 6: Generate YAML/MD Files

Generate three files with today's date as last_updated/last_verified.

**architecture_manifest.yaml**:
```yaml
schema_version: "1.0.0"
project:
  name: [from solution name]
  description: [from README.md or inferred]
  version: [from .csproj VersionPrefix or "1.0.0"]
  framework: [from TargetFramework]
  platform: [inferred from UseWPF, UseWindowsForms, etc.]

last_updated: [today's date YYYY-MM-DD]

context:
  system:
    name: [project name]
    description: [from README or inferred]
    technology: [from framework analysis]

  actors:
    - [list of actors from STEP 4]

containers:
  - [list of containers from STEP 2 with components from STEP 3]

data_flows:
  - [inferred from component interactions]

security_boundaries:
  - [from STEP 5 analysis]

patterns:
  - [from STEP 3 pattern detection]
```

**security_profile.yaml**:
```yaml
schema_version: "1.0.0"
project:
  name: [project name]
  version: [version]

last_verified: [today's date YYYY-MM-DD]

drift:
  threshold_days: 30
  action_on_drift: FAIL_REVIEW

owasp_desktop:
  - [OWASP Desktop checks based on STEP 5]

soc2_alignment:
  - [SOC 2 controls applicable]

custom_rules:
  - [project-specific grep patterns from STEP 5]

review_checklist:
  - [human-readable checklist items]
```

**INDEX.md**:
```markdown
# [Project Name] - CenCon Documentation Index

**Version:** [version]
**Last Updated:** [date]
**Schema:** CenCon Method v1.0

## Overview
[Project description]

## Architecture Diagrams
[Links to context.png and container.png with generation instructions]

## System Components
[Tables of components by container]

## Data Flows
[ASCII diagrams of key flows]

## Security Profile Summary
[Key security controls and compliance alignment]

## Related Documentation
[Links to other docs]

## Maintenance
[How to update this documentation]
```

### STEP 7: Present Results and Get Approval

Display to the user:

```
CenCon Generation Summary

Repository: [repo name]
Solution: [solution file]

Containers Discovered: [count]
[list each container with technology]

Components Discovered: [count]
[list count by container]

External Systems: [count]
[list each external system]

Security Rules Generated: [count]
[list rule names]

Files to Create/Update:
  docs/cencon/architecture_manifest.yaml
  docs/cencon/security_profile.yaml
  docs/cencon/INDEX.md
```

If --diff flag was provided:
- Show additions (lines starting with +)
- Show removals (lines starting with -)
- Show unchanged count

If --dry-run flag was provided:
- Display the generated content
- Say: "Dry run complete. No files were written."
- STOP here

Otherwise ask:
**Approve writing these files?** Reply "yes" to write.

### STEP 8: Write Files

Only after user approval:

1. Create docs/cencon/ directory if it doesn't exist
2. Write architecture_manifest.yaml
3. Write security_profile.yaml
4. Write INDEX.md

Report completion:
```
CenCon documentation generated successfully.

Files created:
  docs/cencon/architecture_manifest.yaml
  docs/cencon/security_profile.yaml
  docs/cencon/INDEX.md

To generate diagrams, run: cc-docgen generate
```

## Notes

- Source code is the ground truth - always analyze actual .cs/.csproj files
- Never hallucinate components - only include what you actually find
- Preserve existing structure when updating (don't remove valid sections)
- Use consistent ID formats (snake_case for IDs)
- Today's date format: YYYY-MM-DD

## Error Handling

If no .sln file found:
- Show error: "No solution file found in repository root"
- STOP

If no .csproj files found:
- Show error: "No project files found"
- STOP

If reading a file fails:
- Note the error and continue with other files
- Include a comment in output about the skipped file

---

**Skill Version:** 1.0
**Last Updated:** 2026-02-21
**Created for:** CenCon Method v1.0

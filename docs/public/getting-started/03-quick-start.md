# Quick Start

This guide walks you through your first CC Director session -- from converting a document to sending an email draft.

## Your First Document Conversion

The most common task is converting Markdown to professional PDFs. Create a simple markdown file:

```markdown
# Quarterly Report

## Summary

Revenue increased 15% quarter-over-quarter.

## Key Metrics

| Metric | Q3 | Q4 |
|--------|----|----|
| Revenue | $1.2M | $1.38M |
| Customers | 450 | 512 |
| NPS | 72 | 78 |
```

Save it as `report.md`, then convert it:

```bash
cc-markdown report.md -o report.pdf --theme boardroom
```

The `boardroom` theme gives you a corporate, executive-style document with serif fonts -- perfect for client-facing deliverables.

### Other output formats

```bash
# Word document
cc-markdown report.md -o report.docx

# HTML
cc-markdown report.md -o report.html

# PowerPoint presentation
cc-powerpoint slides.md -o deck.pptx --theme boardroom
```

## Creating Excel Reports

Convert data files to formatted Excel workbooks:

```bash
# From CSV
cc-excel from-csv sales.csv -o sales.xlsx --theme boardroom --summary all

# From Markdown tables
cc-excel from-markdown report.md -o report.xlsx --all-tables

# From JSON
cc-excel from-json api-data.json -o report.xlsx
```

## Reading Your Email

Check your inbox without leaving the terminal:

```bash
# Outlook
cc-outlook list --unread
cc-outlook read MESSAGE_ID

# Gmail
cc-gmail list --unread
cc-gmail read MESSAGE_ID
```

## Using Claude Code with CC Tools

The real power comes from combining Claude Code with CC Director's tools. Start Claude Code in any project:

```bash
claude
```

Then ask naturally:

- "Convert my notes.md to a PDF with the boardroom theme"
- "Read my latest unread emails"
- "Transcribe this video and summarize the key points"
- "Generate an Excel report from this CSV data"

Claude Code automatically discovers the `cc-*` tools and uses them to fulfill your requests.

## The Communication Manager

CC Director includes a communication approval workflow. When Claude drafts an email or social media post, it goes into a review queue -- not directly to the recipient.

The workflow:

1. Claude drafts content using the `/write` skill
2. Content is queued in the Communication Manager
3. You review and approve (or edit) in the desktop app
4. Only approved content gets sent

This ensures no AI-generated message leaves your accounts without your explicit approval.

## Desktop Automation

For tasks that require interacting with desktop applications:

```bash
# AI-powered: describe what you want
cc-computer "Open Notepad and type Hello World"

# Direct automation: when you know the exact steps
cc-click focus "Notepad"
cc-click type "Hello World"
```

## System Information

Check your hardware at a glance:

```bash
cc-hardware
cc-hardware gpu --json
```

## Next Steps

- [Tools Overview](../tools/overview.md) -- Full reference for all 25+ tools
- Browse the [GitHub repository](https://github.com/cc-director/cc-director) for source code and examples

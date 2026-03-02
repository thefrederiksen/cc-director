# Introduction to CC Director

CC Director is an open-source AI agent orchestration framework that brings together 25+ command-line tools for document conversion, media processing, email, browser automation, and AI workflows -- all designed to work with Claude Code.

## What is CC Director?

CC Director is two things:

1. **An orchestration engine** -- a WPF desktop application that manages AI agent sessions, dispatches tasks, and coordinates communication across tools.
2. **A toolkit of CLI tools** -- standalone utilities (the `cc-*` tools) that handle everything from converting Markdown to PDF, to automating LinkedIn interactions, to transcribing video.

Together, they form a personal AI operating system: you talk to Claude Code, and Claude Code uses CC Director's tools to get real work done on your machine.

## Who is this for?

- **Knowledge workers** who want AI to handle repetitive document, email, and media tasks
- **Developers** who want to extend their Claude Code setup with powerful automation
- **Consultants** who need to produce professional deliverables (PDFs, presentations, Excel reports) from simple markdown

## Key Capabilities

### Document Conversion
Convert Markdown to PDF, Word, HTML, PowerPoint, and Excel -- with professional themes like "boardroom" for executive reports.

### Email and Communication
Read and manage Outlook and Gmail from the command line. Draft communications that go through a human-approval workflow before sending.

### Browser Automation
Persistent browser sessions with workspace management. Specialized tools for LinkedIn and Reddit with human-like delays to avoid detection.

### Desktop Automation
AI-powered desktop automation using a 3-tier visual detection system (UI Automation + OCR + Pixel Analysis) with 98.9% element clickability accuracy.

### Media Processing
Video/audio transcription with timestamps, image generation and analysis, text-to-speech, and photo organization with duplicate detection.

### Web Intelligence
Website auditing across SEO, security, structured data, and AI readiness. Branding recommendations with week-by-week action plans.

## Architecture Overview

```
Claude Code (LLM)
    |
    +-- CC Director Engine (orchestration)
    |       |-- Session management
    |       |-- Task dispatch
    |       +-- Communication Manager (approval queue)
    |
    +-- CC Tools (25+ CLI utilities)
            |-- cc-markdown, cc-excel, cc-powerpoint (documents)
            |-- cc-outlook, cc-gmail (email)
            |-- cc-browser, cc-linkedin, cc-reddit (web)
            |-- cc-computer, cc-trisight, cc-click (desktop)
            |-- cc-transcribe, cc-image, cc-voice (media)
            +-- cc-vault (personal data)
```

## Open Source

CC Director is fully open source. You can browse the code, submit issues, and contribute at [github.com/cc-director/cc-director](https://github.com/cc-director/cc-director).

## Next Steps

- [Installation](installation.md) -- Get CC Director running on your machine
- [Quick Start](quick-start.md) -- Walk through your first session
- [Tools Overview](../tools/overview.md) -- See all available tools

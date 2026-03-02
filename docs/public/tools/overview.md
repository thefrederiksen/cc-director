# Tools Overview

CC Director includes 28 command-line tools for document conversion, media processing, email, browser automation, desktop automation, and AI workflows. All tools are installed to `%LOCALAPPDATA%\cc-director\bin\` and are available on your PATH.

## Quick Reference

### Documents

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-markdown | Markdown to PDF/Word/HTML with themes | Chrome/Chromium |
| cc-excel | CSV/JSON/Markdown to formatted Excel workbooks | None (not yet built) |
| cc-powerpoint | Markdown to PowerPoint presentations | None |

### Email

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-gmail | Gmail CLI: read, send, search, labels, calendar, contacts | Google OAuth |
| cc-outlook | Outlook CLI: email, calendar, attachments, folders | Azure OAuth |

### Web and Social

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-browser | Persistent browser automation with workspaces | Node.js, Playwright |
| cc-linkedin | LinkedIn automation with human-like delays | Playwright, cc-browser |
| cc-reddit | Reddit automation with human-like delays | Playwright, cc-browser |
| cc-spotify | Spotify playback control via browser | cc-browser |
| cc-crawl4ai | AI-ready web crawler to clean markdown | Playwright browsers |
| cc-websiteaudit | Website SEO/security/AI readiness audit | Node.js, Chrome (not yet built) |
| cc-brandingrecommendations | Branding action plans from audit data | Node.js |

### Desktop Automation

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-click | Windows UI automation: click, type, screenshot | Windows, .NET |
| cc-trisight | 3-tier UI element detection (UIA + OCR + pixel) | Windows, .NET |
| cc-computer | AI desktop agent with screenshot-in-the-loop | Windows, .NET, OPENAI_API_KEY |

### Media

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-image | Image generation, analysis, OCR | OPENAI_API_KEY |
| cc-voice | Text-to-speech (OpenAI TTS) | OPENAI_API_KEY |
| cc-whisper | Audio transcription and translation | OPENAI_API_KEY |
| cc-video | Video info, audio extraction, screenshots, frames | FFmpeg |
| cc-transcribe | Video/audio transcription with screenshots | FFmpeg, OPENAI_API_KEY |
| cc-photos | Photo scanning, duplicates, AI descriptions | OPENAI_API_KEY |
| cc-youtube-info | YouTube transcript/metadata extraction | None |

### Data and Utilities

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-vault | Personal vault: contacts, tasks, goals, docs, RAG | None |
| cc-hardware | System hardware info (RAM, CPU, GPU, disk) | None |
| cc-comm-queue | Communication Manager approval queue | None |
| cc-docgen | C4 architecture diagrams from YAML | Graphviz (not yet built) |
| cc-director-setup | Windows installer for CC Director | None |
| cc-personresearch | Person research aggregation | (not yet built) |

---

## Documents

### cc-markdown

Convert Markdown to PDF, Word, or HTML with built-in themes.

```bash
cc-markdown report.md -o report.pdf
cc-markdown report.md -o report.pdf --theme boardroom
cc-markdown report.md -o report.docx
cc-markdown --themes
```

**Themes:** boardroom, terminal, paper (default), spark, thesis, obsidian, blueprint

**Options:** `-o` output file, `--theme` theme name, `--css` custom CSS, `--page-size` a4/letter, `--margin` page margin

### cc-excel

Convert CSV, JSON, and Markdown tables to formatted Excel workbooks with themes, charts, and formulas.

**Status:** Source exists but not yet built.

```bash
cc-excel from-csv sales.csv -o sales.xlsx --theme boardroom
cc-excel from-json data.json -o report.xlsx
cc-excel from-markdown report.md -o report.xlsx --all-tables
cc-excel from-csv sales.csv -o chart.xlsx --chart bar --chart-x 0 --chart-y 1
cc-excel from-csv sales.csv -o report.xlsx --summary all --highlight scale
cc-excel from-spec workbook.json -o output.xlsx
```

**Subcommands:** `from-csv`, `from-json`, `from-markdown`, `from-spec`

### cc-powerpoint

Convert Markdown to PowerPoint presentations.

```bash
cc-powerpoint slides.md -o deck.pptx --theme boardroom
```

Use `---` to separate slides. First `# Title` becomes the title slide.

---

## Email

### cc-outlook

Outlook CLI with email and calendar support via Microsoft Graph API.

```bash
cc-outlook list --unread
cc-outlook read <message_id>
cc-outlook search "project update"
cc-outlook reply <message_id>
cc-outlook forward <message_id>
cc-outlook calendar events -d 14
cc-outlook calendar today
cc-outlook calendar search "standup"
cc-outlook folders
cc-outlook attachments <message_id>
```

### cc-gmail

Gmail CLI with multi-account support.

```bash
cc-gmail list --unread
cc-gmail read <message_id>
cc-gmail search "from:someone@example.com"
cc-gmail reply <message_id>
cc-gmail labels
cc-gmail stats
cc-gmail calendar
cc-gmail contacts
```

---

## Web and Social

### cc-browser

Persistent browser automation with workspace management.

```bash
cc-browser daemon
cc-browser start --workspace myworkspace
cc-browser navigate --url "https://example.com"
cc-browser snapshot --interactive
cc-browser click --ref e3
cc-browser type --ref e4 --text "hello"
cc-browser screenshot --save page.png
cc-browser stop
```

### cc-linkedin

LinkedIn automation with built-in human-like delays.

```bash
cc-linkedin status
cc-linkedin feed --limit 10
cc-linkedin messages --unread
cc-linkedin search "query" --type people
cc-linkedin create "Post content"
cc-linkedin profile <username>
```

**Important:** Always use cc-linkedin for LinkedIn operations. Never use cc-browser directly with LinkedIn.

### cc-reddit

Reddit automation with human-like delays and random jitter.

```bash
cc-reddit status
cc-reddit feed
cc-reddit post <url>
cc-reddit comment <url> "Comment text"
```

**Important:** Always use cc-reddit for Reddit operations. Never use cc-browser directly with Reddit.

### cc-spotify

Spotify playback control via browser automation.

```bash
cc-spotify config --workspace edge-personal
cc-spotify status
cc-spotify now
cc-spotify play / pause / next / prev
cc-spotify shuffle --on
cc-spotify search "Miles Davis"
cc-spotify playlists
cc-spotify volume 75
cc-spotify recommend --mood "chill"
```

**Setup:** Requires a cc-browser workspace with Spotify Web Player open and logged in.

### cc-crawl4ai

AI-ready web crawler that converts pages to clean markdown.

```bash
cc-crawl4ai crawl "https://example.com" -o page.md
cc-crawl4ai crawl <url> --fit --stealth
cc-crawl4ai batch urls.txt -o ./output/
```

### cc-websiteaudit

Comprehensive website auditing across SEO, security, structured data, and AI readiness.

**Status:** Source exists but not yet built.

```bash
cc-websiteaudit example.com -o report.pdf
cc-websiteaudit example.com --format json -o audit.json
cc-websiteaudit example.com --modules technical-seo,security
```

**Modules:** technical-seo, on-page-seo, security, structured-data, ai-readiness

### cc-brandingrecommendations

Produces prioritized, week-by-week branding action plans from website audit data.

```bash
cc-brandingrecommendations --audit audit.json -o plan.md
cc-brandingrecommendations --audit audit.json --budget high --industry saas
```

---

## Desktop Automation

Three tools work together for AI-powered desktop automation:

```
cc-computer (AI Agent - the "brain")
    +-- uses TrisightCore (3-tier detection library)
    +-- calls cc-click for actions

cc-trisight (Detection CLI - the "eyes")
    +-- UI Automation + OCR + Pixel Analysis

cc-click (Automation CLI - the "hands")
    +-- Click, type, screenshot, read text, window management
```

### cc-computer

AI desktop automation agent with screenshot-in-the-loop verification.

```bash
cc-computer "Open Notepad and type Hello World"
cc-computer    # Interactive REPL mode
```

### cc-trisight

Three-tier UI element detection for Windows.

```bash
trisight detect --window "Notepad" --annotate --output annotated.png
```

### cc-click

Low-level Windows UI automation.

```bash
cc-click click <element>
cc-click type <text>
cc-click screenshot
cc-click read-text <element>
cc-click list-windows
cc-click list-elements <window>
```

---

## Media

### cc-transcribe

Transcribe video/audio with timestamps and extract screenshots at content changes.

```bash
cc-transcribe video.mp4
cc-transcribe video.mp4 -o ./output/ --no-screenshots
```

### cc-image

Image generation, analysis, and OCR using OpenAI.

**Status:** BROKEN - needs rebuild.

```bash
cc-image generate "A sunset over mountains" -o sunset.png
cc-image describe image.png
cc-image ocr screenshot.png
```

### cc-voice

Text-to-speech using OpenAI TTS.

```bash
cc-voice "Hello, world!" -o hello.mp3 --voice nova
```

**Voices:** alloy, echo, fable, nova, onyx (default), shimmer

### cc-whisper

Audio transcription and translation using OpenAI Whisper.

```bash
cc-whisper transcribe audio.mp3 -o transcript.txt
cc-whisper translate foreign-audio.mp3
```

### cc-video

Video utilities powered by FFmpeg.

```bash
cc-video info video.mp4
cc-video audio video.mp4 -o audio.mp3
cc-video screenshots video.mp4
cc-video frame video.mp4 --timestamp 01:30
```

### cc-photos

Photo organization with duplicate detection, screenshot identification, and AI descriptions.

```bash
cc-photos source add "D:\Photos" --category private --label "Family"
cc-photos scan
cc-photos discover
cc-photos dupes --cleanup
cc-photos analyze --limit 50
cc-photos search "beach vacation"
cc-photos exclude
```

### cc-youtube-info

Extract transcripts, metadata, and chapters from YouTube videos.

```bash
cc-youtube-info transcript <url> -o transcript.txt
cc-youtube-info info <url> --json
cc-youtube-info chapters <url>
```

---

## Data and Utilities

### cc-hardware

Query system hardware information.

```bash
cc-hardware          # All hardware summary
cc-hardware gpu      # GPU info
cc-hardware --json   # JSON output
```

### cc-vault

Personal data vault with contacts, documents, tasks, goals, ideas, and RAG-powered search.

```bash
cc-vault search "query"
cc-vault ask "question"
cc-vault contacts list --account personal
cc-vault contacts show <id>
cc-vault contacts search "name"
cc-vault docs import file.pdf
cc-vault lists list
cc-vault backup
cc-vault stats
```

### cc-comm-queue

CLI for adding content to the Communication Manager approval queue.

```bash
cc-comm-queue add linkedin post "Content..." --persona mindzie
cc-comm-queue list --status pending
cc-comm-queue status
```

### cc-docgen

Generate C4 architecture diagrams from YAML manifest files.

**Status:** Source exists but not yet built.

```bash
cc-docgen generate --manifest ./docs/architecture.yaml
```

### cc-director-setup

Windows installer for the entire CC Director tools suite. Downloads from GitHub releases, no admin required.

```bash
cc-director-setup
```

---

## Environment Variables

```bash
# Required for AI-powered tools
set OPENAI_API_KEY=your-key-here
```

# Tools Overview

CC Director includes 25+ command-line tools for document conversion, media processing, email, browser automation, and AI workflows. All tools are installed to `%LOCALAPPDATA%\cc-tools\bin\` and are available on your PATH.

## Quick Reference

| Tool | Description | Requirements |
|------|-------------|--------------|
| cc-brandingrecommendations | Branding recommendations from website audit | Node.js |
| cc-browser | Persistent browser automation with profiles | Node.js, Playwright |
| cc-click | Windows UI automation (click, type, inspect) | Windows, .NET |
| cc-comm-queue | Communication Manager queue CLI | None |
| cc-computer | AI desktop automation agent | Windows, .NET, OpenAI API key |
| cc-crawl4ai | AI-ready web crawler to clean markdown | Playwright browsers |
| cc-docgen | C4 architecture diagram generator | Graphviz |
| cc-excel | CSV/JSON/Markdown to formatted Excel | None |
| cc-gmail | Gmail CLI: read, send, search | Google OAuth |
| cc-hardware | System hardware info (RAM, CPU, GPU, disk) | None |
| cc-image | Image generation/analysis/OCR | OpenAI API key |
| cc-linkedin | LinkedIn automation with human-like delays | Playwright browsers |
| cc-markdown | Markdown to PDF/Word/HTML | Chrome/Chromium |
| cc-outlook | Outlook CLI: email and calendar | Azure OAuth |
| cc-photos | Photo organization: duplicates, AI | OpenAI API key |
| cc-powerpoint | Markdown to PowerPoint presentations | None |
| cc-reddit | Reddit automation with human-like delays | Playwright browsers |
| cc-setup | Windows installer for cc-tools suite | None |
| cc-transcribe | Video/audio transcription with screenshots | FFmpeg, OpenAI API key |
| cc-trisight | Windows screen detection and automation | Windows, .NET |
| cc-vault | Secure credential and data storage | None |
| cc-video | Video utilities | FFmpeg |
| cc-voice | Text-to-speech | OpenAI API key |
| cc-websiteaudit | Website SEO/security/AI audit | Node.js, Chrome |
| cc-whisper | Audio transcription | OpenAI API key |
| cc-youtube-info | YouTube transcript/metadata extraction | None |

---

## Document Conversion Tools

### cc-markdown

Convert Markdown to PDF, Word, or HTML with built-in themes.

```bash
cc-markdown report.md -o report.pdf
cc-markdown report.md -o report.pdf --theme boardroom
cc-markdown report.md -o report.docx
cc-markdown --themes
```

**Themes:** boardroom, terminal, paper, spark, thesis, obsidian, blueprint

**Options:** `-o` output file, `--theme` theme name, `--css` custom CSS, `--page-size` a4/letter, `--margin` page margin

### cc-excel

Convert CSV, JSON, and Markdown tables to formatted Excel workbooks with themes, charts, and formulas.

```bash
cc-excel from-csv sales.csv -o sales.xlsx --theme boardroom
cc-excel from-json data.json -o report.xlsx
cc-excel from-markdown report.md -o report.xlsx --all-tables
cc-excel from-csv sales.csv -o chart.xlsx --chart bar --chart-x 0 --chart-y 1
cc-excel from-csv sales.csv -o report.xlsx --summary all --highlight scale
cc-excel from-spec workbook.json -o output.xlsx
```

**Features:** Auto column types, autofilter, freeze panes, charts (bar/line/pie/column), summary rows, conditional formatting, multi-sheet workbooks with formulas

### cc-powerpoint

Convert Markdown to PowerPoint presentations.

```bash
cc-powerpoint slides.md -o deck.pptx --theme boardroom
```

Use `---` to separate slides. First `# Title` becomes the title slide.

---

## Email Tools

### cc-outlook

Outlook CLI with email and calendar support via Microsoft Graph API.

```bash
cc-outlook list --unread
cc-outlook read MESSAGE_ID
cc-outlook search "project update"
cc-outlook calendar events -d 14
cc-outlook calendar today
cc-outlook calendar search "standup"
```

### cc-gmail

Gmail CLI with multi-account support.

```bash
cc-gmail list --unread
cc-gmail read MESSAGE_ID
cc-gmail search "from:someone@example.com"
```

---

## Browser and Web Tools

### cc-browser

Persistent browser automation with workspace management.

```bash
cc-browser start --workspace myworkspace
cc-browser navigate "https://example.com"
cc-browser screenshot -o page.png
cc-browser close
```

### cc-linkedin

LinkedIn automation with built-in human-like delays.

```bash
cc-linkedin status
cc-linkedin feed --limit 10
cc-linkedin messages --unread
cc-linkedin search "query" --type people
```

**Important:** Always use cc-linkedin for LinkedIn operations. Never use cc-browser directly with LinkedIn.

### cc-reddit

Reddit automation with human-like delays and random jitter.

```bash
cc-reddit status
cc-reddit feed
cc-reddit post URL
cc-reddit comment URL "Comment text"
```

**Important:** Always use cc-reddit for Reddit operations. Never use cc-browser directly with Reddit.

### cc-crawl4ai

AI-ready web crawler that converts pages to clean markdown.

```bash
cc-crawl4ai crawl "https://example.com" -o page.md
cc-crawl4ai crawl URL --fit --stealth
cc-crawl4ai batch urls.txt -o ./output/
```

### cc-websiteaudit

Comprehensive website auditing across SEO, security, structured data, and AI readiness.

```bash
cc-websiteaudit example.com -o report.pdf
cc-websiteaudit example.com --format json -o audit.json
cc-websiteaudit example.com --modules technical-seo,security
```

**Grades:** A+ (97+) through F (<60). Modules: technical-seo, on-page-seo, security, structured-data, ai-readiness.

### cc-brandingrecommendations

Produces prioritized, week-by-week branding action plans from website audit data.

```bash
cc-brandingrecommendations --audit audit.json -o plan.md
cc-brandingrecommendations --audit audit.json --budget high --industry saas
```

---

## Desktop Automation Stack

Three tools work together for AI-powered desktop automation:

```
cc-computer (AI Agent - the "brain")
    +-- uses TrisightCore (3-tier detection library)
    +-- calls cc-click for actions

cc-trisight (Detection CLI - the "eyes")
    +-- UI Automation + OCR + Pixel Analysis

cc-click (Automation CLI - the "hands")
    +-- Click, type, inspect, window management
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
cc-click click 100 200
cc-click type "Hello World"
cc-click windows
cc-click focus "Notepad"
```

---

## Media Tools

### cc-transcribe

Transcribe video/audio with timestamps and extract screenshots at content changes.

```bash
cc-transcribe video.mp4
cc-transcribe video.mp4 -o ./output/ --no-screenshots
```

### cc-image

Image generation, analysis, and OCR using OpenAI.

```bash
cc-image generate "A sunset over mountains" -o sunset.png
cc-image describe image.png
cc-image ocr screenshot.png
```

### cc-voice

Text-to-speech using OpenAI TTS.

```bash
cc-voice speak "Hello, world!" -o hello.mp3 --voice nova
```

**Voices:** alloy, echo, fable, nova, onyx, shimmer

### cc-whisper

Audio transcription using OpenAI Whisper.

```bash
cc-whisper transcribe audio.mp3 -o transcript.txt
```

### cc-video

Video utilities powered by FFmpeg.

```bash
cc-video extract-audio video.mp4 -o audio.mp3
cc-video info video.mp4
```

### cc-photos

Photo organization with duplicate detection, screenshot identification, and AI descriptions.

```bash
cc-photos source add "D:\Photos" --category private --label "Family"
cc-photos scan
cc-photos dupes --cleanup
cc-photos analyze --limit 50
cc-photos search "beach vacation"
```

### cc-youtube-info

Extract transcripts, metadata, and chapters from YouTube videos.

```bash
cc-youtube-info transcript URL -o transcript.txt
cc-youtube-info info URL --json
cc-youtube-info chapters URL
```

---

## Utility Tools

### cc-hardware

Query system hardware information.

```bash
cc-hardware          # All hardware summary
cc-hardware gpu      # GPU info
cc-hardware --json   # JSON output
```

### cc-vault

Personal data vault with contacts, documents, tasks, and RAG-powered search.

```bash
cc-vault search "query"
cc-vault ask "question"
cc-vault contacts list
cc-vault docs import file.pdf
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

```bash
cc-docgen generate --manifest ./docs/architecture.yaml
```

### cc-setup

Windows installer for the entire cc-tools suite. Downloads from GitHub releases, no admin required.

```bash
cc-setup
```

---

## Environment Variables

```bash
# Required for AI-powered tools
set OPENAI_API_KEY=your-key-here
```

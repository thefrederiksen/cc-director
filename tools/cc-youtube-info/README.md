# cc-youtube-info

**Extract transcripts, metadata, and information from YouTube videos.**

Get everything you need to know about a YouTube video without downloading it.

## What It Does

- Download transcripts (auto-generated or manual captions)
- Get video metadata (title, duration, channel, description)
- View engagement stats (views, likes, comments)
- List chapters with timestamps
- List available subtitle languages
- Export in multiple formats (text, JSON, SRT, VTT)

## What It Does NOT Do

- Upload videos to YouTube
- Download video/audio files (use yt-dlp for that)
- Modify YouTube content
- Access private videos

## Installation

```bash
cd src/cc-youtube-info
pip install -e .
```

## Commands

### Get Video Information

```bash
# Display video metadata with stats
cc-youtube-info info "https://www.youtube.com/watch?v=VIDEO_ID"

# Output as JSON (includes description, chapters, etc.)
cc-youtube-info info "https://youtu.be/VIDEO_ID" --json
```

### Download Transcript

```bash
# Print transcript to stdout
cc-youtube-info transcript "https://www.youtube.com/watch?v=VIDEO_ID"

# Save to file
cc-youtube-info transcript "URL" -o transcript.txt

# Export as SRT subtitles
cc-youtube-info transcript "URL" --format srt -o captions.srt

# Export as VTT subtitles
cc-youtube-info transcript "URL" --format vtt -o captions.vtt

# Format as paragraphs (removes timestamps)
cc-youtube-info transcript "URL" -p -o transcript.txt

# Output as JSON with metadata
cc-youtube-info transcript "URL" --json > output.json

# Specify language (default: en)
cc-youtube-info transcript "URL" -l es -o spanish.txt

# Force auto-generated captions only
cc-youtube-info transcript "URL" --auto-only
```

### List Available Languages

```bash
# See what languages are available for a video
cc-youtube-info languages "https://www.youtube.com/watch?v=VIDEO_ID"

# Output as JSON
cc-youtube-info languages "URL" --json
```

### List Chapters

```bash
# Show video chapters with timestamps
cc-youtube-info chapters "https://www.youtube.com/watch?v=VIDEO_ID"

# Output as JSON
cc-youtube-info chapters "URL" --json
```

## Output Formats

| Format | Description | Use Case |
|--------|-------------|----------|
| `txt` | Plain text with timestamps | Reading, searching |
| `srt` | SubRip subtitle format | Video editors, players |
| `vtt` | WebVTT subtitle format | Web players, browsers |
| `json` | Structured JSON | Programmatic processing |

## LLM Use Cases

This tool is designed for use with LLMs (Large Language Models):

- **"What is this video about?"** - Use `info --json` to get title, description, and stats
- **"Summarize this YouTube video"** - Use `transcript` to get the full text
- **"What languages are available?"** - Use `languages` to list options
- **"Skip to where they talk about X"** - Use `chapters` to find relevant sections
- **"Convert this video to subtitles"** - Use `transcript --format srt`

## Requirements

- Python 3.11+
- yt-dlp (installed automatically)
- youtube-transcript-api (installed automatically)

## Supported URL Formats

- `https://www.youtube.com/watch?v=VIDEO_ID`
- `https://youtube.com/watch?v=VIDEO_ID`
- `https://youtu.be/VIDEO_ID`
- `http://` variants also supported

## Error Handling

The tool provides clear error messages:

- **Invalid URL** - URL is not a recognized YouTube format
- **Video Not Found** - Video is private, deleted, or unavailable
- **No Subtitles** - Video has no captions available
- **Transcripts Disabled** - Creator has disabled captions for this video

## License

MIT

# cc-transcribe

Video and audio transcription with timestamps and automatic screenshot extraction.

Part of the [CC Tools](../../README.md) suite.

## Features

- **Transcription with Timestamps:** Word-level and segment-level timing
- **Automatic Screenshots:** Extracts frames when content changes
- **Multiple Formats:** Supports MP4, MKV, AVI, MOV, and more
- **Language Support:** Auto-detect or specify language

## Installation

Download from the releases page or install from source:

```bash
pip install -e .
```

## Requirements

- **FFmpeg:** Must be installed and in PATH
- **OpenAI API Key:** Set `OPENAI_API_KEY` environment variable

## Usage

```bash
# Basic transcription
cc-transcribe video.mp4

# Specify output directory
cc-transcribe video.mp4 -o ./output/

# Without screenshots
cc-transcribe video.mp4 --no-screenshots

# Adjust screenshot sensitivity
cc-transcribe video.mp4 --threshold 0.85 --interval 2.0

# Force language
cc-transcribe video.mp4 --language en

# Show video info only
cc-transcribe video.mp4 --info
```

## Output

```
output_directory/
    transcript.txt      # Timestamped transcript
    transcript.json     # Detailed segments with timing
    screenshots/        # Extracted frames
        screenshot_00-00-00.png
        screenshot_00-01-23.png
        ...
```

## License

MIT License - see [LICENSE](../../LICENSE)

# cc-whisper

Audio transcription using OpenAI Whisper.

Part of the [CC Tools](../../README.md) suite.

## Features

- **Accurate Transcription:** Powered by OpenAI Whisper
- **Multi-language:** Auto-detect or specify language
- **Timestamps:** Word and segment-level timing
- **Multiple Formats:** MP3, WAV, M4A, FLAC, OGG, etc.

## Installation

Download from the releases page or install from source:

```bash
pip install -e .
```

## Requirements

Set `OPENAI_API_KEY` environment variable.

## Usage

```bash
# Basic transcription (prints to console)
cc-whisper audio.mp3

# Save to file
cc-whisper audio.mp3 -o transcript.txt

# With timestamps
cc-whisper audio.mp3 -o transcript.txt --timestamps

# Specify language
cc-whisper audio.mp3 --language es

# JSON output
cc-whisper audio.mp3 --json
cc-whisper audio.mp3 -o transcript.json --json --timestamps
```

## Options

| Option | Description | Default |
|--------|-------------|---------|
| `-o, --output` | Output file path | Console |
| `-l, --language` | Language code | Auto-detect |
| `-t, --timestamps` | Include timestamps | False |
| `--json` | JSON output format | False |

## Supported Languages

Whisper supports 50+ languages including:
English (en), Spanish (es), French (fr), German (de), Italian (it), Portuguese (pt), Dutch (nl), Russian (ru), Chinese (zh), Japanese (ja), Korean (ko), and many more.

## License

MIT License - see [LICENSE](../../LICENSE)

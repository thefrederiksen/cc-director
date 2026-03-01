# cc-video

Video utilities: info, extract audio, screenshots.

Part of the [CC Tools](../../README.md) suite.

## Features

- **Video Info:** Duration, resolution, format, size
- **Extract Audio:** MP3, WAV, AAC, FLAC, OGG
- **Screenshots:** Content-aware extraction or specific timestamp

## Installation

Download from the releases page or install from source:

```bash
pip install -e .
```

## Requirements

**FFmpeg** must be installed and in PATH.

## Usage

```bash
# Video information
cc-video info video.mp4

# Extract audio
cc-video audio video.mp4 -o audio.mp3
cc-video audio video.mp4 -o audio.wav --format wav
cc-video audio video.mp4 --bitrate 320k

# Extract screenshots at content changes
cc-video screenshots video.mp4 -o ./frames/
cc-video screenshots video.mp4 -o ./frames/ --threshold 0.85
cc-video screenshots video.mp4 -o ./frames/ --max 20

# Extract single frame
cc-video frame video.mp4 --time 30.5 -o frame.png
```

## Commands

| Command | Description |
|---------|-------------|
| `info` | Show video metadata |
| `audio` | Extract audio track |
| `screenshots` | Extract frames at content changes |
| `frame` | Extract single frame at timestamp |

## Options

### audio
| Option | Description | Default |
|--------|-------------|---------|
| `-o, --output` | Output file | Auto |
| `-f, --format` | mp3, wav, aac, flac, ogg | mp3 |
| `-b, --bitrate` | Audio bitrate | 192k |

### screenshots
| Option | Description | Default |
|--------|-------------|---------|
| `-o, --output` | Output directory | Required |
| `-t, --threshold` | Sensitivity (0-1) | 0.92 |
| `-i, --interval` | Min seconds between | 1.0 |
| `-n, --max` | Max screenshots | None |

## License

MIT License - see [LICENSE](../../LICENSE)

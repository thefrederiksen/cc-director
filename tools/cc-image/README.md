# cc-image

Unified image toolkit: generate, analyze, OCR, resize, and convert.

Part of the [CC Tools](../../README.md) suite.

## Features

- **Resize:** High-quality image resizing with aspect ratio preservation
- **Convert:** Convert between formats (PNG, JPEG, WebP, etc.)
- **Describe:** AI-powered image analysis
- **OCR:** Extract text from images
- **Generate:** Create images with DALL-E

## Installation

Download from the releases page or install from source:

```bash
pip install -e .
```

## Requirements

- **OpenAI API Key:** Required for describe, ocr, and generate commands
  Set `OPENAI_API_KEY` environment variable

## Usage

```bash
# Get image info
cc-image info photo.jpg

# Resize image
cc-image resize photo.jpg -o thumb.jpg --width 800
cc-image resize photo.jpg -o small.jpg --height 600

# Convert format
cc-image convert photo.png -o photo.webp
cc-image convert photo.png -o photo.jpg --quality 85

# AI describe image
cc-image describe photo.jpg

# OCR - extract text
cc-image ocr screenshot.png

# Generate with DALL-E
cc-image generate "A sunset over mountains" -o sunset.png
cc-image generate "A cat wearing a hat" -o cat.png --size 1024x1792 --quality hd
```

## Commands

| Command | Description | Requires API |
|---------|-------------|--------------|
| `info` | Show image metadata | No |
| `resize` | Resize image | No |
| `convert` | Convert format | No |
| `describe` | AI image analysis | Yes |
| `ocr` | Extract text | Yes |
| `generate` | Create with DALL-E | Yes |

## License

MIT License - see [LICENSE](../../LICENSE)

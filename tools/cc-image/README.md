# cc-image

Unified image toolkit: analyze, OCR, catalog, resize, and convert.

Part of the [CC Tools](../../README.md) suite.

## Features

- **Describe:** AI-powered image analysis (single image or a whole folder)
- **OCR:** Extract text from images
- **Batch catalog:** Walk a folder and describe every image to JSON/CSV (resume supported)
- **Resize:** High-quality image resizing with aspect ratio preservation
- **Convert:** Convert between formats (PNG, JPEG, WebP, etc.)
- **Generate:** Text-to-image (DEFERRED - see below)

## Provider: the DevThrottle API

All AI commands (`describe`, `ocr`) run through the **DevThrottle API** - our own inference
gateway - using the live vision model `google/gemma-3-27b-it` via the OpenAI-compatible chat
completions endpoint. **No OpenAI key is required.** Images are downscaled before sending
(long edge ~896px) so bulk work is cheap and fast.

## Configuration

| Environment variable | Purpose | Default |
|----------------------|---------|---------|
| `DEVTHROTTLE_API_KEY` | Your DevThrottle API key (`dt_...`). **Required** for `describe`/`ocr`. | (none) |
| `DEVTHROTTLE_API_BASE` | OpenAI-compatible `/v1` base URL (point at local/staging). | `https://devthrottle.com/api/v1` |
| `DEVTHROTTLE_VISION_MODEL` | Vision model id. | `google/gemma-3-27b-it` |

```bash
# Windows (PowerShell)
$env:DEVTHROTTLE_API_KEY = "dt_live_..."
```

The local commands (`info`, `resize`, `convert`) need no API key.

## Installation

```bash
pip install -e .
```

## Usage

```bash
# Get image info (local, no API)
cc-image info photo.jpg

# Resize / convert (local, no API)
cc-image resize photo.jpg -o thumb.jpg --width 800
cc-image convert photo.png -o photo.webp

# AI describe a single image
cc-image describe photo.jpg

# OCR - extract text
cc-image ocr screenshot.png

# Batch: catalog an entire folder to JSON + CSV (resume supported)
cc-image describe C:\Photos --recursive
cc-image describe C:\Photos --recursive -o C:\Photos\catalog --format both --workers 6
```

### Batch / folder mode

`cc-image describe <folder> --recursive` walks the folder for common image types
(`.jpg .jpeg .png .gif .bmp .webp .tif .tiff`), describes each image through the DevThrottle
vision model, and writes the results next to `--output` (default `<folder>/cc-image-catalog`):

- `<output>.json` - `path`, `description` (and `error` if one image failed)
- `<output>.csv` - the same columns, spreadsheet-friendly

It runs several images at once (`--workers`), backs off and retries on rate-limit/transient
errors, and prints per-image progress. Runs are **resumable**: results are flushed after every
image, so re-running the same command skips images that already succeeded and retries only the
failed ones. This is what lets cc-image catalog a full drive of images cheaply.

### `generate` (deferred)

`cc-image generate` is wired to the DevThrottle text-to-image endpoint
(`POST /v1/images/generations`), but that endpoint is **not built yet**. Until it ships the
command reports a clear "not available yet" message and exits. It does **not** fall back to
OpenAI / DALL-E.

## Commands

| Command | Description | Requires API |
|---------|-------------|--------------|
| `info` | Show image metadata | No |
| `resize` | Resize image | No |
| `convert` | Convert format | No |
| `describe` | AI image analysis (single or `--recursive` folder) | Yes (`DEVTHROTTLE_API_KEY`) |
| `ocr` | Extract text | Yes (`DEVTHROTTLE_API_KEY`) |
| `generate` | Text-to-image | Deferred (endpoint not built) |

## Engines

`describe`/`ocr` default to `--engine devthrottle`. The `openai` and `claude_code` engines
remain available via the shared provider abstraction for callers who explicitly pass
`--engine`, but the DevThrottle path is the default and needs no OpenAI key.

## License

MIT License - see [LICENSE](../../LICENSE)

# cc-trisight

Three-tier UI element detection engine for Windows. Combines UI Automation (UIA), Optical Character Recognition (OCR), and pixel-based visual analysis into a unified element map with annotated screenshots.

## Commands

### detect - Full detection pipeline

```bash
trisight detect --window "Notepad" --annotate --output annotated.png
trisight detect --window "Notepad" --tiers uia,ocr
trisight detect --window "Notepad" --screenshot existing.png --depth 20
```

### uia - UI Automation only (Tier 1)

```bash
trisight uia --window "Notepad"
trisight uia --window "Notepad" --depth 10
```

### ocr - OCR only (Tier 2)

```bash
trisight ocr --screenshot page.png
```

### annotate - Render numbered bounding boxes

```bash
trisight annotate --screenshot page.png --window "Notepad" --output annotated.png
trisight annotate --screenshot page.png --elements elements.json --output annotated.png
```

## Options

| Option | Description | Default |
|--------|-------------|---------|
| `--window, -w` | Target window title (substring match) | required |
| `--screenshot, -s` | Path to screenshot PNG | auto-captured |
| `--tiers` | Detection tiers: uia,ocr,pixel | uia,ocr,pixel |
| `--annotate` | Generate annotated screenshot | false |
| `--output, -o` | Output path for annotated image | - |
| `--depth, -d` | Max UIA tree traversal depth | 15 |
| `--elements, -e` | Pre-computed elements JSON (annotate only) | - |

## Detection Tiers

| Tier | Method | Confidence | Speed |
|------|--------|-----------|-------|
| 1: UIA | Windows UI Automation (FlaUI) | 1.0 | Fast |
| 2: OCR | Windows.Media.Ocr | 0.85-0.9 | Medium |
| 3: Pixel | Python color/edge/symbol detection | 0.56-0.8 | Slow |

## Output Format

```json
{
  "success": true,
  "windowTitle": "Notepad",
  "elementCount": 42,
  "uiaCount": 35,
  "ocrCount": 12,
  "pixelCount": 5,
  "detectionTimeMs": 1250,
  "elements": [
    {
      "id": 1,
      "type": "Button",
      "name": "OK",
      "bounds": {"x": 100, "y": 200, "width": 80, "height": 30},
      "center": [140, 215],
      "automationId": "btnOK",
      "isEnabled": true,
      "confidence": 1.0,
      "source": "Uia"
    }
  ]
}
```

## What It Does NOT Do

- Does not click, type, or interact with elements (use cc-click for actions)
- Does not make AI decisions about what to do (use cc-computer for that)
- Does not work on Linux/macOS (Windows UI Automation only)
- Does not process video or screen recordings

## Requirements

- Windows 10/11
- .NET 10.0 runtime
- Python (optional, for pixel analysis tier)

## Architecture

- **TrisightCore** - Shared detection library (also used by cc-computer)
- **TrisightCli** - Command-line interface
- **ElementFusionEngine** - Merges results from all tiers, deduplicates
- **AnnotatedScreenshotRenderer** - Renders numbered bounding boxes

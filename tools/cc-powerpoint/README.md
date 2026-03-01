# cc-powerpoint

Convert Markdown to PowerPoint presentations with beautiful themes.

## Usage

```bash
cc-powerpoint slides.md -o presentation.pptx
cc-powerpoint slides.md -o presentation.pptx --theme boardroom
cc-powerpoint --themes
cc-powerpoint --version
```

## Markdown Syntax

Use `---` to separate slides:

```markdown
---
# Presentation Title
## Author Name

---
# Slide with Bullets

- Point one
- Point two
  - Sub-point

> Speaker notes go in blockquotes at the end

---
# Section Header

---
# Slide with Table

| Column A | Column B |
|----------|----------|
| Data 1   | Data 2   |

---
# Code Example

```python
def hello():
    print("Hello")
```

---
# Slide with Image

![Description](path/to/image.png)
---
```

## Themes

| Theme | Description |
|-------|-------------|
| boardroom | Corporate, executive style with serif fonts |
| paper | Minimal, clean, elegant (default) |
| terminal | Technical, monospace with dark colors |
| spark | Creative, colorful, modern |
| thesis | Academic, scholarly |
| obsidian | Dark theme with subtle highlights |
| blueprint | Technical documentation style |

## Layout Detection

Layouts are automatically detected from content:

- **Title Slide** - First slide with # heading and optional ## subtitle
- **Section Header** - Slide with only a # heading
- **Content** - Heading + bullet points
- **Table** - Heading + markdown table
- **Code** - Heading + fenced code block
- **Image** - Heading + image, or image-only

## Building

```powershell
.\build.ps1
```

Produces `dist\cc-powerpoint.exe` and deploys to `%LOCALAPPDATA%\cc-director\bin\`.

## Development

```bash
python -m venv venv
venv\Scripts\activate
pip install -e ".[dev]"
python main.py samples/quarterly-report.md -o test.pptx --theme boardroom
pytest tests/ -v
```

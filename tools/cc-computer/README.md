# cc-computer

AI-powered desktop automation agent. Give it natural language instructions and it controls Windows applications using LLM reasoning, screenshot-in-the-loop verification, and three-tier UI element detection.

## Usage

```bash
# Single command (CLI mode)
cc-computer --cli "Open Notepad and type Hello World"

# Interactive REPL
cc-computer --cli

# GUI mode (default)
cc-computer
```

## How It Works

1. Takes a screenshot of the desktop
2. Detects all UI elements (buttons, text fields, menus) using 3-tier detection
3. Overlays numbered bounding boxes on the screenshot
4. LLM sees the annotated screenshot + element list
5. LLM decides what action to take (click, type, keyboard shortcut)
6. Agent executes the action via cc-click
7. Takes new screenshot, verifies result
8. Loops until task is complete

## Available Actions

| Action | Description |
|--------|-------------|
| click | Click UI element by name, ID, or coordinates |
| type_text | Type text into a specific field |
| send_keys | Send keystrokes to focused window |
| press_key | Send special keys (Enter, Tab, Escape, F1-F12) |
| keyboard_shortcut | Execute Ctrl+S, Ctrl+C, Alt+F4, etc. |
| launch_application | Open Notepad, Excel, Chrome, Outlook, etc. |
| take_screenshot | Capture current screen state |
| vision_find_element | Find UI elements using AI vision |
| vision_verify | Verify a condition on screen ("is the dialog open?") |

## Configuration

Edit `appsettings.json`:

```json
{
  "LLM": {
    "ModelId": "gpt-5.2",
    "ApiKey": ""
  },
  "Desktop": {
    "CcClickPath": "cc-click.exe"
  },
  "Detection": {
    "EnablePipeline": true,
    "EnableOcr": true,
    "EnablePixelAnalysis": true
  }
}
```

Or set `OPENAI_API_KEY` environment variable.

## Session Logging

Every session is logged to `%APPDATA%\CCComputer\sessions\` with:
- `activity.jsonl` - All actions and observations
- `screenshots/` - Every screenshot captured
- `evidence_chain.json` - Full audit trail

## What It Does NOT Do

- Does not work on Linux/macOS (Windows only)
- Does not automate web browsers directly (use cc-browser for that)
- Does not run without an LLM API key
- Does not modify system settings or install software

## Dependencies

- cc-click (for UI automation actions)
- cc-trisight (TrisightCore shared library for detection)
- OpenAI API key (for LLM reasoning)
- .NET 10.0 runtime

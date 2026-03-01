# cc-click

Windows UI automation CLI for LLM agents. Click elements, type text, read text, list windows, and capture screenshots using Windows UI Automation (FlaUI).

All output is JSON-formatted for easy integration with automation tools.

## Commands

### list-windows

List visible top-level windows.

```bash
cc-click list-windows
cc-click list-windows --filter "Notepad"
```

### list-elements

List UI elements in a window.

```bash
cc-click list-elements --window "Notepad"
cc-click list-elements --window "Notepad" --type Button
cc-click list-elements --window "Notepad" --depth 10
```

### click

Click a UI element by name, AutomationId, or coordinates.

```bash
cc-click click --window "Notepad" --name "File"
cc-click click --window "Notepad" --id "btnSave"
cc-click click --xy "500,300"
```

### type

Type text into a UI element.

```bash
cc-click type --window "Notepad" --name "Text Editor" --text "Hello World"
cc-click type --window "Notepad" --id "txtInput" --text "Hello"
```

### screenshot

Capture a screenshot.

```bash
cc-click screenshot --output screenshot.png
cc-click screenshot --window "Notepad" --output notepad.png
```

### read-text

Read text content from a UI element.

```bash
cc-click read-text --window "Notepad" --name "Text Editor"
cc-click read-text --window "Notepad" --id "txtInput"
```

## Output Format

All commands output JSON:

```json
{
  "clicked": "File",
  "automationId": "menuFile",
  "name": "File"
}
```

Errors are also JSON:

```json
{
  "error": "Window not found: NonexistentApp"
}
```

## What It Does NOT Do

- Does not work on Linux/macOS (Windows UI Automation only)
- Does not interact with web pages (use cc-browser for that)
- Does not use OCR or vision (use cc-trisight for element detection)
- Does not perform AI-driven automation (use cc-computer for that)

## Requirements

- Windows 10/11
- .NET 10.0 runtime

## Dependencies

- FlaUI.Core / FlaUI.UIA3 - Windows UI Automation
- System.CommandLine - CLI framework

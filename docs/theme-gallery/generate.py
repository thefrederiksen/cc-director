"""Theme Gallery Generator

Generates output files for every cc-tool x theme combination.
Run from the repo root or from docs/theme-gallery/.

Usage:
    python docs/theme-gallery/generate.py
"""

import os
import shutil
import subprocess
import sys
from pathlib import Path

THEMES = ["boardroom", "paper", "terminal", "spark", "thesis", "obsidian", "blueprint"]

# Each entry: (tool_name, subcommand, input_file, output_dir, output_base, output_ext)
# subcommand is None for tools that take positional input directly.
TOOL_CONFIGS = [
    ("cc-html", None, "samples/report.md", "output/html", "report", ".html"),
    ("cc-pdf", None, "samples/report.md", "output/pdf", "report", ".pdf"),
    ("cc-word", None, "samples/report.md", "output/word", "report", ".docx"),
    ("cc-excel", "from-csv", "samples/quarterly-sales.csv", "output/excel", "sales", ".xlsx"),
    ("cc-powerpoint", None, "samples/slides.md", "output/powerpoint", "slides", ".pptx"),
]


def find_gallery_root():
    """Find the theme-gallery directory."""
    # Check if we're already in it
    if Path("samples/report.md").exists() and Path("generate.py").exists():
        return Path(".")
    # Check from repo root
    candidate = Path("docs/theme-gallery")
    if candidate.exists() and (candidate / "samples" / "report.md").exists():
        return candidate
    print("ERROR: Cannot find theme-gallery directory.")
    print("Run from repo root or from docs/theme-gallery/.")
    sys.exit(1)


def check_tool(tool_name):
    """Check if a tool is available on PATH."""
    return shutil.which(tool_name) is not None


def build_command(tool_name, subcommand, input_file, output_file, theme):
    """Build the CLI command for a tool invocation."""
    cmd = [tool_name]
    if subcommand:
        cmd.append(subcommand)
    cmd.extend([str(input_file), "-o", str(output_file), "--theme", theme])
    return cmd


def run_tool(tool_name, subcommand, input_file, output_file, theme):
    """Run a cc-tool and return (success, error_message)."""
    cmd = build_command(tool_name, subcommand, input_file, output_file, theme)
    try:
        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=60,
        )
        if result.returncode != 0:
            stderr = result.stderr.strip() if result.stderr else "no error output"
            return False, f"Exit code {result.returncode}: {stderr}"
        return True, None
    except subprocess.TimeoutExpired:
        return False, "Timed out after 60s"
    except FileNotFoundError:
        return False, f"Tool not found: {tool_name}"


def main():
    gallery_root = find_gallery_root()
    os.chdir(gallery_root)

    print("CC Director Theme Gallery Generator")
    print("=" * 40)
    print()

    # Check tool availability
    available_tools = {}
    for tool_name, _, _, _, _, _ in TOOL_CONFIGS:
        found = check_tool(tool_name)
        available_tools[tool_name] = found
        status = "OK" if found else "NOT FOUND (skipping)"
        print(f"  {tool_name}: {status}")
    print()

    # Create output directories
    for _, _, _, output_dir, _, _ in TOOL_CONFIGS:
        Path(output_dir).mkdir(parents=True, exist_ok=True)

    # Generate outputs
    generated = 0
    skipped = 0
    failed = 0
    errors = []

    for tool_name, subcommand, input_file, output_dir, output_base, output_ext in TOOL_CONFIGS:
        if not available_tools[tool_name]:
            skipped += len(THEMES)
            continue

        input_path = Path(input_file)

        for theme in THEMES:
            output_file = Path(output_dir) / f"{output_base}-{theme}{output_ext}"
            label = f"{tool_name} / {theme}"

            success, error = run_tool(tool_name, subcommand, input_path, output_file, theme)
            if success:
                generated += 1
                print(f"  [+] {label} -> {output_file}")
            else:
                failed += 1
                errors.append((label, error))
                print(f"  [X] {label}: {error}")

    # Summary
    print()
    print("=" * 40)
    print(f"Generated: {generated}")
    print(f"Skipped (tool not found): {skipped}")
    print(f"Failed: {failed}")

    if errors:
        print()
        print("Failures:")
        for label, error in errors:
            print(f"  - {label}: {error}")

    if failed > 0:
        sys.exit(1)


if __name__ == "__main__":
    main()

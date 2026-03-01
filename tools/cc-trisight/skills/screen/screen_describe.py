"""K-20 screen_describe -- Describe what's on screen (for non-Claude callers).

Note: When Claude Code is the orchestrator, it reads screenshots directly
via the Read tool and reasons about them natively. This skill exists for
non-Claude callers or automated pipelines that need a text description.
"""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from trisight_cli import run as cli_run
from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "screen_describe"


def main() -> None:
    parser = argparse.ArgumentParser(description="Describe what's on screen using detection")
    parser.add_argument("--screenshot", "-s", required=True, help="Screenshot path")
    parser.add_argument("--window", "-w", help="Window title (enables UIA detection)")
    args = parser.parse_args()

    if not os.path.isfile(args.screenshot):
        error(SKILL_NAME, f"Screenshot not found: {args.screenshot}")

    log_skill_call(SKILL_NAME, vars(args))

    # Use OCR to get all text on screen
    cli_args = {"--screenshot": args.screenshot}
    exit_code, stdout, stderr, elapsed_ms = cli_run("ocr", cli_args)

    ocr_text = []
    if exit_code == 0:
        try:
            data = json.loads(stdout)
            ocr_text = [r.get("text", "") for r in data.get("regions", [])]
        except json.JSONDecodeError:
            pass

    # Optionally get UIA elements
    uia_elements = []
    if args.window:
        exit_code2, stdout2, stderr2, elapsed_ms2 = cli_run("uia", {"--window": args.window})
        elapsed_ms += elapsed_ms2
        if exit_code2 == 0:
            try:
                uia_data = json.loads(stdout2)
                uia_elements = uia_data.get("elements", [])
            except json.JSONDecodeError:
                pass

    # Build text description
    lines = []
    if ocr_text:
        lines.append(f"Visible text ({len(ocr_text)} regions):")
        for t in ocr_text:
            lines.append(f"  - {t}")
    if uia_elements:
        lines.append(f"\nUI elements ({len(uia_elements)}):")
        for e in uia_elements[:30]:  # Cap at 30 for readability
            name = e.get("name", "")
            etype = e.get("type", "")
            lines.append(f"  [{e.get('id')}] {etype} \"{name}\"")
        if len(uia_elements) > 30:
            lines.append(f"  ... and {len(uia_elements) - 30} more")

    description = "\n".join(lines) if lines else "No text or elements detected on screen."

    log_skill_result(SKILL_NAME, True, f"{len(ocr_text)} text, {len(uia_elements)} UI elements")
    success(SKILL_NAME, {
        "description": description,
        "ocr_text_count": len(ocr_text),
        "uia_element_count": len(uia_elements),
        "elapsed_ms": elapsed_ms,
    })


if __name__ == "__main__":
    main()

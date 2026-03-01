"""K-21 screen_verify -- Verify expected state is visible on screen.

Note: When Claude Code is the orchestrator, it can verify screenshots
directly via the Read tool. This skill provides structured verification
for automated pipelines or non-Claude callers.
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

SKILL_NAME = "screen_verify"


def main() -> None:
    parser = argparse.ArgumentParser(description="Verify expected state on screen")
    parser.add_argument("--screenshot", "-s", required=True, help="Screenshot to verify")
    parser.add_argument("--expect-text", dest="expect_text", action="append", default=[],
                        help="Text that should be visible (can specify multiple)")
    parser.add_argument("--reject-text", dest="reject_text", action="append", default=[],
                        help="Text that should NOT be visible (can specify multiple)")
    parser.add_argument("--window", "-w", help="Window title (enables UIA verification)")
    parser.add_argument("--expect-element", dest="expect_element", action="append", default=[],
                        help="UI element name/type that should exist (can specify multiple)")
    args = parser.parse_args()

    if not os.path.isfile(args.screenshot):
        error(SKILL_NAME, f"Screenshot not found: {args.screenshot}")

    log_skill_call(SKILL_NAME, vars(args))

    # Run OCR to find text
    cli_args = {"--screenshot": args.screenshot}
    exit_code, stdout, stderr, elapsed_ms = cli_run("ocr", cli_args)

    all_text = ""
    if exit_code == 0:
        try:
            data = json.loads(stdout)
            all_text = " ".join(r.get("text", "") for r in data.get("regions", [])).lower()
        except json.JSONDecodeError:
            pass

    # Check text expectations
    results = {"passed": True, "checks": []}

    for text in args.expect_text:
        found = text.lower() in all_text
        results["checks"].append({
            "type": "expect_text",
            "value": text,
            "passed": found,
        })
        if not found:
            results["passed"] = False

    for text in args.reject_text:
        found = text.lower() in all_text
        results["checks"].append({
            "type": "reject_text",
            "value": text,
            "passed": not found,
        })
        if found:
            results["passed"] = False

    # Optionally check UIA elements
    if args.window and args.expect_element:
        exit_code2, stdout2, stderr2, elapsed_ms2 = cli_run("uia", {"--window": args.window})
        elapsed_ms += elapsed_ms2
        element_names = set()
        element_types = set()
        if exit_code2 == 0:
            try:
                uia_data = json.loads(stdout2)
                for e in uia_data.get("elements", []):
                    if e.get("name"):
                        element_names.add(e["name"].lower())
                    if e.get("type"):
                        element_types.add(e["type"].lower())
            except json.JSONDecodeError:
                pass

        for elem in args.expect_element:
            found = elem.lower() in element_names or elem.lower() in element_types
            results["checks"].append({
                "type": "expect_element",
                "value": elem,
                "passed": found,
            })
            if not found:
                results["passed"] = False

    results["elapsed_ms"] = elapsed_ms
    passed_count = sum(1 for c in results["checks"] if c["passed"])
    total = len(results["checks"])

    log_skill_result(SKILL_NAME, results["passed"], f"{passed_count}/{total} checks passed")
    success(SKILL_NAME, results)


if __name__ == "__main__":
    main()

"""Run the Your Throttle contract through BOTH real consumers over ONE directory of fixtures (final
inspection finding F-01).

The browser tests (client-core, the Cockpit page, the phone page) and the mentor report's tests each
hold a copy of this directory and pin its digests. This runner removes the "copy" from the argument:
it puts the product's fixtures in a fresh temporary directory, points both suites at it through
THROTTLE_CONTRACT_DIR, and runs them in the foreground, one after the other. Exit 0 only when every
fixture rendered the recorded answer - or refused it - on BOTH sides.

    python tools/throttle-conformance/contract/run_contract.py [--mentor-dir <path>]

--mentor-dir defaults to D:/ReposFred/devthrottle_internal/tools/mentor. The report's suite needs
its own dependencies (pytest) and the browser suites need the workspace's node_modules.
"""
import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent.parent
DEFAULT_MENTOR = Path("D:/ReposFred/devthrottle_internal/tools/mentor")


def run(label, command, cwd, env):
    print("== " + label + ": " + " ".join(command) + "  (in " + str(cwd) + ")")
    sys.stdout.flush()
    completed = subprocess.run(command, cwd=str(cwd), env=env)
    print("== " + label + (" PASSED" if completed.returncode == 0 else " FAILED (exit " + str(completed.returncode) + ")"))
    return completed.returncode == 0


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--mentor-dir", default=str(DEFAULT_MENTOR))
    args = parser.parse_args()
    mentor = Path(args.mentor_dir)
    if not (mentor / "tests" / "test_throttle_contract.py").is_file():
        print("ERROR: no tests/test_throttle_contract.py under " + str(mentor), file=sys.stderr)
        return 2
    npx = shutil.which("npx")
    if npx is None:
        print("ERROR: npx is not on PATH; the browser suites cannot run", file=sys.stderr)
        return 2

    with tempfile.TemporaryDirectory(prefix="throttle-contract-") as folder:
        shared = Path(folder) / "contract"
        shared.mkdir()
        names = sorted(p.name for p in HERE.glob("*.json"))
        for name in names:
            shutil.copy2(HERE / name, shared / name)
        print("one directory of fixtures for both consumers: " + str(shared))
        for name in names:
            print("  " + name)
        env = dict(os.environ, THROTTLE_CONTRACT_DIR=str(shared))
        results = [
            run("browser client (client-core)", [npx, "vitest", "run", "src/stats/statsClient.contract.test.ts"],
                REPO / "packages" / "client-core", env),
            run("Cockpit page", [npx, "vitest", "run", "src/throttle/YourThrottleView.test.tsx"],
                REPO / "apps" / "cockpit", env),
            run("phone page", [npx, "vitest", "run", "src/pages/YourThrottle.test.tsx"],
                REPO / "apps" / "mobile", env),
            run("mentor report", [sys.executable, "-m", "pytest", "tests/test_throttle_contract.py", "-q"],
                mentor, env),
        ]
    if all(results):
        print("CONTRACT PASS: every fixture rendered the recorded headline, or was refused, on both consumers.")
        return 0
    print("CONTRACT FAIL: the two consumers do not agree on the recorded headline for every fixture.", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())

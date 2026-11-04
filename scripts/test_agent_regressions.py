#!/usr/bin/env python3
"""Run Linux first-boot regression fixtures, including standalone test scripts.

These fixtures use temporary directories and mocked system operations. They do
not install Linux or replace the Windows-to-Linux VM acceptance test.
"""

from pathlib import Path
import subprocess
import sys


def main() -> int:
    if sys.platform != "linux":
        print("Run these Linux permission and shell fixtures on Linux or in WSL.",
              file=sys.stderr)
        return 2
    root = Path(__file__).resolve().parents[1]
    directory = root / "tests" / "agent"
    tests = sorted(set(directory.glob("*_test.py")) | set(directory.glob("test_*.py")))
    if not tests:
        print("No agent regression scripts found.", file=sys.stderr)
        return 2
    failures = []
    for test in tests:
        print(f"\nRunning {test.name}", flush=True)
        try:
            result = subprocess.run([sys.executable, str(test)], cwd=root,
                                    check=False, timeout=180)
            if result.returncode:
                failures.append(test.name)
        except subprocess.TimeoutExpired:
            failures.append(test.name)
            print(f"Timed out: {test.name}", file=sys.stderr)
    print(f"\nAgent regression scripts: {len(tests) - len(failures)}/{len(tests)} passed.")
    if failures:
        print("Failed: " + ", ".join(failures), file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())

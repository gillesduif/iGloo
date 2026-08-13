"""Apply the GitHub-side hardening: branch protection, Dependabot, sign-off.

Run with --apply. Every call is reversible from the repo settings UI.

Branch protection deliberately leaves enforce_admins off: you push straight to
main and requiring checks on your own pushes would lock you out, since the
checks only run after the push lands.
"""
import json
import subprocess
import sys

REPO = "gillesduif/iGloo"

PROTECTION = {
    "required_status_checks": {
        "strict": True,
        # Job names as they appear in the workflows.
        "contexts": ["build-and-test", "analyze", "signed-off-by"],
    },
    "enforce_admins": False,
    "required_pull_request_reviews": None,
    "restrictions": None,
    "allow_force_pushes": False,
    "allow_deletions": False,
    "required_linear_history": True,
    "required_conversation_resolution": True,
}

STEPS = [
    ("branch protection on main", "PUT", f"repos/{REPO}/branches/main/protection", PROTECTION),
    ("Dependabot alerts", "PUT", f"repos/{REPO}/vulnerability-alerts", None),
    ("Dependabot security updates", "PUT", f"repos/{REPO}/automated-security-fixes", None),
    ("require sign-off on web commits", "PATCH", f"repos/{REPO}",
     {"web_commit_signoff_required": True}),
    ("delete merged branches", "PATCH", f"repos/{REPO}", {"delete_branch_on_merge": True}),
]

for label, method, path, _ in STEPS:
    print(f"{method:6} {label}")

if "--apply" not in sys.argv:
    print("\nDry run. Re-run with --apply.")
    raise SystemExit

for label, method, path, payload in STEPS:
    args = ["gh", "api", "--method", method, path]
    if payload is not None:
        args += ["--input", "-"]
    r = subprocess.run(args, input=json.dumps(payload) if payload else None,
                       text=True, capture_output=True, encoding="utf-8")
    print(f"{label}:", "ok" if r.returncode == 0 else "FAILED " + r.stderr.strip()[:200])

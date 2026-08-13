"""Swap M19 and M20: the v1.0 release cannot come after post-1.0 work.

Moves the issues first, then swaps the titles, so the release work ends up on
M19 and the post-1.0 backlog on M20. Run with --apply.
"""
import json
import subprocess
import sys
import time

REPO = "gillesduif/iGloo"
PAUSE = 0.6

RELEASE = ("M19: v1.0 public release",
           "Alpha first; signed binaries once a code-signing certificate exists (an unsigned "
           "executable that repartitions disks trips SmartScreen and looks like malware).")
POST = ("M20: Post-1.0 enhancements",
        "The After v1.0 list from ROADMAP.md, in rough priority order.")


def gh(*args, stdin=None):
    r = subprocess.run(["gh", *args], input=stdin, text=True,
                       capture_output=True, encoding="utf-8")
    if r.returncode != 0:
        raise SystemExit(f"gh {' '.join(args)}\n{r.stderr.strip()}")
    return r.stdout


def issues_on(milestone: int) -> list[int]:
    data = json.loads(gh("api",
                         f"repos/{REPO}/issues?milestone={milestone}&state=all&per_page=100"))
    return [i["number"] for i in data if "pull_request" not in i]


on19, on20 = issues_on(19), issues_on(20)
print(f"#19 holds {len(on19)} issues -> move to #20: {on19}")
print(f"#20 holds {len(on20)} issues -> move to #19: {on20}")
print(f"then #19 = {RELEASE[0]}")
print(f"     #20 = {POST[0]}")

if "--apply" not in sys.argv:
    print("\nDry run. Re-run with --apply.")
    raise SystemExit

for numbers, target in ((on19, 20), (on20, 19)):
    for n in numbers:
        gh("api", "--method", "PATCH", f"repos/{REPO}/issues/{n}",
           "--input", "-", stdin=json.dumps({"milestone": target}))
        print(f"#{n} -> milestone {target}")
        time.sleep(PAUSE)

for slot, (title, desc) in ((19, ("tmp-19", None)), (20, POST), (19, RELEASE)):
    payload = {"title": title} if desc is None else {"title": title, "description": desc}
    gh("api", "--method", "PATCH", f"repos/{REPO}/milestones/{slot}",
       "--input", "-", stdin=json.dumps(payload))
    print(f"milestone #{slot} = {title}")
    time.sleep(PAUSE)

# Open-source release checklist

Working checklist for taking iGloo public on a rebuilt GitHub repository and
relicensing GPL-2.0-only → GPL-3.0.

Everything below was verified against this repository on 2026-07-27, not assumed.

**Progress 2026-07-29:** §1.1–§1.5 done (GPL-3.0-or-later chosen; official
LICENSE text in place; COPYRIGHT added; ADR-010 recorded; all eight GPL-2.0
references updated and the sweep re-run clean). §1.6 decided: rely on root
LICENSE + COPYRIGHT + README statement, no per-file SPDX headers. §1.7
verified (THIRD-PARTY-NOTICES current, FluentAssertions pin warning present).
§5 README rewritten for a public audience (screenshot/GIF still pending, as
planned, for the beta captures).

Findings that shape the plan:

- **Sole copyright holder.** `git log` shows exactly one author across all 48
  commits (Gilles D'huyvetter). Relicensing therefore needs *nobody else's*
  consent — the single biggest blocker in most relicensing efforts does not apply.
- **No tracked build artifacts.** `git ls-files` matches zero `bin/`, `obj/`,
  `.dll`, `.pdb`, `.exe`, `.pyc`. History is clean on that front.
- **All dependencies are permissive** (MIT / Apache-2.0) — no copyleft conflicts.
- **The relicense fixes a real, existing licensing bug** — see §1.1.

---

## 1. Legal / licensing

### 1.1 Why GPLv3 is the correct call (not just preference)

- [ ] Understand the actual problem being fixed. **Serilog is Apache-2.0 and ships
      inside the distributed application.** Apache-2.0 is **incompatible with
      GPL-2.0** (the FSF's position: Apache-2.0's patent-termination clause is an
      additional restriction GPLv2 does not permit), but is **explicitly compatible
      with GPL-3.0**. The current GPL-2.0-only license is therefore in conflict with
      a shipped dependency today. Moving to GPLv3 resolves it. Record this reasoning
      in the ADR (§1.4) — it is the strongest justification for the change.
- [ ] Same applies to test-only Apache-2.0 deps (xunit, FluentAssertions), though
      those are not distributed and so matter less.

### 1.2 Choose the exact license identifier

- [ ] Decide **`GPL-3.0-or-later`** (recommended) vs `GPL-3.0-only`.
      "or-later" lets the project adopt a future GPLv4 without repeating this
      exercise, and is what most new GPL projects choose. Whatever you pick, use
      the SPDX identifier verbatim and consistently.
- [ ] Decide whether the desktop app being GPL affects your funding/commercial
      plans (NLnet/VLAIO are fine with GPL; note it in the ADR).

### 1.3 Replace the license text

- [ ] Download the **official** GPLv3 text — never hand-copy or let a model
      generate it: `curl -o LICENSE https://www.gnu.org/licenses/gpl-3.0.txt`
- [ ] Verify the file starts with "GNU GENERAL PUBLIC LICENSE / Version 3, 29 June
      2007" and is ~35 KB.
- [ ] Add a `COPYRIGHT` or header block naming the copyright holder and year range
      (e.g. `Copyright (C) 2025-2026 Gilles D'huyvetter`). GPLv3's "how to apply"
      appendix describes this.

### 1.4 Record the decision

- [ ] Add an ADR in `docs/decisions/` (the repo already uses ADRs — `architecture.md`
      references one for GPL-2.0). Supersede it rather than editing history:
      `docs/decisions/0XX-relicense-gpl3.md`, status `Accepted`, superseding the
      GPL-2.0 ADR. Include the Apache-2.0/Serilog reasoning from §1.1.

### 1.5 Update every existing GPL-2.0 reference (8 known locations)

- [ ] `README.md:13` — badge (`License: GPL v2` → `GPL v3`, and the shields.io URL)
- [ ] `README.md:290` — "GPL-2.0-only. Same license as the Linux kernel." **Rewrite
      this sentence** — the kernel comparison stops being true under GPLv3.
- [ ] `CONTRIBUTING.md:14` — contribution licensing statement + DCO reference
- [ ] `distros/README.md:113` — "Distro plugin code must be GPL-2.0-only"
- [ ] `docs/architecture.md:251` — ADR list mention
- [ ] `docs/business/risk-register.md:16` — R-08 mitigation text
- [ ] `THIRD-PARTY-NOTICES.md:3` and `:53` — two mentions
- [ ] Re-run the sweep afterwards to confirm zero stragglers:
      `grep -rniE 'gpl.?v?2|gpl-2' --include='*.md' --include='*.cs' --include='*.csproj' . | grep -v ^./LICENSE`

### 1.6 Source headers (decide once, apply consistently)

- [ ] Decide whether to add SPDX headers to source files. Recommended minimal form,
      one line at the top of each `.cs` / `.py`:
      `// SPDX-License-Identifier: GPL-3.0-or-later`
- [ ] If yes, apply to `src/**`, `distros/**/*.cs`, `distros/**/agent/*.py` — and add
      a note to `CONTRIBUTING.md` so new files carry it.
- [ ] Alternative (less noise): rely on the root `LICENSE` + a statement in README.
      Either is legally sufficient; SPDX headers are friendlier to automated scanners
      and to downstream distro packagers.

### 1.7 Third-party notices

- [ ] Verify `THIRD-PARTY-NOTICES.md` lists all current runtime dependencies:
      BouncyCastle.Cryptography 2.3.1 (MIT), CommunityToolkit.Mvvm 8.2.2 (MIT),
      Microsoft.Extensions.* 8.0.0 (MIT), Serilog.* (Apache-2.0),
      Svg.Skia 4.9.0 (MIT), System.Management 8.0.0 (MIT).
- [ ] Note test-only deps separately (xunit, FluentAssertions, Microsoft.NET.Test.Sdk)
      — they are not distributed.
- [ ] **Add an upgrade warning: FluentAssertions changed to a commercial (Xceed)
      license at v8.** Pinned 6.12.0 is Apache-2.0 and safe; an unthinking bump would
      introduce a paid dependency into an OSS project. Record this in the file and
      consider a comment in the `.csproj`.

### 1.8 Trademark / assets

- [ ] Distro logo `NOTICE` files already document source + trademark + nominative
      use (verified for Linux Mint; spot-check the rest of `distros/*/logo/NOTICE`).
      Confirm every `logo/` folder has one.
- [ ] Confirm no distro logo is used in a way implying endorsement (README hero,
      social preview image, app icon).
- [ ] Your own branding: decide whether "iGloo" is a mark you want to assert, and
      whether the name collides with existing projects (search GitHub/PyPI/npm and
      the EUIPO/USPTO databases). Rename cost is far lower *before* launch.

---

## 2. Repository hygiene (do BEFORE the repo is public)

### 2.1 Secrets and private data in history

- [ ] Scan the **entire history**, not just the working tree:
      `gitleaks detect --source . --log-opts="--all"` (or `trufflehog git file://.`)
- [ ] Check for the Wi-Fi/password handling paths ever committing real values —
      the migration manifest carries plaintext PSKs and a Linux password at runtime.
      Confirm no sample/fixture manifest with real data was ever committed.
- [ ] Decide on **email exposure**: commits carry `gilles.dhuyvetter@icloud.com`.
      Public repo = public email (scraped for spam). Options: accept it, or rewrite
      history to a GitHub `noreply` address, or set
      `git config user.email "<id>+<user>@users.noreply.github.com"` going forward.
- [ ] Check for machine-specific paths in committed files
      (`C:\Users\Gilles D'huyvetter\...`) — leaks your username, and breaks others' builds.

### 2.2 History decision

- [ ] Choose one, deliberately:
      - **Keep full history (48 commits)** — shows real engineering, honest bug-fix
        trail. Requires the secret scan above to come back clean.
      - **Squash to a single "initial public release" commit** — guarantees no
        historical leak, but discards the development narrative (which, for a
        project whose credibility rests on careful disk-safety work, has real value).
      - Recommendation: **keep history** if the scan is clean.
- [ ] If rewriting: use `git filter-repo` (not `filter-branch`), and force-push to a
      *fresh* repo so no stale objects survive.

### 2.3 Files that must not ship

- [ ] Verify `.gitignore` covers `bin/`, `obj/`, `*.user`, `.vs/`, `__pycache__/`,
      `*.iso`, `TestResults/`, `*.log`.
- [ ] Confirm no ISO or large binary was ever committed:
      `git rev-list --objects --all | git cat-file --batch-check='%(objecttype) %(objectname) %(objectsize) %(rest)' | sort -k3 -rn | head -20`
- [ ] Remove any local scratch files (`REFACTOR_NOTES.md`, `ANALYZER_PASS_MANIFEST.md`,
      `STATUS.md` drafts) or consciously decide they are public-facing docs.

---

## 3. Safety, liability, and honest status

This project **repartitions disks**. That raises the stakes above a typical release.

- [ ] README must state, above the fold: **alpha, data-loss risk, back up first,
      do not run on a production machine.** (README already does — keep it prominent
      through the rewrite.)
- [ ] Ensure GPLv3 §15–17 (warranty disclaimer / limitation of liability) are intact
      in the LICENSE file — they are your primary liability shield. Do not truncate.
- [ ] Keep the per-distro validation table **honest**: currently Debian is 🚧 pending
      re-validation and Fedora has an open NVIDIA/kernel issue. Do not promote either
      to ✅ until a clean bare-metal pass. Overstating status is the fastest way to
      lose community trust — and to hurt a user's disk.
- [ ] Consider a `docs/SAFETY.md` describing the data-loss guardrails (BR-01/BR-03,
      label-only matching, `clearpart --none`, `biggest_free`) so reviewers can audit
      the safety model without reading all the code.

---

## 4. GitHub repository setup

### 4.1 Create / rebuild

- [ ] Create the new repo (public). Decide name — keep `iGloo` vs something less
      collision-prone.
- [ ] Set **description** (one line, searchable) and **topics**: `linux`, `windows`,
      `dual-boot`, `installer`, `migration`, `dotnet`, `wpf`, `fedora`, `debian`,
      `linux-mint`, `distro-installer`.
- [ ] Upload a **social preview image** (Settings → General) — this is what renders
      on the WAN Show / Reddit / X links. Use your own artwork, not distro logos.
- [ ] Add website link if one exists.

### 4.2 Protections

- [ ] Branch protection on `main`: require PR, require CI to pass, no force-push,
      no deletion. (Even solo — it prevents accidents and signals seriousness.)
- [ ] Enable **Private vulnerability reporting** (Settings → Security). Your
      `SECURITY.md` should point at it rather than a bare email.
- [ ] Enable **Dependabot** (alerts + version updates) — but pin FluentAssertions
      (§1.7) with an `ignore` rule for majors, or it will propose the licensed v8.
- [ ] Enable **secret scanning** + **push protection**.
- [ ] Set Actions permissions to read-only by default; grant per-workflow as needed.

### 4.3 Community files (all already exist — verify, don't recreate)

- [ ] `README.md` — rewrite for a public audience (§5)
- [ ] `LICENSE` — GPLv3 (§1.3)
- [ ] `CONTRIBUTING.md` — update license statement, keep DCO
- [ ] `CODE_OF_CONDUCT.md` — confirm a real contact address is filled in
- [ ] `SECURITY.md` — point to private vulnerability reporting; state supported
      versions and response expectations
- [ ] `CHANGELOG.md` — start a `[Unreleased]` → `[0.1.0]` section (Keep a Changelog)
- [ ] `.github/ISSUE_TEMPLATE/*` + `PULL_REQUEST_TEMPLATE.md` — verify they render
      and ask for the right diagnostics (distro, firmware mode, Secure Boot state,
      GPU, `%LOCALAPPDATA%\iGloo\logs`, `/var/log/igloo/agent.log`)
- [ ] Consider `CODEOWNERS` and `FUNDING.yml` (NLnet/VLAIO/GitHub Sponsors).

---

## 5. README rewrite (the single highest-leverage file)

- [ ] Lead with **what it does in one sentence** and a screenshot/GIF of the wizard.
- [ ] Data-loss warning + alpha status, prominent.
- [ ] Honest per-distro status table.
- [ ] Quickstart: prerequisites, how to build (`dotnet build`), how to run.
- [ ] Architecture at a glance + link to `docs/architecture.md` and the whitepaper.
- [ ] How to add a distro plugin (link `distros/README.md`) — this is your best
      contributor on-ramp.
- [ ] Security model summary: GPG-verified ISOs, pinned key fingerprints, SHA-256.
- [ ] License section (GPLv3) + third-party notices link.
- [ ] Credits/acknowledgements.

---

## 6. CI/CD

- [ ] Verify `.github/workflows/ci.yml` runs on a clean checkout (no local NuGet
      cache assumptions) and builds **serially** where needed — this project has hit
      parallel-build races (`-m:1`).
- [ ] CI must run: `dotnet build` (warnings-as-errors is already on) + `dotnet test`.
- [ ] Add a Python syntax check for the agents:
      `python -m py_compile distros/*/agent/agent.py distros/_debian-family/agent/agent.py`
- [ ] Add JSON Schema validation of `distros/*/distro.json` (a schema already exists
      at `distros/_schema/`).
- [ ] Consider **CodeQL** (free for public repos) — meaningful for a tool with this
      much P/Invoke and disk access.
- [ ] Add a license-header check if you adopt SPDX (§1.6).
- [ ] Badge the CI status in README.

---

## 7. Release

- [ ] Tag `v0.1.0-alpha` (SemVer; the leading `0.` communicates instability).
- [ ] Write release notes from CHANGELOG — call out the disk-safety guardrails and
      the known-broken paths explicitly.
- [ ] Decide whether to publish a built artifact. **A signed installer matters here**:
      an unsigned .exe that repartitions disks will trip SmartScreen and looks exactly
      like malware. Options: ship source-only for alpha (recommended), or obtain a
      code-signing certificate before distributing binaries.
- [ ] If shipping binaries: document the SHA-256 of each artifact in the release.

---

## 8. Launch

- [ ] Announcement post — lead with the problem (Windows 10 EOL, users stranded),
      not the tech.
- [ ] Be explicit about alpha status in the announcement. Under-promise.
- [ ] Prepare for the top questions: why not Ventoy/Rufus, what happens to my data,
      does it work with BitLocker/Secure Boot, which distros, how do I undo it.
- [ ] Have `SECURITY.md` and issue templates working *before* traffic arrives.
- [ ] LTT/WAN Show acknowledgement — keep it factual and link the original segment.

---

## Order of operations (suggested)

1. §2.1 secret scan → decides §2.2 (history keep vs squash)
2. §1.3–§1.7 relicense edits (one commit: "Relicense to GPL-3.0-or-later")
3. §3 honesty pass on status/safety docs
4. §5 README rewrite
5. §6 CI verification
6. Push to the new repo, then §4.2 protections
7. §7 tag + release
8. §8 announce

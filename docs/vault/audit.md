---
title: Vault audit and update record
type: audit
area: vault
status: current
implementation: not-applicable
tags:
  - igloo
  - topic/validation
aliases: []
reviewed: 2026-10-03
reviewed_commit: 5e4ebe846b488d8609612d37665aaefd4d203c88
sources:
  - .gitignore
  - CONTRIBUTING.md
  - docs/architecture/product-boundaries.md
  - docs/architecture/debian-target-root-deployment.md
  - docs/fleet/phase-2.md
  - scripts/validate_docs_vault.py
---

# Vault audit and update record

## Scope and baseline

The checkout is `gillesduif/iGloo`, branch `refactor/community-fleet-foundation`, inspected commit `5e4ebe846b488d8609612d37665aaefd4d203c88`. `docs/` is the active Obsidian vault in the open 1.13.7 UI (the executable reports file version 1.12.7). No `docs/dev-vault/` was present. The working tree already contained source changes, edits to `docs/architecture.md` and `docs/architecture/debian-target-root-deployment.md`, and untracked JSON evidence. This documentation pass does not rewrite them.

Before navigation changes, the tracked-only audit found **64 notes, 120 resolved directed note edges, 21 active orphans, 22 active components, and two link defects**. There was no `docs/README.md`, so Home reachability was undefined. All 61 active tracked notes lacked the proposed frontmatter schema. The local vault also held five ignored Markdown notes under `funding/` and `plans/`; those are not included in tracked metrics or shared navigation. Raw local-vault metrics were 69 notes, 25 active orphans and 26 components.

The two baseline defects were a stale `CoreServices.cs` link in `code/README.md` and a heading fragment in `architecture/recovery-snapshot-v1.md` whose target heading uses a different spelling. These are fixed in the documentation pass. A source-code path is provenance, not an Obsidian graph edge.

## Contradictions retained visibly

- The repository README and Debian manifest describe Debian as validated or `available`; current `DebianPlugin` legacy install entry points throw. The [current state](../current-state.md) and [distribution map](../distributions.md) report the code stop while preserving the earlier hardware record.
- [ADR-008](../decisions/008-installers-never-partition.md) and the older [safety model](../reference/safety-model.md) describe intended protection broadly. The later [ownership](../architecture/community-installation-ownership.md) and [ESP](../architecture/installer-esp-binding.md) audits identify exact-target gaps. The older text is kept as historical design context.
- [Fleet Phase 2](../fleet/phase-2.md) contains read-only capture and fixture results, while production `RecoveryReadiness` remains unavailable. The [Fleet map](../fleet-map.md) does not present a Prepared plan as executable.
- The Debian selected-document lab result is bounded. It does not imply Windows acquisition, agent installation, DeploymentContent, Enrollment or first-boot success.

## Preservation and validation

The 33 JSON files in `docs/architecture/` were hashed before edits and again afterward: **33 unchanged SHA-256 hashes**. Local `.obsidian/` was backed up outside the repository before any configuration change. The ignored local plans, funding draft and Obsidian workspace were not added to Git by this task.

After the update, the **shareable** audit (tracked plus unignored new notes) finds **76 notes, 70 active notes, 321 resolved directed note edges, zero active orphans, one active component, zero unreachable active notes, zero missing or ambiguous links, zero duplicate aliases and zero schema-type problems in notes with frontmatter**. Of those edges, 144 start at navigation hubs and 177 start elsewhere. Removing the nine hub notes still leaves the remaining 61 active notes in one connected component, without isolated notes. The most cited note has 18 incoming links; the project does not depend on Home alone for connectivity.

The four ignored local `plans/` notes remain active orphans and unreachable in the *raw local* audit (81 notes, 74 active, five components). They were not linked into shared navigation because they are private ignored material. The ignored funding draft is excluded from active graph metrics. Two legacy basename families are ambiguous by name alone: `README.md` (four notes) and `architecture.md` (two notes). New links use explicit paths. Maintenance deliberately shows unresolved source-file nodes created by GitHub-readable links outside the `docs/` vault. Those nodes are not missing Markdown notes according to the static validator.

The five global graph bookmarks were opened in the Obsidian UI. Overview showed the domain colors, Architecture without hubs showed its substantive clusters, Current work showed the intended partial/blocked/planned notes with neutral context maps, Safety and evidence showed its focused notes, and Maintenance exposed unresolved source-file nodes. A Phase 2 graph node opened its note; its Local Graph was inspected at depths 1 and 2. Home opened, and its Fleet map link navigated to the intended note. Dense areas still need manual zoom, and layout positions change. No screenshot file was saved. This visual check is limited to this local UI and does not validate installations or recovery behavior.

Run from the repository root:

```powershell
python scripts/validate_docs_vault.py --shareable
python scripts/validate_docs_vault.py --tracked-only --json
git diff --check
git status --short
```

The validator checks Markdown and wikilink destinations, percent-decoded paths, common heading and block targets, basename ambiguity, duplicate aliases, case mismatches, graph edges, orphans, components and Home reachability. It ignores fenced/inline code and HTML comments. It does **not** fully emulate Obsidian's resolver, Markdown reference definitions, nested Markdown destinations or visual rendering; verify representative links in the app.

## Incremental update checklist

1. Confirm branch, commit, dirty state, vault root and ignore rules; preserve unrelated edits.
2. Compare the last `reviewed_commit` with current source changes, including renames and deletions. Revisit only affected claims, source paths, hubs and status properties.
3. Distinguish code, tests, native fixtures, canonical evidence and hardware runs. Do not upgrade an outcome from source existence alone.
4. Add a real hub path and contextual relationship for each new substantive note. Keep one canonical document per subject.
5. Re-run the validator, inspect unresolved and orphan nodes in Obsidian, and compare protected JSON hashes.
6. Update `reviewed` only for claims actually rechecked. An unchanged source should not cause date-only churn.

## Remaining debt

59 inherited active long-form notes still have no frontmatter. Their subjects remain reachable through hubs; add properties when each note receives a substantive review rather than mass-stamping an unverified date. The local ignored plans and funding draft remain unshared. The ignored bookmarks and graph settings are applied locally; a fresh checkout must recreate them from the [Graph Guide](graph-guide.md).

## Four-product follow-up audit — 2026-10-03

This incremental pass inspected the same branch and HEAD plus its current working tree, including the untracked CLI stub, the Community ViewModel, Core/Migration contracts, Fleet protocol, the root roadmap/README, product boundary note, ADRs, business and marketing notes, and the ignored local plans/funding draft. The ignored drafts were read only for context and remain outside shared navigation. The prior 76-note graph above is the **before** baseline for this pass. No app code, protected JSON evidence, release configuration or root README was edited here.

### Coverage and contradictions

- [[architecture/product-boundaries|Product boundaries]] and [[decisions/013-product-family-and-platform|ADR-013]] now state the user-decided four peer products and one Platform. [[current-state]] keeps the implementation separate: Community/Fleet exist, CLI is an untracked Hello World project, AI is an empty solution folder. The Phase 1 two-host diagram remains labeled history.
- [[cli]], [[ai]], [[architecture/shared-orchestration]] and [[architecture/migration-rules]] document the machine interface, intent boundary, extraction seam and filter proposal. Existing `FileMigrationPlan`, Fleet `PreparedMigrationPlan` and Core `PreparationPlanV1` are different records; no universal `InstallationPlan` exists.
- The repository-root README, root roadmap and dated one-pager still describe broad Debian validation or removal/rollback outcomes. They are outside this vault-only edit scope or retained historical presentation. Current Debian plugin entry points, exact-target audits and [[distributions|status map]] limit present claims. The older [Community architecture](../architecture.md) and [[code/README|code guide]] describe a Community pipeline, not a universal four-product runtime. The code guide's absolute “plugins never touch disk/network/firmware” wording was qualified because plugin packages include installer templates and Linux agents.
- The untracked CLI project targets .NET 10 Windows while Community targets .NET 9 Windows. `ArchitectureTests` checks existing Community/Fleet/distro references, not CLI/AI. No build or test run was taken as proof of this new product direction.

### Open technical decisions and documentation debt

| Area | Documented now | Still to decide or verify |
| --- | --- | --- |
| Plan and execution boundary | [[architecture/shared-orchestration]] separates intent, private manifest and Fleet planning | Exact plan contract, validation/invalidation, target binding, confirmation, recovery/reversibility and independent evidence. |
| Platform composition | Existing `App.xaml.cs` registration and extraction seam recorded | Module-owned DI extensions, lifetimes and ADR; the old Phase 1 “one registration needs no module” decision is narrow. |
| CLI machine contract | [[cli]] sets read-only first milestone and proposed commands | Versioned JSON schemas, stable exit codes, stdout/stderr, event/progress model, compatibility policy and secret handoff. |
| AI security and privacy | [[ai]] fixes intent-only authority and non-AI fallback | Threat model, provider/offline data policy, consent, prompt/file-name minimization, auditability and ambiguity handling. |
| Migration rules | [[architecture/migration-rules]] gives a deterministic preview model | Predicate/precedence semantics, scan-to-copy consistency, excluded-byte accounting, custody, schema and ADR after design settles. |
| Distribution/release | [[distributions]] separates manifest from validation | Compatibility matrix by hardware/firmware/BitLocker, plugin/Core/CLI versioning, packaging, signing and upgrade policy. |
| Operations | [[development]] and [[safety-and-evidence]] collect existing validation records | Cross-product test strategy, telemetry/privacy policy, support logging and rollback claims, contributor path for CLI/AI. |
| Commercial boundary | [[direction]] records hypotheses and ADR-010 ([[decisions/010-relicense-gpl3]]) records licensing history | Open-source/commercial product boundaries and actual go-to-market experiments need explicit decisions. |

Only the firm peer-product/Platform ownership decision became a new Accepted ADR. Shared DI, machine schema, secret mechanism and migration rules remain proposals; no speculative Accepted ADR was created. If the shared plan contract stabilizes, its safety invariants merit a separate ADR. The current one-pager and historical campaign may later need a separate public-claims review; they were not copied into new marketing notes.

### Research needed

European digital-sovereignty positioning, endpoint lock-in as a policy bottleneck, current EU funding programmes and eligibility/amounts, public-sector adoption, AI investment programmes and market demand are hypotheses or unverified research questions. The dated tracked one-pager and ignored local funding draft do not establish current programme terms or a confirmed award. [[direction]] records the thesis without treating those claims as facts.

Potential later splits: separate CLI schema reference only after a concrete contract exists; separate migration rule ADR only after semantics are decided; separate compatibility/version matrix once tested environments and release channels exist. Keep these gaps in this audit until then.

### Follow-up validation

The shareable vault moved from **76 notes / 70 active / 321 resolved directed links** to **81 notes / 75 active / 416 links** (169 navigation, 247 substantive). Active orphans stayed at **0**, active components at **1**, Home-unreachable notes at **0**, and link and metadata defects at **0**. Removing hubs leaves one connected active component. These figures are static graph checks, not proof that every edge describes a runtime dependency.

The five existing global graph bookmarks were preserved. Overview, Architecture without hubs and Safety and evidence filters were expanded for the new subject notes; Current work picks up `planned` and `partial` notes and includes ADR-013 as context. CLI and AI have separate overview colors. The local `.obsidian/` files were backed up to an ignored location outside the vault before the narrow JSON edits. In Obsidian 1.13.7, the new CLI and AI notes opened, a CLI-to-AI wikilink navigated, and the overview rendered both colored clusters. Architecture without hubs, Current work and Safety and evidence were also reopened and visually inspected: the new AI/CLI, plan and rule nodes appear in their intended contexts, with no blank Canvas nodes in the refreshed overview. Node labels still overlap when the full overview is zoomed out; zoom in for reading. No screenshot was saved.

All 33 architecture JSON evidence files retained their paths and bytes: tracked JSON has no diff; the three pre-existing untracked evidence hashes match the before-edit SHA-256 values. Ignored plans/funding notes and private Obsidian files remain untracked. Exact commands used: `python scripts/validate_docs_vault.py --shareable --json`, `python scripts/validate_docs_vault.py --tracked-only --json`, `git diff --check`, `git diff --name-only -- docs/architecture/*.json`, `Get-FileHash -Algorithm SHA256` on the untracked evidence, and `git status --short`. `git diff --check` reported only line-ending conversion warnings in previously dirty files; no whitespace error. The tracked-only validator sees missing targets because new, uncommitted notes are intentionally absent from Git's tracked set; use shareable mode to audit this working tree.

### Home correction, 2026-10-03

The earlier Home content was placed in `docs/README.md` while an alias pointed to `iGloo Home`. Opening that alias produced an empty `iGloo Home.md` in this local vault. The substantive content now lives only in [[iGloo Home]], and `docs/README.md` is a short entry pointing to it. The overview filter excludes `[type:redirect]` so the entry does not compete with Home as a graph node. Navigation links formerly inside a Markdown table were rewritten as prose bullets because their wikilink alias pipes rendered incorrectly in Obsidian. The same table-cell problem was corrected in [[current-state]] and this audit.

The corrected shareable audit reports **82 notes, 76 active notes, 418 resolved directed links** (156 navigation, 262 substantive), zero active orphans, one active component, zero notes unreachable from the README entry, and zero link or metadata issues. The Project overview bookmark loaded the updated redirect filter in Obsidian 1.13.7. `iGloo Home` rendered with linked subject paths, and its **Current project state** link opened the intended note. The graph remains a force-directed layout; Home's visual position can shift.

### Product graph and solution check, 2026-10-03

The checkout remained on `refactor/community-fleet-foundation` at `5e4ebe846b488d8609612d37665aaefd4d203c88`; the existing dirty source and Debian work was preserved. The inspected `Igloo.sln` has sibling solution folders for Community, CLI, AI and Fleet. Community is a WPF project; the untracked CLI project is a console stub whose `Program.cs` prints `Hello, World!`; AI is presently an empty solution folder; Fleet has six projects. Shared has Core, Iso, Migration, Preflight and UsbWriter; Distros has four plugin projects. The proposed Platform folder is not yet a solution folder. These are structure and source observations, not passing runtime or installation evidence.

[[iGloo Home]] now gives each peer product a direct, status-qualified entry. [[community]], [[cli]], [[ai]] and [[fleet-map]] use Home for family context; their remaining direct links describe real shared-workflow, machine-interface or private-data relationships. [[shared-engine]] names the observed solution structure. No new note was needed because these subjects already had canonical notes. The root README and roadmap still use older Community-centered presentation; they were read but not changed in this vault-only pass.

The screenshot supplied for this pass showed the full Project overview crowded by Debian architecture, with Home near an edge. In Obsidian 1.13.7, a new **Product family** global graph bookmark was saved with the seven-note filter in [[vault/graph-guide|Graph Guide]]; the five earlier bookmarks remain. The default global graph uses that filter. A Home Local Graph at depth 1 was visually checked with Home at the center and the four products as visible spokes; its five-note filter is documented in the guide because native bookmarks do not save local graphs. The focused global graph also rendered all seven colored nodes. Its force layout placed Home near the edge in one view, so the local graph is the reliable centered reading view; no fixed global node coordinates are promised. The local graph pane was left open. No screenshot file was saved.

Before local `.obsidian/` edits, `graph.json` and `bookmarks.json` were copied to `C:\Users\Gilles D'huyvetter\AppData\Local\iGlooVaultBackups\product-graph-20261003\`. Only the focused filter, saved bookmark and graph zoom were changed. The scale values are saved starting points; exact label placement depends on pane size and simulation. No Debian architecture note, evidence JSON or app code was edited by this pass. The pre-existing dirty `docs/architecture/debian-target-root-deployment.md` retained its starting SHA-256 `09B2F6F6F4C2742C937A70D994C8E6AF74C1B8DD68CAEA89C40477078C25E377`.

The shareable validator moved from **82 notes / 76 active / 418 directed links** (156 navigation, 262 substantive) to **82 / 76 / 422** (157 navigation, 265 substantive). Active orphans stayed at **0**, active components at **1**, Home-unreachable notes at **0**, and link, alias and metadata issues at **0**. The additional edges come from the clearer Home/product navigation and audit context; link count is not a quality target. Exact commands: `python scripts/validate_docs_vault.py --shareable --json`, `git diff --check -- docs`, `git diff --name-only -- docs/architecture/debian-* docs/architecture/*.json`, `Get-FileHash -Algorithm SHA256 -LiteralPath docs/architecture/debian-target-root-deployment.md`, and `git status --short`. `git diff --check` exited 0 with line-ending warnings only. The Debian file shown by `git diff --name-only` was already dirty before this pass and was left unchanged. No build, test, installation or deployment workflow was run.

---
title: Architecture Drift Register
type: register
product: platform
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: architecture
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources:
  - docs/spec/command-contract/README.md
  - docs/architecture/product-boundaries.md
  - Igloo.sln
aliases: []
tags: [igloo]
---

# Architecture Drift Register

**CODE OBSERVATION:** This register compares the supplied 6 October CLI-mediated whitepaper with the 10 October working tree at committed HEAD `366c1ec`. The tree is dirty; source paths below refer to its current local contents. A discrepancy is not silently resolved by a newer file or an older PDF snapshot. The canonical work items are in [[15 - Implementation/Master Implementation Checklist]].

| ID | Claim | Source A (version/date/section) | Source B (path/commit) | Kind | Current evidence | Target intent | Status | Resolution owner / decision needed | Related notes/tasks |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| DR-01 | Every first-party endpoint client uses the CLI process | PDF cover v1.0 / header v1.1, 2026-10-06, A2 §3 | `src/Igloo.Community.App/Igloo.Community.App.csproj`; `src/Igloo.Fleet.Agent/Igloo.Fleet.Agent.csproj`; current working tree at `366c1ec` | boundary | Community references Host; Fleet Agent references both Host and Cli.Client; CLI itself references Host | Community and endpoint Agent use CLI for endpoint capabilities after parity and safety gates | open | Maintainer decision: adopt explicit A2 executable boundary in accepted ADR/spec and define transition exception | [[02 - Architecture/Current Architecture]], [[02 - Architecture/Architecture Convergence Plan]], AC-01, AC-03 |
| DR-02 | In-process Host dispatch is an allowed client interface | Current [Command Contract](../spec/command-contract/README.md), 2026-10-07 working tree | PDF A2, §§3–4 | governance | Host-backed private Community and Fleet adapter paths are documented; client-neutral process protocol exists for a subset | CLI is the sole public executable gateway | open | Accept a migration decision and revise contract language only after exact scope is agreed | [[16 - Decisions/Architecture Source Conflicts]], AC-01 |
| DR-03 | CLI Client, One, target binding and allocation exist | PDF §18 implementation snapshot, 2026-10-06 | `Igloo.sln`, `src/Igloo.Cli.Client/`, `Igloo.One.App/`, Host handler registration in current working tree | version drift | Client and One projects now appear in the solution; One is an unfinished shell; `migration.target.bind` and the narrower `migration.target.allocate` are registered | Continue through safe target identity, custody, recovery, approval and durable migration | partially reconciled | Record snapshot as historical; verify current working-tree code and focused tests before promotion | [[06 - One/iGloo One Overview]], [[03 - CLI and Command Contract/Current Capability Registry]], AC-04 |
| DR-04 | The PDF's illustrative JSON and commands are runnable V1 syntax | PDF §§5–6, illustrative examples | `src/Igloo.Command.Contracts/CommandProtocol.cs`; `src/Igloo.Cli/Igloo.Cli/Program.cs`; command spec at `366c1ec` | protocol | V1 uses integer `protocolVersion: 1`, `capabilityId` / `capabilityVersion`, and typed `invoke --machine` framing; PDF examples explicitly defer exact schema to contract | Keep one versioned machine interface | open documentation reconciliation | Maintainer: label paper examples illustrative; use checked-in schemas for current commands | [[03 - CLI and Command Contract/Command Contract V1]], AC-02 |
| DR-05 | Older documents describe the present platform | `docs/iGloo Home.md`, `docs/current-state.md`, `docs/architecture.md`, earlier Fleet phases | Current solution/project references and command spec at `366c1ec` | stale history | Old Home still says CLI is Hello World and AI folder empty; later code has CLI Client and One shell | Dashboard must point to present branch scope while retaining historical snapshots | open | Documentation maintainer: mark older summaries dated and route readers to dashboard | [[00 - Home/Project Dashboard]], [[20 - Reference/Vault Migration Map]], AC-11 |
| DR-06 | AllocationId binds a complete future layout | PDF §7.4 says allocation absent at its snapshot; current `MigrationAllocationBinding.cs` and command spec | Working tree at `366c1ec` | semantic scope | Advisory `WindowsBootNtfsShrinkIntentV1` ID exists; it does not bind stage-2/seed/ISO or executor geometry | Exact complete allocation and installer parity before prepare | open | Core/storage maintainer must design full LayoutId or versioned allocation semantics and prove readback | [[10 - Safety and Security/Plan Target Allocation and Artifact Bindings]], AC-04 |
| DR-07 | Release/hardware claims in root docs prove this branch | `README.md`, `ROADMAP.md`, older hardware findings | Current code and dated evidence, working tree at `366c1ec` | evidence scope | Root docs report alpha release and historical physical validation; no new hardware run was performed for this vault | Separate shipped version, branch behavior and current hardware qualification | open | Release maintainer: reconcile public claims with exact binary and evidence matrix | [[18 - Releases and Access/Release Overview]], AC-12 |
| DR-08 | CLI placement and metadata prove trusted executable | Current command spec; Cli.Client locator | PDF §§10, 13, 16 and current transport tests | trust | Known location/product metadata/hello are implemented; cryptographic binary provenance and packaging readback are not established | Deterministic compatible executable with suitable trust check | open | Packaging/security decision needed; do not claim signature validation | [[03 - CLI and Command Contract/CLI and Command Contract Overview]], AC-02 |
| DR-09 | A prepared plan or successful process exit proves migration authority/completion | PDF A8–A9; command spec | Plan, operation, recovery and private Host paths at `366c1ec` | safety | Plans remain advisory; public migration prepare/execute/verify absent; ISO journal does not cover cross-reboot migration | Exact authority, durable operation and observed postconditions | open | Core safety design and qualified test evidence required | [[10 - Safety and Security/Safety and Security Overview]], AC-05–AC-09 |

The PDF's title page identifies v1.0, while pages 2–28 say v1.1. See [[20 - Reference/Source and Whitepaper Version Index]]; no definitive corrected edition has been supplied. [[02 - Architecture/Architecture Gap Analysis]] groups the implementation transitions without changing the conflict record.

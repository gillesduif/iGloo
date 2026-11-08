---
title: Project Dashboard
type: hub
product: project
status: mixed
doc_status: current
validation_status: partial
availability: not-applicable
graph_cluster: reference
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [README.md, ROADMAP.md, Igloo.sln, docs/spec/command-contract/README.md, docs/reference/hardware-findings.md]
aliases: [iGloo project dashboard]
tags: [igloo]
---

# Project Dashboard

> **25 October 2026:** the Community boot-registration gate blocked every distro in the VM lab and was replaced by the v0.2-alpha registration path ([ADR-014](../decisions/014-boot-registration-is-not-gated.md)). The beta priorities are in [[00 - Home/Beta Track]]; the convergence rows below are the longer architecture track.

## iGloo at a glance

iGloo is an open-source Windows-to-Linux installation and migration project. The released Community installer is **0.2-alpha**, while this vault describes the substantially different, uncommitted local working tree on `refactor/community-fleet-foundation` at committed HEAD `366c1ec57c6e5bde6d452208d4ef6411ae99ebf1` (inspected 10 October 2026). The [release page](https://github.com/gillesduif/iGloo/releases) and [ROADMAP](https://github.com/gillesduif/iGloo/blob/main/ROADMAP.md) are release context; they do not certify this branch's CLI architecture or its current hardware readiness. [Contribution rules](https://github.com/gillesduif/iGloo/blob/main/CONTRIBUTING.md) require disposable VM validation for runtime-relevant changes. License: GPL-3.0-or-later (`LICENSE` at repository root).

**Read this first:** [[02 - Architecture/Current Architecture]] shows real code/project boundaries; [[02 - Architecture/Target Architecture]] shows the supplied CLI-mediated design; [[00 - Home/Architecture Drift Register]] names the gaps. The supplied PDF's cover says v1.0 and interior says v1.1; its implementation snapshot is historical. [[00 - Home/Source Index]] records source authority.

## Products, platform and providers

| Area | Current working-tree state | Target or boundary |
| --- | --- | --- |
| [Community](../05%20-%20Community/Community%20Overview.md) | WPF wizard and private Host-mediated staging/installation services remain; not fully CLI-mediated | Graphical client of the CLI process after safety and parity gates |
| [CLI](../03%20-%20CLI%20and%20Command%20Contract/CLI%20and%20Command%20Contract%20Overview.md) | Executable human and machine adapter to Host dispatcher; first-party process client exists in untracked working-tree project | Sole executable endpoint gateway with typed public contract |
| [Logical Core](../04%20-%20Core%20and%20Runtime/Core%20and%20Runtime%20Overview.md) | Host composes Runtime, Core services, Preflight, ISO, Migration, USB and providers; ISO journal and selective locks exist | Sole endpoint execution authority with durable cross-reboot state, recovery and verification |
| [One](../06%20-%20One/iGloo%20One%20Overview.md) | Unfinished WPF shell uses CLI Client for read-only/planning; no demonstrated AI workflow | Conversational reasoning client, never privileged shell or execution engine |
| [Fleet](../07%20-%20Fleet/Fleet%20Overview.md) | Own repository since 2026-11-07 (ADR-016); Agent references Cli.Client and Command.Contracts only; Server is separate control plane; assessment, dry-run, approval and prepared plans exist, no execution stage yet | Organisation endpoint administration: Agent executes authorized endpoint work through CLI, Server schedules/policies/sanitizes, console manages the fleet afterwards |
| [Providers](../08%20-%20Distribution%20Providers/Distribution%20Provider%20Overview.md) | Debian, Fedora KDE, Mint Cinnamon and Ubuntu projects/manifests exist; scope and validation differ | Provider deltas below Core; independent frontend may consume public CLI |

## CLI and Command Contract

**REGISTERED CAPABILITY:** The normal Host composition has 19 distinct public IDs, 20 ID/version descriptors because `migration.plan` has v1 and v2. [[03 - CLI and Command Contract/Current Capability Registry]] has the complete source-linked table. Key classes:

| Capability ID | Public/private | Class | Currently registered? | Execution semantics | Risk | Validation scope | Target gap | Source |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `system.*`, `distro.*`, `migration.inspect`, `recovery.inspect`, status/read queries | public | query | yes | observation, discovery, advisory record/status; recovery exactness unavailable | none/low | source and test definitions inspected | exact readiness and migration producer | [[03 - CLI and Command Contract/Current Capability Registry]] |
| `migration.plan` v1/v2, `migration.target.bind`, `migration.target.allocate` | public | plan | yes | non-executable intent, target and NTFS shrink binding | low | source/schema/test definitions inspected | complete layout, authority and custody | [[10 - Safety and Security/Plan Target Allocation and Artifact Bindings]] |
| `iso.acquire`, `artifact.acquire` | public | low-risk cache mutation | yes in normal Host | verified ISO/cache acquisition, no disk installation | low | source/test definitions inspected; no live download in this vault update | installer and durable migration binding | [[03 - CLI and Command Contract/Current Capability Registry]] |
| `migration.prepare`, `migration.execute`, `migration.verify`, install/boot/recovery mutation | public target only | destructive | **no** | unavailable; no public handler | destructive | no execution claimed | safety gates in [[10 - Safety and Security/Safety and Security Overview]] | `IglooHost.cs`, `CommandDispatcher.cs` |
| Community `MigrationManifest` and private Host workflows | private | mixed | not public | local UI workflow only | potentially destructive | historical evidence only | CLI parity and protected custody | [[05 - Community/Community Overview]] |

The current protocol uses integer `protocolVersion: 1`, typed `capabilityId`/`capabilityVersion`, human commands and `invoke --machine` JSON Lines. The whitepaper's examples are illustrative; [[03 - CLI and Command Contract/Command Contract V1]] points to the canonical schemas. A process exit is not domain success.

## Architecture health and safety gates

| Invariant | Current state | Evidence | Blocker | Convergence task |
| --- | --- | --- | --- | --- |
| A1–A4 Core/CLI semantic boundary | partial | Host/dispatcher, machine protocol and selected tests | Community Host edge, packaging trust and full parity | AC-01–03 |
| A5 durable Core operation | ISO scope only | ISO journal and lock code | no public cross-reboot migration OperationId | AC-07–08 |
| A6 Fleet Server isolation | code boundary present; Agent Host path removed 2026-11-07 | Server references Domain/Persistence; Agent references no engine project | execution stage not built | AC-10 done |
| A7 providers below Core | project references present | distro projects and contracts | distro-specific end-to-end proof differs | AC-11 |
| A8–A10 authority, postconditions, secrets | destructive public gate closed | advisory IDs, unavailable recovery, no prepare/execute/verify | complete layout, custody, approval, recovery, replay and verification | AC-04–09 |

[[02 - Architecture/Invariant Conformance Matrix]] gives A1–A10 individually. The precise top risks are incomplete installer-consumed layout, unproven exact recovery, secret custody, no execution-bound approval, no cross-reboot migration journal and no postcondition verifier. Those blockers are reasons to leave destructive public commands unregistered, not evidence of an operational recovery workflow.

## Distribution support and validation

The [released 0.2-alpha](https://github.com/gillesduif/iGloo/releases) and [hardware findings](../reference/hardware-findings.md) record earlier physical runs for Fedora KDE, Mint Cinnamon and Debian. Current dirty-branch changes, especially Debian installer and first-boot work, need their own qualification; the old runs cannot certify them. Ubuntu remains in development with an unresolved installer storage holder problem in [its status record](https://github.com/gillesduif/iGloo/blob/main/distros/ubuntu/STATUS.md). [[08 - Distribution Providers/Distribution Support Matrix]] separates catalogue, provider code, fixture/VM/hardware evidence and release availability.

## Work, evidence and navigation

- **Canonical proposed work:** [[15 - Implementation/Master Implementation Checklist]]; [[00 - Home/Next Actions]] and [[00 - Home/Blockers]] link there without duplicating task checkboxes.
- **Release context:** [M13 detection/removal](https://github.com/gillesduif/iGloo/milestone/13), [M15 boot menu](https://github.com/gillesduif/iGloo/milestone/15), [M16 snapshot/rollback](https://github.com/gillesduif/iGloo/milestone/16), and [all milestones](https://github.com/gillesduif/iGloo/milestones), as indexed by [ROADMAP](https://github.com/gillesduif/iGloo/blob/main/ROADMAP.md). Vault AC items are proposals, not assigned to those milestones.
- **Latest validation for this vault:** source/project-reference and PDF review, static vault checks, native Obsidian graph inspection, and 44/44 focused Command tests; no installation, hardware mutation, production recovery proof or fresh whole-solution test run is implied. [[13 - Testing and Validation/Evidence Register]] records executed checks and prior evidence separately.
- **Explore:** [[00 - Home/Documentation Index]], [[00 - Home/Graph Guide]], [[00 - Home/Documentation Health]], [vault README](../README.md), [[20 - Reference/Vault Migration Map]], [[16 - Decisions/Architecture Source Conflicts]], [[17 - Risks and Issues/Risk Register]].

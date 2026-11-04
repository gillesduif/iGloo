---
title: Evidence Register
type: evidence-register
product: project
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: delivery
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/spec/command-contract/implementation-report.md, docs/reference/hardware-findings.md, docs/fleet/phase-2.md, scripts/validate_docs_vault.py]
aliases: []
tags: [igloo]
---

# Evidence Register

Evidence must name source revision/working-tree state, environment, command or observation, result and practical limit. This vault pass inspected the supplied 28-page PDF, branch/project references, handler registrations, schemas, relevant code and test definitions. Static validation results from this pass are recorded in [[00 - Home/Documentation Health]]. A source file or test definition is **CODE OBSERVATION**, not an executed result. On the dirty working tree, `dotnet test tests/Igloo.Command.Tests/Igloo.Command.Tests.csproj --no-restore --nologo -v:q` passed 44/44 tests on 10 October 2026. This is a bounded command-contract suite result, not whole-solution, installer, hardware, recovery or release qualification.

| Evidence | Scope | Limit |
| --- | --- | --- |
| [[13 - Testing and Validation/Community Repair 2026-10-25]] | Dated Community regression repairs, Windows/Linux checks, package and browser probe observations | Dirty local candidate; installation, reboot and recovery qualification remain separate |
| [Command implementation report](../spec/command-contract/implementation-report.md) | Dated restore/build/unit and Fleet Phase 0/1 checks on earlier working-tree increments | Historical results, not automatic validation of 10 October dirty bytes; no installation or live ISO acquisition in those reports |
| [Hardware findings](../reference/hardware-findings.md) | July–August physical runs and specific faults for Fedora/Mint/Debian | Earlier release-era conditions; do not generalize to new branch layout, Debian or recovery changes |
| [Fleet Phase 2](../fleet/phase-2.md) | Read-only snapshot acquisition and deterministic tests | Exact production recovery readiness and real endpoint execution remain unavailable |
| [[13 - Testing and Validation/Test Matrix]] | Required checks by invariant and failure mode | A plan for proof, not proof itself |

Do not merge fixture, simulated, VM, physical-hardware, release and production evidence into one “verified” label. Where independent readback or current execution is absent, leave validation partial/unverified. The canonical source paths and whitepaper sections are in [[20 - Reference/Requirements Traceability Matrix]].

---
title: Test Matrix
type: matrix
product: platform
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: delivery
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [tests/Igloo.Command.Tests/CommandBoundaryTests.cs, tests/Igloo.Command.Tests/CliProcessBoundaryTests.cs, tests/Igloo.Fleet.Tests/ArchitectureTests.cs, docs/reference/hardware-findings.md]
aliases: []
tags: [igloo]
---

# Test Matrix

**Test definition observed** means a source file was read; **executed evidence** requires a dated command and result in [[13 - Testing and Validation/Evidence Register]]. The PDF's §18 count of 1,495 is historical and cannot validate this dirty working tree. This matrix maps whitepaper §19 to present checks and missing qualification.

| Dimension | Present source or record | Current limit / required acceptance |
| --- | --- | --- |
| Protocol, schemas, errors, versions | `CommandBoundaryTests.cs`, JSON schemas, `CommandProtocol.cs` | Run current tests; add malformed/unknown-field/version/golden schema matrix for each capability version (A3/A4). |
| Real CLI stdout/transport | `CliProcessBoundaryTests.cs`, Cli.Client | Verify pure stdout, hello/accepted/result order, stderr separation, wrong binary/protocol, timeout and process death after acceptance (A4/A10). |
| Cross-client semantic parity | Narrow CLI human/machine and Fleet assessment tests | Compare identical typed request/state/authority through Community, One and Fleet as they migrate; do not equate a reference test with A2 (A2/A3). |
| Architecture dependencies | Fleet/command architecture tests and `.csproj` refs | Current Community/Fleet Host edges are permitted; add tests that fail on bypass only after safe transition (A1/A2/A6/A7). |
| Advisory identity and stale state | Target/allocation tests, command spec | Prove exact complete layout and provider/artifact binding; reject disk reorder/replacement, stale geometry and corruption (A8). |
| Secrets, approval, privilege | Current public contract avoids argv secrets | Protected lifecycle, expiry, one-shot consumption, log/Fleet leak scans and exact approval binding absent (A8/A10). |
| Journal, locks, retry, cancellation | ISO journal and process tests | No migration cross-reboot producer; inject crash, reboot, contention and unknown-outcome transport loss (A5). |
| Recovery and postconditions | Fleet Phase 2 read-only records | Exact WinRE/BCD/EFI/restoration proof unresolved; no migration verifier. Require independent postconditions (A8/A9). |
| Providers, VM and hardware | Provider fixtures, [hardware findings](../reference/hardware-findings.md) | Qualify current bytes on disposable VM and physical matrix per distro, firmware, BitLocker and Secure Boot conditions (A7/A9). |
| Release package and external client | Release notes and source-tree CLI locator | Verify actual installed CLI identity, packaging, public conformance and support; branch tests do not prove 0.2-alpha includes new CLI. |

Failure injection belongs on disposable machines only: disk full, ISO corruption, installer failure, power loss, renamed/reordered disks, encryption/firmware variants and failed recovery. [[15 - Implementation/Master Implementation Checklist]] AC-02–12 owns the work; [[02 - Architecture/Invariant Conformance Matrix]] maps it by invariant.

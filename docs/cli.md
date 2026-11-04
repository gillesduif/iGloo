---
title: iGloo CLI
type: product
area: cli
status: current
implementation: partial
tags:
  - igloo
  - topic/interface
aliases: []
reviewed: 2026-10-06
sources:
  - Igloo.sln
  - src/Igloo.Cli/Igloo.Cli/Program.cs
  - src/Igloo.Cli/Igloo.Cli/Igloo.Cli.csproj
  - src/Igloo.Command.Contracts/CommandProtocol.cs
  - src/Igloo.Core.Runtime/CommandDispatcher.cs
  - src/Igloo.Core.Runtime.Execution/IsoAcquireHandler.cs
  - src/Igloo.Core.Host/IglooHost.cs
  - src/Igloo.Community.App/App.xaml.cs
  - src/Igloo.Community.App/ViewModels/FileStagingViewModel.cs
---

# iGloo CLI

**Current subset:** CLI is the reference frontend for the [command contract V1](spec/command-contract/README.md). It supports `version`, `inspect`, `capabilities list`, `capabilities describe <id>`, `distros list`, `distros inspect <id>`, `distros compatibility <id>`, `migration inspect`, `migration plan <distro> [requested-bytes]`, `migration status <operation-id>`, `operations status <operation-id>`, `recovery inspect`, `iso acquire <id>`, and `invoke` (JSON request on stdin). Add `--json` for one machine result on stdout. CLI references only Contracts and Core.Host. ISO acquisition uses the existing verified service below Host; disk, boot, migration and recovery mutation commands are not registered.

## Current reality and milestones

The solution contains `Igloo.Cli`, `Igloo.Command.Contracts`, `Igloo.Core.Host`, the two internal runtime projects and command tests. The CLI targets `net10.0-windows10.0.19041.0`; distribution packaging and cross-version compatibility still need a release decision. The executable contract subset is defined in the linked specification. It does not yet provide an install flow.

| Step | Scope | State |
| --- | --- | --- |
| Read-only slice | Version, inspection, distro discovery and compatibility | implemented; local execution and fixture tests |
| Machine interface | `invoke` JSON envelope, capability discovery, deterministic exits | implemented for the registered subset |
| ISO acquisition | Pinned verification through the shared Core coordinator | implemented; fixture parity tested, live download not tested here |
| Planning | Advisory migration inspection and plan with a revision fingerprint; always non-executable | implemented as a limited V1 PLAN; exact target binding remains open |
| Apply/install | Explicit confirmation and trusted Platform execution after safety qualification | blocked on plan, orchestration and execution boundaries |

Human mode renders structured results and reports progress on stderr. Machine mode emits one JSON result on stdout with stable error and exit semantics. No password or installation command is accepted. [[architecture/shared-orchestration|Shared orchestration]] still requires a reviewed public mutation request and exact target/recovery binding before CLI can expose Community's existing private execution path. The unfinished [[ai|One]] shell can already consume discovery and typed read-only/planning results through Host.

## Inputs and secrets

Actual user choices are likely distro, Linux username, file/browser/app selection, target disk, dual boot versus replacement and Linux size. The Linux password is a separate sensitive input. Windows identity, locale, timezone, keyboard, hardware, disks, BitLocker, UEFI/Secure Boot and available browsers can be observed where supported; detection still needs validation and a visible chance to correct mistakes. The exact questionnaire and plan fields are undecided.

Never accept a Linux password as an ordinary command argument: shell history, process listings and logs can expose it. iGloo should collect secrets directly with masking and keep them out of JSON output and AI context. A machine caller may propose nonsecret choices, then hand control to iGloo for secret collection and final destructive confirmation. [[safety-and-evidence|Safety and evidence]] and [[ai|AI]] explain those boundaries.

## Contract decisions still needed

- Define a safe public migration plan and endpoint execution schema, including exact target binding, authority and cross-reboot state.
- Decide how automation requests a plan while a local user supplies secrets and confirms the exact observed target. Noninteractive destructive use is not authorized by this vision.
- Define packaging, signing, release/version alignment with Community and plugin compatibility before describing CLI as distributable.

For the implemented subset use [command contract V1](spec/command-contract/README.md).

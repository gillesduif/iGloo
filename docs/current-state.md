---
title: Current project state
type: status
area: direction
status: current
implementation: not-applicable
tags:
  - igloo
  - topic/status
aliases: []
reviewed: 2026-10-03
reviewed_commit: 5e4ebe846b488d8609612d37665aaefd4d203c88
sources:
  - Igloo.sln
  - README.md
  - ROADMAP.md
  - src/Igloo.Fleet.Contracts/PlanningProtocol.cs
  - src/Igloo.Fleet.Agent/Execution/PreCommitGate.cs
  - distros/debian/DebianPlugin.cs
  - distros/debian/distro.json
  - docs/architecture/debian-target-root-deployment.md
  - docs/fleet/phase-2.md
  - src/Igloo.Cli/Igloo.Cli/Program.cs
  - src/Igloo.Community.App/ViewModels/FileStagingViewModel.cs
---

# Current project state

Reviewed on 2026-10-03 at `5e4ebe846b488d8609612d37665aaefd4d203c88`, including the pre-existing working-tree changes noted in the [vault audit](vault/audit.md). This is a documentation snapshot, not a fresh test or deployment result.

**2026-10-06 command-boundary addendum:** The table below remains the 2026-10-03
historical snapshot. The [Phase 2 implementation report](spec/command-contract/implementation-report.md)
records the current Core.Host/CLI/One/Fleet Agent graph, V1 advisory command
subset and validation. In particular, the CLI is no longer a placeholder and
One now has a backend-connected shell without AI functionality.

| Capability | Implementation | Evidence and limit |
| --- | --- | --- |
| Community WPF host and shared services | code present | The solution and project references contain the wizard, Core, Preflight, Iso, Migration and UsbWriter. The [component map](shared-engine.md) records responsibilities. No install was run for this review. |
| Four peer products over Platform | architecturally decided target | ADR-013 ([[decisions/013-product-family-and-platform]]) records Community, CLI, AI and Fleet as peers. The actual solution still groups Shared and Distros separately; product and project structure are not fully aligned. |
| CLI | placeholder only | The untracked `Igloo.Cli` project appears in the working-tree solution and references shared libraries, but its only program prints `Hello, World!`. No proposed commands, JSON contract or install path work today. See [[cli]]. |
| AI | no implementation | The solution has an empty AI folder; no AI project or provider adapter was found. See [[ai]]. |
| Community workflow reuse | partial | `PluginArtifactWriter` is shared in Migration. `FileStagingViewModel` still sequences file staging, Wi-Fi, browser credentials, manifest, image and artifact writes. See [[architecture/shared-orchestration]]. |
| Conversational file rules | planned idea | `FileStagingRequest` accepts folder paths; it has no typed filters or exclusion preview. See [[architecture/migration-rules]]. |
| Fedora KDE and Mint catalog paths | partial | Both manifests say `available`, and the [hardware register](reference/hardware-findings.md) records earlier physical runs. The [ownership audit](architecture/community-installation-ownership.md) identifies later exact-target and ESP requirements; old runs do not validate this checkout. |
| Debian catalog and target-root path | blocked | The manifest still says `available`, but `DebianPlugin` rejects the legacy installer config, boot spec and agent payload. The [Debian deployment record](architecture/debian-target-root-deployment.md) describes partial, guarded lab work and missing production producers. |
| Ubuntu and Deepin | planned | Ubuntu is `in-development`; Deepin is `coming-soon` in their manifests. See [distribution status](distributions.md). |
| Fleet Phase 1 enrollment, assessment, dry-run, evidence and Prepared plans | partial | The [Fleet architecture](fleet/architecture.md) and code contain typed read-only work and planning. These are engineering capabilities; this review did not run a live Fleet demo or verify tenant operation. |
| Fleet remote migration execution | blocked | The work enum permits only `RunAssessment` and `RunMigrationDryRun`; Prepared plans have no execution endpoint. Production `RecoveryReadiness` is `ObservationUnavailable/NotImplemented`. See [Fleet Phase 2](fleet/phase-2.md). |
| Debian bounded selected-document lab result | partial | The [retained result](architecture/debian-target-root-deployment.md#selected-document-path-policy-and-canonical-lab-completion--2026-10-01) records four selected files, protected admission and exact teardown for that lab scope. It does not prove general Windows/NTFS migration, agent deployment, Enrollment or first-boot completion. |

## Decisions and open questions

- The earlier [[architecture/product-boundaries|Community/Fleet foundation]] remains one shared engine. The four-product peer model is the chosen target; it is not yet implemented by CLI or AI.
- [Local MigrationManifest](architecture/product-boundaries.md#execution-artifacts-are-not-fleet-records) can contain private migration data. Fleet's sanitized evidence is a different record; see [Fleet security](fleet/security.md).
- Current [Debian deployment](architecture/debian-target-root-deployment.md) needs independently observed agent installation, DeploymentContent and Enrollment producers before a first-boot outcome can be claimed.
- The [recovery snapshot design](architecture/recovery-snapshot-v1.md) and [Fleet Phase 2 record](fleet/phase-2.md) retain blockers for production Exact readiness. No read-only code or fixture alone closes them.

For the project plan and historical rationale, use the [direction map](direction.md). For contradictions found in older summaries, use the [vault audit](vault/audit.md).

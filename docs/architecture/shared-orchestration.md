---
title: Shared plan and orchestration boundary
type: architecture
area: shared
status: draft
implementation: partial
tags:
  - igloo
  - topic/architecture
  - topic/safety
aliases: []
reviewed: 2026-10-03
reviewed_commit: 5e4ebe846b488d8609612d37665aaefd4d203c88
sources:
  - src/Igloo.Community.App/ViewModels/FileStagingViewModel.cs
  - src/Igloo.Community.App/App.xaml.cs
  - src/Igloo.Core/Models/MigrationManifest.cs
  - src/Igloo.Core/Services/ManifestGeneratorService.cs
  - src/Igloo.Migration/PluginArtifactWriter.cs
  - src/Igloo.Core/Abstractions/IFileStagingService.cs
  - src/Igloo.Fleet.Contracts/PlanningProtocol.cs
---

# Shared plan and orchestration boundary

This remains a **design proposal** for public migration and installation commands between [[architecture/product-boundaries|peer frontends]] and trusted execution. The [command contract V1](../spec/command-contract/README.md) now implements read-only dispatch and shared ISO acquisition. The in-process `MigrationStagingCoordinator` owns Community's private staging sequence, but it is not a public capability and no `InstallationPlan` or `InstallationOrchestrator` has been established. Names below are candidate responsibilities, not settled APIs. Fleet's `PreparedMigrationPlan`, Core's `PreparationPlanV1`, `FileMigrationPlan` and local `MigrationManifest` already mean different things; none should be renamed or conflated without a compatibility review.

## Current split

`FileStagingViewModel` collects user choices and calls `IMigrationStagingWorkflow` from `Igloo.Core.Host`. Host delegates to `MigrationStagingCoordinator` in `Igloo.Core.Runtime.Execution`. That coordinator calls `IFileStagingService.StageAsync`, scans Wi-Fi, attaches Chromium credentials, generates the private `MigrationManifest`, stages wallpaper/account picture, writes it locally and calls `PluginArtifactWriter.WriteAsync`. It is not exposed in JSON because it handles secrets and local handoff state. `App.xaml.cs` now registers the local backend through Host and adds only WPF services. CLI also uses Host but does not offer migration staging until a reviewed public secret/authority/target contract exists. The public `migration.plan` is advisory only, with no executable authority.

## Proposed seams for review

| Responsibility | Candidate owner | Boundary to preserve |
| --- | --- | --- |
| Capture choices and observations | Frontend plus shared read-only collectors | UI/AI text is untrusted; observations can become stale. |
| Validate and preview an installation plan | Core contracts and shared validator, exact service location undecided | Typed supported choices, capability/version checks, target identity and risks before mutation. |
| Prepare migration artifacts | `MigrationStagingCoordinator` in the local execution runtime (implemented for Community) | Keep secrets and `MigrationManifest` local; no public staging command is registered. |
| Execute install | Existing trusted Preflight/Iso/Migration/UsbWriter services and guarded distro path | Revalidate target and consent at commit time; no frontend or plugin-owned arbitrary disk command. |
| Present progress/results | Each frontend | Presentation only; stable machine events require a separate contract. |

An `InstallationPlan` might be the reviewed pre-execution contract; `MigrationManifest` is currently the private execution/first-boot artifact and cannot simply serve as the public JSON preview. `FileMigrationPlan` currently records staging output and included folders, not user intent or filtering rules. [[architecture/migration-rules|Migration rule proposals]] need a validated scan result before preparation. Exact fields, identity binding, expiry, signing/hash policy and invalidation rules remain decisions.

The local composition extension is `IglooHost.AddLocalBackend`; standalone clients use `IglooHost.CreateAsync` or `CreateReadOnlyAsync`. Community adds its WPF services after Host registration. Fleet selects a read-only binding that does not register ISO acquisition as a command. Host does not return an `IServiceProvider` to product callers. Existing Core domain models remain visible transitively in-process, but product project files no longer name individual execution assemblies. The remaining design gap is a reviewed public mutation input, exact target binding and cross-reboot continuation, not service registration.

The [[safety-and-evidence|safety record]] and [[ai|AI trust boundary]] constrain further public command work. This note now records the implemented staging extraction and remaining plan/commit gap.

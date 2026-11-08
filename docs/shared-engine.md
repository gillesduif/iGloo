---
title: Shared engine and component responsibilities
type: hub
area: shared
status: current
implementation: not-applicable
tags:
  - igloo
  - topic/architecture
aliases: []
reviewed: 2026-10-03
reviewed_commit: 5e4ebe846b488d8609612d37665aaefd4d203c88
sources:
  - Igloo.sln
  - src/Igloo.Community.App/Igloo.Community.App.csproj
  - src/Igloo.Fleet.Agent/Igloo.Fleet.Agent.csproj
  - src/Igloo.Fleet.Web/Igloo.Fleet.Web.csproj
  - docs/architecture/product-boundaries.md
  - src/Igloo.Cli/Igloo.Cli/Program.cs
---

# Shared engine and component responsibilities

The [[architecture/product-boundaries|product boundary record]] is the detailed reference map and four-product target. In the inspected working-tree `Igloo.sln`, **Community**, **CLI**, **AI** and **Fleet** are sibling solution folders. Community contains one WPF project, CLI contains an untracked console stub, AI is empty, and Fleet contains six projects. **Shared** contains five libraries, **Distros** contains four plugin projects, and **Tests** contains seven test projects. The intended Platform groups Shared and Distros conceptually; there is no `Platform` solution folder or second engine yet. A solution entry proves structure, not completed behavior or a passing test run.

`Igloo.Core` is the Platform's central contract and model vocabulary; it should remain independent of Community, CLI, AI, Fleet and concrete distro implementations. Preflight, Iso, Migration and UsbWriter are shared capabilities with different consumers, not duplicate engines for each product. [[decisions/013-product-family-and-platform|ADR-013]] sets this target ownership.

| Component | Responsibility and consumer |
| --- | --- |
| `Igloo.Community.App` | WPF wizard and local orchestration; references Core, Preflight, Iso, Migration and UsbWriter. See [Community](community.md). |
| `Igloo.Cli` | Untracked placeholder with `Hello, World!`; project references shared modules, but no CLI capabilities yet. See [[cli]]. |
| iGloo AI | No project under the AI solution folder. The intended role is constrained intent translation; see [[ai]]. |
| `Igloo.Core` | Shared models, `IDistroPlugin`, manifest and plugin discovery. [ADR-004](decisions/004-plugin-architecture.md) explains the plugin choice. |
| `Igloo.Preflight` | Windows hardware/storage observations and Community preparation services. |
| `Igloo.Iso` | Download and verification services used by Community. The [safety model](reference/safety-model.md) records intended guards. |
| `Igloo.Migration` | Local file staging and plugin artifact writing. The private `MigrationManifest` is Community handoff state; see [data boundary](architecture/product-boundaries.md#execution-artifacts-are-not-fleet-records). |
| `Igloo.UsbWriter` | Community fallback USB-writing path. |
| Distribution plugins | Concrete plugins reference Core. Discovery is dynamic, not static App references to every distro; see [distribution map](distributions.md). |
| `Igloo.Fleet.*` | The organisation product's Agent and control-plane projects, in [gillesduif/iGloo-Fleet](https://github.com/gillesduif/iGloo-Fleet) since 2026-11-07 ([ADR-016](decisions/016-fleet-source-available-and-contract-apache.md)). The Agent consumes the Apache-2.0 `Igloo.Command.Contracts` and `Igloo.Cli.Client` packages and references no engine project. |

The [[architecture/shared-orchestration|shared orchestration proposal]] traces the remaining workflow in `FileStagingViewModel` and module-owned DI registrations still to design. [[architecture/migration-rules|Migration rules]] are a future contract, distinct from today's `FileMigrationPlan`. The [code guide](code/README.md) is a file-level tour. [Repository structure](reference/repository-structure.md) gives the layout, while the [architecture map](architecture.md) explains Community's stage-and-commit split. For changes to component boundaries, check project files and architecture tests; a prose diagram alone cannot prove the current reference graph.

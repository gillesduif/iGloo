# Product boundaries: current foundation and four-product target

## Current architectural direction (2026-10-03)

**Decided target:** [[community|Community]] (human-operated GUI), [[ai|One]]
(conversational client), [[fleet-map|Fleet Agent]], humans and independent tools
invoke [[cli|CLI]] through its versioned command contract. CLI alone composes
the local [[shared-engine|Core backend]]. Fleet Server remains an isolated
control plane. Distribution plugins remain below Core and independent of all
product frontends. [[decisions/013-product-family-and-platform|ADR-013]] records
the product family; this executable boundary is the current direction.

```mermaid
flowchart LR
  Community --> CLI
  One --> CLI
  FleetAgent --> CLI
  External[Humans / scripts / third parties] --> CLI
  CLI --> Host["Core.Host / Runtime"]
  Host --> Shared["Core, Preflight, Iso, Migration, UsbWriter"]
  Shared --> Distros["Distro plugins"]
```

**Current code (2026-10-06, CLI transition):** `Igloo.Core.Host` still composes
the local backend. `Igloo.Cli.Client` launches the actual `Igloo.Cli.exe` with
typed JSON Lines. One's unfinished shell and Fleet's development assessment
use this path. Community's preflight cleanup, ISO, private staging,
installation preparation, USB and Windows-maintenance calls still use Host
interfaces; Fleet's trusted dry-run still uses Host for private capacity and
provider facts. These paths require capability and parity work before their
Host references can be removed safely. Fleet Server has no Host, Runtime or
endpoint execution reference. The V1 contract exposes advisory
`migration.plan`, typed status and an explicitly unavailable recovery inspection
alongside the existing queries and ISO cache acquisition. No public installation
or recovery mutation follows from those descriptors. The [implementation
report](../spec/command-contract/implementation-report.md) records the exact
physical graph, remaining safety gaps and validation.

### Trust and execution ownership

Community UI input, CLI arguments, AI intent and Fleet instructions are
untrusted proposals. The Platform must validate a concrete, reviewable plan
against current observations, supported distro capabilities and exact target
identity before trusted services may act. The proposed
[[architecture/shared-orchestration|shared orchestration boundary]] explains
where plan validation and Community workflow extraction are still missing.
[[architecture/migration-rules|Migration rules]] are a separate planned input
model. Frontends may present or collect choices; they must not implement their
own partitioning, boot setup, ISO verification or distro installation logic.
Plugins declare supported behavior and supply distro-specific artifacts;
trusted iGloo services control and verify execution. Fleet's current
read-only planning protocol remains separate from any local installation plan.

### Historical Phase 1 baseline below

The rest of this note records the inspected Community/Fleet foundation at its
original review. Its two-host language and solution tree describe that phase,
not a claim that CLI and AI are already implemented. The old `Igloo.App` label
in the original-reference diagram names the pre-rename project; the current
project is `Igloo.Community.App`. See [[current-state]] for the current snapshot.

One canonical upstream repository and solution, `Igloo.sln`, contains two hosts
around the same migration engine. Community is the local, human-operated WPF
wizard. Fleet is endpoint assessment plus control-plane orchestration. It has no
remote migration execution in either Phase 0 or Phase 1.

Phase 1 preserves the project-reference graph below and Community behavior. It
adds certificate enrollment, SQLite planning persistence, typed read-only work,
evidence-bound approval and non-executable Prepared plans. Fleet.Web is now an
engineering operator CLI as well as a status client; it still references only
Contracts. See the [Phase 1 baseline](../fleet/phase-1-baseline.md) and
[current Fleet architecture](../fleet/architecture.md).

## Inspected baseline

Before edits: clean working tree; .NET SDK 10.0.401 installed on this Windows host;
restore/build succeeded and all 259 tests passed. The project targets are mixed:
Community uses net9.0-windows10.0.19041.0; Core, Iso, Migration and distros use
net8.0; Preflight and UsbWriter use net8.0-windows10.0.19041.0.
No framework or existing package version was upgraded.

The original **project-reference** graph was:

```mermaid
flowchart TD
    App["Igloo.App"] --> Core["Igloo.Core"]
    App --> Preflight["Igloo.Preflight"]
    App --> Iso["Igloo.Iso"]
    App --> Migration["Igloo.Migration"]
    App --> UsbWriter["Igloo.UsbWriter"]
    Preflight --> Core
    Iso --> Core
    Migration --> Core
    UsbWriter --> Core
    Distros["FedoraKde / Debian / LinuxmintCinnamon / Ubuntu"] --> Core
    Tests["Six test projects"] --> Subjects["Corresponding project under test"]
```

Distro discovery is dynamic through Core's DistroRegistry/DistroLoader, not an
App project reference to every distro. Ubuntu remains in development. Inspection
covered the solution, project files/references, shared abstractions and manifest,
WPF startup and view models, preflight and migration services, tests, distro
projects, architecture/reference documentation, workflows, installer/build
scripts, Directory.Build.props and .editorconfig. There is no checked-in
Directory.Packages file, NuGet configuration or global.json.

## Final solution folders

```text
Igloo.sln
├── Community
│   └── Igloo.Community.App
├── Fleet
│   ├── Igloo.Fleet.Contracts
│   ├── Igloo.Fleet.Domain
│   ├── Igloo.Fleet.Agent
│   ├── Igloo.Fleet.Server
│   ├── Igloo.Fleet.Persistence
│   └── Igloo.Fleet.Web
├── Shared
│   ├── Igloo.Core
│   ├── Igloo.Preflight
│   ├── Igloo.Iso
│   ├── Igloo.Migration
│   └── Igloo.UsbWriter
├── Distros
│   ├── Igloo.Distro.FedoraKde
│   ├── Igloo.Distro.Debian
│   ├── Igloo.Distro.LinuxmintCinnamon
│   └── Igloo.Distro.Ubuntu
└── Tests
    ├── Igloo.Community.App.Tests
    ├── Igloo.Core.Tests
    ├── Igloo.Preflight.Tests
    ├── Igloo.Iso.Tests
    ├── Igloo.Migration.Tests
    ├── Igloo.UsbWriter.Tests
    └── Igloo.Fleet.Tests
```

Source projects live in src/, distro projects in distros/, tests in tests/.

## Final dependencies

Arrows mean a direct ProjectReference. Each original test project references its
subject; Fleet.Tests references Agent, Server and Web.

```mermaid
flowchart TD
    Community["Igloo.Community.App"] --> Core["Igloo.Core"]
    Community --> Preflight["Igloo.Preflight"]
    Community --> Iso["Igloo.Iso"]
    Community --> Migration["Igloo.Migration"]
    Community --> UsbWriter["Igloo.UsbWriter"]
    Preflight --> Core
    Iso --> Core
    Migration --> Core
    UsbWriter --> Core
    Distros["Four distro projects"] --> Core
    Agent["Igloo.Fleet.Agent"] --> Core
    Agent --> Preflight
    Agent --> Contracts["Igloo.Fleet.Contracts"]
    Agent --> Domain["Igloo.Fleet.Domain"]
    Domain --> Contracts
    Server["Igloo.Fleet.Server"] --> Domain
    Server --> Persistence["Igloo.Fleet.Persistence"]
    Persistence --> Domain
    Web["Igloo.Fleet.Web"] --> Contracts
```

Agent references Domain to reuse eligibility policy; it does not reference unused
Iso, Migration or UsbWriter functionality. Server cannot reach endpoint services.
Contracts has no project dependencies. Domain owns storage interfaces; Persistence
implements them. ArchitectureTests enforce these direct reference boundaries and
absence of WPF in Fleet. There are no alternate engine implementations.

## Community compatibility and extraction

The App directory, project, root/C# namespaces, XAML classes and App.Tests became
Igloo.Community.App and Igloo.Community.App.Tests. References, friend assemblies,
analyzer file scopes, installer source paths and documentation follow the rename.

Preserved: AssemblyName iGloo (iGloo.exe/iGloo.dll), version 0.2-alpha, app.manifest
identity/elevation, Inno Setup AppId 7B4E9C2A-6F3D-4A1B-9E5C-2D8F0A3B6C71,
installation/shortcut names, %LOCALAPPDATA%/Igloo logs/staging paths, manifest JSON
schema/property names, plugin identities and existing configuration behavior.
No update channel or registry identity is changed.

Classification of code inspected in WPF:
- Views, controls, converters, design data, progress and navigation are UI-only.
- User selection, password/browser/Wi-Fi collection, wallpaper/account picture
  attachment and assembling wizard state are Community orchestration.
- Manifest generation, file staging, direct-install, USB and ISO services already
  reside in shared projects.
- Plugin installer-config/extra-file/first-boot-payload writing was reusable code
  inside FileStagingViewModel. It is now PluginArtifactWriter in Igloo.Migration;
  Community calls it, keeping rendering/write order, bytes and missing-file logs.
  The net8 writer materializes ReadOnlyMemory to byte arrays because the original
  net9 File.WriteAllBytesAsync overload is unavailable in net8.

Community's composition root remains in App.xaml.cs. Agent's composition root
registers only the existing WindowsPreflightChecker plus assessment/identity
services. A new shared DI module is unnecessary for this one registration.

## Execution artifacts are not Fleet records

MigrationManifest remains local execution/handoff state, containing private
migration information. Fleet's MigrationEvidence contains an allowlisted
AssessmentResult plus server receipt time. Fleet never serializes a manifest,
loads user migration state, or collects browser/Wi-Fi credentials.

See [Fleet architecture](../fleet/architecture.md) and
[Phase 0 workflow](../fleet/phase-0.md).

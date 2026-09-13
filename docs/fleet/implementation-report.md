# Community/Fleet foundation implementation report

Historical Phase 0 verification report, recorded before local commit organization.
See [Phase 1](phase-1-implementation-report.md) for the subsequent trusted planning work.

## Result

Implemented the Community/Fleet product boundary and a working read-only Fleet
Phase 0. Igloo.sln remains the sole canonical solution. No changes were committed
or pushed. No migration, partition modification, ISO deployment or reboot was run.

The complete [project tree and dependency diagrams](../architecture/product-boundaries.md)
include the graph recorded before implementation and the final direct references.

## Repository changes

Solution folders: Community, Fleet, Shared, Distros, Tests.

```text
Community: Igloo.Community.App
Fleet:     Igloo.Fleet.Contracts, Igloo.Fleet.Domain, Igloo.Fleet.Agent,
           Igloo.Fleet.Server, Igloo.Fleet.Persistence, Igloo.Fleet.Web
Shared:    Igloo.Core, Igloo.Preflight, Igloo.Iso, Igloo.Migration, Igloo.UsbWriter
Distros:   Igloo.Distro.FedoraKde, Igloo.Distro.Debian,
           Igloo.Distro.LinuxmintCinnamon, Igloo.Distro.Ubuntu
Tests:     Igloo.Community.App.Tests, Igloo.Core.Tests, Igloo.Preflight.Tests,
           Igloo.Iso.Tests, Igloo.Migration.Tests, Igloo.UsbWriter.Tests,
           Igloo.Fleet.Tests
```

Final dependency summary (arrows mean project references):

```text
Community.App -> Core, Preflight, Iso, Migration, UsbWriter
Preflight / Iso / Migration / UsbWriter / all four distros -> Core
Fleet.Agent -> Fleet.Contracts, Fleet.Domain, Core, Preflight
Fleet.Domain -> Fleet.Contracts
Fleet.Server -> Fleet.Domain, Fleet.Persistence
Fleet.Persistence -> Fleet.Domain
Fleet.Web -> Fleet.Contracts
```

Core and Fleet.Contracts have no project dependencies. Existing tests reference
their corresponding project; Fleet.Tests references Agent, Server and Web.

Added immutable protocol/evidence DTOs, migration lifecycle and eligibility rules,
replaceable identity/authentication/storage seams, a headless one-shot Agent,
authenticated loopback API, bounded in-memory storage, a read-only Web status
client and a repeatable local demonstration script. Architecture tests enforce the
product reference boundaries.

## Community rename and shared extraction

Renamed App/App.Tests directories, projects, namespaces, XAML declarations and
friend references; updated solution, docs, installer source paths and analyzer
scopes. Kept AssemblyName iGloo, version 0.2-alpha, app.manifest contents, installer
AppId, installation/shortcut identity and LocalApplicationData/Igloo storage.
Manifest JSON and distro plugin identities are unchanged.

Moved only the reusable plugin-generated installer/extra/first-boot-file writing
routine from FileStagingViewModel into Igloo.Migration.PluginArtifactWriter.
A regression test verifies bytes, directories, passed manifest and call ordering.
Community keeps its existing composition root, selection flow and migration
services. Fleet Phase 0 never calls the extracted writer.

## What works end to end

scripts/test-fleet-phase0.ps1 starts the development Server, runs the actual
Windows Agent, registers identity/capability/version, sends heartbeat, invokes
WindowsPreflightChecker through IPreflightChecker, submits sanitized evidence and
retrieves it through the API. It then stops its Server process.

The final real-machine demonstration passed. It returned NeedsReview because
BitLocker status was Unknown; this is valid assessment evidence, not an execution
failure. No migration readiness guarantee or approval is inferred.

See [exact development commands](phase-0.md).

## Security and privacy

Outbound evidence contains UUIDs, versions/schema/protocol, timestamps, RAM/disk
counts, fixed check identifiers/statuses and eligibility/outcome. No raw findings,
hardware names, disk labels, paths, user files, credentials, Wi-Fi secrets, full
manifest or exception text are mapped. Unknown JSON properties are rejected.

The local Server requires a development token on every route and binds only IPv4
loopback. Endpoint overrides/config reload cannot expose it network-wide. Agent
disables redirects and proxies. This is explicitly shared-token development
authentication, not certificate enrollment or per-device authorization.

## Verification

Executed from the repository root on Windows with installed SDK 10.0.401; existing
target frameworks and major dependencies are unchanged.

Baseline, before modifications:

```powershell
dotnet restore Igloo.sln
dotnet build Igloo.sln --no-restore
dotnet test Igloo.sln --no-build --no-restore
```

All succeeded: 259 tests, no pre-existing test/build failures.

Staged checks:

```powershell
dotnet build Igloo.sln
dotnet test Igloo.sln --no-build
dotnet test tests/Igloo.Fleet.Tests
dotnet test Igloo.sln
```

Build/test checks followed the rename, skeleton, contracts/domain, Agent and Server
stages. Integration tests were then added for the Web/API surface. Intermediate
issues were fixed: renamed .editorconfig scope, immutable collection test equality,
net8 byte-writing overload, and analyzer feedback. These were implementation
issues, not pre-existing failures. Failed-build test runs using older binaries
were not used as final verification.

Final authoritative checks:

```powershell
dotnet restore Igloo.sln
dotnet build Igloo.sln -c Release --no-restore -warnaserror
dotnet test Igloo.sln -c Release --no-build --no-restore
./scripts/test-fleet-phase0.ps1
git diff --check
```

Restore succeeded. Release build: **zero warnings, zero errors**. Tests:
**278 passed, zero failed, zero skipped**: Core 65, Preflight 74, Iso 19,
Migration 21, UsbWriter 23, Community.App 58, Fleet 18. The real Windows
demonstration passed twice, including once after final protocol serialization
changes. Diff check found no whitespace errors (Git reports normal LF/CRLF
conversion notices). Application manifest comparison matched the baseline.

Logs remain locally under test-logs/fleet-baseline-*, fleet-final-* and
fleet-demo-*. They are development artifacts, not committed source.

## Deviations and remaining risks

- The repository already mixes net8 libraries and a net9 WPF host; Fleet follows
  net8 and a Windows-specific Agent target without upgrading the repository.
- Preflight also contains destructive services. Fleet registers only the existing
  read-only checker; no shared project relocation or second engine was needed.
- Agent references Domain for one eligibility policy. No unused migration/ISO/USB
  dependencies were added.
- Fleet.Web is a compiling status client library, with API inspection as the
  engineering surface. No new frontend framework/dashboard was introduced.
- Assessment is Agent-initiated and one-shot; no server command queue or remote
  destructive operation exists.
- No production certificate identity, authorization, persistent evidence store,
  offline spool/retry or installed Windows service is provided.
- Existing probes are best effort and have incomplete error/completeness metadata;
  distro-specific eligibility is outside Phase 0.
- Installer upgrade and interactive/destructive Community VM migration were not
  exercised. Runtime identity was checked and all Community/shared tests pass.
- The extracted writer retains existing trusted-plugin path and missing-file
  behavior; plugin payload hardening is a separate concern.

## Recommended Phase 1

Design certificate-backed enrollment/authorization and durable evidence first.
Then add migration profiles, a remote dry-run, explicit eligibility evidence and
operator approval, ending in a prepared-but-not-executed job. Do not move directly
to remote or mass destructive migration.

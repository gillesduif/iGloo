# Phase 1 inspected baseline

At the start of Phase 1, the uncommitted Phase 0 working tree was the starting
point, rather than HEAD's pre-Fleet tree. No existing work was reverted.
Inspection confirmed this graph:

```text
Community.App -> Core, Preflight, Iso, Migration, UsbWriter
Preflight / Iso / Migration / UsbWriter / four distro plugins -> Core
Fleet.Agent -> Fleet.Contracts, Fleet.Domain, Core, Preflight
Fleet.Domain -> Fleet.Contracts
Fleet.Server -> Fleet.Domain, Fleet.Persistence
Fleet.Persistence -> Fleet.Domain
Fleet.Web -> Fleet.Contracts
```

Core and Fleet.Contracts have no project references. Architecture tests enforce
these boundaries. Agent uses IPreflightChecker and no destructive service.
IDistroPlugin.CheckCompatibility is the existing read-only target-policy hook.
The existing checker also supplies read-only disk sizing data; planning need not
invoke partition, installer, USB, file-staging or boot APIs.

Baseline commands (before Phase 1 source edits):

```powershell
dotnet restore Igloo.sln
dotnet build Igloo.sln -c Release --no-restore -warnaserror
dotnet test Igloo.sln -c Release --no-build --no-restore
./scripts/test-fleet-phase0.ps1
```

All passed: 278 tests; zero build warnings/errors; real Windows Phase 0 assessment
submitted/retrieved with NeedsReview. Logs: test-logs/phase1-baseline-*.
Existing identity is per-user UUID, authentication is loopback shared token,
storage is bounded in-memory, and Fleet.Web is a typed status client library.
Phase 1 extends these seams in a separate trusted protocol/mode.

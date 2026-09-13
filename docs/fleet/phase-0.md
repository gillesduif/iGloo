# Run Fleet Phase 0 locally

Use Windows and the repository's existing .NET 8/9 SDK/runtime prerequisites.
The Agent requires Windows; the control-plane libraries/server are platform-neutral.
Run from the repository root. No administrator elevation is requested by Fleet;
unavailable protected probes require review.

## Build and test

```powershell
dotnet restore Igloo.sln
dotnet build Igloo.sln -c Release --no-restore -warnaserror
dotnet test Igloo.sln -c Release --no-build --no-restore
```

## Automated local demonstration

```powershell
./scripts/test-fleet-phase0.ps1 -Configuration Release
```

This starts a hidden loopback Server with a generated development token, runs the
real Windows Agent/preflight, retrieves its stored assessment and stops the Server.
It refuses to use a port already occupied. Evidence and logs go to test-logs/.
The token is never printed and the previous environment value is restored.
The persistent local development UUID file remains for subsequent runs.

This creates only the local identity and development logs/evidence; it never
modifies partitions, deploys an ISO, stages a migration or reboots.

## Two-terminal workflow

In terminal 1, generate a token. Copy its value privately into terminal 2's
environment (do not commit it or put it in a URL):

```powershell
$env:IGLOO_FLEET_DEV_TOKEN = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
dotnet run --project src/Igloo.Fleet.Server -c Release --no-build -- --development-local
```

In terminal 2, use the same token value:

```powershell
$env:IGLOO_FLEET_DEV_TOKEN = '<same generated token from terminal 1>'
dotnet run --project src/Igloo.Fleet.Agent -c Release --no-build -- --development-local
$headers = @{ Authorization = 'Bearer ' + $env:IGLOO_FLEET_DEV_TOKEN }
Invoke-RestMethod http://127.0.0.1:5187/health -Headers $headers
# Use the assessment and device IDs printed by Agent:
Invoke-RestMethod http://127.0.0.1:5187/assessments/<assessment-id> -Headers $headers | ConvertTo-Json -Depth 8
Invoke-RestMethod http://127.0.0.1:5187/agents/<device-id> -Headers $headers | ConvertTo-Json -Depth 8
```

Stop the Server with Ctrl+C. All its in-memory evidence then disappears.

## API surface

All requests require Authorization: Bearer <development-token>.
POST bodies use application/json.

| Method | Route | Result |
|---|---|---|
| GET | /health | Protocol and development-only marker |
| POST | /agents | Register/re-register identity, versions and capabilities |
| GET | /agents/{deviceId} | Registration and latest heartbeat time |
| POST | /heartbeats | Update known identity/capabilities; 204 |
| POST | /assessments | Validate and store immutable evidence; 201 |
| GET | /assessments/{assessmentId} | Sanitized assessment |

400 distinguishes invalid requests and unsupported protocol/Agent versions;
401 indicates failed development authentication or unknown identity; 404 means
not found; 409 means identity/evidence conflict or development storage capacity.
Bodies use FleetError. Request bodies are limited to 16 KiB.
There are no migration execution endpoints.

Fleet.Web compiles as a minimal read-only FleetStatusClient library. API inspection
is the engineering surface for this phase; no separate UI server is needed.

See [architecture and security limitations](architecture.md) and
[product boundaries](../architecture/product-boundaries.md).

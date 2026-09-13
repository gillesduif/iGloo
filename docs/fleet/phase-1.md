# Fleet Phase 1 operations

Phase 1 provides trusted read-only planning. It stops at Prepared; it cannot run
migration commands. Phase 0 remains available via its separate development mode.

See the [implementation report](phase-1-implementation-report.md) for final test
totals, the real Windows demonstration, deviations and remaining risks.

## Verify and demonstrate

From the repository root on Windows with existing .NET 8/9 prerequisites:

```powershell
dotnet restore Igloo.sln
dotnet build Igloo.sln -c Release --no-restore -warnaserror
dotnet test Igloo.sln -c Release --no-build --no-restore
./scripts/test-fleet-phase1.ps1
./scripts/test-fleet-phase0.ps1
git diff --check
```

The Phase 1 script creates a fresh private test-logs/phase1-demo-<uuid> directory,
initializes protected CA/TLS keys and SQLite, starts a hidden HTTPS Server,
creates/consumes an enrollment token, runs the actual Windows checker twice
(assessment and dry-run), records explicit engineering approval if allowed,
prepares a non-executable plan, restarts Server and verifies durable inputs/hashes.
It stops processes and restores environment variables. Artifacts remain locally
for review. The operator secret is not passed into Agent processes.

A genuine Blocked/Unknown result stops the demo before approval. It is not bypassed
to produce a successful screenshot. See [dry-run semantics](dry-run.md) for
unknown disk sizing and NeedsReview policy.

## Initialize/start a Server

```powershell
$env:IGLOO_FLEET_SERVER_DATA = "$env:LOCALAPPDATA/Igloo/FleetServer"
dotnet run --project src/Igloo.Fleet.Server -c Release --no-build -- --initialize-trust
$env:IGLOO_FLEET_OPERATOR_SECRET = '<securely generated secret of at least 32 characters>'
dotnet run --project src/Igloo.Fleet.Server -c Release --no-build -- --trusted
```

Initialization creates schema 1 and protected keys. Re-initialization refuses to
overwrite trust. Startup migrates/checks the database without discarding records.

Default TLS name localhost, bind 127.0.0.1, port 5188. For an explicit network
pilot set IGLOO_FLEET_TLS_NAME before initialization and
IGLOO_FLEET_LISTEN_ADDRESS before startup. Use that TLS name in every client URI.
Do not use a proxy/header to impersonate client certificates.

## Operator terminal

Provision the public CA PEM independently to this principal first.

```powershell
$env:IGLOO_FLEET_SERVER_URI = 'https://localhost:5188/'
$env:IGLOO_FLEET_CA_CERT = "$env:LOCALAPPDATA/Igloo/FleetServer/fleet-root.pem"
$env:IGLOO_FLEET_OPERATOR_SECRET = '<same separate operator secret>'
$web = 'src/Igloo.Fleet.Web/bin/Release/net8.0/Igloo.Fleet.Web.dll'
'{"lifetimeMinutes":10}' | Set-Content token-request.json
$enrollment = dotnet $web POST /v2/operator/enrollment-tokens token-request.json | ConvertFrom-Json
# Deliver $enrollment.token privately to the intended Agent once. Never log/commit it.
dotnet $web GET /v2/operator/enrollment-tokens
dotnet $web GET /v2/operator/devices
```

To revoke an unused token:

```powershell
dotnet $web POST "/v2/operator/enrollment-tokens/$($enrollment.tokenId)/revoke"
```

## Agent terminal

Do not set the operator secret in this process. Provision the public CA file
through a trusted channel and use the exact HTTPS name in its SAN.

```powershell
$env:IGLOO_FLEET_SERVER_URI = 'https://localhost:5188/'
$env:IGLOO_FLEET_CA_CERT = '<provisioned public fleet-root.pem path>'
$env:IGLOO_FLEET_AGENT_DATA = "$env:LOCALAPPDATA/Igloo/Fleet/trusted"
$env:IGLOO_FLEET_DISTROS = (Resolve-Path distros).Path
$env:IGLOO_FLEET_ENROLLMENT_TOKEN = '<one-time token delivered privately>'
dotnet run --project src/Igloo.Fleet.Agent -c Release --no-build -- --enroll
$env:IGLOO_FLEET_ENROLLMENT_TOKEN = $null
# Subsequent one-shot heartbeat/spool/pull cycles:
dotnet run --project src/Igloo.Fleet.Agent -c Release --no-build -- --trusted
```

No inbound Agent listener or Windows service is installed.

## Profile, work, review and approval

Back in the operator terminal:

```powershell
'{"name":"Debian feasibility","distroId":"debian","minimumAvailableBytes":21474836480,"requireSecureBoot":false}' | Set-Content profile.json
$profile = dotnet $web POST /v2/operator/profiles profile.json | ConvertFrom-Json
$devices = dotnet $web GET /v2/operator/devices | ConvertFrom-Json
$deviceId = $devices[0].identity.deviceId
@{deviceId=$deviceId; type=1; profileRevisionId=$profile.revisionId; lifetimeMinutes=60} |
    ConvertTo-Json | Set-Content work.json
$work = dotnet $web POST /v2/operator/work work.json | ConvertFrom-Json
# Now run one --trusted Agent cycle in its terminal.
$history = dotnet $web GET /v2/operator/evidence | ConvertFrom-Json
$evidence = $history | Where-Object dryRunId -eq $work.workItemId
$evidence | ConvertTo-Json -Depth 12
```

Work type 0 is RunAssessment (profileRevisionId=null); type 1 is RunMigrationDryRun.
Eligibility: Eligible=0, NeedsReview=1, Blocked=2, Unknown=3.
Review exact evidence/reasons before the following explicit planning approval.
Blocked/Unknown will be rejected.

```powershell
@{dryRunId=$evidence.dryRunId; evidenceHash=$evidence.evidenceHash;
  profileRevisionId=$profile.revisionId; decisionId=$evidence.decision.decisionId;
  reviewReason='<specific review reason for any NeedsReview uncertainty>'} |
    ConvertTo-Json | Set-Content approval.json
$approval = dotnet $web POST /v2/operator/approvals approval.json | ConvertFrom-Json
@{approvalId=$approval.approvalId} | ConvertTo-Json | Set-Content prepare.json
dotnet $web POST /v2/operator/plans prepare.json
dotnet $web GET /v2/operator/plans
dotnet $web GET /v2/operator/audit
# Revocation invalidates the prepared plan:
dotnet $web POST "/v2/operator/approvals/$($approval.approvalId)/revoke"
```

A profile update appends a revision:

```powershell
dotnet $web POST "/v2/operator/profiles/$($profile.profileId)/revisions" profile.json
```

Disable/revoke the Agent (Active=0, Disabled=1, Revoked=2):

```powershell
'{"status":2}' | Set-Content trust.json
dotnet $web POST "/v2/operator/devices/$deviceId/trust" trust.json
```

Views are GET /v2/operator/devices, enrollment-tokens, profiles, work, evidence,
approvals, plans and audit. Fleet.Web is an engineering CLI plus typed status
client; it has no commercial dashboard or execution button.
All POST JSON rejects unexpected properties. No manual database editing is needed.

Read [security limitations](security.md), [enrollment recovery](enrollment.md),
[persistence](persistence.md), [profiles](migration-profiles.md) and
[dry-run/approval rules](dry-run.md) before a network pilot.

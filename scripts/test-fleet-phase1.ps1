param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$dotnet = (Get-Command dotnet).Source
$serverDll = Join-Path $repoRoot "src/Igloo.Fleet.Server/bin/$Configuration/net8.0/Igloo.Fleet.Server.dll"
$agentDll = Join-Path $repoRoot "src/Igloo.Fleet.Agent/bin/$Configuration/net8.0-windows10.0.19041.0/Igloo.Fleet.Agent.dll"
$webDll = Join-Path $repoRoot "src/Igloo.Fleet.Web/bin/$Configuration/net8.0/Igloo.Fleet.Web.dll"
foreach ($binary in @($serverDll, $agentDll, $webDll)) {
    if (!(Test-Path -LiteralPath $binary)) { throw "Build the solution in $Configuration before running the demo." }
}
if (Get-NetTCPConnection -LocalPort 5188 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 5188 is already in use.' }
$demoDirectory = Join-Path $repoRoot ('test-logs/phase1-demo-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $demoDirectory | Out-Null
$savedEnvironment = @{}
foreach ($name in @('IGLOO_FLEET_SERVER_DATA', 'IGLOO_FLEET_AGENT_DATA', 'IGLOO_FLEET_CA_CERT',
    'IGLOO_FLEET_OPERATOR_SECRET', 'IGLOO_FLEET_ENROLLMENT_TOKEN', 'IGLOO_FLEET_SERVER_URI', 'IGLOO_FLEET_DISTROS',
    'IGLOO_FLEET_TLS_NAME', 'IGLOO_FLEET_LISTEN_ADDRESS')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$serverProcess = $null
$script:serverStarts = 0
function Start-DemoServer {
    $script:serverStarts++
    $options = @{
        FilePath = $dotnet
        ArgumentList = @(('"' + $serverDll + '"'), '--trusted')
        WorkingDirectory = $repoRoot
        WindowStyle = 'Hidden'
        PassThru = $true
        RedirectStandardOutput = (Join-Path $demoDirectory "server-$script:serverStarts.log")
        RedirectStandardError = (Join-Path $demoDirectory "server-$script:serverStarts-error.log")
    }
    $process = Start-Process @options
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw 'Trusted server exited during startup; inspect demo logs.' }
        if (Get-NetTCPConnection -LocalPort 5188 -State Listen -ErrorAction SilentlyContinue) { return $process }
        Start-Sleep -Milliseconds 250
    }
    Stop-Process -Id $process.Id
    throw 'Trusted server did not start.'
}
function Invoke-Operator([string]$method, [string]$route, $body = $null) {
    $commandArgs = @($webDll, $method, $route)
    if ($null -ne $body) {
        $bodyPath = Join-Path $demoDirectory 'operator-request.json'
        $body | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $bodyPath
        $commandArgs += $bodyPath
    }
    $response = & $dotnet @commandArgs
    if ($LASTEXITCODE -ne 0) { throw "Operator request failed: $method $route" }
    if ($response) { return ($response -join [Environment]::NewLine | ConvertFrom-Json) }
}
function Invoke-Agent([string]$mode) {
    # The operator credential is never inherited by the Agent process.
    $operatorCredential = $env:IGLOO_FLEET_OPERATOR_SECRET
    $env:IGLOO_FLEET_OPERATOR_SECRET = $null
    try {
        & $dotnet $agentDll $mode
        if ($LASTEXITCODE -ne 0) { throw 'Trusted Agent cycle failed; retained results are in its protected metadata directory.' }
    } finally { $env:IGLOO_FLEET_OPERATOR_SECRET = $operatorCredential }
}
try {
    # Enforce source/project safety boundaries before invoking the actual hardware checker.
    & $dotnet test (Join-Path $repoRoot 'tests/Igloo.Fleet.Tests') -c $Configuration --no-build --filter FullyQualifiedName~Phase1SafetyTests | Out-File (Join-Path $demoDirectory 'safety-test.log')
    if ($LASTEXITCODE -ne 0) { throw 'Phase 1 destructive-service boundary assertion failed.' }
    $env:IGLOO_FLEET_SERVER_DATA = Join-Path $demoDirectory 'server'
    $env:IGLOO_FLEET_AGENT_DATA = Join-Path $demoDirectory 'agent'
    $env:IGLOO_FLEET_CA_CERT = Join-Path $env:IGLOO_FLEET_SERVER_DATA 'fleet-root.pem'
    $env:IGLOO_FLEET_DISTROS = Join-Path $repoRoot 'distros'
    $env:IGLOO_FLEET_SERVER_URI = 'https://localhost:5188/'
    $env:IGLOO_FLEET_TLS_NAME = 'localhost'
    $env:IGLOO_FLEET_LISTEN_ADDRESS = '127.0.0.1'
    $env:IGLOO_FLEET_OPERATOR_SECRET = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
    & $dotnet $serverDll --initialize-trust
    if ($LASTEXITCODE -ne 0) { throw 'Trust initialization failed.' }
    $serverProcess = Start-DemoServer
    $token = Invoke-Operator POST '/v2/operator/enrollment-tokens' @{ lifetimeMinutes = 10 }
    $env:IGLOO_FLEET_ENROLLMENT_TOKEN = $token.token
    Invoke-Agent '--enroll'
    $env:IGLOO_FLEET_ENROLLMENT_TOKEN = $null
    $token = $null
    $devices = @(Invoke-Operator GET '/v2/operator/devices')
    if ($devices.Count -ne 1) { throw 'Expected one enrolled device.' }
    $deviceId = $devices[0].identity.deviceId
    $profile = Invoke-Operator POST '/v2/operator/profiles' @{
        name = 'Phase 1 Debian planning demonstration'
        distroId = 'debian'
        minimumAvailableBytes = 21474836480
        requireSecureBoot = $false
    }
    $assessmentWork = Invoke-Operator POST '/v2/operator/work' @{
        deviceId = $deviceId; type = 0; profileRevisionId = $null; lifetimeMinutes = 30
    }
    Invoke-Agent '--trusted'
    $work = Invoke-Operator POST '/v2/operator/work' @{
        deviceId = $deviceId; type = 1; profileRevisionId = $profile.revisionId; lifetimeMinutes = 30
    }
    Invoke-Agent '--trusted'
    $history = @(Invoke-Operator GET '/v2/operator/evidence')
    $evidence = $history | Where-Object dryRunId -eq $work.workItemId
    if ($null -eq $evidence) { throw 'Dry-run evidence missing.' }
    Write-Output ('Eligibility=' + $evidence.decision.status + '; Reasons=' + ($evidence.decision.reasons -join ','))
    if ($evidence.decision.status -notin @(0, 1)) {
        $evidence | ConvertTo-Json -Depth 14 | Set-Content (Join-Path $demoDirectory 'blocked-evidence.json')
        throw 'Current machine is Blocked/Unknown. No approval or plan created; inspect evidence. Never override this safety gate.'
    }
    # Explicit engineering approval of a NON-EXECUTABLE plan, retaining any NeedsReview reasons.
    $approval = Invoke-Operator POST '/v2/operator/approvals' @{
        dryRunId = $evidence.dryRunId; evidenceHash = $evidence.evidenceHash
        profileRevisionId = $profile.revisionId; decisionId = $evidence.decision.decisionId
        reviewReason = 'Reviewed Phase 1 read-only demonstration evidence. Unknown probes require revalidation before any future execution.'
    }
    $plan = Invoke-Operator POST '/v2/operator/plans' @{ approvalId = $approval.approvalId }
    if ($plan.status -ne 0 -or $plan.safetyBoundary -notlike 'Non-executable*') { throw 'Plan did not stop at Prepared.' }
    Stop-Process -Id $serverProcess.Id
    $serverProcess.WaitForExit()
    $serverProcess = Start-DemoServer
    $restoredPlan = @(Invoke-Operator GET '/v2/operator/plans') | Where-Object planId -eq $plan.planId
    $restoredEvidence = @(Invoke-Operator GET '/v2/operator/evidence') | Where-Object dryRunId -eq $evidence.dryRunId
    $restoredProfiles = @(Invoke-Operator GET '/v2/operator/profiles')
    if ($restoredPlan.evidenceHash -ne $plan.evidenceHash -or $restoredEvidence.evidenceHash -ne $evidence.evidenceHash -or
        $restoredPlan.status -ne 0 -or $restoredProfiles[0].revisionId -ne $profile.revisionId) {
        throw 'Durable restart verification failed.'
    }
    @{ Plan = $restoredPlan; Evidence = $restoredEvidence; Profile = $profile; AssessmentCount = $history.Count; Safety = 'PreparedOnly' } |
        ConvertTo-Json -Depth 16 | Set-Content (Join-Path $demoDirectory 'verified-plan.json')
    Write-Output "PASS: mTLS enrollment, actual preflight/dry-run, explicit approval and Prepared plan $($plan.planId)."
    Write-Output "PASS: server restart preserved evidence hashes/profile/plan. No execution capability exists. Artifacts: $demoDirectory"
} finally {
    if ($null -ne $serverProcess -and !$serverProcess.HasExited) { Stop-Process -Id $serverProcess.Id }
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name]) }
}

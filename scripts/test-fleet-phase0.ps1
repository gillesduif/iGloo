param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$serverDll = Join-Path $repoRoot "src/Igloo.Fleet.Server/bin/$Configuration/net8.0/Igloo.Fleet.Server.dll"
$agentDll = Join-Path $repoRoot "src/Igloo.Fleet.Agent/bin/$Configuration/net8.0-windows10.0.19041.0/Igloo.Fleet.Agent.dll"
if (!(Test-Path -LiteralPath $serverDll) -or !(Test-Path -LiteralPath $agentDll)) {
    throw "Build Igloo.sln -c $Configuration before running this script."
}
if (Get-NetTCPConnection -LocalPort 5187 -State Listen -ErrorAction SilentlyContinue) {
    throw 'Port 5187 is already in use. Stop the existing server before running the isolated demonstration.'
}
$logDirectory = Join-Path $repoRoot 'test-logs'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$previousToken = $env:IGLOO_FLEET_DEV_TOKEN
$env:IGLOO_FLEET_DEV_TOKEN = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$serverProcess = $null
try {
    $startOptions = @{
        FilePath = (Get-Command dotnet).Source
        ArgumentList = @(('"' + $serverDll + '"'), '--development-local')
        WorkingDirectory = $repoRoot
        WindowStyle = 'Hidden'
        PassThru = $true
        RedirectStandardOutput = (Join-Path $logDirectory 'fleet-demo-server.log')
        RedirectStandardError = (Join-Path $logDirectory 'fleet-demo-server-error.log')
    }
    $serverProcess = Start-Process @startOptions
    $headers = @{ Authorization = 'Bearer ' + $env:IGLOO_FLEET_DEV_TOKEN }
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($serverProcess.HasExited) { throw 'Fleet server exited during startup.' }
        try {
            $health = Invoke-RestMethod 'http://127.0.0.1:5187/health' -Headers $headers
            $ready = $health.developmentOnly
            if ($ready) { break }
        } catch { Start-Sleep -Milliseconds 250 }
    }
    if (!$ready) { throw 'Fleet server did not become ready.' }
    $agentOutput = & dotnet $agentDll --development-local 2>&1
    $agentExit = $LASTEXITCODE
    $agentOutput | Set-Content -LiteralPath (Join-Path $logDirectory 'fleet-demo-agent.log')
    if ($agentExit -ne 0) { throw "Fleet Agent failed with exit $agentExit." }
    $match = [regex]::Match(($agentOutput -join [Environment]::NewLine), 'Assessment ([0-9a-f-]{36}) for')
    if (!$match.Success) { throw 'Agent did not report an assessment identifier.' }
    $assessmentId = $match.Groups[1].Value
    $evidence = Invoke-RestMethod "http://127.0.0.1:5187/assessments/$assessmentId" -Headers $headers
    if ($evidence.assessmentId -ne $assessmentId -or $evidence.checks.Count -lt 6) {
        throw 'Stored evidence does not match a completed readiness assessment.'
    }
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $logDirectory 'fleet-demo-evidence.json')
    Write-Output "PASS: real Windows preflight submitted and retrieved as assessment $assessmentId."
    Write-Output "Eligibility enum: $($evidence.eligibility). Evidence: test-logs/fleet-demo-evidence.json"
} finally {
    if ($null -ne $serverProcess -and !$serverProcess.HasExited) { Stop-Process -Id $serverProcess.Id }
    $env:IGLOO_FLEET_DEV_TOKEN = $previousToken
}

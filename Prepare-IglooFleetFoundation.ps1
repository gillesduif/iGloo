#Requires -Version 7.0

<#
.SYNOPSIS
    Prepares the iGloo repository for the Community / Fleet architecture work.

.DESCRIPTION
    Safe, repeatable preparation script for:
      - validating git + GitHub CLI
      - validating the current repository
      - requiring a clean working tree
      - updating main with --ff-only
      - creating an annotated pre-Fleet baseline tag
      - creating/pushing the Community/Fleet foundation branch
      - creating/updating GitHub labels
      - creating a GitHub milestone
      - creating one umbrella issue with the Fleet Phase 0 checklist

    This script deliberately DOES NOT:
      - rename projects
      - modify source code
      - modify Igloo.sln
      - merge the Deepin branch
      - execute any migration-related code

    It is safe to re-run. Existing GitHub objects are reused where possible.
#>

[CmdletBinding()]
param(
    [string]$Repo             = "gillesduif/iGloo",
    [string]$Remote           = "origin",
    [string]$BaseBranch       = "main",
    [string]$FoundationBranch = "refactor/community-fleet-foundation",
    [string]$ArchitectureTag  = "pre-community-fleet-split"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$MilestoneTitle = "Community / Fleet Architecture Foundation"
$IssueTitle     = "Community / Fleet Architecture Foundation + Fleet Phase 0"

function Write-Step {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "    OK: $Message" -ForegroundColor Green
}

function Write-Warn {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host "    WARNING: $Message" -ForegroundColor Yellow
}

function Stop-Script {
    param([Parameter(Mandatory)][string]$Message)
    Write-Host ""
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

function Invoke-Git {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & $script:GitExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-GitOutput {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $Output = & $script:GitExe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed:`n$($Output -join "`n")"
    }
    return ($Output -join "`n").Trim()
}

function Invoke-Gh {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & $script:GhExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-GhOutput {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $Output = & $script:GhExe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "gh $($Arguments -join ' ') failed:`n$($Output -join "`n")"
    }
    return ($Output -join "`n").Trim()
}

function Test-LocalBranch {
    param([Parameter(Mandatory)][string]$Branch)
    & $script:GitExe show-ref --verify --quiet "refs/heads/$Branch"
    return ($LASTEXITCODE -eq 0)
}

function Test-RemoteBranch {
    param(
        [Parameter(Mandatory)][string]$RemoteName,
        [Parameter(Mandatory)][string]$Branch
    )
    & $script:GitExe ls-remote --exit-code --heads $RemoteName $Branch *> $null
    return ($LASTEXITCODE -eq 0)
}

function Test-LocalTag {
    param([Parameter(Mandatory)][string]$Tag)
    & $script:GitExe show-ref --verify --quiet "refs/tags/$Tag"
    return ($LASTEXITCODE -eq 0)
}

function Test-RemoteTag {
    param(
        [Parameter(Mandatory)][string]$RemoteName,
        [Parameter(Mandatory)][string]$Tag
    )
    & $script:GitExe ls-remote --exit-code --tags $RemoteName "refs/tags/$Tag" *> $null
    return ($LASTEXITCODE -eq 0)
}

Write-Host ""
Write-Host "============================================================" -ForegroundColor Magenta
Write-Host " iGloo - Community / Fleet Foundation Preparation" -ForegroundColor Magenta
Write-Host "============================================================" -ForegroundColor Magenta
Write-Host ""
Write-Host "Repository : $Repo"
Write-Host "Base       : $BaseBranch"
Write-Host "Branch     : $FoundationBranch"
Write-Host "Tag        : $ArchitectureTag"
Write-Host ""

Write-Step "Checking required tools"

$GitCommand = Get-Command git -CommandType Application -ErrorAction SilentlyContinue
if (-not $GitCommand) { Stop-Script "git was not found in PATH." }

$GhCommand = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue
if (-not $GhCommand) { Stop-Script "GitHub CLI (gh) was not found in PATH." }

$script:GitExe = $GitCommand.Source
$script:GhExe  = $GhCommand.Source

Write-Ok "git found: $($script:GitExe)"
Write-Ok "gh found:  $($script:GhExe)"

Write-Step "Locating Git repository"

try {
    $RepoRoot = Get-GitOutput @("rev-parse", "--show-toplevel")
}
catch {
    Stop-Script "The current directory is not inside a Git repository."
}

Set-Location $RepoRoot
Write-Ok "Repository root: $RepoRoot"

Write-Step "Checking GitHub authentication"

& $script:GhExe auth status
if ($LASTEXITCODE -ne 0) {
    Stop-Script "GitHub CLI is not authenticated. Run: gh auth login"
}
Write-Ok "GitHub authentication available"

Write-Step "Verifying repository identity"

try {
    $CurrentRepo = Get-GhOutput @(
        "repo", "view",
        "--json", "nameWithOwner",
        "--jq", ".nameWithOwner"
    )
}
catch {
    Stop-Script "Could not determine the GitHub repository."
}

if ($CurrentRepo -ne $Repo) {
    Stop-Script "Expected '$Repo' but current repository is '$CurrentRepo'."
}

Write-Ok "Repository confirmed: $CurrentRepo"

Write-Step "Checking working tree"

$Status = Get-GitOutput @("status", "--porcelain")
if ($Status) {
    Write-Host ""
    Write-Host $Status
    Write-Host ""
    Stop-Script "Working tree is not clean. Commit or stash these changes before continuing."
}
Write-Ok "Working tree clean"

Write-Step "Fetching branches and tags"
Invoke-Git @("fetch", $Remote, "--prune", "--tags")
Write-Ok "Fetch complete"

Write-Step "Synchronizing $BaseBranch"
Invoke-Git @("switch", $BaseBranch)
Invoke-Git @("pull", "--ff-only", $Remote, $BaseBranch)

$CurrentMainCommit = Get-GitOutput @("rev-parse", "HEAD")
$CurrentMainShort  = Get-GitOutput @("rev-parse", "--short", "HEAD")
Write-Ok "$BaseBranch is at $CurrentMainShort"

Write-Step "Preparing architecture baseline tag"

if (Test-LocalTag -Tag $ArchitectureTag) {
    $BaselineCommit = Get-GitOutput @("rev-list", "-n", "1", $ArchitectureTag)
    $BaselineShort  = Get-GitOutput @("rev-parse", "--short", $BaselineCommit)
    Write-Ok "Existing baseline tag reused: $ArchitectureTag -> $BaselineShort"
}
else {
    $BaselineCommit = $CurrentMainCommit
    Invoke-Git @(
        "tag", "-a", $ArchitectureTag,
        "-m", "iGloo baseline before Community/Fleet architecture split",
        $BaselineCommit
    )
    $BaselineShort = Get-GitOutput @("rev-parse", "--short", $BaselineCommit)
    Write-Ok "Created annotated tag: $ArchitectureTag -> $BaselineShort"
}

if (-not (Test-RemoteTag -RemoteName $Remote -Tag $ArchitectureTag)) {
    Invoke-Git @("push", $Remote, $ArchitectureTag)
    Write-Ok "Baseline tag pushed to GitHub"
}
else {
    Write-Ok "Baseline tag already exists on GitHub"
}

Write-Step "Preparing foundation branch"

$LocalBranchExists  = Test-LocalBranch -Branch $FoundationBranch
$RemoteBranchExists = Test-RemoteBranch -RemoteName $Remote -Branch $FoundationBranch

if ($LocalBranchExists) {
    Invoke-Git @("switch", $FoundationBranch)
    Write-Ok "Using existing local branch: $FoundationBranch"
}
elseif ($RemoteBranchExists) {
    Invoke-Git @("switch", "--track", "-c", $FoundationBranch, "$Remote/$FoundationBranch")
    Write-Ok "Created local tracking branch from $Remote/$FoundationBranch"
}
else {
    Invoke-Git @("switch", "-c", $FoundationBranch, $BaselineCommit)
    Invoke-Git @("push", "-u", $Remote, $FoundationBranch)
    Write-Ok "Created and pushed foundation branch"
}

$RemoteBranchExists = Test-RemoteBranch -RemoteName $Remote -Branch $FoundationBranch
if (-not $RemoteBranchExists) {
    Invoke-Git @("push", "-u", $Remote, $FoundationBranch)
    Write-Ok "Published foundation branch"
}

Write-Step "Creating/updating GitHub labels"

$Labels = @(
    @{ Name = "area:community";   Color = "0E8A16"; Description = "iGloo Community product work" },
    @{ Name = "area:fleet";       Color = "1D76DB"; Description = "iGloo Fleet product work" },
    @{ Name = "type:architecture";Color = "5319E7"; Description = "Architecture and structural work" },
    @{ Name = "phase:0";          Color = "FBCA04"; Description = "Fleet Phase 0 / readiness foundation" },
    @{ Name = "safety-critical";  Color = "B60205"; Description = "Changes affecting migration safety or destructive operations" }
)

foreach ($Label in $Labels) {
    Invoke-Gh @(
        "label", "create", $Label.Name,
        "--repo", $Repo,
        "--color", $Label.Color,
        "--description", $Label.Description,
        "--force"
    )
}

Write-Ok "GitHub labels ready"

Write-Step "Preparing GitHub milestone"

$MilestonesRaw = Get-GhOutput @(
    "api",
    "repos/$Repo/milestones?state=all&per_page=100"
)

$Milestones = @()
if ($MilestonesRaw) {
    $Milestones = @($MilestonesRaw | ConvertFrom-Json)
}

$ExistingMilestone = $Milestones |
    Where-Object { $_.title -eq $MilestoneTitle } |
    Select-Object -First 1

if (-not $ExistingMilestone) {
    $MilestoneDescription = @"
Foundation for the explicit iGloo Community / iGloo Fleet product architecture.

Scope:
- Community project naming
- Fleet project family
- shared-engine boundaries
- Fleet Contracts / Domain / Agent / Server / Persistence / Web skeleton
- Fleet Phase 0 remote readiness assessment
- architecture documentation

No remote destructive migration in this milestone.
"@

    $CreatedMilestoneRaw = Get-GhOutput @(
        "api",
        "-X", "POST",
        "repos/$Repo/milestones",
        "-f", "title=$MilestoneTitle",
        "-f", "description=$MilestoneDescription"
    )

    $ExistingMilestone = $CreatedMilestoneRaw | ConvertFrom-Json
    Write-Ok "Created milestone #$($ExistingMilestone.number): $MilestoneTitle"
}
else {
    Write-Ok "Milestone already exists: #$($ExistingMilestone.number)"
}

Write-Step "Preparing architecture umbrella issue"

$IssuesRaw = Get-GhOutput @(
    "issue", "list",
    "--repo", $Repo,
    "--state", "all",
    "--limit", "200",
    "--json", "number,title,url"
)

$Issues = @()
if ($IssuesRaw) {
    $Issues = @($IssuesRaw | ConvertFrom-Json)
}

$ExistingIssue = $Issues |
    Where-Object { $_.title -eq $IssueTitle } |
    Select-Object -First 1

if (-not $ExistingIssue) {
    $IssueBody = @"
# Goal

Establish a clear long-term product architecture for **iGloo Community** and **iGloo Fleet** while preserving one shared migration engine.

Foundation branch:

    $FoundationBranch

Pre-refactor baseline:

    $ArchitectureTag

Baseline commit:

    $BaselineCommit

## Product structure

    Community
    └── Igloo.Community.App

    Fleet
    ├── Igloo.Fleet.Contracts
    ├── Igloo.Fleet.Domain
    ├── Igloo.Fleet.Agent
    ├── Igloo.Fleet.Server
    ├── Igloo.Fleet.Persistence
    └── Igloo.Fleet.Web

    Shared
    ├── Igloo.Core
    ├── Igloo.Preflight
    ├── Igloo.Iso
    ├── Igloo.Migration
    └── Igloo.UsbWriter

## Foundation work

- [ ] Establish Visual Studio solution folders
- [ ] Rename `Igloo.App` to `Igloo.Community.App`
- [ ] Preserve installer/update/runtime compatibility
- [ ] Create `Igloo.Fleet.Contracts`
- [ ] Create `Igloo.Fleet.Domain`
- [ ] Create `Igloo.Fleet.Agent`
- [ ] Create `Igloo.Fleet.Server`
- [ ] Create `Igloo.Fleet.Persistence`
- [ ] Create `Igloo.Fleet.Web`
- [ ] Document allowed project dependency directions

## Fleet Phase 0

- [ ] Introduce explicit Fleet protocol versioning
- [ ] Introduce replaceable device identity abstraction
- [ ] Agent registration/enrolment foundation
- [ ] Agent heartbeat and capability reporting
- [ ] Headless invocation of existing iGloo preflight logic
- [ ] Sanitized readiness assessment DTO mapping
- [ ] Server assessment ingestion
- [ ] Minimal assessment persistence
- [ ] Assessment retrieval/API
- [ ] Minimal engineering inspection surface
- [ ] End-to-end local Phase 0 demo

## Domain foundation

- [ ] Fleet device identity
- [ ] Assessment model
- [ ] Eligibility decision
- [ ] Migration run model
- [ ] Migration evidence model
- [ ] Explicit migration state machine
- [ ] Reject invalid state transitions
- [ ] Unit tests for state transitions

## Safety / privacy invariants

- [ ] Community and Fleet use the same shared migration engine
- [ ] Fleet does not depend on Community/WPF
- [ ] Community does not depend on Fleet
- [ ] Fleet Server does not perform endpoint migration operations
- [ ] Fleet Agent owns endpoint-side execution
- [ ] `MigrationManifest` remains endpoint execution/handoff state
- [ ] Fleet uses separate sanitized `MigrationEvidence`
- [ ] No automatic upload of user files or secrets
- [ ] No Wi-Fi/browser/password secrets sent to Fleet
- [ ] No remote destructive migration in Phase 0
- [ ] No partition modifications in Phase 0
- [ ] No reboot orchestration in Phase 0
- [ ] No mass migration endpoints

## Verification

- [ ] Existing build baseline recorded before refactor
- [ ] Existing test baseline recorded before refactor
- [ ] `dotnet restore`
- [ ] `dotnet build`
- [ ] `dotnet test`
- [ ] Existing Community behaviour preserved
- [ ] New Fleet tests passing
- [ ] Architecture documentation updated

## Explicit non-goals

Not part of this milestone:

- Production multi-tenancy
- Billing/subscriptions
- Complex analytics/dashboarding
- Kubernetes
- Generic endpoint management
- Patch management
- EDR
- Full RBAC/SSO
- Production HA
- Remote destructive migration
- Canary execution
- Mass rollout
- Government/offline deployment

## Architectural rule

> **Safety before orchestration.**

Community and Fleet are separate products around the same trusted iGloo migration infrastructure. Fleet orchestrates existing endpoint capabilities; it does not introduce a second migration engine.
"@

    $TempFile = New-TemporaryFile

    try {
        Set-Content -Path $TempFile.FullName -Value $IssueBody -Encoding utf8

        $IssueUrl = Get-GhOutput @(
            "issue", "create",
            "--repo", $Repo,
            "--title", $IssueTitle,
            "--body-file", $TempFile.FullName,
            "--milestone", $MilestoneTitle,
            "--label", "type:architecture",
            "--label", "area:community",
            "--label", "area:fleet",
            "--label", "phase:0"
        )

        Write-Ok "Created umbrella issue"
        Write-Host "    $IssueUrl" -ForegroundColor DarkGray
    }
    finally {
        Remove-Item -Path $TempFile.FullName -Force -ErrorAction SilentlyContinue
    }
}
else {
    Write-Ok "Umbrella issue already exists: #$($ExistingIssue.number)"
    Write-Host "    $($ExistingIssue.url)" -ForegroundColor DarkGray
}

Write-Step "Final verification"

$CurrentBranch = Get-GitOutput @("branch", "--show-current")
$CurrentCommit = Get-GitOutput @("rev-parse", "--short", "HEAD")
$FinalStatus   = Get-GitOutput @("status", "--porcelain")

if ($CurrentBranch -ne $FoundationBranch) {
    Stop-Script "Expected to finish on '$FoundationBranch' but current branch is '$CurrentBranch'."
}

if ($FinalStatus) {
    Write-Warn "Working tree is unexpectedly no longer clean:"
    Write-Host $FinalStatus
}
else {
    Write-Ok "Working tree remains clean"
}

Write-Ok "Current branch: $CurrentBranch"
Write-Ok "Current commit: $CurrentCommit"

Write-Host ""
Write-Host "============================================================" -ForegroundColor Green
Write-Host " READY FOR CODEX" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
Write-Host ""
Write-Host "Baseline tag :" -NoNewline
Write-Host " $ArchitectureTag" -ForegroundColor Cyan
Write-Host "Work branch  :" -NoNewline
Write-Host " $FoundationBranch" -ForegroundColor Cyan
Write-Host "Repository   :" -NoNewline
Write-Host " https://github.com/$Repo/tree/$FoundationBranch" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next step:"
Write-Host "Give Codex the Community/Fleet Foundation + Fleet Phase 0 master spec."
Write-Host ""
Write-Host "Deepin remains on its own branch. Do not merge it into this foundation."
Write-Host ""

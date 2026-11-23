# Cutting a release

Every step from a clean working tree to a published GitHub release. Commands are
PowerShell; `&&` is a parser error there, so a guarded chain reads
`A; if ($?) { B }`.

## What you need installed

| Tool | Where the build expects it |
|---|---|
| .NET 10 SDK | `C:\Program Files\dotnet` |
| Inno Setup 6 | `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe` |
| GitHub CLI (`gh`) | on `PATH`, authenticated |

## 1. Green before anything else

```powershell
dotnet build -c Release
```

```powershell
dotnet test -c Release --no-build
```

The agents are not part of the solution. Their suite runs on Linux, as the CI job
`linux-agent-regressions` does; on Windows run it in WSL from the repository root:

```powershell
wsl -- python3 scripts/test_agent_regressions.py
```

It ends with `Agent regression scripts: N/N passed.` Do not continue past a red suite.

## 2. Version numbers

`<IglooRelease>` in `src/Igloo.Community.App/Igloo.Community.App.csproj` is the
name of the release (`beta1`): `build-setup.bat` reads it and passes it to Inno
Setup as `/DIglooVersion`, which names the output file and the entry in Installed
apps. Four places do **not** follow automatically:

| File | Field | When |
|---|---|---|
| `src/Igloo.Community.App/Igloo.Community.App.csproj` | `<Version>`, the number of the program file (`1.0.0-beta.1`) | every release |
| `installer/iGloo.iss` | `VersionInfoVersion=1.0.0.0` | every major.minor bump |
| `installer/iGloo.iss` | `#define IglooVersion` | the fallback when ISCC is run by hand |
| `.github/ISSUE_TEMPLATE/bug_report.yml` | version placeholder | every release |

`AppId` in `iGloo.iss` must never change - it is what makes an install an
upgrade rather than a second copy.

Sweep for leftovers before moving on:

```powershell
Select-String -Path *.md,installer\*.iss,src\Igloo.Community.App\*.csproj,.github\ISSUE_TEMPLATE\*.yml -Pattern "<previous release>" | Select-Object Path,Line
```

The references in `docs/marketing/` and `docs/funding/` are historical and stay
as they are.

## 3. Changelog and release notes

In `CHANGELOG.md`, close the section: rename `## [Unreleased]` to
`## [<version>] - <date>` and add the compare link at the bottom of the file.
This was missed for 0.1-alpha, which left a released version sitting under
`[Unreleased]` and the links pointing at the version before it.

Rewrite `RELEASE-NOTES.md` for this version: what is new, what is validated,
known limitations with issue links, and a placeholder for the hash. The file is
handed to `gh release create` verbatim, so it is the release body.

Check the known limitations against the open issues rather than copying the
previous release:

```powershell
gh issue list --state open --label bug --limit 30
```

## 4. Commit

One-line conventional subjects, no bodies, grouped by subsystem. Nothing may be
outstanding except deliberate exclusions:

```powershell
git status --porcelain
```

## 5. Build the installer

```powershell
.\installer\build-setup.bat
```

Five steps: copy `src\` and `distros\` to `C:\Temp\igloo-build`, read the
release name from the csproj, `dotnet publish`, copy the result to
`installer\publish`, compile `iGloo.iss`. It prints the output path,
`installer\output\iGloo-Community-<release>.exe`, and the SHA256 when it finishes.

The copy to `C:\Temp` is not optional: publishing from a path containing an
apostrophe fails with MSB3094, because the SDK's publish Copy step collapses
`%(RelativePath)` on quoted paths. Ordinary builds from the real checkout are
fine; only `publish` breaks.

> **Architecture.** Releases are `win-x64`, self-contained: that is what
> `build-setup.bat` publishes, and what 0.1-alpha shipped. The two Visual Studio
> profiles in `src/Igloo.Community.App/Properties/PublishProfiles/` publish
> `win-x64`, self-contained as well; the release still comes from
> `build-setup.bat`, which also restores and publishes the packaged CLI.

`installer\output\` is not cleaned between releases, so older installers stay
behind. It is in `.gitignore`, so nothing can be committed by accident; the risk
is only picking the wrong file in step 7.

## 6. Record the hash

Put the SHA256 that step 5 printed into `RELEASE-NOTES.md`, then verify against
the file you are about to upload:

```powershell
Get-FileHash installer\output\iGloo-Community-<release>.exe -Algorithm SHA256
```

```powershell
git add RELEASE-NOTES.md; if ($?) { git commit -m "chore(release): record the <version> installer hash" }
```

The hash has to be committed before the tag, so the tag points at notes that
match the binary.

## 7. Tag and publish

```powershell
git tag -a v<version> -m "iGloo Community <release>"
```

```powershell
git push origin main --follow-tags
```

```powershell
gh release create v<version> --title "iGloo Community <release>" --notes-file RELEASE-NOTES.md installer\output\iGloo-Community-<release>.exe
```

Name the installer path in full. Tab completion in `installer\output\` will
offer the previous release too.

## 8. Afterwards

- Close the issues this release fixed, and the milestone if it is done.
- Check the release page: the notes render, and the asset downloads and matches
  the published hash.

## Checklist

```
[ ] dotnet build + dotnet test green
[ ] agent suites green
[ ] <IglooRelease> and <Version> set in Igloo.Community.App.csproj
[ ] VersionInfoVersion bumped in iGloo.iss
[ ] issue template placeholder bumped
[ ] CHANGELOG section closed, compare link added
[ ] RELEASE-NOTES rewritten, limitations checked against open issues
[ ] working tree clean
[ ] installer built, SHA256 noted
[ ] hash committed
[ ] tag pushed
[ ] release created with the right asset
[ ] release page checked
```

# ADR-006: Migrate UI layer from WinUI 3 to WPF

**Status:** Accepted — supersedes ADR-001 (C# / .NET 8 / WinUI 3) and ADR-005 (WinUI 3 deployment)
**Date:** 2026-05-18

## Context

ADR-001 chose WinUI 3 for its modern Fluent look and Windows 11 nativeness.
In practice, the development experience proved too costly for a pre-alpha, solo-maintainer project:

- **Unpackaged self-contained deployment is unstable.** Even with `WindowsAppSDKSelfContained=true`
  and the correct NuGet version, the app crashed with `STATUS_STOWED_EXCEPTION` (0xC000027B) on
  every `dotnet run`. The crash originates in native WinUI code before or during XAML
  initialisation; it is non-deterministic across WinAppSDK minor versions and cannot be caught
  by managed `try/catch`. Debugging cost: multiple full sessions with no resolution.

- **Mandatory VS 2022 toolchain for CLI builds.** `MrtCore.PriGen.targets` requires
  `Microsoft.Build.AppxPackage.dll`, which is only present inside VS 2022's MSBuild directory.
  The `AppxMSBuildToolsPath` workaround in `Directory.Build.props` hard-codes VS edition paths and
  breaks on any CI agent or developer machine that uses the .NET SDK toolchain without VS.

- **Platform-locked to x64/ARM64.** WinUI 3 rejects `AnyCPU`, forcing Platforms and
  RuntimeIdentifier overrides throughout the project file and solution.

- **No functional upside yet.** At M1/M2 the UI is a skeleton window and a pre-flight report.
  Fluent design tokens provide no value until there is real UI to render.

## Decision

Replace `<UseWinUI>true</UseWinUI>` with `<UseWPF>true</UseWPF>` in `Igloo.App.csproj`.
All other projects (`Igloo.Core`, `Igloo.Preflight`, `Igloo.Iso`, plugin assemblies) are
unchanged — they have no UI-framework dependency.

## Consequences

**What we give up:**
- Native Fluent / WinUI design language. The app will look like a standard WPF application
  until custom styles are added.
- `NavigationView`, `InfoBar`, `TeachingTip`, and other WinUI-exclusive controls must be
  reimplemented or sourced from a third-party WPF library (e.g. ModernWpfUI) when needed.

**What we gain:**
- `dotnet run` works without VS installed and without platform flags.
- No external runtime dependency; WPF is part of the .NET 8 SDK.
- `AnyCPU` target; no forced x64/ARM64 overrides.
- `Directory.Build.props` is now empty — no VS-path hacks.
- `TreatWarningsAsErrors=true` can be enforced from day one.

**Migration path if WinUI 3 is revisited:**
WinUI 3 can be re-adopted once the project has a CI pipeline that pins a specific VS Build Tools
version, and once the WinAppSDK self-contained crash is reproduced on a clean VM and reported
upstream. ADR-005 documents the deployment model that was in place; ADR-001 documents the
original rationale. Neither is deleted — they form the audit trail.

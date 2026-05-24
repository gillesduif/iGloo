# ADR-001: C# / .NET 8 / WinUI 3 (unpackaged)

**Status:** Accepted
**Date:** 2026-05-16

## Context

The Windows-side app does heavy WMI work (BitLocker, TPM, GPU detection, partition inspection), modifies the Windows Boot Manager, drives non-destructive partition operations, presents a GUI, and ships as a redistributable installer-grade tool.

## Decision

**C# / .NET 8 with WinUI 3, unpackaged deployment.**

## Rationale

**C# / .NET 8:**
- Deepest WMI/COM/Win32 bindings of any mainstream language. `System.Management` is built into the BCL.
- Maintainer is fluent in C#. For a project where shipping matters more than language exploration, this is decisive.
- .NET 8 supports single-file self-contained publish. The output is a single `Igloo.exe` that runs on Win10 1809+ or Win11 with no separate runtime install.

**WinUI 3 over WPF:**
- ~50% of Igloo's target users are already on Windows 11; the trend is one-way.
- Modern Fluent design out of the box, native Win11 look.
- WinUI 3 supports Windows 10 1809+ officially — the Win10 holdouts (a meaningful share of the migration target audience, since "Win10 went EOL" is a major migration trigger) are not excluded.

**Unpackaged deployment over MSIX:**
- A migration tool is run once, not installed long-term. MSIX packaging, App Installer, identity, etc. are overkill.
- `WindowsPackageType=None` + `EnableMsixTooling=false` + dynamic Windows App SDK bootstrap gives us a plain executable.
- Caveat: the user must have the Windows App SDK runtime installed, or we self-contain it. We self-contain by default.

## Alternatives considered

- **WPF.** Simpler deployment story, no Windows App SDK dependency, but visibly dated and looks like a 2015 tool on Win11. Rejected.
- **Rust + Tauri.** Smaller binaries, memory safety. Rejected because WMI bindings are thin and the maintainer would be on the learning curve during the critical bring-up.
- **Go + Wails.** Same WMI-binding gap; weaker GUI ecosystem.

## Consequences

- Single-binary distribution requires self-contained publish (~80-100 MB compressed). Acceptable for an installer-grade one-shot tool.
- The custom unpackaged `Program.cs` bootstrap is non-trivial. Documented in `Program.cs` and considered the "weird part" of the project — every WinUI 3 unpackaged app needs it.

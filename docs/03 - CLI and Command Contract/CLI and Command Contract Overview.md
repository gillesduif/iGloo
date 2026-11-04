---
title: CLI and Command Contract Overview
type: moc
product: cli
status: in-progress
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: cli
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [src/Igloo.Cli/Igloo.Cli/Program.cs, src/Igloo.Cli.Client/Igloo.Cli.Client.csproj, scripts/igloo.cmd, docs/spec/command-contract/README.md]
aliases: []
tags: [igloo]
---

# CLI and Command Contract Overview

`Igloo.Cli.exe` is the distinct executable gateway in the current branch; `iGloo.exe` is Community's WPF application. The CLI has human commands, a one-result `--json` form and `invoke --machine` JSON Lines process transport. Both enter the Host dispatcher for registered capabilities. The `Igloo.Cli.Client` project references Command.Contracts only and handles process invocation, framing and typed results for first-party clients; it does not implement endpoint migration. [[03 - CLI and Command Contract/Command Contract V1]] links the canonical schemas and transport rules.

[[03 - CLI and Command Contract/Current Capability Registry]] distinguishes the 19 public IDs from private Host workflows and unregistered high-risk commands. Current client use is uneven: [[06 - One/iGloo One Overview]] and a Fleet Agent path use the process client; [[05 - Community/Community Overview]] remains Host-bound for private workflow. Process location/metadata checks and a protocol hello are present but do not establish cryptographic binary identity. [[00 - Home/Architecture Drift Register]] DR-01/02/08 and [[02 - Architecture/Architecture Convergence Plan]] AC-01–03 track those gaps.

## Running the CLI during development

`Program.FindDistrosDirectory` accepts only the executable's own build output or a packaged `cli` directory with `distros` beside it, so the executable cannot be copied onto `PATH`; a copy elsewhere fails startup with `LocalBackendInitializationFailed` and exit code 4. `scripts/igloo.cmd` is the observed development launcher: it resolves the repository root from its own location, selects `Debug` unless `IGLOO_CONFIGURATION` names another configuration, and forwards arguments and standard input to the built executable. A missing build exits 4 with the `dotnet build` line to run. Putting a one-line forwarding shim named `igloo.cmd` in a directory already on `PATH` makes `igloo` a plain command in both PowerShell and Command Prompt; this is developer convenience and carries no installation authority.

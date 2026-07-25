# Contributing to iGloo

Thanks for wanting to help. iGloo moves people's data and rewrites their disk
layout, so the bar for changes is higher than a typical app. This guide covers
how to build it, the rules that keep it safe, and how to get a change merged.

## Before you start

- **Test on a VM, never your daily driver.** The install and removal paths
  repartition disks. Use a throwaway Windows VM (or spare machine) with nothing
  you care about on it.
- Open an issue before a large change so we can agree on the approach.
- By contributing, you agree your work is licensed under the project's
  [GPL-2.0](LICENSE) and you certify the [DCO](#developer-certificate-of-origin).

## Building

Requirements:

- Windows 10 (2004+) or Windows 11 — the app is WPF and uses Windows-only APIs.
- .NET SDK 9.0 or newer (the app targets `net9.0-windows`; the libraries target
  `net8.0`).

```bash
dotnet build Igloo.sln -c Debug
dotnet test  Igloo.sln -c Debug
```

The full suite is 157 tests across six projects. They must stay green.

## The rules that matter

### 1. Zero analyzer warnings — and zero suppressions

The solution builds clean under `AnalysisMode=All` + `EnforceCodeStyleInBuild`
(both set in `Directory.Build.props`), and three projects treat warnings as
errors. **Fix the code; don't silence the analyzer.** We do not use
`[SuppressMessage]`, `#pragma warning disable`, or `<NoWarn>`. If an analyzer
rule is genuinely wrong for the whole project, that's an `.editorconfig` policy
change discussed in a PR — not a one-off suppression.

Prefer real handling over broad catches: catch the specific exception types an
operation can throw, or use the `Try…`-returns-fallback pattern, so control flow
stays honest.

### 2. The plugin contract is frozen

`IDistroPlugin` and the types it touches are a public contract that shipped
plugins depend on. Don't change its shape. New distros are added as new plugins,
not by editing the interface.

### 3. A partitioning fix in one distro means auditing them all

The distros share a mental model but not code. If you fix a data-loss or
partition-safety bug in one distro's pipeline, **check every other distro for the
same bug in the same PR.** This is a hard rule — it's how we've avoided shipping
the same disk bug five times.

### 4. Runtime-relevant changes need VM validation

If you touch the install/removal pipeline, the first-boot agents, boot-entry
handling, or ISO verification, run a real end-to-end install in a VM and say so
in the PR (which distro, dual-boot vs. replace, what you checked). "Builds and
unit tests pass" is not enough for these paths.

### 5. Match the surrounding code

Follow `.editorconfig`. Match the comment style: explain *why*, especially the
non-obvious hardware/firmware reasons. New behavior needs a test.

## Submitting a pull request

1. Branch off `main`.
2. Keep the change focused; separate refactors from behavior changes.
3. `dotnet build` clean (no new warnings) and `dotnet test` green.
4. Fill in the PR template, including the VM-validation box if it applies.
5. Sign your commits off (DCO, below).

## Developer Certificate of Origin

We use the [DCO](https://developercertificate.org/): a lightweight sign-off that
you have the right to submit the code under the project's license. Add a
`Signed-off-by` line to each commit:

```bash
git commit -s -m "Your message"
```

That records `Signed-off-by: Your Name <your@email>` using your `git config`
identity. Note that this identity becomes part of the public history.

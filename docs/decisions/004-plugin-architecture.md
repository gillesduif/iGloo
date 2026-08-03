# ADR-004: Distros as plugins, not core code

**Status:** Accepted
**Date:** 2026-05-16

## Context

Igloo's long-term goal is distro-agnostic migration. The short-term reality is shipping Fedora KDE first. The architecture must make the long-term goal cheap to deliver without delaying v1.

## Decision

**Distros are plugins.** Each distro is a self-contained folder under `distros/` with its own assembly implementing `IDistroPlugin`. Igloo's core code references no concrete distro.

## Rationale

- **Zero-code distro additions.** A community member adds Mint, Zorin, or openSUSE by copying `distros/_template/`, filling in metadata and an installer-driver template, opening a PR. No changes to `src/`.
- **Per-distro ownership.** A maintainer for the Mint plugin commits to that distro only. If they go silent, only Mint is affected - Fedora, openSUSE, others keep working.
- **Isolation.** Each plugin loads into its own `AssemblyLoadContext`. A buggy plugin cannot take down the host app or other plugins.
- **Forces the right abstractions early.** Building the plugin contract while implementing Fedora as the reference forces us to identify what's distro-specific (installer driver, package manager, agent) versus what's distro-agnostic (pre-flight, ISO download, partition prep, manifest format, boot manager work). The boundary becomes obvious as soon as you try to draw it.

## Alternatives considered

- **Hardcode Fedora for v1, plugin-ify later.** Cheaper short-term, much more expensive long-term. Every "but how does it work on Mint?" question would be deferred to a future rewrite. Building the plugin model now costs maybe 2 weeks; retrofitting it later costs months.
- **Plugin model but distros live in separate repos.** Cleaner separation but worse discoverability and harder cross-cutting changes when the IDistroPlugin contract evolves. Keep distros in-tree for v1; reconsider when there are >5 of them.

## Consequences

- **Positive:** Community contributions are a one-folder PR. Distros version independently. Isolation against buggy plugins.
- **Positive:** The reference (Fedora KDE) functions as the contributor documentation. "Look at how Fedora does it" is the answer to most questions.
- **Negative:** Slightly more initial complexity. `IDistroPlugin` has to be designed thoughtfully because changing it later forces every plugin to update.
- **Open:** Plugin discovery mechanism (AssemblyLoadContext details, plugin signing for trusted-source guarantees) implemented in M2.

---
title: Distribution Provider Overview
type: moc
product: provider
status: mixed
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: providers
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [src/Igloo.Core/Abstractions/IDistroPlugin.cs, docs/reference/adding-a-distribution.md, distros/README.md]
aliases: []
tags: [igloo]
---

# Distribution Provider Overview

Providers extend the target side **below logical Core**. They contribute catalogue/compatibility facts, installer rendering and first-boot deltas, not independent disk/boot execution authority. The current provider projects reference Core abstractions and contain distro-specific manifests/scripts. [ADR-004](../decisions/004-plugin-architecture.md), the [distribution contribution guide](../reference/adding-a-distribution.md) and [installer shared scripts](https://github.com/gillesduif/iGloo/blob/main/distros/_shared/installer/README.md) describe their current contract. [[02 - Architecture/Target Architecture]] distinguishes this provider axis from consumer UIs above CLI.

A distro maintainer may also build a separate frontend, but in the target it consumes CLI machine capabilities without linking to privileged Core internals. That UI is not itself a provider. Provider discovery does not promise installation: catalogue `available`, a compiled plugin, a fixture render and a physical end-to-end run are separate evidence levels. [[08 - Distribution Providers/Distribution Support Matrix]] records the current distinction for Debian, Fedora KDE, Mint and Ubuntu.

The current command registry advertises provider IDs according to loaded/available catalogue entries; it is not a hardware certificate. A1/A7 require Core-owned mutation and provider independence. [[13 - Testing and Validation/Test Matrix]] calls for reference guardrails, fixture conformance, disposable VM installer/first-boot runs and exact physical-hardware evidence before a public support claim.

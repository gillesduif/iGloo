---
title: Distributions and compatibility
type: hub
area: distributions
status: current
implementation: not-applicable
tags:
  - igloo
  - topic/compatibility
aliases: []
reviewed: 2026-10-03
reviewed_commit: 5e4ebe846b488d8609612d37665aaefd4d203c88
sources:
  - distros/fedora-kde/distro.json
  - distros/linuxmint-cinnamon/distro.json
  - distros/debian/distro.json
  - distros/ubuntu/distro.json
  - distros/deepin/distro.json
  - distros/debian/DebianPlugin.cs
  - docs/reference/hardware-findings.md
---

# Distributions and compatibility

The catalog manifest is a UI status, not a proof of a safe installation on the current checkout. The [current state](current-state.md) separates code, earlier hardware evidence and present blockers. Plugins are discovered through Core; [ADR-004](decisions/004-plugin-architecture.md) and [the contributor guide](reference/adding-a-distribution.md) explain the model.

| Distribution | Manifest state | What the evidence says |
| --- | --- | --- |
| Fedora KDE | `available` | Earlier physical runs are recorded in [hardware findings](reference/hardware-findings.md). Exact ownership and ESP handling are revisited in the [installation audit](architecture/community-installation-ownership.md). |
| Linux Mint Cinnamon | `available` | Earlier hardware runs exist; [ESP binding](architecture/installer-esp-binding.md) records the multiple-ESP hazard and the need for exact supported selection. |
| Debian 13 | `available` | This catalog value conflicts with the current `DebianPlugin` legacy entry points, which throw `NotSupportedException`. [Debian deployment](architecture/debian-target-root-deployment.md) is a partial guarded replacement, not a general install path. |
| Ubuntu | `in-development` | `distros/ubuntu/STATUS.md` and [ADR-009](decisions/009-park-ubuntu-ship-beta-with-three.md) explain the parked path. |
| Deepin | `coming-soon` | Experimental guarded work exists, but no current end-to-end install validation is established here. |
| Other catalog entries | `coming-soon` | Metadata is present; no plugin or complete installer capability should be inferred. |

The [distribution guide](reference/adding-a-distribution.md) and [shared engine map](shared-engine.md) identify the plugin contract. [Safety and evidence](safety-and-evidence.md) explains why a status label, build or fixture does not supersede an exact-target stop condition.

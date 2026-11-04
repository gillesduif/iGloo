---
title: Community and user migration
type: hub
area: community
status: current
implementation: not-applicable
tags:
  - igloo
  - topic/migration
aliases: []
reviewed: 2026-10-03
reviewed_commit: 5e4ebe846b488d8609612d37665aaefd4d203c88
sources:
  - src/Igloo.Community.App/Igloo.Community.App.csproj
  - src/Igloo.Community.App/App.xaml.cs
  - src/Igloo.Preflight/DirectInstallService.cs
  - docs/architecture.md
  - docs/architecture/community-installation-ownership.md
---

# Community and user migration

Community is the local human-operated WPF wizard. The [operation guide](reference/operation.md) explains the user flow; the [architecture map](architecture.md) explains Windows preparation, installer handoff and Linux first boot. The [code guide](code/README.md) identifies the main files, but its older flow prose must be read with the current [ownership audit](architecture/community-installation-ownership.md).

Community is one of four peer products in [[iGloo Home|the product family]], rather than the Platform itself. `FileStagingViewModel` still orchestrates sensitive collection, manifest creation and artifact preparation. [[architecture/shared-orchestration|Shared orchestration]] records the proposed extraction before [[cli|CLI]] reuses this flow. [[architecture/migration-rules|Conversational migration rules]] are planned and do not yet change Community's folder selection.

## Workflow and data

- [Preflight and installation preparation](architecture.md) use shared services described in [Shared engine](shared-engine.md). [ISO verification](reference/safety-model.md) and [disk safety](business/business-rules.md) constrain staging.
- The [browser migration guide](reference/browser-migration.md) and [credential decision](decisions/011-chromium-credential-migration.md) explain sensitive data handling. [Display migration](reference/display-migration.md) is tied to [hardware findings](reference/hardware-findings.md), rather than a universal display guarantee.
- [Password hashing](reference/password-hashing.md) covers installer account handling. [User use cases](business/use-cases.md) explain which migration choices are user driven.
- The [visual guide](guide/README.md) is a walkthrough, not execution evidence. The [multi-boot menu findings](reference/boot-menu.md) record earlier GRUB and UEFI observations, constrained by the later [ownership audit](architecture/community-installation-ownership.md).

## Current installation ownership

The [installation ownership review](architecture/community-installation-ownership.md) connects the [ESP binding audit](architecture/installer-esp-binding.md), [target-root design](architecture/target-root-deployment.md) and the [Debian deployment continuation](architecture/debian-target-root-deployment.md). Its protected JSON records remain separate byte-bound artifacts. The [Debian session boundary](architecture/debian-session-boundary.md) and [isolation boundary](architecture/debian-isolation-boundary.md) describe conditions for native execution, not permission to run it.

The [Debian bootstrap decision](architecture/debian-bootstrap-primitive.md), [configured-root artifact](architecture/debian-configured-root-artifact.md), [neutralization](architecture/debian-configured-root-neutralization.md), [canonical lab acquisition](architecture/debian-canonical-lab-acquisition.md), and [post-import configuration](architecture/debian-post-import-configuration.md) document the sequence of bounded lab results. [Helper contracts](architecture/debian-core-helper-contracts.md), [v5 configuration](architecture/debian-core-configuration-v5.md), [capacity stop](architecture/debian-core-v5-capacity-block.md), [checkpoint experiment](architecture/debian-checkpoint-derived-configuration.md), [compatibility review](architecture/debian-core-configuration-compatibility.md), and [initramfs continuation](architecture/debian-initramfs-continuation.md) retain distinct outcomes; none imply general production readiness.

## Connections

The [distribution map](distributions.md) separates catalog visibility from verified installability. The [safety map](safety-and-evidence.md) explains why historical hardware observations and lab records have narrow scope. Fleet can reuse read-only assessment components, but the [product boundary](architecture/product-boundaries.md) prevents Fleet plans from invoking this workflow.

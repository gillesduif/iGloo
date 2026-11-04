---
title: Product direction and decisions
type: hub
area: direction
status: current
implementation: not-applicable
tags:
  - igloo
  - topic/decisions
aliases: []
reviewed: 2026-10-03
reviewed_commit: 5e4ebe846b488d8609612d37665aaefd4d203c88
sources:
  - ROADMAP.md
  - docs/business/README.md
  - docs/decisions/006-wpf-over-winui3.md
  - docs/decisions/009-park-ubuntu-ship-beta-with-three.md
  - docs/funding/igloo-one-pager.md
  - docs/marketing/campaign-0.1-alpha.md
---

# Product direction and decisions

The repository-root `ROADMAP.md` is the milestone index. The [current state](current-state.md) checks its claims against code and evidence at the inspected commit. [Business documentation](business/README.md), [use cases](business/use-cases.md) and [risk register](business/risk-register.md) explain the users and priorities; the [white paper](whitepaper/igloo-whitepaper.md) is a research treatment, not a current validation certificate.

## Product family and roadmap gap

[[decisions/013-product-family-and-platform|ADR-013]] makes Community, CLI, AI and Fleet peer upper layers over one Platform. This is an architectural decision about ownership, not a claim that all four ship. The root roadmap is still centered on the existing Community milestones; it does not sequence [[cli|CLI]] or [[ai|AI]]. A proposed order is: extract and validate a shared read-only plan path; deliver CLI `check`/`distros`/plan preview; settle schema, exit codes and secret handoff; only then consider apply/install and AI adapters. This is a planning hypothesis, not an approved release schedule. Fleet's [[fleet-map|execution boundary]] remains separate.

## Product thesis and research

**Hypothesis:** safe technical migration, choice guidance, data/app/browser continuity and credible reversibility may be the missing adoption layer between a person's wish to leave Windows and existing Linux installers. Core addresses technical complexity; AI might reduce choice paralysis by translating a goal into supported options. This is not a proven market conclusion. [[business/use-cases|User use cases]] and [[reference/comparison|comparison]] are existing inputs; [[safety-and-evidence|safety evidence]] limits any promise about reversibility.

Possible lines such as “A guided exit from Windows”, “The Linux installer for humans and machines”, “You don't choose Linux. You tell iGloo what you want” and “Describe what you want. iGloo handles the choices” are **marketing concepts**, not selected branding. The older [alpha campaign](marketing/campaign-0.1-alpha.md) and [one-pager](funding/igloo-one-pager.md) are dated presentation material. Their hardware, rollback, funding and policy claims must be rechecked before external reuse.

**Strategic hypothesis, research needed:** endpoint migration may help European digital sovereignty and reduce proprietary platform lock-in. A provider-neutral [[ai|AI]] interface could avoid dependence on one model vendor, including with a local model, but privacy, offline operation and compatibility remain unproven. Verify current EU funding programmes, policy fit, public-sector/open-source adoption and AI investment programmes from current primary sources before making eligibility, amounts or impact claims. The local ignored funding draft is private working material, not evidence for published claims.

## Architecture decisions

- [ADR-004](decisions/004-plugin-architecture.md) chose distro plugins; the [shared engine](shared-engine.md) shows the current project references.
- [[decisions/013-product-family-and-platform|ADR-013]] fixes the four-product target and the intent/execution ownership rule. Shared DI APIs, versioned machine output and migration-rule semantics still need decisions; see [[vault/audit|the gap audit]].
- [ADR-006](decisions/006-wpf-over-winui3.md) superseded [ADR-001](decisions/001-language-and-ui.md) and [ADR-005](decisions/005-winui3-deployment.md). The earlier decisions remain as history.
- [ADR-007](decisions/007-ntfs-partition-for-large-isos.md) and [ADR-008](decisions/008-installers-never-partition.md) record storage choices. The [later ownership audit](architecture/community-installation-ownership.md) identifies requirements not established by those historical decisions.
- [ADR-009](decisions/009-park-ubuntu-ship-beta-with-three.md) parks Ubuntu; the [distribution map](distributions.md) compares this with current manifests.
- [ADR-010](decisions/010-relicense-gpl3.md) covers licensing. [ADR-011](decisions/011-chromium-credential-migration.md) covers credential migration and its [browser guide](reference/browser-migration.md).
- [ADR-012](decisions/012-two-phase-first-boot-bootstrap.md) separates installer hooks from first-boot work, a boundary relevant to the [Debian deployment continuation](architecture/debian-target-root-deployment.md).

The [Windows 11 redesign charter](design/win11-redesign.md) is design direction. [Comparison](reference/comparison.md) is market context. The tracked [one-pager](funding/igloo-one-pager.md) and launch materials are historical presentation material; ignored local funding drafts and plans are outside this shared navigation.

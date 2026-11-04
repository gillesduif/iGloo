---
title: Next Actions
type: navigation
product: project
status: mixed
doc_status: current
validation_status: not-applicable
availability: not-applicable
graph_cluster: delivery
last_reviewed: 2026-10-25
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [ROADMAP.md, docs/spec/command-contract/implementation-report.md]
aliases: []
tags: [igloo]
---

# Next Actions

**Beta first (25 October 2026).** The installer path is the priority: run the r6 Debian/Mint/Fedora end-to-end test after the boot-registration repair ([ADR-014](../decisions/014-boot-registration-is-not-gated.md)), then work the B-items in [[00 - Home/Beta Track]] in order (idempotent retry, fail-before-resize, browser-lock handling, Windows-side end-to-end check). The architecture convergence sequence below continues on its own track and does not gate beta.

Use the single checkbox for each AC item in [[15 - Implementation/Master Implementation Checklist]]. In order: resolve the A2 process-gateway governance conflict (AC-01); prove CLI packaging/trust and complete read-only parity (AC-02/03); close exact layout, secret custody, approval and recovery proof (AC-04/05/06); then consider durable prepare, replay and verification (AC-07/08). [[02 - Architecture/Architecture Convergence Plan]] explains dependencies and acceptance tests. The branch also needs separate release/hardware reconciliation (AC-12); [ROADMAP](https://github.com/gillesduif/iGloo/blob/main/ROADMAP.md) remains the official release milestone index.

---
title: Fleet Overview
type: moc
product: fleet
status: in-progress
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: fleet
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [src/Igloo.Fleet.Agent/Igloo.Fleet.Agent.csproj, src/Igloo.Fleet.Server/Igloo.Fleet.Server.csproj, src/Igloo.Fleet.Agent/TrustedAgentCommand.cs, docs/fleet/phase-2.md]
aliases: []
tags: [igloo]
---

# Fleet Overview

> **7 November 2026:** Fleet's code moved to [gillesduif/iGloo-Fleet](https://github.com/gillesduif/iGloo-Fleet) under FSL-1.1-ALv2 ([ADR-016](../decisions/016-fleet-source-available-and-contract-apache.md)). The Agent references `Igloo.Cli.Client` and `Igloo.Command.Contracts` only; the Host seams are gone (AC-10).

**PRODUCT DEFINITION:** iGloo Fleet is the organisation product: a European answer to Microsoft's endpoint administration for enterprises and governments that migrate their Windows PCs to Linux and keep managing them from one console. [[decisions/015-fleet-console-component-library|ADR-015]] names the audience, administrators who know Microsoft 365 and Intune, air-gapped government networks included; the console design's module catalogue (inventory, software, updates, configuration, security, storage, networking, monitoring, automation, reports) is the scope. The read-only work items below are the current implementation stage, not the product.

Fleet has a Server control plane and an endpoint Agent. **CODE OBSERVATION:** Server references Fleet Domain/Persistence, with no direct CLI/Core.Host endpoint edge. Agent references the `Igloo.Cli.Client` and `Igloo.Command.Contracts` packages only. Its trusted command path invokes CLI for assessment and dry-run planning and reports a cycle with no execution capability; the Host-backed transition seams were removed on 2026-11-07 (AC-10). Fleet network work covers assessment and dry-run/prepared planning, not remote destructive execution. See [Fleet architecture](../fleet/architecture.md), [Phase 1](../fleet/phase-1.md) and [Phase 2](../fleet/phase-2.md) for dated evidence.

**NORMATIVE TARGET:** Fleet Server handles identity, cohort policy, scheduling, approvals and allowlisted evidence. Only the local Agent may translate an authorized endpoint task into a CLI request (whitepaper A6, §12.3). The Agent must still obey Core's exact state, privilege, recovery and authority checks; a prepared Fleet plan is not an endpoint execution permit. Sanitized Fleet evidence must not contain private manifests, raw user data, credentials or arbitrary diagnostic paths.

[[02 - Architecture/Architecture Convergence Plan]] AC-10 (done 2026-11-07) removed the Agent Host bypass. [[10 - Safety and Security/Safety and Security Overview]] records the destructive gates; [[13 - Testing and Validation/Test Matrix]] includes Server isolation, Agent translation and sanitization checks. [[09 - Workflows/Workflow Overview]] separates present assessment from a future target execution flow.

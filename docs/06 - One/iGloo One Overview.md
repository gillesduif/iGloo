---
title: iGloo One Overview
type: moc
product: one
status: in-progress
doc_status: current
validation_status: partial
availability: branch-only
graph_cluster: one
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [Igloo.One.App/Igloo.One.App.csproj, Igloo.One.App/OneBackendClient.cs, docs/spec/command-contract/implementation-report.md]
aliases: []
tags: [igloo]
---

# iGloo One Overview

**CODE OBSERVATION:** The untracked `Igloo.One.App` project is in the local solution. Its `.csproj` references Command.Contracts and `Igloo.Cli.Client`, not Core.Host; `OneBackendClient` uses the process client for typed backend calls. The shell is unfinished and no complete AI/reasoning workflow, approval policy or model-provider integration was verified. It must not be presented as a released or operational AI product. The older [AI concept note](../ai.md) is a dated design snapshot.

**NORMATIVE TARGET:** One is a conversational/reasoning client that discovers supported capabilities, translates user intent into explicit typed proposals and presents results through the same CLI gateway as Community and Fleet Agent (whitepaper §§4.5, 12.2). It is neither a privileged shell nor a second installer. Ambiguous intent requires clarification; prompt-injected documents or model output do not grant disk/boot authority. Core must revalidate state and exact authorization (A8), and secrets remain outside model context and argv (A10).

The existing read-only/planning process path is only the first integration seam. [[03 - CLI and Command Contract/Current Capability Registry]] tells One what can currently be requested. [[02 - Architecture/Architecture Convergence Plan]] AC-03/05 and [[13 - Testing and Validation/Test Matrix]] define further parity, tool safety and leak tests. No target mutation tool should be exposed until the underlying public capability is independently qualified.

The branch-only [[06 - One/Tux Mascot|Tux Mascot]] adds a native WPF visual component and a development demo. It does not change One's backend capability or product-readiness status.

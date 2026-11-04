---
title: Capability and Evidence Template
type: template
product: project
status: not-applicable
doc_status: current
validation_status: not-applicable
availability: not-applicable
graph_cluster: reference
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/spec/command-contract/README.md]
aliases: []
tags: [igloo]
---

# Capability and Evidence Template

Copy this note only for a real new capability or evidence review. Replace every bracketed instruction before publishing; do not count this template as implementation or validation. Use English note names, properties, diagrams and descriptions. Keep one canonical task in [[15 - Implementation/Master Implementation Checklist]] or its source issue.

## Capability contract

- ID and capability version: [from handler and discovery]
- Kind, risk, authority, provider scope: [from descriptor]
- Input/result/event schema IDs: [link canonical files]
- Current registration and runtime path: [Host composition and source revision]
- Preconditions, state revision, locks and postconditions: [exact checks]
- Sensitive fields and data minimization: [no real values]
- Human and machine modes: [same semantics, observed test]

## Evidence and change decision

- Claim label: NORMATIVE TARGET / CODE OBSERVATION / REGISTERED CAPABILITY / HISTORICAL SNAPSHOT / EXECUTED TEST EVIDENCE / ROADMAP COMMITMENT / PROPOSAL / UNKNOWN.
- Source and revision: [path, section, commit and dirty-state note]
- Environment and test command: [fixture / process / VM / hardware / release]
- Result and artifact: [scope and accessible reference; no private bytes]
- Gap and canonical task: [one link]
- Decision status: [accepted only with a source-linked ADR]

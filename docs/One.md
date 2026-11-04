---
title: iGloo One
type: product
area: ai
status: current
implementation: planned
tags:
  - igloo
  - topic/intent
aliases: []
reviewed: 2026-10-03
reviewed_commit: 5e4ebe846b488d8609612d37665aaefd4d203c88
sources:
  - Igloo.sln
  - src/Igloo.Cli/Igloo.Cli/Program.cs
  - src/Igloo.Core/Models/MigrationManifest.cs
  - docs/architecture/product-boundaries.md
---

# iGloo One

**Architectural rule:** “The AI doesn't install Linux. iGloo installs Linux. AI translates intent.” AI is one of the four peers in [[iGloo Home|the product family]], not an alternative disk or boot engine. `Igloo.One.App` is now an unfinished WPF shell that references only Command Contracts and Core.Host and can call read-only/planning commands. There is no AI model integration or complete One product flow. This is a backend integration with a firm trust boundary, not an installation capability.

## From intent to a supported choice

A user might ask for a familiar desktop, to retain Windows, allocate 150 GB and migrate important files. AI can propose a supported distro, dual boot, size and explicit migration selection. Ambiguous disk, data or compatibility choices require a question rather than a guess. iGloo must then validate the choices against current observations, show a concrete preview and obtain user confirmation before any mutation. AI must not invent shell, partitioning or installer commands. See [[architecture/product-boundaries|product boundaries]] and [[architecture/shared-orchestration|plan validation]].

```text
human intent → AI proposal → explicit iGloo choices → Platform validation
             → preview and user confirmation → trusted execution (future)
```

This addresses a **product hypothesis**: Core reduces technical complexity; AI may reduce choice paralysis. People can describe the outcome they want without learning ext4, GRUB or installer stacks. AI must stay inside the available [[distributions|distro capabilities]] and have an accessible, fully usable non-AI path through [[community|Community]] or [[cli|CLI]].

[[architecture/migration-rules|Conversational migration rules]] are a distinct experimental extension: language becomes deterministic filters, then a scan/preview before consent. No filter engine or AI translation is implemented. [[cli|The machine interface]] is a possible model-neutral boundary; OpenAI, Claude, Mistral, a local model or another provider could produce the same constrained choices. Provider independence, offline support and privacy are design goals, not tested properties.

## Privacy and safety questions

- Decide which user text or device observations may leave the machine, under what consent, retention and logging policy. File names and migration data can be sensitive.
- Keep Linux passwords and browser credentials out of model prompts, ordinary CLI arguments, diagnostics and machine JSON. iGloo collects secrets directly; see [[safety-and-evidence|the data boundary]].
- Define capability negotiation, schema/version compatibility, uncertainty handling and audit records for AI proposals versus validated choices. A model response never becomes execution authority by itself.
- Confirm destructive action through iGloo's trusted UI/flow and exact target revalidation. Fleet approval is a different organizational mechanism, not a shortcut to Community execution.

The [[direction|product thesis]] records possible positioning without declaring a market claim proven.

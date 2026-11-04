---
title: Graph Guide
type: guide
product: project
status: implemented
doc_status: current
validation_status: partial
availability: not-applicable
graph_cluster: reference
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/.obsidian/graph.json, docs/.obsidian/bookmarks.json, scripts/validate_docs_vault.py]
aliases: [iGloo graph guide]
tags: [igloo]
---
# Graph Guide

Open [[00 - Home/Project Dashboard]] for a reading path, then the native Obsidian Graph for related notes. A graph line means one note links to another; it does **not** prove a runtime call, trust relationship, direction or implementation status. Read the sentence around a link and the directional Mermaid diagrams in [[02 - Architecture/Current Architecture]] and [[02 - Architecture/Target Architecture]]. The force layout cannot pin CLI or Core to fixed coordinates.

## Saved global views

The ten views below are native global graph bookmarks in the ignored local `docs/.obsidian/bookmarks.json`. All ten were opened and visually inspected in Obsidian 1.13.7 on 10 October 2026. The first six older bookmarks remain. Each new view hides tags and attachments, shows existing files and orphans, and uses the domain colors below; the Implementation and Evidence view uses status colors first. Folder-scoped queries implicitly omit `19 - Templates/`, `99 - Archive/` and `20 - Reference/Generated Reports/`. The exact search expressions are portable for a different Obsidian setup.

### 1. iGloo Platform Overview

Read the Dashboard, current/target architecture and domain overviews as a compact platform map.

```text
path:"00 - Home/Project Dashboard.md" OR path:"02 - Architecture/Architecture Overview.md" OR path:"02 - Architecture/Current Architecture.md" OR path:"02 - Architecture/Target Architecture.md" OR path:"00 - Home/Architecture Drift Register.md" OR path:"03 - CLI and Command Contract/CLI and Command Contract Overview.md" OR path:"04 - Core and Runtime/Core and Runtime Overview.md" OR path:"05 - Community/Community Overview.md" OR path:"06 - One/iGloo One Overview.md" OR path:"07 - Fleet/Fleet Overview.md" OR path:"08 - Distribution Providers/Distribution Provider Overview.md" OR path:"09 - Workflows/Workflow Overview.md" OR path:"10 - Safety and Security/Safety and Security Overview.md"
```

### 2. CURRENT Code Architecture

Inspect code/project boundaries, registered commands and current client paths.

```text
path:"02 - Architecture/Current Architecture.md" OR path:"04 - Core and Runtime/" OR path:"03 - CLI and Command Contract/Current Capability Registry.md" OR path:"05 - Community/" OR path:"07 - Fleet/"
```

### 3. TARGET CLI-mediated Architecture

Explore A1-A10, One, provider and safety target concepts; read note status before inferring implementation.

```text
path:"02 - Architecture/Target Architecture.md" OR path:"02 - Architecture/Invariant Conformance Matrix.md" OR path:"06 - One/" OR path:"08 - Distribution Providers/" OR path:"10 - Safety and Security/"
```

### 4. Architecture Drift and Convergence

Follow source conflicts into ordered, canonical work items.

```text
path:"00 - Home/Architecture Drift Register.md" OR path:"02 - Architecture/Architecture Convergence Plan.md" OR path:"15 - Implementation/" OR path:"16 - Decisions/" OR path:"02 - Architecture/Architecture Gap Analysis.md"
```

### 5. CLI and Public Contract

Inspect command registration, schemas, protocol and transport.

```text
path:"03 - CLI and Command Contract/" OR path:"11 - Data and Contracts/" OR path:"spec/command-contract/"
```

### 6. Client Ecosystem

Separate Community, One and Fleet endpoint roles.

```text
path:"05 - Community/" OR path:"06 - One/" OR path:"07 - Fleet/" OR path:"03 - CLI and Command Contract/CLI and Command Contract Overview.md"
```

### 7. Core Safety and Durable Operations

Inspect authority, allocation, state and recovery gaps.

```text
path:"04 - Core and Runtime/" OR path:"10 - Safety and Security/" OR path:"14 - Operations/"
```

### 8. Distribution Providers

Inspect providers separately from historical distro evidence.

```text
path:"08 - Distribution Providers/" OR path:"distributions.md" OR path:"reference/adding-a-distribution.md"
```

### 9. Implementation and Evidence

Locate proposed work, risk, tests and release qualification.

```text
path:"13 - Testing and Validation/" OR path:"15 - Implementation/" OR path:"17 - Risks and Issues/" OR path:"18 - Releases and Access/" OR path:"00 - Home/Blockers.md"
```

### 10. Maintenance and Link Audit

Find the vault health notes and the previously isolated backend audit.

```text
path:"00 - Home/" OR path:"20 - Reference/" OR path:"vault/" OR path:"architecture/backend-cleanup-audit-2026-10-06.md"
```

## Local Graph and no-hub check

Open a capability, component, decision or evidence note and use Local Graph depth 1 for immediate links; depth 2 reveals broader context. Local Graph has no saved graph bookmark. The Dashboard Local Graph was opened, its inherited old filter cleared, and its new neighbors appeared. Clicking the Architecture Drift Register node in the global Drift view opened that note and updated its Local Graph.

The older **Architecture without hubs** global bookmark applies `-[type:hub]` to an architecture-focused filter. It was opened in Obsidian: substantive Debian/Core/Fleet notes still formed a connected neighborhood, while the 6 October backend audit appeared separately because its new contextual link is through a hub. Static metrics and limitations are in [[00 - Home/Documentation Health]].

## Colors and metadata

New notes have one `graph_cluster` property. The following native Graph color groups were applied before the older `area` rules. Obsidian 1.13.7 accepted the `[graph_cluster:core]` property filter and displayed the Core overview note; the Platform, Current, Target and Drift views visibly rendered distinct domain colors.

| Domain | Query | Color |
| --- | --- | --- |
| safety | `[graph_cluster:safety]` | `#FB7185` |
| architecture | `[graph_cluster:architecture]` | `#94A3B8` |
| cli | `[graph_cluster:cli]` | `#38BDF8` |
| core | `[graph_cluster:core]` | `#2DD4BF` |
| community | `[graph_cluster:community]` | `#7DD3FC` |
| one | `[graph_cluster:one]` | `#C084FC` |
| fleet | `[graph_cluster:fleet]` | `#A78BFA` |
| providers | `[graph_cluster:providers]` | `#FBBF24` |
| delivery | `[graph_cluster:delivery]` | `#86EFAC` |
| reference | `[graph_cluster:reference]` | `#A1A1AA` |

In **Implementation and Evidence** only, `blocked` is coral, `planned`/`proposed` blue, `in-progress`/`mixed` amber, `implemented` green, and `validation_status:verified` teal before domain fallback. A color is a search match, not proof of release or validation. Older notes may match the retained `area` groups; no mass rewrite of historical metadata was made.

## Use and limits

The graph is a conceptual index. The saved **CURRENT** and **TARGET** bookmarks are different filters over the same vault, not duplicate capability contracts. Inspect note frontmatter and linked source before interpreting target nodes as current. The native force layout may overlap labels in a narrow pane; expand the pane or zoom to inspect a neighborhood. Graph search cannot identify every broken link by itself; run the static validator in [[00 - Home/Documentation Health]] and inspect real unresolved nodes.

[[20 - Reference/Graph Configuration]] records the local settings and backup. The previous [graph guide](../vault/graph-guide.md) remains available for its earlier bookmarks.

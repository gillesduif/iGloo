---
title: Graph Guide
type: guide
area: vault
status: current
implementation: not-applicable
tags:
  - igloo
  - topic/navigation
aliases: []
reviewed: 2026-10-03
reviewed_commit: 5e4ebe846b488d8609612d37665aaefd4d203c88
sources:
  - docs/.obsidian/graph.json
  - docs/.obsidian/core-plugins.json
  - scripts/validate_docs_vault.py
---

# Graph Guide

> [!IMPORTANT] Current graph guide
> This note documents the older 3 October local graph configuration. For the numbered CLI/Core vault and its Current, Target and Drift views, open [[00 - Home/Graph Guide]].

Open [[iGloo Home|Home]] for a reading path; use the native Graph for exploration. `iGloo Home.md` is the substantive Home in the `docs/` vault. `docs/README.md` is only a short entry pointing there. A line means one note links to another. It may mean navigation, historical context, validation or an actual dependency; read the sentence around the link before interpreting it. The graph does not assign typed edges or fixed positions.

## Note properties

New and substantively reviewed notes use `title` (text), `type` (text), `area` (one text value), `status` (text), `implementation` (text), `tags` (list), `aliases` (list), `reviewed` (date), `reviewed_commit` (text) and `sources` (list of repository-relative paths). The [template](note-template.md) contains placeholders; ordinary notes should not.

`status` describes the *document*: `current`, `draft`, `historical`, `superseded`. `implementation` describes a *capability*: `live`, `partial`, `planned`, `blocked`, `unknown`, `not-applicable`. A current note can document blocked software. Mixed capabilities belong in a table, as in [[current-state]]. The primary `area` is one of `community`, `cli`, `ai`, `fleet`, `shared`, `distributions`, `safety`, `delivery`, `direction`, `vault`. Existing long-form notes without reviewed properties remain searchable; do not mass-stamp them.

The `sources` list is provenance, not a list of Obsidian note links. For new navigation use explicit vault-path `[[wikilinks]]`, especially for ambiguous note names such as `README.md`. Existing GitHub-readable Markdown links remain valid. Links to source files outside `docs/` may produce unresolved graph nodes in Obsidian; the overview hides those nodes while Maintenance exposes them.

## Saved global views

These filters are for the Graph **Search files** field. Parentheses and `OR` have Obsidian Search meaning. Every graph bookmark stores its own options. Native bookmarks can reopen global graphs; local graphs cannot be bookmarked. The local `.obsidian/` and its bookmarks are ignored by Git, so a fresh checkout must recreate the views from this guide. The focused Product family view is the default global graph; Project overview retains the full active knowledge map.

### Product family — default graph

```text
path:"iGloo Home.md" OR path:"community.md" OR path:"cli.md" OR path:"ai.md" OR path:"fleet-map.md" OR path:"shared-engine.md" OR path:"current-state.md"
```

This is the first view to open when asking how the Visual Studio solution fits together. It shows [[iGloo Home|Home]], the four peer products, their common [[shared-engine|Platform]], and [[current-state|the implementation-status note]]. The graph lines come from the notes' actual wikilinks; the filter selects the seven notes and does not manufacture dependencies. Tags and attachments are off, Existing files only is on, and orphans remain visible. Use the same domain colors as Project overview below. The native force layout cannot pin Home to a fixed coordinate, but this focused view keeps the product relationships legible. Open Home for the authoritative reading path, then switch to Project overview or a domain view for detail.

### A. Project overview

```text
-path:"funding/" -path:"marketing/" -path:"plans/" -path:".canvas" -path:"vault/note-template" -path:"vault/audit" -[type:redirect] -[status:historical] -[status:superseded]
```

Tags off, attachments off, **Existing files only on**, orphans on. This full active-knowledge view includes detailed Debian architecture and may be crowded at fit-to-screen scale. It removes ignored local drafts, blank Canvas files and historical-only material without globally excluding them. Canvas nodes can remain visible even when attachment nodes are off, so the explicit `-path:".canvas"` filter keeps the note overview focused; Maintenance still exposes them. Existing files only hides the out-of-vault source-path nodes; it does not establish that all links work. Use Maintenance to inspect unresolved nodes. The saved color groups are evaluated top to bottom; the first matching group supplies the one visible color:

| Color | Query |
| --- | --- |
| Red `#E05252` safety | `[area:safety] OR path:"reference/safety-model.md" OR path:"reference/hardware-findings.md" OR path:"reference/password-hashing.md" OR path:"business/risk-register.md" OR path:"architecture/recovery-snapshot-v1.md" OR path:"architecture/installer-esp-binding.md"` |
| Violet `#A78BFA` Fleet | `[area:fleet] OR path:"fleet/"` |
| Cyan `#45C5D7` Community | `[area:community] OR path:"architecture/debian-" OR path:"architecture/community-" OR path:"reference/browser-" OR path:"reference/display-" OR path:"reference/operation.md" OR path:"guide/"` |
| Light blue `#5BB8FF` CLI | `[area:cli]` |
| Orchid `#D88EE8` AI | `[area:ai]` |
| Teal `#37B7A4` shared engine | `[area:shared] OR path:"architecture/product-boundaries.md" OR path:"architecture.md" OR path:"code/README.md"` |
| Amber `#E2B34F` distributions | `[area:distributions] OR path:"reference/adding-a-distribution.md" OR path:"decisions/009-"` |
| Blue `#5B9DF0` delivery | `[area:delivery] OR path:"reference/building.md" OR path:"reference/repository-structure.md"` |
| Pink `#DA8DBD` direction | `[area:direction] OR path:"decisions/" OR path:"business/" OR path:"whitepaper/" OR path:"design/" OR path:"reference/comparison.md"` |
| Gray `#94A3B8` vault | `[area:vault]` |

The priority matters: `business/risk-register.md` matches safety and direction and appears red in the inspected overview. A note does not display two group colors. Unclassified legacy notes remain neutral.

### B. Architecture without hubs

```text
(path:"architecture/" OR path:"fleet/" OR path:"architecture.md" OR path:"reference/adding-a-distribution.md" OR path:"decisions/004-" OR path:"decisions/008-" OR path:"decisions/013-" OR path:"cli.md" OR path:"ai.md") -[type:hub] -path:"plans/"
```

Use the overview domain groups, Existing files only on, tags/attachments off, orphans on. Excluding the new hubs reveals which component, workflow and decision notes connect on their own. This view will still show some legacy notes without properties because their paths are part of the architecture.

### C. Current work

```text
([implementation:partial] OR [implementation:planned] OR [implementation:blocked] OR path:"current-state.md" OR path:"community.md" OR path:"fleet-map.md" OR path:"distributions.md" OR path:"safety-and-evidence.md" OR path:"fleet/phase-2.md" OR path:"architecture/community-installation-ownership.md" OR path:"architecture/installer-esp-binding.md" OR path:"architecture/debian-target-root-deployment.md" OR path:"architecture/recovery-snapshot-v1.md" OR path:"architecture/debian-initramfs-continuation.md" OR path:"decisions/009-" OR path:"decisions/013-") -path:"vault/" -path:"funding/" -path:"plans/"
```

The explicit paths include the risk and decision notes alongside neutral context maps; Graph Search does not expand to neighbors automatically. The maps use `implementation: not-applicable` because each summarizes capabilities with different states. Use a separate color order: blocked red `#E05252` (`[implementation:blocked] OR path:"fleet/phase-2.md" OR path:"architecture/installer-esp-binding.md" OR path:"architecture/debian-target-root-deployment.md"`); partial amber `#E2B34F` (`[implementation:partial] OR path:"architecture/community-installation-ownership.md" OR path:"architecture/recovery-snapshot-v1.md" OR path:"architecture/debian-initramfs-continuation.md"`); planned blue `#5B9DF0` (`[implementation:planned] OR path:"decisions/009-"`). Existing files only on, orphans on.

### D. Safety and evidence

```text
path:"safety-and-evidence.md" OR path:"reference/safety-model.md" OR path:"reference/hardware-findings.md" OR path:"reference/browser-migration.md" OR path:"architecture/recovery-snapshot-v1.md" OR path:"architecture/installer-esp-binding.md" OR path:"architecture/community-installation-ownership.md" OR path:"architecture/debian-target-root-deployment.md" OR path:"architecture/debian-session-boundary.md" OR path:"fleet/security.md" OR path:"fleet/persistence.md" OR path:"fleet/phase-2.md" OR path:"business/risk-register.md" OR path:"decisions/008-" OR path:"decisions/013-" OR path:"architecture/shared-orchestration.md" OR path:"architecture/migration-rules.md" OR path:"ai.md" OR path:"cli.md"
```

Use the overview domain colors and Existing files only on. This is a focused reading selection; it does not prove each named guard executed on a current machine.

### E. Maintenance

```text
-path:"funding/" -path:"marketing/" -path:"plans/" -path:"vault/note-template"
```

Keep orphans on and **Existing files only off**. Inspect unresolved destination nodes and isolated notes. Some unresolved nodes are source-code links that work on GitHub but lie outside this vault; distinguish those from missing vault notes and the [validator's findings](audit.md). Do not globally exclude files merely to make the graph look clean.

## Useful controls

Open a note's **Local Graph** with the core command. For a centered five-spoke map, open [[iGloo Home|Home]], set Local Graph depth to **1**, and enter this Local Graph **Search files** filter:

```text
path:"iGloo Home.md" OR path:"community.md" OR path:"cli.md" OR path:"ai.md" OR path:"fleet-map.md"
```

This was checked in the local Obsidian UI: Home appears at the center and the four product notes are visible around it. The filter hides Home's other immediate links only in that Local Graph. Clear it to explore all Home links, or use depth 2 for broader context; depth 2 can become crowded. Local Graph settings live in this workspace and cannot be saved as a native graph bookmark. Turn on **Existing files only** for a cleaner local context; turn it off when investigating unresolved destinations. Turn arrows on locally when tracing who cites what: an arrow follows the written link, not necessarily software call direction. Backlinks show incoming mentions; Search finds properties and text; Bookmarks restore the six global views; Properties show lifecycle and implementation separately.

In the overview, the tested starting values are text fade 0.30, node size 0.95, link thickness 0.70, center force 0.65, repel force 8, link force 1 and link distance 150. Zoom or collapse sidebars for long labels. These settings are a starting point for this vault and theme, not a fixed layout promise.

## Maintenance

After a source change, use the [audit checklist](audit.md), then inspect the overview for new orphans and Maintenance for unresolved nodes. Test representative links by opening [[iGloo Home|Home]], [product boundaries](../architecture/product-boundaries.md), [Fleet Phase 2](../fleet/phase-2.md), [Debian deployment](../architecture/debian-target-root-deployment.md) and [hardware findings](../reference/hardware-findings.md). A static edge count or JSON parse cannot prove Obsidian rendering.

To recreate a saved view in a fresh local vault, open Graph view, enter the exact filter, set the toggles and groups above, then right-click the **global graph tab → Bookmark** and name it. Leave any existing bookmark groups intact. Do not try to bookmark a Local Graph.

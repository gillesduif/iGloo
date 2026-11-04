---
title: Documentation Health
type: evidence
product: project
status: not-applicable
doc_status: current
validation_status: partial
availability: not-applicable
graph_cluster: reference
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [scripts/validate_docs_vault.py, docs/00 - Home/Graph Guide.md]
aliases: [iGloo documentation health]
tags: [igloo]
---

# Documentation Health

This is the latest **static** audit of the `docs/` Obsidian vault, not a claim about application behavior. Run `python scripts/validate_docs_vault.py --shareable --json` from the repository root to refresh it. The checker traverses Markdown notes, frontmatter, local links and graph connectivity. Obsidian itself is needed to confirm rendering, graph filters, colors and saved bookmarks.

| Check | Result on 10 October 2026 | Scope |
| --- | --- | --- |
| Notes and active notes | 130 total; 122 active | Active excludes templates and archive |
| Links | 716 total; 343 navigation, 373 substantive; zero broken or ambiguous targets | Markdown and wikilink resolver, not every Obsidian extension |
| Orphans, graph components, Home-unreachable | 0 orphans; 1 component; 0 Home-unreachable | Static directed/undirected analysis |
| Without hub notes | 2 components; the dated backend audit is separate | Removing navigation hubs removes its new contextual link; substantive architecture notes remain linked |
| Frontmatter metadata | 0 issues in notes with properties; 60 legacy notes lack the new schema | Legacy omissions reported separately and retained in place |
| Native Obsidian rendering | All ten new global graph bookmarks opened in Obsidian 1.13.7; Dashboard table, colors and representative node navigation checked | Spatial positions are nondeterministic; narrow panes can overlap labels |

Before this pass, the audit had 85 notes, 79 active notes, 423 edges, one orphan (the 6 October backend cleanup audit), two components and one Home-unreachable note. [[04 - Core and Runtime/Core and Runtime Overview]] now links that audit. The older notes were retained in place. [[20 - Reference/Vault Migration Map]] documents additions and legacy routes; [[13 - Testing and Validation/Evidence Register]] distinguishes this static evidence from product validation.

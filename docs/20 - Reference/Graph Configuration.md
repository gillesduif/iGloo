---
title: Graph Configuration
type: guide
product: project
status: implemented
doc_status: current
validation_status: verified
availability: not-applicable
graph_cluster: reference
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/.obsidian/graph.json, docs/.obsidian/bookmarks.json, docs/vault/graph-guide.md]
aliases: []
tags: [igloo]
---

# Graph Configuration

The established vault root is `docs/`. Its `.obsidian/` is ignored local configuration, not a shareable public document. At baseline, native Graph, Backlinks, Search, Bookmarks, Properties and Templates were enabled, and six older global graph bookmarks existed. On 10 October 2026, `graph.json` gained ten `graph_cluster` color groups before the older `area` groups, and `bookmarks.json` gained ten named global views. The six older bookmarks and all their options were retained. Known Obsidian keys and a UI-created graph bookmark supplied the format; no theme or plugin settings were replaced.

The original `graph.json` and `bookmarks.json` were copied with matching SHA-256 hashes to `C:\Users\Gilles D'huyvetter\.codex\backups\igloo-vault-2026-10-10\` before local changes. This backup is outside the repository. The configuration was reloaded in Obsidian 1.13.7. All ten new bookmarks appeared and were opened; the Platform, Current, Target and Drift graphs displayed distinct domain colors. The property query `[graph_cluster:core]` returned the Core overview. The Drift graph node opened the matching note and updated Local Graph. The inherited Dashboard Local Graph filter was cleared; its new neighbors appeared. The old **Architecture without hubs** view still showed substantive links with a separately visible backend audit.

To reproduce the views in another setup, open `docs/` in Obsidian, enable Existing files only, hide tags and attachments, show orphans, paste one exact query from [[00 - Home/Graph Guide]] into Graph Search files, and add the guide's color groups in order. Save a **global** graph as a bookmark; use Local Graph depth 1/2 from a selected note. Local Graph configurations are not saved as graph bookmarks. The native force layout can overlap labels in a narrow pane and does not encode edge direction or call authority. The static validator can confirm links and metadata but not native graph rendering. [[20 - Reference/Vault Maintenance]] describes incremental updates.

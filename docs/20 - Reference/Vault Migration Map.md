---
title: Vault Migration Map
type: migration-map
product: project
status: implemented
doc_status: current
validation_status: partial
availability: not-applicable
graph_cluster: reference
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/README.md, docs/iGloo Home.md, docs/vault/graph-guide.md, .gitignore]
aliases: []
tags: [igloo]
---

# Vault Migration Map

The established vault is repository `docs/`, with a local ignored `.obsidian/`. `docs/dev-vault` is ignored but absent. Numbered folders were added **inside** `docs/`; no competing vault was created. Existing canonical public technical documents remain at stable paths. Existing ignored/private `docs/funding/`, `docs/plans/` and local Obsidian files remain in place and are not copied into numbered public folders. The pre-existing untitled Canvas files remain untouched.

| Old location | New or canonical location | Action | Incoming-link handling | Preservation note |
| --- | --- | --- | --- | --- |
| `docs/README.md` | [[00 - Home/Project Dashboard]] | Keep GitHub entry; add current dashboard pointer | Existing README path remains valid | No independent technical copy |
| `docs/iGloo Home.md` | [[00 - Home/Project Dashboard]] for current navigation | Keep older Home as dated 2026-10-03 snapshot with prominent pointer | Existing wikilinks keep resolving | Historical wording is not current branch truth |
| `docs/current-state.md`, `docs/cli.md`, `docs/ai.md`, `docs/shared-engine.md`, `docs/fleet-map.md` | Numbered current/target notes by domain | Retain original notes; synthesize current 10 October state in new notes | Link from new MOCs and dashboard | Old reviewed commits/dates remain visible |
| `docs/spec/command-contract/*` | Same paths, canonical public spec and schemas | Link/summarize in [[03 - CLI and Command Contract/Command Contract V1]] | Relative Markdown links and wikilinks point to original | No copied schema |
| `docs/architecture/*`, `docs/fleet/*`, `docs/reference/*` | Same paths, canonical public evidence and designs | Link to focused numbered maps | Preserve incoming GitHub/Obsidian paths | Do not move byte-sensitive JSON receipts |
| `docs/decisions/*` | Same stable ADR IDs | Index from [[16 - Decisions/Architecture Source Conflicts]] | Existing ADR links remain valid | No invented accepted decision |
| `docs/whitepaper/igloo-whitepaper.md` | Same historical August publication | Distinguish from supplied October PDF | Existing links remain valid | Do not overwrite or republish supplied PDF |
| `docs/vault/graph-guide.md`, `docs/vault/audit.md` | [[00 - Home/Graph Guide]], [[00 - Home/Documentation Health]] | Keep prior guides as dated local history | New guide links back | Local `.obsidian` stays ignored |
| `docs/architecture/backend-cleanup-audit-2026-10-06.md` | Same evidence note | Add contextual link from current architecture and audit map | Existing path remains valid | Historically orphaned; retain original bytes |

No existing technical document was moved. This avoids broken external references and two independently maintained canonical specifications. [[20 - Reference/Vault Maintenance]] defines how future changes reconcile the new maps with stable public docs.

---
title: Vault Maintenance
type: guide
product: project
status: implemented
doc_status: current
validation_status: partial
availability: not-applicable
graph_cluster: reference
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [scripts/validate_docs_vault.py, .gitignore, docs/spec/command-contract/README.md]
aliases: []
tags: [igloo]
---

# Vault Maintenance

On a later pass, record branch, HEAD, dirty files and source revisions first. Review changed `.csproj` references, Host/dispatcher registrations, schemas, CLI Client/One/Community/Fleet edges, provider contracts, accepted ADRs, issues and executed evidence. Then update the one canonical capability/status note, [[00 - Home/Architecture Drift Register]], [[02 - Architecture/Invariant Conformance Matrix]], [[13 - Testing and Validation/Test Matrix]] and the relevant task checkbox. Promote a status only after source **and** scoped test evidence; retain resolved drift history instead of deleting it.

Keep new documentation in English. Preserve canonical public paths under `docs/spec/`, `docs/architecture/`, `docs/fleet/`, `docs/reference/`, `docs/decisions/` and `docs/whitepaper/`. Do not copy the supplied PDF, private plans, funding drafts, receipts or credential data into a public graph. `.obsidian/` and `docs/dev-vault` are ignored by repository `.gitignore`; do not force-add them. [[20 - Reference/Vault Migration Map]] records the current no-move arrangement.

For static checks, run `python scripts/validate_docs_vault.py --shareable --json` and `git diff --check -- docs`. Inspect the JSON for unresolved links, orphans, components and Home reachability; a zero exit alone does not prove the Obsidian graph looks right. [[00 - Home/Graph Guide]] records native filters and visual acceptance. Do not stamp unchanged notes with a fresh review date.

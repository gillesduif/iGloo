---
title: Conversational migration rules
type: capability
area: shared
status: draft
implementation: planned
tags:
  - igloo
  - topic/migration
  - topic/intent
aliases: []
reviewed: 2026-10-03
reviewed_commit: 5e4ebe846b488d8609612d37665aaefd4d203c88
sources:
  - src/Igloo.Core/Abstractions/IFileStagingService.cs
  - src/Igloo.Migration/FileStagingService.cs
  - src/Igloo.Core/Models/MigrationManifest.cs
  - src/Igloo.Community.App/ViewModels/FileStagingViewModel.cs
---

# Conversational migration rules

**Planned capability idea, not implemented:** [[ai|AI]] may translate “take Downloads, but only files smaller than 1 GB” into a deterministic proposal such as `folder = Downloads` and `maxFileSizeBytes = 1073741824`. [[cli|CLI]] and [[community|Community]] should be able to show and edit the same explicit rules without AI. The user approves the resolved selection after a real scan, not the language alone.

```text
language or UI choice → typed rules → validated scan → preview/statistics
                      → user confirmation → trusted staging/migration
```

Possible later predicates include excluded ISO/ZIP/VM images, included PDFs, age limits, screenshots and `node_modules`. These are examples, not supported syntax. The model needs clear scope, precedence, path normalization, symlink/reparse-point policy, date semantics, inaccessible-file outcomes, deterministic ordering and versioning. Sensitive file names must stay within the approved data boundary; a model should not receive a raw personal file inventory by default.

The preview should account for found files, selected files, exclusions by each rule, selected bytes and excluded bytes. Counts and reasons need a consistent policy for overlapping rules and changed files between scan and copy. Before an apply step, revalidate or invalidate stale scans; report partial copies and evidence without equating a copied-byte counter with a verified migrated file.

**Existing contracts are narrower.** `FileStagingRequest` contains only `DistroId` and `FolderPaths`. `FileStagingService.ScanFolders` enumerates those folders, skips reparse points, uses `IgnoreInaccessible`, counts bytes and copies the jobs; it has no rule predicates or exclusion statistics. Core's `FileMigrationPlan` in `MigrationManifest` records staging path, total bytes and included folders. It is a private handoff record, not a public intent schema. A new rule contract should not silently overload it; design this alongside [[architecture/shared-orchestration|shared plan validation]] and [[architecture/product-boundaries#Execution artifacts are not Fleet records|Fleet data separation]].

Open decisions: rule schema/version and validation, privacy of scans, preview consistency guarantees, glob/type/date behavior, consent for exclusions, and whether rule evaluation belongs in Core contracts or Migration. No ADR is accepted for this experiment.

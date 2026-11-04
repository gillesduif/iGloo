# ADR-013: Four peer products over one iGloo Platform

**Status:** Accepted as target architecture
**Date:** 2026-10-03

## Context

The earlier [[architecture/product-boundaries|Community/Fleet foundation]] established a shared engine and separated Fleet's control plane from Community's local installer. A CLI project now exists only as a placeholder; the AI solution folder has no implementation. Treating Community as “the real iGloo” would invite duplicate workflow and safety logic when these interfaces are added.

## Decision

[[community|Community]], [[cli|CLI]], [[ai|AI]] and [[fleet-map|Fleet]] are peer upper-layer products. They consume a shared [[shared-engine|iGloo Platform]] of Core contracts and shared capabilities. Distribution plugins are Platform extensions. A plugin must not depend on any upper layer; upper layers must not hardcode a concrete distro implementation.

The Platform owns validation and trusted execution. Frontend input, model output and Fleet instructions are untrusted until reduced to supported, explicit choices and checked against current observations. AI may interpret and propose intent, but cannot invent or directly execute disk, boot, ISO or installer operations. Plugin code supplies distro-specific declarations and artifacts inside the trusted plugin boundary; it does not receive authority to bypass Platform safety gates. The [[safety-and-evidence|existing safety evidence]] determines what execution is presently qualified.

## Consequences

- Community remains a WPF frontend; its current orchestration in `FileStagingViewModel` is a [[architecture/shared-orchestration|shared-service extraction candidate]], not code to copy into CLI.
- CLI can expose a model-neutral machine interface after versioned schemas and exit codes are designed. A project stub is not that contract.
- AI must surface a validated preview, ask when intent is ambiguous, and keep secrets outside model context. [[architecture/migration-rules|Conversational file filters]] remain a proposal.
- Fleet retains its distinct authorization, sanitized evidence and presently read-only work protocol. This ADR does not create remote installation authority.
- The conceptual `Platform/Shared` and `Platform/Distros` solution grouping is a target organization. This ADR does not claim solution folders were changed.

## Relationship to earlier decisions

[[decisions/004-plugin-architecture|ADR-004]] already decided distro plugins and Core's lack of concrete distro references. ADR-013 extends the product-side boundary without rewriting that history. [[decisions/008-installers-never-partition|ADR-008]] records historical partition ownership; later exact-target audits still govern current readiness. Shared DI module APIs, plan/schema/versioning, secret collection mechanism and migration rule semantics remain open design decisions, not implicit parts of this accepted target.

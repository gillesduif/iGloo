---
title: Source and Whitepaper Version Index
type: source-register
product: project
status: mixed
doc_status: current
validation_status: partial
availability: not-applicable
graph_cluster: reference
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [docs/whitepaper/igloo-whitepaper.md, docs/spec/command-contract/README.md, Igloo.sln]
aliases: []
tags: [igloo]
---

# Source and Whitepaper Version Index

The supplied 28-page PDF is *iGloo: A CLI-Mediated Architecture for Operating-System Installation and Migration*, dated 6 October 2026. Its filename and cover identify **v1.0**, while interior page headers on pages 2–28 identify **v1.1**. This is unresolved source metadata, not a license to silently choose either edition. The PDF is a local supplied source outside the repository; it was read but not copied or committed. Its normative A1–A10 Table 1 is on pages 7–8, with design details in §§4–17, implementation snapshot in §18, validation in §19 and open questions in §20. [[02 - Architecture/Target Architecture]] and [[02 - Architecture/Invariant Conformance Matrix]] are concise paraphrases with section references.

The [August 2026 migration whitepaper](../whitepaper/igloo-whitepaper.md) is a separate historical publication. [ADR-013](../decisions/013-product-family-and-platform.md) accepts a four-product family target but does not settle every CLI process, authority or protocol detail. The current [Command Contract](../spec/command-contract/README.md) and schemas govern runnable V1 syntax. `Igloo.sln`, `.csproj`, dispatcher registration and focused tests govern implementation claims. Dated lab records govern only their recorded execution scope. [[00 - Home/Source Index]] provides the source hierarchy and [[00 - Home/Architecture Drift Register]] preserves conflicts.

**Review anchor:** committed HEAD `366c1ec57c6e5bde6d452208d4ef6411ae99ebf1`, branch `refactor/community-fleet-foundation`, dirty working tree inspected 10 October 2026. SHA alone cannot reproduce uncommitted One/CLI Client/contract changes. Future reviews must record the new tip and local changes before promoting claims.

---
title: Project Overview
type: moc
product: project
status: mixed
doc_status: current
validation_status: partial
availability: not-applicable
graph_cluster: reference
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [README.md, ROADMAP.md, CONTRIBUTING.md, docs/decisions/013-product-family-and-platform.md]
aliases: []
tags: [igloo]
---

# Project Overview

iGloo helps Windows users migrate to Linux through an open-source Community installer. The accepted [product-family ADR](../decisions/013-product-family-and-platform.md) also describes CLI, One and Fleet as distinct client/control surfaces over a shared platform. The supplied October whitepaper sharpens that target: logical Core owns endpoint execution, and CLI is the canonical executable gateway. [[02 - Architecture/Current Architecture]] describes what this checkout actually does; [[02 - Architecture/Target Architecture]] describes the intended separation.

The [0.2-alpha release](https://github.com/gillesduif/iGloo/releases) is an earlier Community prerelease. [[00 - Home/Project Dashboard]] scopes the uncommitted branch-only CLI Client, One shell, advisory capabilities and Fleet work separately. [ROADMAP](https://github.com/gillesduif/iGloo/blob/main/ROADMAP.md) and [GitHub milestones](https://github.com/gillesduif/iGloo/milestones) remain release planning context. [[18 - Releases and Access/Release Overview]] lists qualification gates; [[08 - Distribution Providers/Distribution Support Matrix]] separates distro evidence levels.

Contributors should begin with [CONTRIBUTING](https://github.com/gillesduif/iGloo/blob/main/CONTRIBUTING.md), [[12 - Development/Developer Onboarding]] and [[10 - Safety and Security/Safety and Security Overview]]. Open-source interoperability does not imply a general public destructive API; the current [Command Contract](../spec/command-contract/README.md) is a bounded project specification.

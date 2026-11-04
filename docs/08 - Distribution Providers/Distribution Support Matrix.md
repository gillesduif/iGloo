---
title: Distribution Support Matrix
type: matrix
product: provider
status: mixed
doc_status: current
validation_status: partial
availability: unknown
graph_cluster: providers
last_reviewed: 2026-10-10
reviewed_commit: 366c1ec57c6e5bde6d452208d4ef6411ae99ebf1
sources: [distros/debian/distro.json, distros/fedora-kde/distro.json, distros/linuxmint-cinnamon/distro.json, distros/ubuntu/distro.json, docs/reference/hardware-findings.md]
aliases: []
tags: [igloo]
---

# Distribution Support Matrix

The table separates manifest state, code and historical evidence from this branch's qualification. It is not a certification that a new uncommitted installer path is safe. [[08 - Distribution Providers/Distribution Provider Overview]] explains the provider boundary.

| Distribution | Source manifest / provider | Recorded validation | Current qualification limit |
| --- | --- | --- | --- |
| Fedora KDE | `available`; provider and Anaconda path | Earlier physical-hardware runs in [findings](../reference/hardware-findings.md) | Current exact owned-storage renderer is opt-in and fixture tested; native writer/readback and end-to-end parity remain open. |
| Linux Mint Cinnamon | `available`; provider and Ubiquity/preseed path | Earlier physical-hardware runs in [findings](../reference/hardware-findings.md) | [ESP binding](../architecture/installer-esp-binding.md) documents a multiple-ESP hazard; current branch needs exact qualification. |
| Debian 13 | `available`; provider and substantial guarded deployment work | Earlier release-era hardware records; new local canonical lab evidence is narrower | Legacy entry points can reject with `NotSupportedException`; new checkpoint/user-data/agent-stage evidence does not establish a general current install path. See [Debian deployment](../architecture/debian-target-root-deployment.md). |
| Ubuntu | `in-development`; Subiquity path | [status record](https://github.com/gillesduif/iGloo/blob/main/distros/ubuntu/STATUS.md) describes partial VM stages | Curtin partition-table/holder problem and no complete validated current run. |
| Deepin and other catalogue entries | mostly `coming-soon` metadata; experimental Deepin work separate | No current public install claim from catalogue alone | Require reviewed provider, exact installer ownership and end-to-end evidence before promotion. |

Historical 0.2-alpha release claims live on [GitHub Releases](https://github.com/gillesduif/iGloo/releases). The [root README](https://github.com/gillesduif/iGloo/blob/main/README.md) describes earlier physical validation. [[13 - Testing and Validation/Evidence Register]] records what this vault pass itself executed. No new hardware test was run to generate this matrix.

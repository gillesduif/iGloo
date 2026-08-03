# Risk register

Living document; reviewed when a milestone closes. Scale: likelihood/impact
**L**ow / **M**edium / **H**igh. "Status" says how much of the mitigation is
real today (July 2026) versus planned.

| # | Risk | L | I | Mitigation | Status |
|---|---|---|---|---|---|
| R-01 | **User data loss** - a partitioning path wipes Windows or user files | M | **H** | BR-01/BR-03 discipline; free-space-only configs; all-preserved storage declarations; installer-preset bans; validation requires "Windows survives" proof per distro | Mitigated for the 3 validated distros; the risk *materialised twice in testing* (Debian partman, Ubuntu curtin) and was caught in VMs - exactly what the VM matrix is for |
| R-02 | **Boot breakage** - machine boots neither OS | L | H | One-shot `BootNext` (auto-fallback to Windows); ESP preserved & reused, never reformatted; BCD untouched | Mitigated; no occurrence across all test runs |
| R-03 | **Supply-chain / tampered ISO** | L | **H** | BR-02 fail-closed verification, pinned fingerprints, bundled keys, HTTPS-only (BR-10) | Mitigated (live-tested against all 4 distros' signing schemes) |
| R-04 | **Mid-install failure leaves a half-state** | M | M | Idempotent re-runs (label re-detection, resume); persistent logs (BR-07); planned one-click rollback (M14) | Partially mitigated - M14 closes it |
| R-05 | **Hardware matrix unknowns** - OEM firmware quirks, Secure Boot variants, exotic layouts | **H** | M | That *is* M15: closed beta across firmware vendors × Secure Boot × BitLocker × GPU; preflight grows a blocker per discovered fatal combo (BR-06) | Open - the next milestone exists to burn this down |
| R-06 | **Resource exhaustion during install** (e.g. Ubuntu's in-RAM installer on small machines) | M | M | Preflight RAM floors per distro (BR-06) block before any disk change | Mitigated for known cases |
| R-07 | **Distro ships a breaking installer change** (new subiquity/Anaconda behaviour, renamed squashfs layers…) | M | M | Per-distro plugins isolate blast radius; per-installer field-note rules documented; catalog `status` flag can park a distro in minutes (as done for Ubuntu) | Accepted & managed - this is the Wubi failure mode, contained by architecture |
| R-08 | **Bus factor = 1** - sole maintainer | H | M | Documentation layer (architecture, BRs, ADRs, STATUS dossiers, white paper) written to make the project survivable; open source (GPL-3.0-or-later) makes forks possible; funding (NLnet/VLAIO) buys maintainer time | Partially mitigated - this documentation push is the mitigation |
| R-09 | **Trust/reputation** - one viral "iGloo ate my disk" story | L | **H** | Everything above, plus: alpha/beta gating, honest status tables (no overclaiming), reversibility story (M13/M14), open source auditability | Managed; never fully closeable |
| R-10 | **Windows updates change the environment** (partition moves, BitLocker auto-enable, boot changes) | M | M | Preflight re-checks every run; nothing assumed from previous runs; RTC/UTC and similar coexistence fixes applied at install time | Accepted - monitored per Windows feature update |
| R-11 | **Legal/trademark** - distro names & logos in the catalog | L | M | Nominative use with disclaimers (README footer); partnership conversations planned with distro projects | Accepted; revisit before 1.0 |

## Reading the register

The two structural insights: the **highest-impact risks (R-01, R-03, R-09) are
design-mitigated** - their countermeasures are architecture, not process - while
the **highest-likelihood risk (R-05) is precisely the next milestone**. That
alignment (roadmap = risk burn-down) is deliberate and worth preserving when
priorities shift.

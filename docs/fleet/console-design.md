# Fleet operator console design

Design specification for `src/Igloo.Fleet.Web`, the Fleet operator console.
Phase 1, step 2: this document is the thing to review. No production code has
been changed.

Inputs: the [console audit](console-audit.md), the seventeen operator routes in
`src/Igloo.Fleet.Server/TrustedFleetServer.cs`, the record and enum vocabulary in
`src/Igloo.Fleet.Contracts` and `src/Igloo.Fleet.Domain`, and the Fleet documents
listed under [sources](#sources). The component library decision is
[ADR-015](../decisions/015-fleet-console-component-library.md).

The nine open points raised in the Phase 1 review were decided on 2026-11-01;
the decisions and their consequences are recorded in
[Decisions from the Phase 1 review](#decisions-from-the-phase-1-review) and
applied throughout. Phase 2 is scoped to the console only: the Fleet Server,
Domain and Contracts are not changed in this work.

## 1. What this console is

The console is a **read-and-approve** interface over Fleet's planning records. An
operator can enrol devices, set their trust status, define migration profiles,
request read-only work, read the evidence that comes back, approve a specific
piece of evidence and prepare a plan from that approval.

It cannot start, schedule or stop anything on an endpoint. A prepared plan is a
signed-off planning artefact; no code path consumes it to execute. The console
must never imply otherwise — not in a label, not in an icon, not in an empty
state, and not in a navigation entry for a page that does not exist.

### Design rules

1. **Every screen element traces to a record or an endpoint.** If no operator
   route returns it, it is not on screen. A column Fleet cannot fill is not a
   column.
2. **Unavailable is stated once, factually, in prose.** Not as a disabled
   button, not as a "Planned" badge, not as a navigation entry.
3. **Status is never colour alone.** Every status badge carries text and a
   distinct icon shape.
4. **Verbs match authority.** Request, submit, approve, prepare, revoke,
   disable. Never run, deploy, start, apply, remediate, push or execute.
5. **Sample data is labelled wherever it appears**, on every page, in
   Development only.

## 2. Glossary

One term per concept. The UI label column is binding: that exact string goes in
the resource file, and no synonym is used anywhere else.

| UI label | Definition | Where it lives in code |
|---|---|---|
| Enrollment | A certificate identity registered with the Fleet Server — the result of enrolling. The primary noun of the console; what the code calls a device. Not a physical machine — see note below. | `TrustedDeviceView`, `DeviceIdentity.DeviceId`, `EnrollmentId` |
| Enrollment ID | The identifier shown for an enrollment. Code carries three GUIDs per enrollment (`DeviceId`, `AgentId`, `EnrollmentId`); the console leads with `DeviceId` because every operator route is keyed by it, and shows the other two on the detail page. | `TrustedDeviceView.Identity.DeviceId` |
| Agent | The endpoint process acting for an enrollment. Runs one cycle at a time; no inbound listener. | `DeviceIdentity.AgentId`, `TrustedHeartbeat` |
| Enrolling | The one-time exchange of a token and a certificate signing request for a client certificate. Used as a verb; the noun for the result is Enrollment. | `EnrollmentRequest`, `EnrollmentResponse`, `EnrollmentService` |
| Enrollment token | A single-use secret valid 1–60 minutes. Shown in plaintext once, at creation. | `EnrollmentTokenCreated`, `EnrollmentTokenInfo` |
| Trust status | Whether the Server accepts work and evidence from an enrollment: Active, Disabled or Revoked. | `AgentTrustStatus`, `ChangeAgentTrustRequest` |
| Client certificate | The certificate issued at enrollment, identified by its SHA-256 hash and expiry. | `TrustedDeviceView.CertificateHash`, `.CertificateExpiresAtUtc` |
| Capability | What an agent reports it can do. Required capabilities gate work requests. | `PlanningCapability`, `TrustedHeartbeat.Capabilities` |
| Last contact | The server-recorded time of the enrollment's most recent heartbeat. | `TrustedDeviceView.LastHeartbeatUtc` |
| Migration profile | A named set of feasibility requirements: distribution, minimum free space, Secure Boot requirement. Not an execution recipe. | `MigrationProfileSpec` |
| Profile revision | An immutable, numbered snapshot of a profile. A new revision supersedes approvals and plans for earlier ones. | `MigrationProfileRevision` |
| Work item | One typed read-only task for one enrollment, with a lifetime and an expiry. Its ID is the one identifier the console shows for the whole chain: code reuses the same GUID as `AssessmentId` and `DryRunId`, and the Server rejects any result where they differ. | `ReadOnlyWorkItem.WorkItemId` (= `AssessmentResult.AssessmentId` = `DryRunEvidence.DryRunId`) |
| Work type | Assessment or Migration dry run. The only two values that exist. | `ReadOnlyWorkType` |
| Work status | Pending, Leased, Completed or Expired. | `ReadOnlyWorkStatus` |
| Lease | A five-minute exclusive claim an agent takes on a work item. | `ReadOnlyWorkItem.LeaseId`, `.LeaseExpiresAtUtc` |
| Assessment | The allowlisted preflight observation an agent returns. Contains fixed check results, RAM total and disk count — nothing identifying. | `AssessmentResult` |
| Check | One fixed preflight subject: Firmware, Secure Boot, TPM, BitLocker, Memory, Disks, Unknown finding. | `CheckId`, `PreflightCheckResult` |
| Check result | Passed, Information, Warning, Blocked or Unknown, per check. | `CheckStatus` |
| Migration dry run | A read-only feasibility run of an assessment plus profile facts against one profile revision. | `ReadOnlyWorkType.RunMigrationDryRun`, `DryRunFacts` |
| Evidence | The immutable server record of one submitted result: the result, its hash, the receipt time, the decision and the capability snapshot. | `DryRunEvidence` |
| Evidence hash | SHA-256 over the canonical JSON of the result. An approval names the exact hash it approves. | `EvidenceIntegrity.Hash` |
| Decision | The Server's planning verdict for one piece of evidence, with reasons and a rules version. Computed for every result, but meaningful only for a dry run — see Eligibility and Assessment outcome. | `PlanningDecision` |
| Eligibility | Eligible, Needs review, Blocked or Not determined. The decision's verdict, shown for **dry-run** evidence only. | `PlanningEligibility` |
| Assessment outcome | Ready for review, Needs review or Blocked. The agent's own verdict over its checks, server-validated, shown for **assessment** evidence. An assessment has no dry-run facts, so its decision is always Not determined with reason Missing evidence; showing that would read as a fault. | `EligibilityStatus`, `AssessmentResult.Eligibility`, `EligibilityDecision.Evaluate` |
| Reason | A fixed reason code attached to a decision, for example Insufficient space. | `PlanningReason` |
| Approval | An operator's explicit decision bound to one evidence hash, decision and profile revision. Valid four hours. | `PlanApproval`, `ApprovalState` |
| Review reason | Operator text required when approving evidence whose eligibility is Needs review. Maximum 500 characters. | `ApprovePlanRequest.ReviewReason` |
| Prepared plan | A non-executable planning artefact created from an approval. No endpoint consumes it. | `PreparedMigrationPlan`, `PreparedPlanState` |
| Safety boundary | The fixed sentence every prepared plan carries, stating it is not executable. | `PreparedMigrationPlan.SafetyBoundary` |
| Audit event | An append-only record of one action, with actor, action, target and correlation. | `FleetAuditEvent` |
| Actor | Who caused an audit event: Operator, Agent or Server. | `FleetAuditEvent.ActorType`, `.ActorId` |
| Correlation ID | The identifier linking a work item to its evidence, decision, approval and plan. | `ReadOnlyWorkItem.CorrelationId` |
| Console sign-in | The local account used to open this web application. Separate from the Server's operator credential. | `FleetOperatorAccount`, `FleetOperatorStore` |

**Note on "enrollment" versus "device".** The code, the API and all six Fleet
documents say *device*. The console says *enrollment*, decided in the Phase 1
review, because `DeviceId` identifies a certificate identity, not hardware:
re-enrolling produces a new `DeviceId` and `AgentId`, and
[enrollment.md](enrollment.md) states that Fleet "cannot deduplicate physical
machines through hostname reuse". An administrator coming from Intune reads
"device" as a machine and would be wrong in a way that matters — two rows can
be one machine. The word *device* appears in the console only where it quotes a
code identifier (`DeviceId`). The console does not explain the relationship in
page chrome; Scope and limits states it.

### Words this console does not use for current capability

Device (except as a quoted identifier), cohort, group, ring, fleet health,
managed device, inventory, telemetry, compliance, policy, posture, remediation,
deployment, execution, recovery, readiness, onboarding, provisioning. Each
either names a capability Fleet does not have or is a second name for a term
above. Module pages (section 3) use them only as page titles and column
headers over sample or Server-provided rows, never as a claim about Fleet's
capabilities.

Also banned, from the brief: seamless, effortless, powerful, unlock, empower,
leverage, magic, smart, AI-powered, revolutionary. No exclamation marks, no
emoji.

## 3. Information architecture

Twenty-one capability routes, every one backed by an operator endpoint or by
local console state, plus 64 module pages over `IModuleDataSource`. Compare with
the 98 routes and 86 navigation entries audited in F1.

```
Header (not in the left nav)
  Global search ............ in-page, scoped to the current list
  Help ..................... opens Scope and limits
  Account menu ............. console account, Settings, Sign out

Overview .................... /
Enrollments
  All enrollments ........... /enrollments
  Enrollment ................ /enrollments/{deviceId}
      Tabs: Overview · Trust and certificate · Agent · Evidence · Work · Plans · Audit
  Enrollment tokens ......... /enrollments/tokens
Planning
  Migration profiles ........ /planning/profiles
  Profile ................... /planning/profiles/{profileId}
  Work items ................ /planning/work
  Work item ................. /planning/work/{workItemId}
  Evidence .................. /planning/evidence
  Evidence record ........... /planning/evidence/{dryRunId}
  Approvals ................. /planning/approvals
  Approval .................. /planning/approvals/{approvalId}
  Prepared plans ............ /planning/plans
  Prepared plan ............. /planning/plans/{planId}
Audit
  Audit events .............. /audit

Inventory
  Hardware .................. /inventory/hardware
  Operating systems ......... /inventory/os
  Network ................... /inventory/network
  Agent versions ............ /inventory/agent-versions
Cohorts ..................... /cohorts
Software .................... /software/{applications|packages|repositories|deployment|installed}
Updates ..................... /updates/{available|security|rings|deployment|history}
Configuration ............... /configuration/{policies|profiles|scripts|environment|services|scheduled-jobs|ssh}
Security .................... /security/{posture|disk-encryption|secure-boot|firewall|ssh|accounts|sudo|certificates|compliance}
Storage ..................... /storage/{disks|partitions|filesystems|mounts|lvm|raid|encryption}
Networking .................. /networking/{interfaces|addresses|routes|dns|firewall|connectivity}
Monitoring .................. /monitoring/{health|cpu-memory|storage|network|services|events|logs|alerts}
Automation .................. /automation/{scripts|jobs|schedules|history|templates|maintenance-windows}
Reports ..................... /reports/{inventory|compliance|updates|security|migrations|custom}
  Record .................... /{module}/{page}/{recordId}

Settings (account menu)
  Server connection ......... /settings/server
  Console accounts .......... /settings/accounts
  Scope and limits .......... /settings/scope
  About ..................... /settings/about

Unauthenticated
  Sign in ................... /login
  First-run setup ........... /setup
  Access denied ............. /access-denied
  Page not found ............ router fallback
```

Three capability groups plus Overview and Audit, then the module groups. The
left navigation is collapsible, the current group is expanded, and the current
page is marked with `aria-current="page"`.

Routes that exist today and are **removed**: the 81 placeholder routes, the six
orphan routes (`/approvals`, `/audit`, `/evidence`, `/plans`, `/profiles`,
`/work` — `/audit` is reused with new content), and `/devices`, `/devices/*`,
`/profile`, `/administration/*`, `/home/*`.

### How unavailable capability is shown

Two ways, and nothing else.

**Scope and limits** (`/settings/scope`), reachable from Help in the header and
from the account menu, states in prose what Fleet does — enrollment, trust,
read-only assessment and dry run, evidence, approval, prepared plans, audit —
what it does not do and why, and that a prepared plan is not an execution
permit. Where an operator could reasonably expect more on a specific page, that
page carries one factual sentence inline, for example on the prepared plan page:
*"No Fleet operation consumes a prepared plan. Migration runs locally, under the
Community application or the CLI, with its own checks."*

**Module groups** follow Audit events: Inventory, Cohorts, Software, Updates,
Configuration, Security, Storage, Networking, Monitoring, Automation and
Reports, 64 pages in all, defined once in `ModuleCatalog` (Presentation). Every
page is the same component, `ModulePage`: a list with sortable columns, a
status filter, in-page search, a column chooser for optional columns, CSV
export (`/export/modules/{module}/{page}.csv`), pagination, a details page per
record and, where the object is operator-created (cohorts, applications,
repositories, deployments, rings, policies, profiles, scripts, jobs, schedules,
templates, maintenance windows, custom reports), a create dialog whose fields
come from the catalog and whose result is written to the console audit log.
Rows come from `IModuleDataSource`. In Development the `ModuleSampleStore`
generates deterministic sample rows that reference the sample enrollments, and
every page carries the sample-data bar. Outside Development the
`UnavailableModuleDataSource` is registered and each page states "This Fleet
Server doesn't provide data for this page." with no grid and no create action.
Module statuses use one extra scale, `StatusCatalog.Module`, with the colour
and shape rules of section 5. Remote execution and recovery remain absent
([ADR-013](../decisions/013-product-family-and-platform.md)).

## 4. Shell

### Header

Left to right: product name **iGloo Fleet** linking to Overview; global
search; Help; the account menu. There is no environment badge.

- **Sample data bar.** In Development, when sample data is active, every page
  opens with a `MessageBar` of intent `Warning` at the top of the main
  landmark: *"Sample data. This page shows sample data."* — which closes audit
  finding F14.
- **Theme menu** offers Use system setting, Light and Dark. The choice is stored
  per console account and applied through the Fluent UI script
  (`Blazor.theme.setThemeMode`, see ADR-015); the server also renders the stored
  choice as `body[data-theme]` so the first paint has the right colours.
- **Reconnect dialog.** When the circuit drops, a themed dialog (tokens, not the
  framework's white default) says the connection was lost, counts down retries
  and offers Retry or Resume. Strings live in ShellStrings; the script is
  `wwwroot/reconnect.js`, served from the console itself.
- **Global search** filters the current list and is disabled with an explanatory
  tooltip on pages that are not lists. There is no cross-object search endpoint;
  the console does not pretend to have one.
- **Account menu** shows the console display name and user name, the role as
  recorded locally, and links to Settings pages and Sign out. It must not list
  operator or role management that does not exist (audit F5).

### Left navigation

Collapsible, grouped as in section 3. Groups expand to show their pages. Collapse
state persists per browser. At 1280 px the navigation is collapsed to icons with
accessible names; below 1280 px is out of scope.

### Breadcrumbs

On every page below the top level. `Devices / 10000000-… / Evidence`. The last
item is the current page and is not a link. The device segment shows the
shortened device ID with the full value in its tooltip.

### Page frame

```
┌─ breadcrumbs ────────────────────────────────────────────────────────┐
│ H1 page title                                                        │
│ (no description line; the title and breadcrumbs carry the context)   │
├─ command bar ────────────────────────────────────────────────────────┤
│ [Primary action] [Secondary]            [Refresh] [Export] [Columns] │
├─ filter row (lists only) ────────────────────────────────────────────┤
│ [search] [filter pill ×] [filter pill ×]              Clear filters  │
├─ content ────────────────────────────────────────────────────────────┤
│                                                                      │
├─ footer ─────────────────────────────────────────────────────────────┤
│ Showing 1–25 of 148            [‹] 1 2 3 [›]        25 per page ▾    │
└──────────────────────────────────────────────────────────────────────┘
```

Exactly one `<h1>` per page, which is the page title, closing audit finding F11.

### Rendering

`InteractiveServer` with prerendering, set once in `Routes.razor`. Sign-in,
first-run setup and sign-out stay static antiforgery-protected form posts, as
they are today. This closes audit finding F3 and satisfies the Fluent UI
requirement recorded in [ADR-015](../decisions/015-fleet-console-component-library.md).

### Themes and density

Light and dark, switched through `ThemeService` and persisted per console
account. The default follows the operating-system preference. Density follows
the Fluent default; tables use the compact row height. Both themes must pass
4.5:1 for text and 3:1 for non-text indicators, measured and recorded in Phase 2.

### Paging, sorting and filtering

The Server returns whole collections with no filtering, sorting or paging
(audit, "Capability envelope"). All of it therefore happens client-side, over
the full collection already loaded. Consequences, which the design accepts:

- Row counts are exact, because the whole collection is in hand.
- Audit events are the only collection likely to grow without bound. The audit
  page loads the full collection and virtualizes the grid.
- No "load more" affordance implies server paging that does not exist.
- There is no single-object endpoint either, so every detail page loads its
  collection and selects one record from it. A detail page opened by URL for an
  identifier that is not in the collection shows "Not found", not an error.

### Multi-select

**None.** Every mutating operator route acts on a single object, so a multi-row
selection would exist only to drive a loop that could half-succeed with no
transaction and no single audit event. Bulk needs are served by CSV export.

### Secrets

An enrollment token's plaintext exists only in the response to the create call.
The console shows it once, in the dialog that created it, with a Copy button and
the line *"This token is shown once. Copy it now and deliver it to the device
over a channel you trust."* It is never written to logs, never re-rendered,
never placed in a URL, and the dialog cannot be reopened. Closing the dialog
returns to the token list, where the token appears by ID only.

## 5. Status catalogue

Every status is defined once here and rendered by one shared component. Colour is
always accompanied by text and a distinct icon shape.

### Trust status — `AgentTrustStatus`

| Value | UI label | Icon | Colour role | Meaning |
|---|---|---|---|---|
| `Active` | Active | Checkmark circle | Success | The Server accepts work and evidence from this device. |
| `Disabled` | Disabled | Pause circle | Warning | Temporarily refused. Can be set back to Active. |
| `Revoked` | Revoked | Prohibited | Error | Permanently refused. Terminal; approvals and plans are invalidated. |

### Client certificate — derived

| Condition | UI label | Icon | Colour role |
|---|---|---|---|
| `CertificateExpiresAtUtc` in the future | Valid until *date* | Certificate | Neutral |
| Expires within 14 days | Expires in *n* days | Warning triangle | Warning |
| Expired | Expired | Error circle | Error |

The 14-day threshold is a console presentation rule, not a Server rule, and the
page says so. Phase 1 has no automatic renewal ([enrollment.md](enrollment.md)).

### Enrollment token — derived from `EnrollmentTokenInfo`

| Condition | UI label | Icon | Colour role |
|---|---|---|---|
| Not consumed, not revoked, not expired | Available | Key | Information |
| `Consumed` | Used | Checkmark circle | Neutral |
| `Revoked` | Revoked | Prohibited | Neutral |
| Past `ExpiresAtUtc` | Expired | Clock dismiss | Neutral |

Consumed, revoked and expired are all neutral: none is a problem, they are just
not usable.

### Work status — `ReadOnlyWorkStatus`

| Value | UI label | Icon | Colour role |
|---|---|---|---|
| `Pending` | Waiting for agent | Clock | Information |
| `Leased` | Claimed by agent | Arrow sync | Information |
| `Completed` | Completed | Checkmark circle | Success |
| `Expired` | Expired | Clock dismiss | Neutral |

### Check result — `CheckStatus`

| Value | UI label | Icon | Colour role |
|---|---|---|---|
| `Passed` | Passed | Checkmark circle | Success |
| `Information` | Information | Info circle | Information |
| `Warning` | Warning | Warning triangle | Warning |
| `Blocked` | Blocked | Prohibited | Error |
| `Unknown` | Not determined | Question circle | Neutral |

`Unknown` is rendered as **Not determined** because the agent could not read the
subject, which is different from a negative result.

### Eligibility — `PlanningEligibility`

| Value | UI label | Icon | Colour role | What the operator can do |
|---|---|---|---|---|
| `Eligible` | Eligible | Checkmark circle | Success | Approve, no review reason needed. |
| `NeedsReview` | Needs review | Warning triangle | Warning | Approve only with a review reason. |
| `Blocked` | Blocked | Prohibited | Error | Cannot be approved. |
| `Unknown` | Not determined | Question circle | Neutral | Cannot be approved. |

Eligible never reads as "ready": it means this evidence may be approved, nothing
more. The evidence page states underneath: *"Eligible means this evidence can be
approved for planning. It is not a migration permit."*
[dry-run.md](dry-run.md) records the rule that Not determined never becomes
Eligible; the console must not imply that re-reading alone will change it.

### Assessment outcome — `EligibilityStatus` (assessment evidence only)

| Value | UI label | Icon | Colour role | Meaning |
|---|---|---|---|---|
| `ReadyForReview` | Ready for review | Checkmark circle | Success | Every required check passed; an operator may now decide whether to request a dry run. |
| `NeedsReview` | Needs review | Warning triangle | Warning | A warning or an unreadable check; look at the checks before requesting a dry run. |
| `Blocked` | Blocked | Prohibited | Error | At least one check blocked. |

Ready for review never reads as "ready to migrate". An assessment cannot be
approved — only dry-run evidence can — so no approve action appears on
assessment evidence, and the page says why.

### Approval state — `ApprovalState`

| Value | UI label | Icon | Colour role |
|---|---|---|---|
| `Approved` | Approved | Checkmark circle | Success |
| `Revoked` | Revoked | Prohibited | Neutral |
| `Expired` | Expired | Clock dismiss | Neutral |
| `Superseded` | Superseded | Arrow swap | Neutral |

### Prepared plan state — `PreparedPlanState`

| Value | UI label | Icon | Colour role |
|---|---|---|---|
| `Prepared` | Prepared | Document checkmark | Success |
| `Expired` | Expired | Clock dismiss | Neutral |
| `Invalidated` | Invalidated | Prohibited | Warning |
| `Superseded` | Superseded | Arrow swap | Neutral |

### Server connection — console-side, derived

| Condition | UI label | Colour role |
|---|---|---|
| Last request succeeded | Connected | Success |
| 401 or 403 | Not authorized | Error |
| TLS validation failed | Certificate not trusted | Error |
| Connection refused or timed out | Unreachable | Error |

These four are distinct because they need four different operator actions, which
closes the generic "Devices are unavailable" state in audit finding F4.

## 6. Pages

Every page specifies: source, layout, command bar, columns or fields, and its
states. Shared states are defined once in section 7.

### 6.1 Overview — `/`

**Source:** `GET /v2/operator/devices`, `/evidence`, `/work`, `/approvals`,
`/plans`.

Four tiles across the top, each a link to a pre-filtered list, each stating what
it counts. No charts.

| Tile | Value | Links to |
|---|---|---|
| Enrollments | Count of enrollments, with a second line "*n* Active · *n* Disabled · *n* Revoked" | `/enrollments` |
| Evidence received | Count of evidence records | `/planning/evidence` |
| Evidence awaiting approval | Dry-run evidence whose eligibility is Eligible or Needs review and that no Approved approval references | `/planning/evidence` with that filter applied |
| No contact in *n* days | Enrollments whose last contact is older than the threshold, or that have never reported | `/enrollments` with that filter applied |

The fourth tile is deliberately **not** called health. Fleet agents are one-shot
processes started by hand or by a local scheduler
([architecture.md](architecture.md)); there is no expected heartbeat interval, so
heartbeat age cannot be presented as a health signal. The tile states an age and
its threshold, and the page footnotes: *"Agents report when they run. A gap does
not by itself indicate a fault."* The threshold is a console setting
(`Fleet:Console:StaleContactDays`, default 7); its final value is deferred to the
end of Phase 2 (Decisions, point 8).

Below the tiles, two panels:

- **Recent activity** — the ten most recent audit events, with a link to Audit.
- **Server connection** — the connection status from section 5, the Server URI,
  the protocol version, and a link to Settings → Server connection.

The third tile's "awaiting approval" count is computed in the console. The Server
decides at approval time whether evidence is still current
(`PlanningValidity.EvidenceError`), so the tile's footnote reads: *"The Server
re-checks that evidence is current when you approve it."*

In the page specifications below, *device* appears only inside code identifiers
and API routes, which are the Server's words. Everything operator-facing says
*enrollment*.

### 6.2 All enrollments — `/enrollments`

**Source:** `GET /v2/operator/devices`.
**Description:** none (section 8).

**Command bar.** Left: **Create enrollment token** (opens the dialog from 6.4).
Right: Refresh · Export CSV · Columns.

**Filters.** Search over device ID, agent ID and enrollment ID. Filter pills:
Trust status, Capability, Last contact (Within 24 hours / 7 days / 30 days /
Older / Never), Certificate (Valid / Expires within 14 days / Expired).

**Columns.** Default columns marked •.

| Column | Source | Notes |
|---|---|---|
| • Enrollment ID | `Identity.DeviceId` | First eight characters, monospace, full value in tooltip and on copy |
| • Trust status | `Status` | Badge |
| • Last contact | `LastHeartbeatUtc` | Absolute date and time, local, with relative age as secondary text. "Never" when null |
| • Agent version | `Heartbeat.AgentVersion` | "Not reported" when no heartbeat |
| • Certificate | derived | Badge |
| • Capabilities | `Heartbeat.Capabilities` | Count, with the list in a tooltip |
| iGloo version | `Heartbeat.IglooVersion` | Off by default |
| Protocol | `Heartbeat.Protocol` | Off by default |
| Agent ID | `Identity.AgentId` | Off by default |
| Enrollment ID | `EnrollmentId` | Off by default |
| Certificate expires | `CertificateExpiresAtUtc` | Off by default |

There is no **Device name**, **OS**, **OS version** or **Model** column. Fleet
does not collect any of them (audit F7). The page does not explain this; Scope
and limits does.

**Row action.** The enrollment ID is a link. The whole row is not a link, and no
row carries `role="link"` (audit F11).

**States.** Loading, empty, no results, error, sample data.

### 6.3 Enrollment — `/enrollments/{deviceId}`

**Source:** `GET /v2/operator/devices` filtered client-side, plus `/evidence`,
`/work`, `/approvals`, `/plans`, `/audit` filtered by this device.

**Title.** The shortened enrollment ID, with the full ID and a Copy button beneath.
**Command bar.** Left: **Change trust status**. Right: Refresh.

Seven tabs. Each tab is a route segment so it can be linked and bookmarked.

| Tab | Content |
|---|---|
| Overview | Identity card (enrollment ID, agent ID, the Server's `EnrollmentId`, and the "certificate identity" sentence); Last contact card; Capabilities card; a summary line per related collection with a link to its tab |
| Trust and certificate | Current trust status, the change action with its confirmation, certificate hash with Copy, issue and expiry dates, and the sentence that Phase 1 has no automatic renewal |
| Agent | Agent version, iGloo version, protocol version, capability list with the plain-language meaning of each |
| Evidence | The evidence grid of 6.8, scoped to this device |
| Work | The work grid of 6.7, scoped to this device |
| Plans | The approvals and prepared plans for this device |
| Audit | The audit grid of 6.10, scoped to this device's correlation IDs |

The twelve tabs of the current page become seven, and none of them is a
"Planned" badge (audit F2).

**Change trust status** opens a dialog: the three values as radio options with
their one-line meanings from section 5, and an explicit confirmation step for
Revoked that states *"Revoked is permanent. This device's approvals and prepared
plans are invalidated and it cannot be set back to Active."* The dialog is
keyboard-operable, returns focus to the command bar button, and reports the
outcome in a `MessageBar` on the page, not a transient toast.

### 6.4 Enrollment tokens — `/enrollments/tokens`

**Source:** `GET` and `POST /v2/operator/enrollment-tokens`,
`POST /v2/operator/enrollment-tokens/{id}/revoke`.

**Description:** none (section 8).

**Command bar.** Left: **Create token**. Right: Refresh · Export CSV · Columns.
**Filters.** Search over token ID. Pills: state (Available / Used / Revoked /
Expired), created by.

**Columns.** Token ID (short, copyable) · State · Created · Expires · Created by.

**Row action.** **Revoke**, enabled only while the token is Available, with a
confirmation dialog.

**Create token dialog.** One field, lifetime in minutes, numeric, 1–60, default
10, with the valid range in the field description and client validation matching
the Server's. On success the dialog switches to a result view containing the
plaintext token in a read-only field, a Copy button, the expiry time and the
one-time warning from section 4. The only way out is Close, which returns to the
list. The token value is never logged, never re-rendered and never in a URL.

**Note on "Created by".** It is the Server's actor string, currently always
`engineering-operator`. The console keeps its own record of which console
account performed the action, and shows both — see
[Decisions](#decisions-from-the-phase-1-review), point 7.

### 6.5 Migration profiles — `/planning/profiles`

**Source:** `GET` and `POST /v2/operator/profiles`.

**Description:** none (section 8).

**Command bar.** Left: **Create profile**. Right: Refresh · Export CSV · Columns.
**Filters.** Search over name. Pills: distribution, Secure Boot required.

**Columns.** Name · Distribution · Minimum free space · Secure Boot required ·
Latest revision · Created.

Rows group by `ProfileId` and show the latest revision; the revision count links
to the profile page.

**Create profile dialog.** Name (1–80 characters); Distribution (a Select of
exactly `debian`, `fedora-kde`, `linuxmint-cinnamon` — Ubuntu is rejected by the
Server and is not offered); Minimum free space (20 GiB to 1 TiB, entered in
GiB, hint "20 to 1024 GiB."); Secure Boot required (a switch with no hint).
Client validation matches `PlanningService.SaveProfile` exactly, so a valid form
cannot produce a `ProfileInvalid` response. The planner's larger-of rule is
documented in Scope and limits, not in the dialog.

### 6.6 Profile — `/planning/profiles/{profileId}`

Header: profile name and the latest revision number.
**Command bar.** Left: **Add revision**. Right: Refresh · Export CSV.

A revision table, newest first: Revision · Created · Distribution · Minimum free
space · Secure Boot required · Work items using it.

An inline `MessageBar` of intent `Warning` above the **Add revision** action:
*"A new revision supersedes approvals and prepared plans made against earlier
revisions. A new dry run is required."* The confirmation dialog repeats it.

### 6.7 Work items — `/planning/work`

**Source:** `GET` and `POST /v2/operator/work`.

**Description:** none (section 8).

**Command bar.** Left: **Request work**. Right: Refresh · Export CSV · Columns.
**Filters.** Search over work item ID, device ID and correlation ID. Pills: type,
status, device.

**Columns.** Work item ID (short) · Device · Type · Status · Created · Expires ·
Attempts · Profile revision.

**Request work dialog.** Device (a searchable Select listing Active devices only
— Disabled and Revoked devices are rejected by the Server and are therefore
absent, with the reason stated); Type (Assessment or Migration dry run); Profile
revision (shown only for a dry run, required there, forbidden otherwise); Lifetime
in minutes (1–1440, default 60).

The dialog checks the device's reported capabilities against the type's
requirements before enabling submit, and when they are missing says which:
*"This device has not reported MigrationDryRunV1 and ProfileSchemaV1. A dry run
cannot be requested until its agent reports them."* The Server would return
`CapabilityUnsupported`; the console should not need to ask.

The success message is *"Work item created. It will be carried out the next time
this device's agent runs."* — not "started", not "queued for execution".

### 6.8 Evidence — `/planning/evidence`

**Source:** `GET /v2/operator/evidence`.

**Description:** none (section 8).

**Command bar.** Left: none — evidence is created by agents, not operators.
Right: Refresh · Export CSV · Columns.
**Filters.** Search over evidence ID, device ID, correlation ID and evidence
hash. Pills: eligibility, work type, reason, device, received (date range),
approval state (Awaiting approval / Approved / Not approvable).

**Columns.** Evidence ID (short) · Device · Type · Eligibility · Reasons (count,
with the list in a tooltip) · Received · Outcome.

### 6.9 Evidence record — `/planning/evidence/{dryRunId}`

Four stacked cards, then the action.

1. **Decision** — eligibility badge, decision ID, rules version, and every reason
   as a row with its code, its plain-language meaning and the fact it came from.
2. **Checks** — one row per check: subject, result badge, and for `Unknown` the
   sentence *"The agent could not read this subject."* The six fixed checks are
   always listed; a seventh row appears only when an unknown finding was mapped.
3. **Dry run facts** (dry runs only) — target supported, Secure Boot observed,
   available bytes, required bytes, and the plug-in hash with Copy. Available
   bytes null renders as "Not determined", not 0.
4. **Provenance** — work item, correlation ID, profile revision, agent and iGloo
   versions, capability snapshot, received time, and the evidence hash with Copy.

**Approve** is the primary action. It is present only when eligibility is
Eligible or Needs review. For Blocked and Not determined there is no disabled
button; there is a sentence: *"Blocked evidence cannot be approved. Resolve the
reasons above and request a new dry run."*

**Approve dialog.** Shows the exact evidence hash, decision ID and profile
revision being approved, so that what is approved is visible. A **Review reason**
field is required when eligibility is Needs review (maximum 500 characters, with
a counter) and optional otherwise. Above the field: *"This text is stored in the
Fleet database. Do not put secrets in it."* The confirmation line reads: *"You
are approving this evidence for planning. This does not authorize a migration."*

Beneath the whole page, once: *"Evidence is immutable. A new result for the same
work item must match it exactly or it is rejected."*

### 6.10 Approvals — `/planning/approvals` and `/planning/approvals/{approvalId}`

**Source:** `GET` and `POST /v2/operator/approvals`,
`POST /v2/operator/approvals/{id}/revoke`.

**List columns.** Approval ID (short) · Device · State · Approved · Expires ·
Profile revision · Review reason (truncated, full text in the detail page) ·
Approved by.

**Filters.** Pills: state, device, profile. Search over approval ID, device ID
and evidence hash.

**Detail page.** The approval's fields, the evidence it is bound to with a link,
the prepared plan it produced if any, and the audit events sharing its
correlation ID.

**Actions.** **Prepare plan**, present only while the state is Approved and no
plan exists for this approval. **Revoke approval**, present while the state is
Approved, with a confirmation that states *"Revoking also invalidates the
prepared plan created from this approval."*

Approvals expire four hours after they are made. The detail page shows the
remaining time and the rule.

### 6.11 Prepared plans — `/planning/plans` and `/planning/plans/{planId}`

**Source:** `GET` and `POST /v2/operator/plans`.

**Description:** none (section 8).

**List columns.** Plan ID (short) · Device · State · Created · Expires ·
Distribution · Profile revision · iGloo version.

**Detail page.** Identity and lineage (plan, approval, evidence, decision,
profile revision, evidence hash, plug-in hash), state, validity window, and —
rendered prominently rather than in fine print — the plan's own `SafetyBoundary`
string, which the Server sets to *"Non-executable planning artifact. Recovery
readiness and fresh execution authorization required in a future phase."*

There is no action on this page other than Export. The page states: *"No Fleet
operation consumes a prepared plan. Migration runs locally, under the Community
application or the CLI, with its own checks."*

### 6.12 Audit events — `/audit`

**Source:** `GET /v2/operator/audit`.

**Description:** none (section 8).

**Command bar.** Right: Refresh · Export CSV · Columns.
**Filters.** Search over actor, action, target ID and correlation ID. Pills:
actor type (Operator / Agent / Server), action, date range.

**Columns.** Time · Actor type · Actor · Action · Target · Correlation ID.

Actions are rendered as sentence-case labels from a fixed map, with the raw code
in a tooltip: `PlanApproved` → "Plan approved", `AssessmentReceived` →
"Assessment received", `PreparedPlanInvalidated` → "Prepared plan invalidated",
and so on. An unmapped action renders its raw code rather than being hidden.

Target ID links to the object it names when the type can be inferred from the
action.

The grid is virtualized. A note states that the Server returns the whole audit
collection and the console sorts and filters it locally.

### 6.13 Settings → Server connection — `/settings/server`

Shows the configured Server URI, the connection status from section 5, the
protocol version the Server reports, and the CA certificate path in use.

This page carries the honest statement of audit finding F4: the browser host
cannot currently authenticate to the Server. Until that is implemented it reads:
*"This console cannot yet send operator credentials to the Fleet Server. Operator
requests are made with the command-line client. See the Fleet Phase 1 operations
guide."* with a link, and no retry button that cannot work.

When the credential path is implemented, this page is where the operator
credential and the CA certificate are configured, and the four connection states
each get their own remedy sentence.

### 6.14 Settings → Console accounts — `/settings/accounts`

Lists the local console accounts: display name, user name, role, created.

`FleetOperatorStore` creates exactly one administrator and has no operation to
add a second. The page therefore shows the one account and states: *"This
release supports one console administrator, created at first run. Additional
accounts and roles are not available."* No **Add** button, no disabled control.

### 6.15 Settings → Scope and limits — `/settings/scope`

The capability statement from section 3. Written for an administrator evaluating
the product, not for an engineer. Names what Fleet does, what it does not do and
why, and states that a prepared plan is not an execution permit.

### 6.16 Settings → About — `/settings/about`

Console version, protocol version, Server URI, licence, third-party notices, and
a link to Scope and limits.

### 6.17 Sign in — `/login`

Unchanged in behaviour: a static antiforgery-protected form post. Changes: the
username `pattern` attribute is corrected or removed so Chromium does not reject
it (audit F11); the error message stays generic; failures are rate-limited and
recorded.

### 6.18 First-run setup — `/setup`

Unchanged in behaviour. Same `pattern` fix. The password rule (12–128 characters)
is stated before the field, not only after a failure.

### 6.19 Access denied and Page not found

Both use the standard page frame with an `<h1>`, a plain explanation, and one
link back to Overview. Access denied names the signed-in account so the operator
knows which identity was refused.

## 7. Shared states

Every page implements all applicable states. They are components, not per-page
markup.

| State | When | Shape |
|---|---|---|
| Loading | A request is in flight | Skeleton rows for grids, spinner for detail pages, `aria-busy` on the region |
| Empty | The collection is genuinely empty | Icon, one-line title naming the object, one sentence explaining how the object comes to exist, and the primary action when the operator can create one |
| No results | Filters exclude everything | "No *objects* match the current filters" and a **Clear filters** action. Never the Empty state |
| Error | The request failed | The specific connection state from section 5, what it means, what to do, and a **Retry** action that works |
| Not authorized | 401 or 403 | Stated as an authorization failure against the Fleet Server, distinct from console sign-in, with a link to Server connection |
| Unavailable | The capability does not exist | One factual sentence in prose. No button, enabled or disabled |
| Sample data | Development with sample data on | Page-level `MessageBar`, on every page |

Empty-state copy, for example, on Evidence: *"No evidence yet. Evidence appears
when an agent completes a work item and submits its result."*

## 8. Copy rules

- Microsoft Writing Style Guide, in the register of the Microsoft 365, Intune
  and Entra admin centers. Sentence case for every title, label, button, column
  header and menu item; only proper nouns and the product name are capitalised.
- Pages carry no description line, no definitional sentence ("An enrollment is
  a certificate identity, not a device record") and no explanatory note under
  tiles, tables or columns. The title, breadcrumbs, column headers and status
  labels carry the context, as in the Intune and Entra admin centers. The only
  prose pages are Scope and limits and About. The `Description` parameter of the
  page frame remains for computed one-liners (a profile's current revision, the
  work type of an evidence record, the not-found message).
- Contractions are used where the style guide uses them ("can't", "doesn't",
  "isn't"). No "we", no "please", no "simply", no "just", no "right now".
- Field hints state the accepted range or the required condition in one short
  fragment ("1 to 60 minutes."). Inline notes appear only where an action is not
  available and name the state that blocks it ("Blocked evidence can't be
  approved."). Anything longer is cut or moved to Scope and limits.
- The `.resx` files are the source of truth for wording; sentences quoted in
  this document are indicative.
- Second person, present tense, active voice. "Request work", not "Work can be
  requested".
- No marketing language. The banned-word list in section 2 is enforced by a
  review step, and the words appear in the review checklist.
- No exclamation marks, no emoji, no "please", no "simply", no "just".
- Buttons are verbs that match the authority in section 1.
- Errors say what happened, then what to do. No error text contains a stack
  trace, an exception type, a raw Server diagnostic string, a file path or a
  credential.
- Dates and times are absolute UTC in ISO order (`2026-11-01 14:30 UTC`), with
  relative age as secondary text where it helps, never relative alone. UTC
  rather than the operator's zone because Blazor Server does not know the
  browser's zone without script, and because an audit question should have one
  answer wherever the console or the operator sits. CSV exports carry ISO 8601
  UTC timestamps.
- Identifiers are monospace, shortened to eight characters on screen, with the
  full value available by tooltip and Copy.
- Byte sizes use binary units (GiB, TiB) because the domain does, and show the
  exact byte count in a tooltip.
- One title format: `{Page} - iGloo Fleet`, one separator, plain ASCII, closing
  audit findings F8, F9 and F12.

## 9. Localization

- Every operator-facing string moves into `.resx`. Nothing user-visible stays in
  Razor markup or `appsettings.json`.
- One resource file per page or shared component, mirroring the component path,
  plus `SharedStrings.resx` for statuses, actions and states. Status labels live
  in exactly one file, so section 5 has one implementation.
- `IStringLocalizer<T>` injected per component.
- English (`.resx`, the neutral fallback) ships first, in US English following
  the Microsoft Writing Style Guide (enroll, license, username, "Try again",
  "{0} hr ago"). `nl`, `fr` and `de` files exist with a subset of keys, so the
  structure is proved rather than promised, but they are not offered until
  complete.
- `AddRequestLocalization` with the **offered** cultures only
  (`ConsoleOptions.OfferedCultures`, currently `en`), resolved from the
  `.AspNetCore.Culture` cookie and never from `Accept-Language`. A culture joins
  the offered list only when its satellites cover every key; until then the
  files exist as structure and nothing resolves to them. Reason, observed on
  2026-11-01: with `Accept-Language: nl` the shell vocabulary rendered in Dutch
  over English pages. The cookie is set by `POST /auth/culture` from the
  account menu, which lists the offered cultures and says that more appear when
  their translation is complete.
- Dates, times and numbers formatted with `CultureInfo.CurrentCulture`.
  Identifiers, hashes and enum codes are never localized.
- No string is built by concatenation. Counts use composite formats with
  placeholders so word order can change.

## 10. Accessibility

Target: WCAG 2.1 AA and EN 301 549.

- One `<h1>` per page; heading levels descend without gaps.
- Landmarks: one `<header>`, one `<nav aria-label="Main navigation">`, one
  `<main>`, one `<footer>`. A skip link to `<main>` is the first focusable
  element.
- Every interactive element is reachable and operable by keyboard, in a logical
  order. No element carries a click handler without a keyboard equivalent.
- Visible focus on every focusable element in both themes, at 3:1 against its
  background.
- Tables keep their table semantics. No `role="link"` on a `<tr>` (audit F11);
  the identifier cell holds the link.
- Dialogs trap focus, close on Escape, and return focus to the control that
  opened them.
- Status is never colour alone: text plus a distinct icon shape, per section 5.
- Every control has a programmatic name. Icon-only buttons carry an accessible
  name and a tooltip with the same text.
- Live regions: grid results announce their count on filter change; command
  outcomes are announced through a `MessageBar` in a polite live region.
- Contrast: 4.5:1 for text, 3:1 for non-text indicators, measured in both themes.
- Evidence, per Phase 2: an axe scan of every page with zero serious or critical
  violations, in both themes, at 1366×768 and 1920×1080, plus a recorded keyboard
  walkthrough of the enrollment, work, approval and prepare flows.

## 11. Security and compliance requirements for Phase 2

Carried forward from the audit, to be implemented and then verified:

- A Content-Security-Policy restricted to `'self'`, with no `unsafe-inline` for
  scripts, plus `X-Content-Type-Options`, `Referrer-Policy`,
  `X-Frame-Options`/`frame-ancestors`, and HSTS. The CSP is the control that
  enforces the air-gap claim in
  [ADR-015](../decisions/015-fleet-console-component-library.md).
- HTTPS redirection, and no plain-HTTP endpoint in `launchSettings.json`.
- A fallback authorization policy, so authorization does not depend only on the
  router.
- Rate limiting and lockout on sign-in, and audit records for console sign-in,
  sign-out and failure.
- Operator credential and CA root configuration for the browser host, matching
  the chain validation `FleetOperatorCli` already performs.
- No secret in a log, a URL or a re-render.
- `THIRD-PARTY-NOTICES.md` updated for the library change.

## 12. Build and verification

- `dotnet build` with 0 warnings under the repository's `AnalysisMode=All`.
- `ArchitectureTests` continues to pass: Fleet.Web references only Contracts.
- bUnit tests for the status component, each shared state, the grids' filter and
  sort behaviour, the dialogs' validation, and the rule that no approve action
  renders for Blocked or Not determined evidence.
- Playwright screenshots of every page in section 6, at 1366×768 and 1920×1080,
  light and dark, into `test-logs/fleet-console/`.
- An axe scan per page, with the report saved alongside the screenshots.
- `src/Igloo.Fleet.Web/README.md` and the Fleet documents updated to describe
  what was observed, not what was intended.

## Decisions from the Phase 1 review

Decided 2026-11-01. The first eight are the open points raised at the end of
Phase 1; the ninth is the scope rule for Phase 2.

1. **Two eligibility scales → two labels, split by work type.** Dry-run
   evidence shows **Eligibility** (`PlanningEligibility`). Assessment evidence
   shows **Assessment outcome** (`EligibilityStatus`). Basis: `PlanningRules.Evaluate`
   has one call site, in `Submit`, and runs for both work types; `ReadOnlyPlanner`
   never builds `DryRunFacts` for an assessment and `ValidateResult` rejects one
   that has them, so every assessment's decision is Not determined with reason
   Missing evidence. Rendering that would present a non-fault as a fault.

2. **One identifier, labelled Work item.** `WorkItemId`, `AssessmentId` and
   `DryRunId` are the same GUID; the Server rejects anything else. "Dry run ID"
   and "Assessment ID" never appear in the console. The operator-visible noun is
   plain **Evidence**, with work type as a column.

3. **Cohorts are a module page, not a Server feature.** No contract, record or
   endpoint exists, and `RequestReadOnlyWork` takes exactly one `DeviceId`. The
   enterprise-correct shape — a Server-side named set of enrollments with
   audited membership, where a request against a cohort is one audited
   operation that fans out server-side — is a Contracts and Server change. A
   console-side group (tags held only by the console) would not be auditable
   and would diverge from the Server's authority model, so it is not built on
   the Server. The Cohorts page shows sample cohorts in Development and the
   "doesn't provide data" state elsewhere.

4. **No "dry-run plan".** The terms are **migration dry run** and **prepared
   plan**.

5. **Enrollment is the primary noun**, not Device. Routes, navigation, glossary
   and copy updated throughout; *device* survives only inside quoted code
   identifiers and API routes.

6. **`MigrationRun` / `MigrationState` are never surfaced.** They are referenced
   by nothing in `src/` outside their own file. Removing them from Domain is a
   backend change and is outside Phase 2; it is recorded as a follow-up.

7. **Operator attribution: fix what the console can fix, record the rest.**
   The Server attributes every operator action to `engineering-operator`;
   changing that is a Server change and is outside Phase 2. The console therefore
   keeps its own append-only audit record, written in the console's data
   directory, of console sign-in, sign-out, failed sign-in and every mutating
   action it sends, with the console account, UTC time, the request summary and
   the Server's returned identifier. Pages that show "Approved by" or "Created
   by" show the Server value and, beside it, "from console account *name*" when
   the console record exists. The Server change — per-operator credentials so
   that the audit trail carries a real principal — is recorded as a follow-up.

8. **Stale-contact threshold deferred.** The Overview tile and the enrollment
   filter use the console setting `Fleet:Console:StaleContactDays`, default 7.
   The value is revisited when Phase 2 is complete.

9. **Planned modules stay visible, then became pages.** The nine placeholder
   groups and Cohorts were first kept as a collapsed Roadmap group behind a
   "Not available in this release" notice, then shown as plain top-level
   entries, and after step 2 the user asked for the former sub-pages to be
   built. They are now the module groups of section 3: working list, details
   and create pages over a sample data source in Development, and a stated
   "doesn't provide data" state elsewhere. The header's environment badge was
   removed at the same time. Remote execution and recovery are excluded.

**Phase 2 scope.** This work changes `src/Igloo.Fleet.Web`, its tests and
documentation only. Fleet Server, Domain, Contracts, Persistence and Agent are
not modified. Everything the console needs from the Server is built against the
existing v2 operator routes; where the Server lacks something the console needs,
the console states the limit and the gap is recorded as a follow-up, not worked
around.

Follow-ups recorded for the Server (not part of Phase 2): per-operator operator
credentials and actor attribution (7); removal of unreachable `MigrationRun`
vocabulary from Domain (6); a cohort contract so the Cohorts page can be
backed by the Server (3).

## Phase 2, step 2: Planning and Audit pages

Observed on 2026-11-01, same branch, nothing committed. Built: Migration
profiles (list, profile revisions, create and add-revision dialog), Work items
(list, detail, request dialog with the capability check), Evidence (list with
the awaiting-approval filter, record page with decision, checks, dry-run facts,
provenance and the approve dialog), Approvals (list, detail, prepare and revoke),
Prepared plans (list, detail with the safety boundary first), Audit events, CSV
export for every collection, the account-menu language control, and the copy
pass to US English and Microsoft style. Mixed-language rendering is closed by
offering only complete cultures. Verified by the same three suites: 0 warnings;
183 unit tests; the UI matrix then covered 25 routes × 4 variants plus the flow
tests, with zero axe violations of any impact and no CSP error. After the module
pages (section 3) the figures are 187 unit tests and 364 UI tests (89 routes × 4
variants, one record page per module group, the create dialog, the theme
switch and the earlier flows), still with zero axe violations. Screenshots are
viewport captures, because the Fluent layout scrolls inside a 100dvh shell and
a full-page capture only adds blank canvas.

## Phase 2, step 1: what was built and what was observed

Observed on 2026-11-01 on branch `refactor/community-fleet-foundation`; nothing
committed.

**Built.** The shell (header, collapsible navigation, breadcrumbs, page frame,
footer, light and dark themes), the Overview, All enrollments, Enrollment (seven
tabs), Enrollment tokens with the shown-once token dialog, the module pages
(then ten prose pages, since replaced by section 3's groups), the four Settings pages, sign-in, first-run setup, access denied, not
found and error pages, the operator client with sample and live paths, the
console audit log, CSV export, request localization with English plus `nl`,
`fr` and `de` satellites, and the security headers of section 11. The Planning
pages and the Audit page are the next steps; their navigation entries stay
hidden until they exist.

**Verified.**

- `dotnet build` of `src/Igloo.Fleet.Web`, `tests/Igloo.Fleet.Tests` and
  `tests/Igloo.Fleet.Web.UiTests`: 0 warnings, 0 errors under `AnalysisMode=All`.
- `tests/Igloo.Fleet.Tests`: 180 passed, including `ArchitectureTests` (Fleet.Web
  still references only Contracts), the resource contracts (banned words,
  satellite keys, every status and audit action labelled, every navigation entry
  routable) and bUnit coverage of the status badge, failure states, Overview
  tiles, enrollment list states and the revoked-enrollment page.
- `tests/Igloo.Fleet.Web.UiTests`: thirteen routes at 1366×768 and 1920×1080 in
  light and dark, 52 page renders, each with a screenshot and an axe report in
  `test-logs/fleet-console/`. Zero axe violations of any impact after the fixes
  below, zero Content Security Policy errors in the browser console, exactly one
  `h1` per page. The sign-in page, keyboard path (skip link first, then the
  main landmark), in-page search filtering and the create-token flow (token
  shown once, absent from the page and the server log afterwards) are separate
  tests. Final run: 59 of 59 passed.

**Found and fixed during verification.** A brand-coloured top bar failed 4.5:1
for the product name and account name in light theme, so the header is a
neutral surface; the account button's `title` made its accessible name differ
from its visible text; avatar initials failed contrast in dark theme and were
replaced by an icon; the sample-data bar sat outside every landmark and moved
inside `main`; two grid columns had empty headers; `FluentNumberInput` tried to
load an input-mask library from unpkg.com, which the CSP blocked and which
terminated the circuit — the create-token dialog now uses a numeric text input
and [ADR-015](../decisions/015-fleet-console-component-library.md) records the
constraint.

**Not verified.** Contrast was checked by axe on the rendered pages, not by a
separate measurement of every token pair. The live Server path was exercised by
unit tests against its failure classification only; no Fleet Server was started
in this step. The `nl`, `fr` and `de` satellites were checked for key validity,
not reviewed by a translator.

**Follow-ups outside this console** (recorded in Decisions, not started):
per-operator Server credentials and actor attribution; removal of the
unreachable `MigrationRun` vocabulary from Domain; a cohort contract.

## Sources

Read on 2026-11-01, on branch `refactor/community-fleet-foundation`:

- `src/Igloo.Fleet.Web/**`, `src/Igloo.Fleet.Contracts/**`,
  `src/Igloo.Fleet.Domain/**`, `src/Igloo.Fleet.Server/TrustedFleetServer.cs`,
  `tests/Igloo.Fleet.Tests/ArchitectureTests.cs`
- [console-audit.md](console-audit.md)
- Read in full: [architecture.md](architecture.md), [phase-1.md](phase-1.md),
  [enrollment.md](enrollment.md), [security.md](security.md),
  [migration-profiles.md](migration-profiles.md), [dry-run.md](dry-run.md)
- Read by section headings only: [phase-2.md](phase-2.md), 2,624 lines of
  Community and endpoint execution-prerequisite journal. Nothing in it is cited
  above, and nothing in this design depends on it. It should be read in full
  before any console surface touches execution authorization.
- [ADR-013](../decisions/013-product-family-and-platform.md),
  [product-boundaries.md](../architecture/product-boundaries.md),
  [Fleet Overview](../07%20-%20Fleet/Fleet%20Overview.md)
- [win11-redesign.md](../design/win11-redesign.md) for the design principles
  that carry over

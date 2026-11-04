# Fleet operator console audit (Phase 1, step 1)

Audit of `src/Igloo.Fleet.Web` as it stands on branch
`refactor/community-fleet-foundation`. No production code was changed to produce
this document.

Provenance is marked per finding:

- **Observed** — reproduced by running the app and looking at the result.
- **Read** — established by reading the source only.

## Method

1. Read every file in `src/Igloo.Fleet.Web` (`Program.cs`, `Components/`,
   `Services/`, `Authentication/`, `FleetStatusClient.cs`, `FleetOperatorCli.cs`,
   `appsettings.json`, `wwwroot/`), plus `Igloo.Fleet.Contracts`,
   `Igloo.Fleet.Domain` and the operator route table in
   `src/Igloo.Fleet.Server/TrustedFleetServer.cs`.
2. Built the project: `dotnet build src/Igloo.Fleet.Web -c Debug` — succeeded,
   0 warnings, 0 errors.
3. Ran it in `Development` with a throwaway `FleetAuthentication:DataDirectory`
   and drove it in a browser: first-run setup, sign-in, dashboard, device list,
   device details, a placeholder route and an orphan route. The existing
   operator store under `%LOCALAPPDATA%/iGloo/Fleet.Web` was not touched.

## What the project is today

| Aspect | State |
|---|---|
| Target | `net10.0`, `Microsoft.NET.Sdk.Web` |
| Project references | `Igloo.Fleet.Contracts` only — enforced by `ArchitectureTests` |
| Package references | `ColorlibHQ.AdminLTE.AspNetCore` 0.3.1 (MIT) |
| Render model | `AddRazorComponents()` only. No interactive render mode anywhere |
| Routes | 98 `@page` directives across 15 components |
| Navigation | 14 groups, 86 leaf entries, defined in `appsettings.json` |
| Operator auth | Cookie auth, local `operators.json`, single `Administrator` |
| Data source | `IFleetDeviceDataSource`: sample in Development, live otherwise |
| Second mode | A `GET`/`POST` first argument starts the operator CLI instead of the web host |
| Tests | `WebAuthenticationTests`, `ArchitectureTests`. No bUnit, no Playwright, no axe |

## Capability envelope the console can actually serve

Every operator route the Fleet Server exposes
(`src/Igloo.Fleet.Server/TrustedFleetServer.cs`, read):

| Method | Route | Purpose |
|---|---|---|
| GET | `/v2/operator/devices` | Enrolled devices |
| POST | `/v2/operator/devices/{id}/trust` | Set trust status |
| GET | `/v2/operator/enrollment-tokens` | Enrollment tokens |
| POST | `/v2/operator/enrollment-tokens` | Create a token |
| POST | `/v2/operator/enrollment-tokens/{id}/revoke` | Revoke a token |
| GET | `/v2/operator/profiles` | Migration profile revisions |
| POST | `/v2/operator/profiles` | Create a profile |
| POST | `/v2/operator/profiles/{id}/revisions` | Append a revision |
| GET | `/v2/operator/work` | Work items |
| POST | `/v2/operator/work` | Request work |
| GET | `/v2/operator/evidence` | Dry-run evidence |
| GET | `/v2/operator/approvals` | Plan approvals |
| POST | `/v2/operator/approvals` | Approve a plan |
| POST | `/v2/operator/approvals/{id}/revoke` | Revoke an approval |
| GET | `/v2/operator/plans` | Prepared plans |
| POST | `/v2/operator/plans` | Prepare a plan |
| GET | `/v2/operator/audit` | Audit events |

That is the whole surface. There is no execution route, no device-scoped GET, no
server-side filtering, sorting or paging, and no single-object endpoint.

## Findings

### F1 — The navigation claims about forty capabilities Fleet does not have (observed)

81 of the 86 navigation entries resolve to `FleetModulePlaceholder.razor`. Whole
groups — Software, Updates, Configuration, Security, Storage, Networking,
Monitoring, Automation, Reports — describe a general-purpose endpoint management
product. Entries include `/migration/executions`, `/migration/recovery`,
`/storage/partitions`, `/configuration/scripts`, `/automation/scripts` and
`/security/sudo`.

`/migration/executions` and `/migration/recovery` are the most serious: Fleet is
architecturally forbidden from remote execution
([ADR-013](../decisions/013-product-family-and-platform.md),
[security](security.md) "Hard execution boundary"), so these are not merely
unbuilt — they describe authority Fleet must never present.

Only `/`, `/devices` and the device detail route show real Fleet data.
`/devices/groups`, `/devices/hardware` and `/devices/os` are "Planned" stubs.

### F2 — The placeholder page promises delivery (observed)

Every placeholder renders:

> This iGloo Fleet capability now has a permanent navigation entry and route.
> Backend contracts and live management actions will be connected as the
> subsystem is implemented.

This is a roadmap promise in operator-facing copy, it says "live management
actions", and it speaks in internal engineering terms ("backend contracts",
"navigation entry and route"). The "Planned" badges on the device sub-pages and
on eleven device-detail tabs are the same pattern in shorter form.

### F3 — Nothing on any page is interactive (observed)

`Program.cs` calls `AddRazorComponents()` without
`AddInteractiveServerComponents()`, no component declares `@rendermode`, and
`App.razor` does not load `blazor.web.js`. Every page therefore renders as
static SSR.

Reproduced: typing `kiosk` into the device search box left all eight rows
visible; clicking the **Hardware** tab on a device page left Overview showing.
The same applies to **Refresh**, **Try again**, the row click handler and
`<Tabs @bind-ActiveId>`.

The search field, the refresh buttons, the row navigation and the twelve device
tabs are therefore visible, focusable controls that do nothing. For an operator
console this is worse than their absence.

### F4 — The live data path cannot authenticate (read)

`FleetStatusClient` is registered with a base address only. It sets no
`Authorization` header and installs no handler that validates the Fleet Server
certificate against the independently provisioned private root. Every
`/v2/operator/` request from the browser host therefore fails — the Server
rejects an unauthenticated operator request with `EnrollmentFailure`
(`TrustedFleetServer.cs`), and the private CA is not in the host trust store.

Outside Development the app registers `LiveFleetDeviceDataSource`, so `/devices`
can only ever reach its "Devices are unavailable" state. The README states this
("Browser-to-Server operator credential provisioning is not connected yet"), but
the UI does not: it shows a generic failure with a **Try again** button that is
itself inert (F3).

`FleetStatusClient` also mixes protocol versions: `GetVersionAsync`,
`GetDeviceAsync` and `GetAssessmentAsync` target Phase 0 v1 routes (`health`,
`agents/{id}`, `assessments/{id}`) that the trusted v2 Server does not expose.

### F5 — Console sign-in has no relationship to Server authorization (read)

The console authenticates operators against a local `operators.json` through
`FleetOperatorStore`. The Fleet Server authenticates operators with a single
shared secret and records every operator action under the fixed actor string
`engineering-operator` (`EngineeringOperatorAuthenticator`).

A named console operator's approval would therefore be written to the audit
trail as `engineering-operator`, and `PlanApproval.ApprovedBy` carries that same
literal. Any console screen that shows "approved by" must not imply the
signed-in operator's identity reached the Server.

The console also offers no real role separation: `FleetOperatorStore` creates
exactly one `Administrator` and has no operation to add a second account, while
`OperatorMenu` links to `/administration/operators` and `/administration/roles`,
both placeholders.

### F6 — Security headers required by the brief are absent (read)

`Program.cs` configures no Content-Security-Policy, no HSTS, no
`X-Content-Type-Options`, no `Referrer-Policy`, no frame protection and no HTTPS
redirection. `AllowedHosts` is `*`. Cookie hardening is in place (`__Host-`
prefix, `HttpOnly`, `Secure`, `SameSite=Strict`) and antiforgery is validated on
all three auth endpoints, but the cookie cannot be issued over the plain-HTTP
endpoint that `launchSettings.json` also binds (`http://localhost:61298`).

There is no sign-in rate limiting, no lockout and no audit record of console
sign-in, sign-out or failure.

Authorization is enforced in `Routes.razor` by an `AuthorizeView` plus an
`AllowAnonymous` attribute check rather than by a fallback authorization policy,
so a page added outside the router would not be covered.

### F7 — The device list presents columns Fleet will never fill (read)

`FleetDevices.razor` shows **Device name**, **OS**, **OS version** and **Model**.
Fleet evidence deliberately excludes hostname, disk labels, GPU names and
operating-system identity ([architecture](architecture.md), "Privacy and
evidence"), so outside Development these four columns resolve to
`"Not available"` and the first eight characters of a GUID. The values currently
shown come from a hardcoded `switch` in the page that maps the eight sample
GUIDs to invented machine names and models.

The same page labels devices as an "inventory", and the dashboard counts
"Managed devices" and "Healthy devices" — three terms for an object the domain
calls an enrolled device with a trust status, and two measures Fleet does not
compute.

### F8 — Text encoding is corrupted in shipped strings (observed)

`FleetModulePlaceholder.razor` line 85 contains a multiply-encoded UTF-8
separator where a middle dot was intended. The browser tab for
`/migration/executions` reads `Executions` followed by roughly sixty mojibake
characters before `iGloo Fleet`. The dashboard's "Platform status" panel shows
the same corruption in place of three em dashes, so the defect is visible in the
page body as well as in the title.

### F9 — `Approvals.razor` puts markup inside `<PageTitle>` (observed)

`PageTitle` content becomes the `<title>` element. The browser tab for
`/approvals` reads literally:

```
Approvals - <span class= igloo-brand ><img src= /img/igloo-logo.svg ...
```

### F10 — Two parallel, divergent page families (observed)

`/approvals`, `/audit`, `/evidence`, `/plans`, `/profiles` and `/work` exist,
are reachable and appear in no navigation menu. They use an older CSS idiom
(`page-header`, `eyebrow`, `panel`) that predates the AdminLTE shell, so they
render outside the standard page frame, and each states "&lt;Name&gt; UI is ready
for wiring." — an engineering status message shown to operators.

Meanwhile the menu points at `/migration/approvals`, `/migration/evidence`,
`/migration/plans`, `/migration/profiles` and `/fleet/work-queue`, which are
placeholders. The same five concepts therefore have two routes each, neither of
which works.

### F11 — Accessibility defects (observed)

On `/devices`:

- No `<h1>`. The page title is an `<h3>` and the card title is another `<h3>`, so
  the document starts at level 3. `Routes.razor` already compensates with
  `FocusOnNavigate Selector="h1, h3"`.
- Table rows carry `role="link"` with `tabindex="0"`, which replaces the row's
  table semantics and nests an anchor inside a link role.
- Trust status is rendered as an icon with `title`, `aria-label` and
  visually-hidden text, but no visible text label. The icon glyph differs per
  status, so this is close to compliant, but it has not been measured.

Chromium rejects the username `pattern` on `/setup`:

```
Pattern attribute value [A-Za-z0-9._-]+ is not a valid regular expression:
Invalid regular expression: /[A-Za-z0-9._-]+/v: Invalid character in character class
```

The unescaped `-` makes the pattern invalid under the `v` flag, so that client
validation silently does not run.

No automated accessibility check exists in the repository, and contrast has not
been measured in either theme.

### F12 — Title, label and capitalisation drift (observed)

Three different title separators are in use (`|`, `-`, and the corrupted
character), "Available Updates" is title case where the rest of the product uses
sentence case, and the same object is called "Managed devices", "All devices",
"Device inventory" and "Devices" across one screen pair.

### F13 — No localization structure (read)

Every operator-facing string is a literal in Razor markup or in
`appsettings.json`. There is no `.resx`, no `IStringLocalizer` and no request
localization. The brief requires English first with nl, fr and de structurally
ready.

### F14 — Sample data is labelled, but not everywhere and not consistently (read)

`Program.cs` correctly refuses `Fleet:UseSampleData` outside Development, and
`FleetDevices.razor` and `FleetDeviceDetails.razor` show a "Demo data" badge.
The dashboard does not, and the term used is "Demo data" where the configuration
key, the README and the brief all say *sample data*.

## What should be kept

- The Contracts-only project reference and the `ArchitectureTests` that enforce it.
- The sample-data guard in `Program.cs`: configuration cannot enable sample data
  outside Development.
- Cookie hardening and antiforgery validation on the three auth endpoints.
- `FleetOperatorStore`'s atomic temp-file write, Unix file mode, ASP.NET Core
  Identity `PasswordHasher` and rehash-on-verify.
- `FleetOperatorCli`: its custom-root chain validation, EKU check, disabled
  redirects and proxy, and single-shot token printing are the reference for how
  the browser host must reach the Server.
- The sentence already on the dashboard: "The shell deliberately does not invent
  telemetry."

## Where this leads

The redesign in [console-design.md](console-design.md) starts from the seventeen
operator routes above rather than from a product silhouette, replaces the
86-entry navigation with one that matches them, and records the component
library decision in
[ADR-015](../decisions/015-fleet-console-component-library.md).

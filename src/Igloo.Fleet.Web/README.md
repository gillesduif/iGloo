# Fleet operator console (Igloo.Fleet.Web)

This .NET 10 executable is the Fleet operator console: a Blazor Web App on
Fluent UI Blazor 5.0.1 ([ADR-015](../../docs/decisions/015-fleet-console-component-library.md))
plus the original engineering operator CLI. It references only
`Igloo.Fleet.Contracts`; `ArchitectureTests` enforces that. It has no access to
repositories, endpoint services or execution of any kind.

Design, glossary, information architecture, status catalogue and the decisions
behind them: [docs/fleet/console-design.md](../../docs/fleet/console-design.md).
The audit that preceded the rebuild: [docs/fleet/console-audit.md](../../docs/fleet/console-audit.md).

## What is implemented (Phase 2, steps 1 and 2)

Observed on 2026-11-01 with `dotnet build` at 0 warnings under `AnalysisMode=All`,
`tests/Igloo.Fleet.Tests` green (187 tests), and the Playwright/axe suite in
`tests/Igloo.Fleet.Web.UiTests` (364 tests: 89 routes x 4 variants, 11 module
record pages, create, theme, search, keyboard and token flows) writing viewport
screenshots and axe reports to `test-logs/fleet-console/`.

| Area | State |
|---|---|
| Shell | Header (product, environment badge, in-page search, help, theme, account), collapsible left navigation, breadcrumbs, page frame with command bar, footer. Interactive server rendering; sign-in, setup and sign-out stay static form posts. |
| Pages | Overview; All enrollments, Enrollment (7 tabs), Enrollment tokens; Migration profiles and Profile; Work items and Work item; Evidence and Evidence record; Approvals and Approval; Prepared plans and Prepared plan; Audit events; 64 module pages in 11 groups (list, record, create); Settings (Server connection, Console accounts, Scope and limits, About); sign-in, first-run setup, access denied, not found, error. |
| Dialogs | Create enrollment token (shown once), Change trust status, Create profile / Add revision, Request work (capability check before submit), Approve evidence (exact hash, review reason rules), Prepare plan and Revoke approval confirmations. |
| Data | `IFleetOperatorClient` covers the seventeen operator routes. `LiveFleetOperatorClient` sends the Bearer operator credential and validates the Server certificate against the provisioned private root (`FleetServerTrust`, shared with the CLI). `SampleFleetOperatorClient` serves deterministic sample data in Development only and labels it on every page. |
| Security | CSP (`default-src 'self'`, scripts from self plus a per-request nonce for Blazor's import map, inline styles allowed because Fluent components set style attributes), `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy`, COOP/CORP, HSTS and HTTPS redirection outside Development, fallback authorization policy, sign-in rate limiting (10 per minute per address), `__Host-` cookie. |
| Audit | `console-audit.jsonl` beside the operator store records sign-in, failed sign-in, sign-out and every mutating operator call with the console account. Token plaintext is never written. |
| Localization | All operator-facing strings in `Resources/*.resx` (US English, Microsoft Writing Style Guide). `nl`, `fr` and `de` satellites exist as structure only; a culture is offered to operators (`ConsoleOptions.OfferedCultures`) and resolvable at all only once it is complete, so a page is never half in one language. The culture is set by explicit choice (cookie), never from `Accept-Language`. |
| Export | `GET /export/{enrollments,enrollment-tokens,profiles,work-items,evidence,approvals,plans,audit}.csv`, UTF-8 with BOM, whole collection, formula characters neutralized. |

## Running

```powershell
dotnet run --project src/Igloo.Fleet.Web
```

In Development the console uses sample data and needs no Fleet Server. The
first visit opens `/setup`, which creates the single console administrator in
`%LOCALAPPDATA%/iGloo/Fleet.Web/operators.json` (or `FleetAuthentication:DataDirectory`).
Keep that directory outside source control and protect it as authentication data.

Outside Development the console talks to a trusted Fleet Server. Configure:

- `Fleet:Server:Uri` or `IGLOO_FLEET_SERVER_URI` — the HTTPS origin.
- `Fleet:Server:CaCertificatePath` or `IGLOO_FLEET_CA_CERT` — the independently provisioned `fleet-root.pem`.
- `IGLOO_FLEET_OPERATOR_SECRET` in the console process environment — the separate operator credential; never put it in a configuration file.
- `AllowedHosts` — the host name operators use; the default allows loopback only.

Settings → Server connection shows which of these are set and the result of a
health check. Sample data cannot be enabled outside Development; `Fleet:UseSampleData=true`
in any other environment fails startup.

## Operator CLI

`dotnet Igloo.Fleet.Web.dll GET|POST /v2/operator/<resource> [body.json]` is
unchanged; see [Phase 1 operations](../../docs/fleet/phase-1.md). It validates the
Server certificate against the provisioned root and sends the operator credential.
Enrollment token creation prints its one-time secret; protect that output.

## Limits that are stated in the UI, not hidden

- The Fleet Server records every operator action as the shared actor
  `engineering-operator`. The console keeps its own audit log with the console
  account and shows both. Per-operator Server identity is a Server change.
- One console administrator exists, created at first run. There is no account
  or role management; Settings → Console accounts says so.
- A prepared plan is a non-executable artefact. No page offers to run anything.
- Module pages (Inventory, Cohorts, Software, Updates, Configuration, Security, Storage, Networking, Monitoring, Automation, Reports; 64 pages from `ModuleCatalog`) show sample rows in Development and state that the Fleet Server provides no data for them in any other environment. Nothing on them runs anything on an enrollment.

## Verification

```powershell
dotnet build src/Igloo.Fleet.Web -c Debug
dotnet test tests/Igloo.Fleet.Tests
dotnet build tests/Igloo.Fleet.Web.UiTests -c Debug
pwsh tests/Igloo.Fleet.Web.UiTests/bin/Debug/net10.0/playwright.ps1 install chromium
dotnet test tests/Igloo.Fleet.Web.UiTests --no-build
```

The UI suite starts the built console on a free port with a throwaway operator
store, creates the first administrator through the real setup form, and for
every page at 1366×768 and 1920×1080 in light and dark saves a viewport screenshot
and an axe report to `test-logs/fleet-console/`, failing on any serious or critical
violation, on a CSP violation in the browser console, or on more than one `h1`.

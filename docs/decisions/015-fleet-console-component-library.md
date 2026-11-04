# ADR-015: Fluent UI Blazor for the Fleet operator console

**Status:** Accepted (reviewed 2026-11-01)
**Date:** 2026-11-01

## Context

`src/Igloo.Fleet.Web` is an engineering preview built on
`ColorlibHQ.AdminLTE.AspNetCore`. The
[console audit](../fleet/console-audit.md) found that the shell works but that
the product it describes is not Fleet, and that nothing on any page is
interactive because the app runs entirely as static server-side rendering.

Rebuilding the console for enterprise and government administrators raises a
prior question: which component library carries the shell, the data grid, the
dialogs and the theming. The target audience knows the Microsoft 365, Intune and
Entra admin centres, which are built with Fluent 2. The deployment target
includes air-gapped government networks, so every asset must be served from the
application's own origin. EN 301 549 and WCAG 2.1 AA apply.

Three options were considered.

### Option A — stay on AdminLTE 4

`ColorlibHQ.AdminLTE.AspNetCore` 0.3.1, MIT, by Colorlib. Verified by inspecting
the package in the local NuGet cache: it ships compiled AdminLTE CSS and JS,
Bootstrap 5.3, Bootstrap Icons including the font files, and OverlayScrollbars as
static web assets, and a search of the package's `staticwebassets` and `lib`
directories found no reference to any CDN, jsDelivr, unpkg, Cloudflare or Google
Fonts host. It is already integrated, already builds with
`AnalysisMode=All` at zero warnings, and its `DashboardLayout`, `Card`,
`InfoBox`, `SmallBox` and `AuthLayout` components are in use.

Against it: AdminLTE is a Bootstrap admin *template*, not the Fluent design
language. It gives an administrator who knows Intune no recognition. It has no
data grid — the current device list is a hand-written `<table>` — so sorting,
column choosing, pinned columns, paging and virtualization would all be built by
hand, and each would need its own accessibility work. The package is at version
0.3.1 from a single vendor, with no stated support policy, and its component set
is a thin wrapper over Bootstrap markup rather than a controlled design system.

### Option B — adopt Fluent UI Blazor

`Microsoft.FluentUI.AspNetCore.Components`. Facts, from the
[GitHub repository](https://github.com/microsoft/fluentui-blazor), the
[NuGet listing](https://www.nuget.org/packages/Microsoft.FluentUI.AspNetCore.Components/)
and the [documentation site](https://www.fluentui-blazor.net/), read on
2026-11-01:

- **Version.** 5.0.1, published 2026-10-14. 5.0.0 was published 2026-09-25 after
  five release candidates. The 4.x line is still receiving updates (4.14.5,
  2026-09-30); v4 code is on the `archive-v4` branch and v4 documentation has
  moved to a separate site. No end-of-life date for v4 is published.
- **Licence and support.** MIT. NuGet owners are Microsoft and fluentui-blazor.
  The README states plainly that the library "is an open source project and is
  **not** an official part of ASP.NET Core, which means it's **not** officially
  supported", and that support is "offered on a best effort basis through the
  GitHub repository **only**." There is no Microsoft support SLA.
- **Target frameworks.** net8.0, net9.0, net10.0 and net11.0. The documentation
  site itself runs on .NET 10.
- **Offline assets.** The stylesheet is referenced as
  `_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.bundle.scp.css`,
  an application-relative path served from the package's static web assets.
  Fetching that bundle from the documentation site returned 174,641 bytes
  containing no `@import` and no `url()` pointing at an external origin. Its
  `--fontFamilyBase` token resolves to a system font stack beginning with
  Segoe UI, so no web font is downloaded. Icons and emoji are separate opt-in
  packages (`...Components.Icons`, `...Components.Emoji`).
- **Render mode.** The documentation states that under the Blazor default of
  static server-side rendering "every component (including ours) is statically
  rendered on the server and will not be interactive", and that "most components
  will display correctly but will not offer complete, if any, functionality."
  The installation guide says the library "requires interactive rendering".
- **Theming.** One brand colour generates a sixteen-step ramp; the documentation
  states that ramp "passes contrast checks for accessibility". The default brand
  is `#0F6CBD`. Light and dark are switched through `ThemeService.SetThemeAsync`
  and expressed as a `data-theme` attribute on `<body>`, which application CSS
  can key off.
- **Data grid.** `FluentDataGrid` documents sorting, multi-column sorting,
  custom comparers, column types, auto-fit and reorderable columns, pinned
  columns, paging and custom paging, remote data, virtualization,
  master/detail, and explicit `LoadingContent` and `EmptyContent` slots.
- **Accessibility.** No WCAG or EN 301 549 conformance claim was found on the
  README, the NuGet page or in the documentation navigation. The only
  accessibility statement found is the contrast claim for the generated colour
  ramp.

### Option C — plain Blazor with hand-written CSS

Total control, no third-party dependency, no licence or supply-chain question.
Against it: a data grid, a dialog layer, a navigation tree, a combobox and a
date picker that are all keyboard-operable and screen-reader correct is a large
amount of work that is not Fleet's problem to solve, and it would have to be
re-verified against WCAG by us alone.

## Decision

**Adopt Fluent UI Blazor v5 (Option B)** for the Fleet operator console, and
remove `ColorlibHQ.AdminLTE.AspNetCore` from `src/Igloo.Fleet.Web` once the
shell, navigation and the device pages have been ported.

Reasons, in order of weight:

1. **Recognition.** Fluent 2 is the design language of the admin centres the
   target audience uses daily. Option A cannot produce that, and Option C would
   mean re-deriving it.
2. **The data grid.** The console is mostly lists of immutable records:
   devices, tokens, profile revisions, work items, evidence, approvals, plans
   and audit events. A documented, accessible grid with sorting, paging and
   column control is the single largest piece of the brief, and Option B ships
   one.
3. **Offline operation.** Assets are served from the application's own origin
   with no external URL in the stylesheet bundle, which satisfies the air-gap
   constraint that Option C would also satisfy and that no CDN-based library
   would.
4. **Interactivity is required either way.** The console needs an interactive
   render mode to fix the defects in audit finding F3, so Fluent's requirement
   costs nothing additional.

The library's requirement is adopted deliberately: the console will run with
`InteractiveServer` rendering, with the sign-in, first-run setup and sign-out
forms deliberately left as static antiforgery-protected HTML posts, as they are
today.

## Consequences

- **Accessibility conformance stays ours.** The library publishes no WCAG or
  EN 301 549 conformance claim, so adopting it is not evidence of conformance.
  Every page must still pass an automated axe scan with zero serious or critical
  violations plus a keyboard walkthrough, and contrast must be measured in both
  themes. The generated brand ramp is a starting point, not a result.
- **No vendor support.** The library is explicitly not an officially supported
  part of ASP.NET Core. For a government deployment this must be stated to the
  customer. The mitigation is the MIT licence: the source can be vendored or
  forked if the project stalls. A compensating control is to keep Fleet's own
  component surface small, so that the number of library components the console
  depends on stays auditable.
- **Version freshness is a risk.** v5.0.x is weeks old. The console should pin
  an exact version, not a floating range, and a version bump is a reviewed change
  with a fresh screenshot and accessibility run, not a routine update.
- **CSP enforces the air-gap claim.** Inspecting the package after installation
  (2026-11-01) found two external URLs in its JavaScript module:
  `https://unpkg.com/imask@7.6.1/...` and `https://unpkg.com/sortablejs@1.15.6/...`,
  loaded lazily by `FluentTextInput` masking (`MaskPattern`), by
  `FluentNumberInput` (always, for its number mask) and by `FluentSortableList`.
  The console's Content-Security-Policy is `script-src 'self'` plus a
  per-request nonce for Blazor's import map, so the load fails closed: observed
  on 2026-11-01, a `FluentNumberInput` in the create-token dialog threw
  "Failed to load IMask from CDN" and terminated the circuit, which is exactly
  the behaviour wanted in an air-gapped network and exactly why the console
  must not use it. `FluentNumberInput`, input masks and sortable lists are
  therefore off limits in the console unless those libraries are vendored; the
  Playwright run asserts that no CSP error occurs on any page. The stylesheet
  bundle contains no external reference.
- **Two icon sets during the port.** Bootstrap Icons come in with AdminLTE and
  Fluent icons come in with the new package. The port must not leave both; the
  AdminLTE package reference is removed in the same step that removes the last
  `bi bi-*` class.
- **`THIRD-PARTY-NOTICES.md` changes.** AdminLTE, Bootstrap, Bootstrap Icons and
  OverlayScrollbars come out; Fluent UI Blazor and Fluent UI System Icons go in.
- **Boundaries are unchanged.** Fleet.Web still references only
  `Igloo.Fleet.Contracts`; `ArchitectureTests` continues to enforce that. No
  component library changes what Fleet is allowed to do, and nothing in this
  decision creates remote execution authority
  ([ADR-013](013-product-family-and-platform.md)).

## Relationship to earlier decisions

[ADR-001](001-language-and-ui.md) and [ADR-006](006-wpf-over-winui3.md) govern
the Community desktop application's UI stack; this decision is about a web
console and does not touch them. The
[Windows 11 redesign charter](../design/win11-redesign.md) is the source of truth
for Community's WPF surface. Its principles — clarity over impressiveness,
hierarchy through typography and spacing, semantic-only colour, accessibility as
architecture — carry over to the console as design rules. Its WPF design tokens
do not; the console uses the Fluent 2 token set.

[ADR-013](013-product-family-and-platform.md) makes Fleet a peer product with its
own authorization and read-only work protocol. This decision changes only how the
Fleet operator console is rendered.

## Observed 2026-11-01: theme switching

`IThemeService.SetThemeAsync(ThemeMode)` in 5.0.1 did not change the page in
this console (tokens, `body[data-theme]` and the `fluentui-blazor:theme-settings`
storage key all stayed as they were), while the library's own script function
`Blazor.theme.setThemeMode("light" | "dark" | null)` does all three. The console
therefore calls that function directly (`Presentation/ConsoleTheme.cs`) from the
theme menu and once after the circuit connects, so the stored preference and
the browser agree. Revisit when the library fixes or documents the service.


# iGloo — Windows 11 redesign charter

**Status: adopted (July 2026). Supersedes the glassmorphism direction.**
This is the source of truth for all UI work. If a change conflicts with this
document, the change is wrong or this document gets amended first — never both
drifting apart.

## The one-sentence brief

iGloo must read as a **first-party Windows 11 utility** (Settings, Visual Studio
Installer, Windows Security): calm, typographic, transparent — because users
trust it with their operating system. One deliberate exception: the Cover Flow
distro picker is iGloo's signature and stays, elevated to premium-control quality.

## Design principles (decision rules, not slogans)

1. **Clarity beats impressiveness.** Any choice between "looks cool" and
   "reads instantly" → reads instantly.
2. **Hierarchy through typography and spacing, never through decoration.**
   Colored boxes, gradients, glows and shadows are not hierarchy tools.
3. **Color is semantic only.** Blue = primary action · Green = success ·
   Amber = warning · Red = destructive. Neutral everywhere else.
4. **Every screen answers:** Where am I? · What is happening? · Why? ·
   What's next? · Can I safely continue? If not → redesign, don't restyle.
5. **Transparency for power users, guidance for beginners.** Details
   (verification, partitions, logs) are one click away, never in the way.
6. **Accessibility is architecture:** keyboard path, visible focus, contrast,
   AutomationProperties on everything interactive. (The earlier "kill the OS
   focus ring" fix was wrong by this charter — replaced by a *designed* ring.)

## Design tokens (dark theme, Win11-derived)

| Token | Value | Role |
|---|---|---|
| `Brush.Layer.Card` | `#0DFFFFFF` | card/surface fill (solid, no gradient) |
| `Brush.Layer.CardHover` | `#15FFFFFF` | hover fill |
| `Brush.Stroke.Card` | `#17FFFFFF` | 1px card & control strokes |
| `Brush.Stroke.Strong` | `#8AFFFFFF` | input bottom edge (WinUI idiom) |
| `Brush.Accent` | `#4CC2FF` | accent fill / links (dark-mode accent) |
| `Brush.Accent.Hover` / `.Pressed` | `#5FCBFF` / `#3DAEE8` | accent states |
| `Brush.OnAccent` | `#053345` | text on accent fill (AA on #4CC2FF) |
| Corner radius | **4** controls · **8** cards/overlays | Win11 geometry |
| Type ramp | Caption 12 · Body 13–14 · BodyStrong 14/600 · Subtitle 20/600 · Title 28/600 | Segoe UI Variable |
| Spacing | 4-px grid; section gap 24; label→control 8 | |
| Focus | 2px light ring, 3px offset, rounded (`FocusVisual.Win11`) | applied app-wide |
| Effects | none. No glow, no drop shadows on controls. | strokes carry depth |

Existing semantic brushes (`Brush.Warning`, `.Success`, `.Danger`) stay; their
*use* is restricted to meaning (rule 3).

## Screen-by-screen audit & directives

Method per screen: evaluate → keep what works → name violations → redesign →
justify. Executed iteratively **with screenshots** (blind redesign is how UIs
end up incoherent). Status column tracks progress.

| Screen | Keep | Main violations (charter rule) | Redesign directive | Status |
|---|---|---|---|---|
| Window chrome + stepper | dark chrome, step count | steps are anonymous dashes (R4) | **Left rail** listing all 8 steps by name with done/current/todo states (VS Installer pattern); content column max ~720px | ☐ |
| Welcome | tone | decorative hero space (R2) | Title + one-paragraph promise + "What iGloo will do" 3-item summary + primary CTA; alpha warning as amber InfoBar, not a card | ☐ |
| Preflight | findings model | severity via colored cards (R2/R3) | Settings-style rows: icon + name + one-line result, chevron expands detail; blockers pinned top with remedy; "Copy report" for power users | ☐ |
| Distro picker (signature) | **Cover Flow stays** | chips were glass; focus invisible (R6) | Cover Flow as premium control: neutral chips (pivot-style), designed focus ring, ←/→ + type-ahead, reduced-motion fallback; caption block fixed-height (no reflow) | ☐ |
| ISO download | phase text | progress lacks *why* (R5) | One progress line + phase; verification as explicit checklist rows (SHA-256 ✓ · Signature ✓ · Key pinned ✓) — the trust moment, show it; details expander: URL, size, speed, fingerprint | ☐ |
| Migration setup | grouping | mixed control styles, tall inputs | Two-column form grid (label 160px / control), 32px input height, folder toggles as Win11 toggle-chips with sizes ("Documents · 1.2 GB") | ☐ |
| Disk selection | mode choice | consequence unclear (R4/R5) | Before/after partition bar visualization; explicit sentence: "Windows keeps X GB, Linux gets Y GB"; replace-mode gated by typed confirmation | ☐ |
| Staging / Direct install | step reporting | wall-of-progress (R1) | Checklist of named steps (done ✓ / active spinner / pending); persistent "Windows stays bootable until reboot" reassurance line; log link | ☐ |
| Reboot handoff | countdown | abruptness (R4) | "What happens next" 3 bullets (reboot → unattended install → first boot takes minutes) + Cancel until T-0 | ☐ |

## The Cover Flow covenant

It is not Apple nostalgia; it is iGloo's identity. Quality bar: flawless
keyboard/mouse/trackpad interaction, type-ahead selection, honest focus
visuals, 60fps or graceful degradation, `prefers-reduced-motion` respect,
screen-reader announcement of the centered item. Its *frame* (chips, captions,
badges) follows the Fluent language so the 3D stage feels curated, not themed.

## Working protocol

1. Foundation tokens + core controls land first (done — see IglooTheme.xaml).
2. One screen per iteration: implement → screenshot → refine → check ☑ above.
3. No new one-off hex values in pages; tokens only. New needs → new token here.
4. WPF hygiene per iteration: extract repeated XAML into styles/controls,
   flatten gratuitous Grid nesting, keep MVVM (no code-behind logic).

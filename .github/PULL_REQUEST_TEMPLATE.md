<!-- Thanks for contributing! Keep the change focused; separate refactors from behavior changes. -->

## What and why

<!-- What does this change, and what problem does it solve? Link any related issue. -->

Closes #

## Checklist

- [ ] `dotnet build Igloo.sln` is clean — **no new warnings, no suppressions added**
      (`[SuppressMessage]` / `#pragma warning` / `<NoWarn>` are not allowed).
- [ ] `dotnet test Igloo.sln` is green; new behavior has a test.
- [ ] Code matches `.editorconfig` and the surrounding style.
- [ ] Commits are signed off (`git commit -s` — DCO).

## Safety-critical paths (delete if not applicable)

- [ ] **Touched the install / removal pipeline, first-boot agents, boot entries,
      or ISO verification** → I ran a real end-to-end install in a VM.
  - Distro & mode:
  - What I verified (dual-boot preserved? files/Wi-Fi migrated? removal reclaimed space?):
- [ ] **Fixed a partitioning / data-loss bug in one distro** → I checked every
      other distro for the same bug and addressed it here (or confirmed they're clear).

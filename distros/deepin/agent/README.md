# Agent integration is blocked

No executable agent is packaged while safe installation is unverified.
`GetAgentPayloadAsync` refuses; it does not return an empty success or copy the
complete Debian-family agent. See [STATUS.md](../STATUS.md).

The current ISO's immutable mount script makes `/opt` and `/etc` writable and
binds writable `/var`; its persistent hook maps `/usr/local` to `/var/usrlocal`.
Use those intended locations for the future payload, service, state and logs.
Never disable immutable protection globally to make the agent run.

Before implementation, demonstrate all of the following on an installed VM:

- The actual OOBE-created user is available before user migration. Do not race
  Deepin's first-boot account creation or assume a username from directory names.
- Seed and migration manifest are bound to the approved installation identities.
  Ambiguous labels, missing files or stale payloads cause a logged refusal.
- Only tested shared migration functions are reused. DDE settings, display APIs,
  browser locations, NVIDIA handling, package sources and EFI behavior require
  Deepin-specific verification. Do not point APT at Debian/Ubuntu repositories.
- Progress is atomic per operation, retries preserve existing user data, secrets
  are redacted after use, and `.done` means success rather than merely execution.
- Failure is visible in `/var/log/igloo` and does not prevent desktop boot. Slow
  network/package work must not hold the display manager indefinitely.
- Restart, interrupted migration and immutable system updates preserve the state
  and run only incomplete steps. No installer-partition cleanup before a proven
  successful independent boot and an exact ownership match.

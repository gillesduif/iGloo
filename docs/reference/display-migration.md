# Display layout migration

Carrying resolution, refresh rate, rotation and monitor positions from Windows to
the Linux desktop. Every finding below came from a bare-metal run, not from docs.

## Where the layout lands, per desktop

| Desktop | Session config | Applied at login by |
|---|---|---|
| GNOME (Debian) | `~/.config/monitors.xml` | mutter, plus `display-apply-gnome.py` over D-Bus |
| Cinnamon (Mint) | `~/.config/cinnamon-monitors.xml` | muffin, plus `display-apply.py` over xrandr |
| KDE Plasma (Fedora) | kscreen | `kscreen-doctor` from the login hook |

The greeter reads its own copy, so the same file is written into
`/var/lib/gdm3/.config`, `/var/lib/gdm/.config` and `/var/lib/lightdm/.config`
— otherwise a portrait screen is still sideways at the login prompt, which is the
first thing the user sees.

## Mutter discards the whole file over one negative coordinate

Windows places the primary monitor at 0,0 and lets the others run negative. Mutter
does not accept that, and it does not skip just the offending monitor — it throws
away the entire configuration:

```
Failed to read monitors config file '/home/…/.config/monitors.xml':
Fout in regel 21 teken 15: Expected a number, got -826
```

That single rejection is why rotation, refresh rate *and* position all fell back
at once on the Debian run of 2026-08-19, which read as three separate bugs.
`_match_display_layouts()` now shifts the origin so the top-left monitor sits at
(0,0); the relative geometry is unchanged.

Measured on the reporting machine: DP-4 `(0,0)` → `(0,826)`, HDMI-A-2
`(3840,-826)` → `(3840,0)`.

## Cinnamon forgets the layout at the next boot

muffin reads the same schema as mutter, but under a different filename:
`cinnamon-monitors.xml`. Writing only `monitors.xml` leaves Cinnamon with no
stored configuration, so the layout lasts exactly one session — the login hook
applies it over xrandr, muffin overrides it at the next session start.

Symptom on the Mint run of 2026-08-20: rotation correct on first boot, gone after
a reboot. The agent now writes both filenames, in the user's config and in every
greeter directory.

## Connector matching

Two Samsung Odyssey G70D panels report the **same** EDID serial (`H1AK500000`),
so identity matching alone binds both staged monitors to one connector. Connector
matching runs first, consumes each live monitor once, and normalises the kernel
name `HDMI-A-2` to mutter's `HDMI-2`. Covered by
`tests/agent/…` and `test-logs/gnome_applier_test.py`.

## Scale is deliberately fixed at 1

`<scale>1</scale>` is hardcoded and has been since the feature landed. Migrating
the Windows scaling percentage is a separate decision — Windows and GNOME apply
scaling differently, and a wrong value invalidates the whole configuration under
the parser behaviour described above.

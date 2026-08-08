# GRUB theme: Stylish (vendored)

Source: https://github.com/vinceliuice/grub2-themes
License: GPL-3.0 (see LICENSE in this directory)
Upstream commit: 80dd04ddf3ba7b284a7b1a5df2b1e95ee2aad606

Vendored for milestone M17 (end-user-friendly boot menu). The first-boot
agents install one of these variants to `/boot/grub/themes/stylish/` and point
`GRUB_THEME` at it.

## Variants

- `1080p/` - panels up to 1920x1080
- `4k/` - panels above 2560x1440

The variant is chosen at first boot from the migrated display layout in the
migration manifest (`displays[].widthPx` / `heightPx`), using the same
thresholds as upstream's `install.sh`.

## How this tree was assembled

Equivalent to upstream `./install.sh -g -t stylish -i color -s <variant>`,
minus the parts that are never referenced:

- `config/theme-<variant>.txt` -> `theme.txt`
- `backgrounds/<variant>/background-stylish.jpg` -> `background.jpg`
- `assets/info-<variant>.png` -> `info.png`
- `assets/assets-select/select-<variant>/*.png`
- `assets/assets-color/icons-<variant>/` -> `icons/`
- From `common/`, ONLY the fonts the theme.txt references:
  1080p uses `unifont-16.pf2` + `terminus-14.pf2`;
  4k uses `unifont-32.pf2` + `terminus-18.pf2`.
  Upstream copies every `.pf2`; the unreferenced ones (notably `unifont-24.pf2`,
  ~4 MB) were left out to keep the installer seed small.

Do not run upstream's `install.sh` on a target system from the agent: it is
interactive and edits `/etc/default/grub` in place. The agents install the
theme declaratively instead.

## Archive build

The bootable artifacts shipped in the agent payload are built from this tree:

```
tar -czf distros/_debian-family/agent/grub-theme-stylish-<variant>.tar.gz \
    -C distros/_shared/grub-theme/stylish/<variant> .
```

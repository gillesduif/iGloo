# GRUB theme: iGloo Community

The boot menu the first-boot agents install: a white screen with the igloo, the
name iGloo Community and the menu in Montserrat. The agents extract one archive
to `/boot/grub/themes/stylish/` (`/boot/grub2/themes/stylish/` on Fedora) and
point `GRUB_THEME` at its `theme.txt`.

The folder on the target and the two archives keep the name `stylish` from the
theme they first carried. The installer seed, the seed verifier and the preseed
scripts list the archives by that name, so a rename is a change to the seed
contract and not to the look.

## What is in an archive

| Files | From | License |
|---|---|---|
| `theme.txt`, `logo.png`, `select_*.png` | `igloo/<variant>/`, generated | GPL-3.0, this repository |
| `montserrat-semibold-*.pf2`, `montserrat-OFL.txt` | `igloo/<variant>/` and `igloo/OFL.txt` | SIL Open Font License 1.1 |
| `icons/`, `terminus-*.pf2` | `stylish/<variant>/`, vendored | GPL-3.0 (icons), SIL Open Font License 1.1 (Terminus) |

The Montserrat fonts are bitmap renderings of Montserrat SemiBold 7.200,
"Copyright 2011 The Montserrat Project Authors
(https://github.com/JulietaUla/Montserrat)". GRUB draws fonts without
anti-aliasing, so the glyphs are rendered 1-bit. They cover Basic Latin,
Latin-1 and Latin Extended-A; GRUB takes any other character from its own
`unicode.pf2`.

## Variants

- `1080p/` - panels up to 2560x1440
- `4k/` - larger panels, every size one and a half times

The variant is chosen at first boot from the migrated display layout in the
migration manifest (`displays[].widthPx` / `heightPx`). That is the panel, not
the mode GRUB gets: a firmware or a virtual machine can offer GRUB far less.
The menu is therefore 60% of the screen wide, and the logo is the only part
with a fixed width: 588 pixels in `1080p`, 881 in `4k`. `1080p` shows complete
from 640x480 and `4k` from 1024x768. On a narrower mode GRUB squeezes the logo.

## Limits read in the GRUB 2.12 source, not seen on a screen

- With Secure Boot on, GRUB refuses every font that is not inside its signed
  image, so the fonts of a theme are not loaded. The titles then appear in
  GRUB's own 16-pixel font; the logo with the name is an image and stays.
  This held for the previous theme as well.
- Terminal text (messages after an entry is chosen, the `c` command line, the
  `e` editor) is drawn light on GRUB's own dark terminal background. The theme
  gives the terminal the whole screen, so nothing of the white menu stays
  beside it.
- A title longer than the menu is cut off at its edge. At 1024 pixels wide
  that is about 40 characters in `1080p`.

## Building

```
py -3 scripts/generate_grub_theme.py [--preview <folder>]
```

It rewrites `igloo/<variant>/` and both archives; the same inputs give the same
bytes. It needs Pillow, `docs/assets/iGloo.png` and the Montserrat files in
`C:\Windows\Fonts`. `--preview` draws the menu at 1024x768, 1920x1080 and
3840x2160 with the bitmaps of the fonts and the positions of `theme.txt`; it is
a drawing by the script, not a GRUB screen.

`tests/agent/grub_theme_files_test.py` checks that each archive holds the
checked-in files, that every file and font `theme.txt` names is in it, and that
the fonts and images are in a form GRUB loads.

## Vendored part: Stylish

Source: https://github.com/vinceliuice/grub2-themes
License: GPL-3.0 (see LICENSE in this directory)
Upstream commit: 80dd04ddf3ba7b284a7b1a5df2b1e95ee2aad606

`stylish/<variant>/` was assembled as upstream
`./install.sh -g -t stylish -i color -s <variant>` does. Only its `icons/` and
its Terminus font go into the archives now. Its `theme.txt`, `background.jpg`,
`info.png`, `select_*.png` and Unifont files are no longer shipped.

Do not run upstream's `install.sh` on a target system from the agent: it is
interactive and edits `/etc/default/grub` in place. The agents install the
theme declaratively instead.

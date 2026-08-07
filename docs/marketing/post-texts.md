# Launch post texts (iGloo 0.1-alpha)

Working document, 5 August 2026. Video link placeholder: [VIDEO]. Adapt per
channel; do not copy-paste identical text across subreddits.

---

## Recording setup (VMware + OBS)

- Create the "old way" VM: Debian netinst ISO attached, default settings.
- Create the "iGloo" VM: Windows installed, **firmware type set to EFI** in
  VM settings (VMware defaults to BIOS; iGloo targets UEFI). Leave Secure Boot
  off. Give the virtual disk enough free space for the shrink plus the Debian
  install.
- Test the full iGloo run in VMware once before recording. If any pre-flight
  check misbehaves in a VM, better to know before the camera rolls.
- OBS: one scene per side, display capture of the VMware window, 1080p60.
  Record both runs separately; sync them in the edit at the "one click" moment.
- No voiceover needed; optional quiet music. The timers carry the story.

---

## Show HN (Hacker News)

Title:
```
Show HN: iGloo – one-click migration from Windows to Linux, no USB drive needed
```

Body:
```
I built iGloo, an open source Windows application that migrates a Windows
machine to Linux with one click. It shrinks the Windows partition from inside
Windows, installs the distribution with a fully preseeded installer, and on
first boot restores files, Wi-Fi profiles, browser data, wallpaper and the
per-monitor display layout, and installs GPU drivers (including NVIDIA).
Windows stays intact as a dual-boot fallback.

Fedora KDE, Linux Mint and Debian 13 are validated end-to-end on physical
hardware. Here is a split-screen video of a manual Debian install next to an
iGloo install: [VIDEO]

It is an alpha: unsigned installer (SmartScreen will warn), known limitations
listed in the release notes. GPL-3.0. Feedback very welcome, especially from
people with installer, partitioning or bootloader experience.

https://github.com/gillesduif/iGloo
```

Post Tuesday-Thursday, 13:00-15:00 UTC. Reply to every comment in the first
hours; that matters more than the post text.

---

## r/linux4noobs

Title:
```
I built a tool that installs Debian (or Fedora, or Mint) for you, straight
from Windows. No USB stick, no BIOS settings, no partitioning. Alpha, looking
for feedback.
```

Body:
```
Hi all. I kept seeing the same story: people want to try Linux but the
installation itself is the wall. So I spent months building iGloo: a Windows
program that does the whole migration for you. You pick a distribution and
which folders to keep, click once, and when the machine reboots you land in
Linux with your files, Wi-Fi, wallpaper and screen setup already done.
Windows stays next to it until you decide otherwise.

I made a split-screen video of the old way (manual Debian install) versus
iGloo, with timers on both sides: [VIDEO]

Honest caveats: this is an alpha. Tested on real hardware with Debian 13,
Fedora KDE and Linux Mint, but every PC is different, so back up your files
first. The installer is not signed yet, so SmartScreen will warn you once.

Happy to answer anything. If you try it, I would love to hear what happened,
good or bad.

https://github.com/gillesduif/iGloo
```

## r/windows10

Title:
```
Windows 10 support ended and your PC cannot run 11? I built a free, open
source tool that moves you to Linux in one click.
```

Body: same skeleton as r/linux4noobs, but lead with the Windows 10 angle:
unsupported hardware, security updates gone, and no need to buy a new PC.
Keep the same honest caveats paragraph.

## r/debian and r/linuxmint

Title (adjust per sub):
```
iGloo 0.1-alpha: unattended Debian 13 installs from inside Windows, validated
on bare metal. Open source, feedback wanted.
```

Body: shorter, more technical. Mention what is validated for their distro
specifically (preseeded installer, NVIDIA driver install, per-monitor display
layout, wallpaper and file migration). Link the per-distro docs in the repo.
Ask for testers with unusual hardware.

## r/linux

Check the sidebar first: r/linux restricts self-promotion. Safest route:
modmail the moderators, show the video, ask if a post is acceptable, or post
in the weekly self-promotion thread if one is active. Do not just post.

## X (30s cutdown)

```
Installing Debian, old way vs one click. Same machine, two timers.

iGloo 0.1-alpha is open source: https://github.com/gillesduif/iGloo

[VIDEO]
```

## LinkedIn

Lead with the builder story: solo developer, months of bare-metal testing,
WAN Show coverage before the first release, now looking for funding to work
on it full-time. Link video and repo. Tone: professional, no hype.

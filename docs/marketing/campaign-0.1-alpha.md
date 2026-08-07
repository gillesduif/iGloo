# iGloo 0.1-alpha launch campaign

Working document, 5 August 2026. Owner: Gilles D'huyvetter. Budget: EUR 0
(owned and earned channels only; paid channels noted for later).

---

## 1. Campaign overview

- **Name:** "The old way vs the iGloo way"
- **Summary:** a split-screen video that shows a manual Debian installation and
  an iGloo installation side by side, in real conditions, with honest counters.
- **Primary objective:** 1,000 GitHub stars and 500 release downloads within
  30 days of the video launch.
- **Secondary objectives:**
  - 3+ inbound contacts from stakeholders (distros, End of 10, hardware vendors)
  - qualitative feedback from 20+ real users to feed the 0.2 backlog
  - evidence of demand for the Sovereign Tech Fund application (prevalence)

## 2. Target audience

- **Primary:** Windows 10 users who cannot upgrade to Windows 11 and are
  Linux-curious but blocked by the installation process. They discover through
  Reddit (r/linux4noobs, r/windows10), YouTube and word of mouth. Buying stage:
  aware of the problem, afraid of the solution.
- **Secondary:** the Linux community (r/linux, r/linuxmint, r/debian, Hacker
  News). They will not use iGloo themselves, but they decide whether the video
  spreads, and they send it to exactly the primary audience ("finally something
  for my dad"). Buying stage: validators and amplifiers.

## 3. Key messages

- **Core message:** installing Linux used to be the hardest part of leaving
  Windows; now it is a download.
- **Supporting messages:**
  1. The old way is not stupid, it is just long and full of traps. (Respect the
     manual process; do not make it a straw man.)
  2. iGloo does the whole job: partition, install, drivers, display, files,
     Wi-Fi, wallpaper.
  3. It is alpha software and honest about it: checksums, safety model,
     back-up advice. Trust is the product.
  4. Open source, GPL-3.0, validated on physical hardware across three
     distributions.
- **Proof points:** the video itself (both sides shown, uncut timers), the
  WAN Show mention before any release existed, the three green bare-metal
  runs, the published release notes with known limitations.

## 4. The video: concept and shot list

**Format:** split screen, left "The old way", right "The iGloo way". Both sides
show a persistent on-screen timer and a click counter. Total length 2 to 3
minutes; long stretches in timelapse at 8-16x with the timer still visible.
One 30-second cutdown for X and Reddit.

**Left side (manual Debian in a VM):**
1. Download the ISO, verify you picked the right one
2. Download Rufus, write the USB stick
3. Reboot, find the boot-menu key, pick the USB
4. Installer: language, keyboard, network, mirror
5. Partitioning: the moment every beginner freezes. Show it honestly, uncut
6. Install, reboot, remove the USB
7. First boot: install drivers, fix display scaling, set the wallpaper,
   connect Wi-Fi
8. Counter summary

**Right side (iGloo):**
1. Download iGloo-Setup-0.1-alpha.exe
2. SmartScreen warning: show it. One click, "More info, Run anyway". This is
   the honest cost of an unsigned alpha and showing it builds trust
3. Answer the wizard questions (folders, distro, dual-boot)
4. One click. Timelapse through the automated work
5. Reboot into the boot menu, pick Debian
6. First login: everything already done. Show the restored wallpaper, the
   Wi-Fi connected, the display layout
7. Counter summary

**End card:** side-by-side numbers: elapsed time, clicks, decisions required,
reboots, moments of doubt. Then: "iGloo 0.1-alpha. Open source.
github.com/gillesduif/iGloo. Back up what you love."

**Honesty rules (non-negotiable):**
- The old way is performed by someone competent, not fumbled. The point is the
  step count, not stupidity.
- No cuts that hide waiting time on the iGloo side; timelapse is fine,
  invisible cuts are not.
- The SmartScreen warning and the alpha label stay in.

**Production notes:**
- Record both sides separately in OBS, sync them in editing; do not try to run
  two VMs live on one screen.
- The iGloo VM needs a Windows install with enough free disk to shrink and
  install into; test the full run once before recording.
- Edit in Kdenlive (on brand), DaVinci Resolve or CapCut.
- Export: 1080p landscape for YouTube, plus a 30-second cutdown, plus a GIF
  loop of the split screen for the README.

## 5. Channel strategy

| Channel | Why | Format | Effort |
|---|---|---|---|
| YouTube | Home of the main video; searchable forever | Full video, title: "Installing Debian: the old way vs one click" | Medium |
| r/linux, r/linuxmint, r/debian | Amplifiers; rules vary per sub, read them; r/linux has self-promo constraints | 30s cutdown + link, be active in comments for 48h | Medium |
| r/linux4noobs, r/windows10 | Primary audience lives here | Text post + video, framed as "I built this for you, feedback wanted" | Low |
| Show HN (Hacker News) | High-leverage for dev tools; "Show HN: iGloo, one-click Windows-to-Linux migration" | Text + repo link; post Tue-Thu, 13:00-15:00 UTC | Low |
| X | Cutdown + GIF, tag the WAN Show context | 30s cutdown | Low |
| LinkedIn | Funding and stakeholder visibility | Text post, slightly more formal, mention the funding search | Low |
| Facebook | Done (3 August) | - | - |
| End of 10 / KDE / Mint | Not media, but the video is the pitch asset | Outreach mail with the video link | Low |

## 6. Content calendar

| Week | Content piece | Channel | Status |
|---|---|---|---|
| W1 (now) | Record both VM runs; test iGloo VM run first | - | pending |
| W1 | Edit main video + cutdown + GIF | - | pending |
| W1 | Update README with the GIF near the top | GitHub | pending |
| W2, day 1 | Publish video on YouTube | YouTube | pending |
| W2, day 1 | Show HN post | HN | pending |
| W2, day 1-2 | Reddit posts (one per sub, tailored, not copy-paste) | Reddit | pending |
| W2, day 2 | X cutdown + LinkedIn post | X, LinkedIn | pending |
| W2, day 3-4 | Stakeholder outreach with the video (End of 10, Mint, Fedora Magazine) | Email | pending |
| W3-4 | Reply to every comment; collect bug reports into GitHub issues | All | pending |

## 7. Success metrics

| Metric | Target (30 days) | Tracking |
|---|---|---|
| GitHub stars | 1,000 | repo |
| Release downloads | 500 | GitHub release insights |
| YouTube views | 10,000 | YouTube Studio |
| Show HN front page | top 30 | HN |
| Stakeholder replies | 3+ | email |
| New GitHub issues (real bugs) | 10+ | issue tracker |

## 8. Risks and mitigations

- **The old-way side looks exaggerated.** Mitigation: competent performer,
  uncut partitioning moment, respectful framing.
- **A viewer hits a real bug and the thread turns.** Mitigation: respond fast,
  convert to GitHub issue, fix publicly; alpha honesty is the shield.
- **Subreddit self-promo bans.** Mitigation: read each sub's rules, tailor
  each post, ask mods when in doubt, lead with the video not the ask.

## 9. Next steps

1. Decide VM software and recording setup (OBS), confirm the iGloo VM run works
2. Record the old-way run
3. Edit; publish per calendar
4. Draft the outreach mails (templates live in docs/funding/)

# iGloo Roadmap

This roadmap tracks the project by milestone. Dates are deliberately absent:
the project is maintained by one person and the hardware matrix decides the
pace, not the calendar. Status reflects the actual state of the code and its
real-hardware validation, not aspirations.

Scope lives on the [milestones](https://github.com/gillesduif/iGloo/milestones)
so there is one source of truth. This page is the index.

Current release: 0.1-alpha. Fedora KDE, Linux Mint Cinnamon and Debian each
install unattended alongside Windows 11 on real hardware.

## Now

- [M13 Linux detection and removal](https://github.com/gillesduif/iGloo/milestone/13)
- [M15 Boot menu](https://github.com/gillesduif/iGloo/milestone/15)

## Next

- [M16 Pre-install safety snapshot and rollback](https://github.com/gillesduif/iGloo/milestone/16)
- [M17 Finalize UI](https://github.com/gillesduif/iGloo/milestone/17)
- [M18 Closed beta](https://github.com/gillesduif/iGloo/milestone/18)
- [M20 v1.0 public release](https://github.com/gillesduif/iGloo/milestone/20)

## Done

- [M1+M2 Skeleton and pre-flight detection](https://github.com/gillesduif/iGloo/milestone/2)
- [M3 ISO acquisition](https://github.com/gillesduif/iGloo/milestone/3)
- [M4 Migration setup and staging](https://github.com/gillesduif/iGloo/milestone/4)
- [M5 USB writer](https://github.com/gillesduif/iGloo/milestone/5)
- [M6 Disk selection UI + kickstart safety](https://github.com/gillesduif/iGloo/milestone/6)
- [M7 First-boot agent for Fedora KDE](https://github.com/gillesduif/iGloo/milestone/7)
- [M8 Direct install without USB](https://github.com/gillesduif/iGloo/milestone/8)
- [M9 Multi-distro expansion](https://github.com/gillesduif/iGloo/milestone/9)
- [M10 Security hardening](https://github.com/gillesduif/iGloo/milestone/10)
- [M11 Open-source readiness](https://github.com/gillesduif/iGloo/milestone/11)
- [M12 Windows installer packaging](https://github.com/gillesduif/iGloo/milestone/12)
- [M14 Bare-metal validation round (August 2026)](https://github.com/gillesduif/iGloo/milestone/14)

## After v1.0

Tracked as [M19 Post-1.0 enhancements](https://github.com/gillesduif/iGloo/milestone/19),
in rough priority order:

1. Ubuntu validation
2. Migration-report welcome screen
3. Boot-menu recovery entry
4. Catalog activation
5. Wizard localization
6. Accessibility pass
7. LUKS full-disk encryption option
8. Reproducible builds and signed releases
9. Cross-platform exploration

## How priorities are set

1. **Data safety outranks everything.** A partitioning fix in one distro
   triggers an audit of all distros in the same change (CONTRIBUTING.md rule 3).
2. **Validation beats features.** A distro only moves to done after a clean
   end-to-end run on real hardware, and the status never overstates.
3. **The hardware matrix decides.** New work is sequenced by the risk register
   in `docs/business/risk-register.md`, not by what would demo well.

Want to change this list? Open an issue. Want to work on it? See
[`CONTRIBUTING.md`](CONTRIBUTING.md).

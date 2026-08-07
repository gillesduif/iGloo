# Operation

```
┌──────────────────────────────────┐         ┌──────────────────────────────┐
│   Windows (iGloo.exe)            │         │   Linux installer            │
├──────────────────────────────────┤         ├──────────────────────────────┤
│ 1. Pre-flight hardware check     │         │                              │
│ 2. Distribution selection        │         │                              │
│ 3. ISO download; SHA-256 and     │         │                              │
│    GPG verification (pinned      │         │                              │
│    fingerprint)                  │         │                              │
│ 4. Wizard → migration manifest   │         │                              │
│ 5. Windows partition shrink      │         │                              │
│ 6. Staging partition creation    │  ────►  │ 8. UEFI → GRUB → installer   │
│    (kernel, initrd, installer    │         │ 9. Unattended installation   │
│     configuration, manifest,     │         │    (kickstart / preseed /    │
│     agent, full ISO if required) │         │     autoinstall)             │
│ 7. One-shot UEFI boot entry →    │         │ 10. First boot: agent runs   │
│    reboot                        │         │     before the login screen  │
└──────────────────────────────────┘         └──────────────────────────────┘
```

The Windows component and the Linux component exchange state through a single
`migration-manifest.json` file on the FAT32 staging volume. The first-boot
agent executes as a systemd oneshot unit ordered before the display manager,
so configuration completes before the first login. A fallback USB path (raw
ISO write plus staging partition on removable media) is available for machines
where direct installation is not possible. The complete description is in
[`docs/architecture.md`](../architecture.md).

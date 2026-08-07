# Comparison with existing tools

Earlier Windows-to-Linux installers were each tied to a single distribution
and did not migrate user data:

| Tool | Distribution | Data migration | Maintenance state |
|---|---|---|---|
| Wubi | Ubuntu | None | Discontinued |
| Operese | Kubuntu | Partial | Active, single distribution |
| Mint Stick | Linux Mint | None | Active, single distribution |
| **iGloo** | **Any supported distribution** | **Files, Wi-Fi networks, browser profiles, drivers, applications** | **In development** |

Each distribution is a self-contained plugin under `distros/` that consists of
a declarative boot specification, an installer configuration template and a
first-boot agent. Four distributions across four unrelated installer stacks
(Anaconda, debian-installer, Ubiquity/casper, subiquity) execute on the same
pipeline without pipeline modifications.

iGloo also supports the reverse operation. It detects an existing Linux
installation and removes it: it deletes the Linux partitions, removes the EFI
boot entries, restores the Windows bootloader and extends the Windows volume
into the resulting unallocated space.

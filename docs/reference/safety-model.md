# Safety and security model

iGloo writes to the partition table and the boot manager. The design target
for every failure in this class of operation is a clean abort with no data
modification. The implementation applies the following measures:

- **Uses a one-shot UEFI boot entry.** The installer boots through `BootNext`,
  a one-time NVRAM variable that the firmware clears after a single use. If
  the installer does not start, the next boot returns to Windows.
- **Uses Windows-native partition resize.** The shrink operation calls
  `Resize-Partition`, the same WMI mechanism used by Disk Management. The
  codebase contains no custom NTFS logic.
- **Restricts installer partitioning to unallocated space.** Unattended
  installer configurations target only the unallocated region created by the
  shrink operation and each distribution is tested against installer defaults
  that escalate to whole-disk erasure.
- **Verifies every downloaded ISO image.** Each image is checked against its
  SHA-256 checksum and its GPG signature. Signing keys are pinned by full
  160-bit fingerprint and bundled with the application where distribution
  policy permits. Short key IDs are not accepted. Verification failure aborts
  the installation.
- **Retains a functional display driver fallback.** On NVIDIA systems the
  installed system keeps the nouveau driver active until the proprietary
  driver is installed. A failed proprietary driver build produces a working
  desktop with a logged error instead of a system without display output.
- **Writes persistent execution traces.** Every unattended phase appends to
  logs under `/var/log/igloo*` on the target system and
  `%LOCALAPPDATA%\Igloo\logs` on the Windows host, so failures remain
  diagnosable after the fact.
- **Redacts credentials after first use.** The migration manifest clears the
  Linux account password and the encrypted browser credential blobs after the
  agent consumes them and Wi-Fi key files are written with `0600 root:root`
  permissions. Browser credentials travel as an AES-256-GCM envelope keyed
  from the Linux account password; the design is recorded in
  `docs/decisions/011-chromium-credential-migration.md`.

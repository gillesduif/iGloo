# Validation — 2026-09-09

Scope: the blocked Deepin plugin and preservation of existing repository behavior.
These results do **not** validate Linux installation, partitioning or migration.

## Build and tests

Windows host; .NET SDK **9.0.304** selected with a temporary `global.json` outside
the repository, matching the .NET 9 CI toolchain. Commands below used the absolute
solution path while running from that temporary directory. No SDK configuration
was added to the repository.

| Check | Result |
|---|---|
| `dotnet restore Igloo.sln` | Passed, all 18 projects restored/up to date. |
| `dotnet build Igloo.sln -c Release --no-restore -warnaserror` | Passed, zero warnings/errors. Full CA rules and build style analyzers enabled by Directory.Build.props. |
| `dotnet test Igloo.sln -c Release --no-build --verbosity normal` | Passed, all seven test projects: Core, App, Preflight, Iso, Migration, UsbWriter, Deepin. |
| Focused Deepin Release build with `-warnaserror` | Passed, zero warnings/errors. |
| Focused Deepin tests | **56 passed**, zero failed. |
| Existing `tests/agent/*_test.py` scripts under WSL Ubuntu-24.04 | All six passed: boot_order, chromium_profile, gecko_profiles, gnome_applier, grub_root_uuid, monitors_xml. Each script was executed directly, not via empty unittest discovery. |
| Deepin JSON Schema Draft 2020-12 + URI format validation | Passed. |
| Full catalog schema validation | **18/19 passed**. Existing Ubuntu `status: in-development` is outside the schema enum; the same failure was verified on `main`. Left untouched. |
| `git diff --check` | Passed. |
| Packaged Deepin DLL versus source-folder discovery DLL | SHA256 identical; app output metadata also says coming-soon. |
| CodeQL | Not executed locally: CLI unavailable. Existing CI CodeQL workflow remains required. |

Deepin tests check real packaged metadata and its coming-soon status; permanent
blocking even if metadata says available or omits status; all executable
entrypoints refusing; RAM/Secure Boot/NVIDIA findings; null inputs and cancellation;
missing/malformed/invalid metadata; unknown/missing/ambiguous target data;
unresolved placeholders; and malformed stale installer/agent resources.

There is deliberately no successful template-rendering, boot-spec or payload
packaging test: those capabilities are not implemented. Tests require refusal,
so a future developer cannot mistake a passing suite for a validated installation.
There is no disk-layout generator left to test. The next deployment implementation
needs a separate aggressive identity/layout and disposable-disk test suite.

## Repository safety

- Branch: `feature/deepin`.
- Feature HEAD and `main` remain `2f8f8cea148915042b137536d12e9cfcbcad1890`.
- `test/extract-cookies` remains `9f1c924be09e1f68b60386239bb4f548393474c2`.
- Index is empty; nothing committed or pushed.
- All eight UNRELATED/UNCERTAIN files in REVIEW.md were compared to their initial
  snapshot using SHA256 and remain byte-for-byte unchanged.
- Reviewed Deepin-driven edits to the app, core, preflight, allocation tests,
  `.gitattributes` and third-party notice were removed through normal file edits;
  those files have no diff against `main`.
- No destructive Git command, host partition write or firmware write was used.

The initial snapshot remains at the temporary path recorded in REVIEW.md.
It contains the rejected prototype if historical investigation needs it.

## Outstanding validation

No VM was booted and no end-to-end install was attempted. No QEMU executable was
available in the checked Windows/WSL command paths. More fundamentally, the owned
target contract and constrained deployment driver still need implementation;
booting the stock ISO would not validate those missing components.

See STATUS.md for the required VM and physical-hardware matrices. Until those
pass, keep Deepin coming-soon and all executable entrypoints blocked. The
pre-existing shared-agent `--fix-boot-order` defect documented in REVIEW.md is
not covered by the six passing agent scripts and remains outside this change.

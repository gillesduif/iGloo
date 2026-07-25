# Analyzer-cleanup pass — change manifest

Review aid for the July 2026 warning-cleanup pass. The solution now builds with
**0 warnings / 0 errors** under `AnalysisMode=All` + `EnforceCodeStyleInBuild`
(both persisted in `Directory.Build.props`). This file lists exactly which files
that pass touched and the kind of change in each, so the analyzer edits can be
reviewed apart from any other in-flight working-tree changes.

Per-rule policy (why, not just what) lives in `.editorconfig` and in the
`[SuppressMessage]` justifications on the defensive hardware/EFI-probing catches.

> Runtime-relevant (contract shapes, catch behaviour, P/Invoke marshalling, ConfigureAwait):
> the `REFACTOR_NOTES.md` Debian/Mint VM end-to-end validation is still required before merge.

## Totals by change kind

| Count | Change |
|------:|--------|
| 91 | StringComparison (CA1307/1310) |
| 49 | ConfigureAwait (CA2007) |
| 43 | null-guard (CA1062) |
| 40 | culture (CA1304/1305) |
| 35 | narrowed catch (CA1031) |
| 33 | suppression |
| 28 | LibraryImport (CA/SYSLIB, rule 1) |
| 27 | P/Invoke partial |
| 14 | contract: string->Uri (CA1054/1056) |
| 5 | contract: byte[]->ReadOnlyMemory (CA1819) |
| 3 | P/Invoke search-path pin (CA5392) |
| 3 | csproj: AllowUnsafeBlocks |
| 3 | async I/O (CA1849) |

## Files touched (51)

- `Directory.Build.props` — suppression ×1
- `distros/debian/DebianPlugin.cs` — StringComparison (CA1307/1310) ×13; null-guard (CA1062) ×2; narrowed catch (CA1031) ×1; ConfigureAwait (CA2007) ×1
- `distros/fedora-kde/FedoraKdePlugin.cs` — StringComparison (CA1307/1310) ×24; contract: string->Uri (CA1054/1056) ×2; null-guard (CA1062) ×2; culture (CA1304/1305) ×2; narrowed catch (CA1031) ×1; ConfigureAwait (CA2007) ×1
- `distros/linuxmint-cinnamon/LinuxmintCinnamonPlugin.cs` — StringComparison (CA1307/1310) ×9; null-guard (CA1062) ×2; ConfigureAwait (CA2007) ×2; narrowed catch (CA1031) ×1
- `distros/ubuntu/UbuntuPlugin.cs` — StringComparison (CA1307/1310) ×9; null-guard (CA1062) ×2; ConfigureAwait (CA2007) ×2; narrowed catch (CA1031) ×1
- `src/Igloo.App/App.xaml.cs` — suppression ×1; culture (CA1304/1305) ×1
- `src/Igloo.App/Behaviors/SmoothScroll.cs` — null-guard (CA1062) ×2
- `src/Igloo.App/ChromeInterop.cs` — P/Invoke partial ×9; LibraryImport (CA/SYSLIB, rule 1) ×8; P/Invoke search-path pin (CA5392) ×1
- `src/Igloo.App/Controls/CoverFlow3DControl.cs` — null-guard (CA1062) ×3
- `src/Igloo.App/Controls/PartitionBarPanel.cs` — null-guard (CA1062) ×2
- `src/Igloo.App/Igloo.App.csproj` — LibraryImport (CA/SYSLIB, rule 1) ×1; csproj: AllowUnsafeBlocks ×1
- `src/Igloo.App/ViewModels/DirectInstallViewModel.cs` — null-guard (CA1062) ×3; contract: string->Uri (CA1054/1056) ×2; culture (CA1304/1305) ×2; suppression ×2
- `src/Igloo.App/ViewModels/DiskSelectionViewModel.cs` — null-guard (CA1062) ×2
- `src/Igloo.App/ViewModels/DistroRecommender.cs` — null-guard (CA1062) ×1
- `src/Igloo.App/ViewModels/DistroSelectionViewModel.cs` — null-guard (CA1062) ×1; suppression ×1
- `src/Igloo.App/ViewModels/FileStagingViewModel.cs` — null-guard (CA1062) ×2; suppression ×1
- `src/Igloo.App/ViewModels/IsoAcquisitionViewModel.cs` — null-guard (CA1062) ×1; contract: byte[]->ReadOnlyMemory (CA1819) ×1; narrowed catch (CA1031) ×1; suppression ×1
- `src/Igloo.App/ViewModels/KeymapDetection.cs` — StringComparison (CA1307/1310) ×24; narrowed catch (CA1031) ×1
- `src/Igloo.App/ViewModels/LinuxUsernameRules.cs` — suppression ×1
- `src/Igloo.App/ViewModels/MainWindowViewModel.cs` — null-guard (CA1062) ×9
- `src/Igloo.App/ViewModels/MigrationSetupViewModel.cs` — null-guard (CA1062) ×1
- `src/Igloo.App/ViewModels/PreflightViewModel.cs` — suppression ×5
- `src/Igloo.App/ViewModels/UsbWriterViewModel.cs` — null-guard (CA1062) ×2; suppression ×2; culture (CA1304/1305) ×2
- `src/Igloo.Core/Abstractions/IDirectInstallService.cs` — contract: string->Uri (CA1054/1056) ×1
- `src/Igloo.Core/Abstractions/IDistroPlugin.cs` — contract: byte[]->ReadOnlyMemory (CA1819) ×3
- `src/Igloo.Core/Abstractions/IIsoAcquisitionService.cs` — contract: byte[]->ReadOnlyMemory (CA1819) ×1
- `src/Igloo.Core/Models/DistroManifest.cs` — contract: string->Uri (CA1054/1056) ×5
- `src/Igloo.Core/Plugins/DistroLoader.cs` — narrowed catch (CA1031) ×1
- `src/Igloo.Core/Plugins/DistroRegistry.cs` — narrowed catch (CA1031) ×1
- `src/Igloo.Core/Services/ManifestGeneratorService.cs` — null-guard (CA1062) ×3
- `src/Igloo.Iso/IsoAcquisitionService.cs` — ConfigureAwait (CA2007) ×20; narrowed catch (CA1031) ×4; suppression ×2; null-guard (CA1062) ×1
- `src/Igloo.Iso/PgpCleartextVerifier.cs` — narrowed catch (CA1031) ×1
- `src/Igloo.Iso/PgpDetachedVerifier.cs` — narrowed catch (CA1031) ×1
- `src/Igloo.Migration/FileStagingService.cs` — ConfigureAwait (CA2007) ×5; null-guard (CA1062) ×1; narrowed catch (CA1031) ×1
- `src/Igloo.Preflight/DirectInstallService.cs` — narrowed catch (CA1031) ×9; culture (CA1304/1305) ×7; LibraryImport (CA/SYSLIB, rule 1) ×6; P/Invoke partial ×6; suppression ×3; contract: string->Uri (CA1054/1056) ×2; StringComparison (CA1307/1310) ×2
- `src/Igloo.Preflight/EfiBootEntries.cs` — P/Invoke partial ×7; LibraryImport (CA/SYSLIB, rule 1) ×6; StringComparison (CA1307/1310) ×3; suppression ×2; P/Invoke search-path pin (CA5392) ×1
- `src/Igloo.Preflight/Igloo.Preflight.csproj` — LibraryImport (CA/SYSLIB, rule 1) ×1; csproj: AllowUnsafeBlocks ×1
- `src/Igloo.Preflight/LinuxRemovalService.cs` — culture (CA1304/1305) ×5; suppression ×3; narrowed catch (CA1031) ×2
- `src/Igloo.Preflight/PartitionResizeService.cs` — culture (CA1304/1305) ×6; suppression ×1
- `src/Igloo.Preflight/WindowsAppScanner.cs` — narrowed catch (CA1031) ×3; suppression ×1
- `src/Igloo.Preflight/WindowsPreflightChecker.cs` — culture (CA1304/1305) ×13; suppression ×1
- `src/Igloo.Preflight/WindowsWifiScanner.cs` — narrowed catch (CA1031) ×4; StringComparison (CA1307/1310) ×2; suppression ×1
- `src/Igloo.UsbWriter/Igloo.UsbWriter.csproj` — LibraryImport (CA/SYSLIB, rule 1) ×1; csproj: AllowUnsafeBlocks ×1
- `src/Igloo.UsbWriter/NativeMethods.cs` — LibraryImport (CA/SYSLIB, rule 1) ×5; P/Invoke partial ×5; P/Invoke search-path pin (CA5392) ×1
- `src/Igloo.UsbWriter/UsbWriterService.DiskPreparation.cs` — ConfigureAwait (CA2007) ×1; suppression ×1
- `src/Igloo.UsbWriter/UsbWriterService.GrubPatching.cs` — StringComparison (CA1307/1310) ×5; suppression ×1
- `src/Igloo.UsbWriter/UsbWriterService.cs` — ConfigureAwait (CA2007) ×17; culture (CA1304/1305) ×2; narrowed catch (CA1031) ×2; null-guard (CA1062) ×1; suppression ×1
- `tests/Igloo.App.Tests/DistroRecommenderTests.cs` — contract: string->Uri (CA1054/1056) ×1
- `tests/Igloo.Core.Tests/DistroManifestTests.cs` — contract: string->Uri (CA1054/1056) ×1
- `tests/Igloo.Iso.Tests/PgpDetachedVerifierTests.cs` — suppression ×1
- `tests/Igloo.Migration.Tests/FileStagingServiceTests.cs` — async I/O (CA1849) ×3

## Notable judgment calls (not mechanical)

- **CA1031 on raw WMI/disk/EFI/firmware probing** kept broad + `[SuppressMessage]`
  with justification (crash-safety on non-standard hardware); only well-known
  file/registry/process/DriveInfo surfaces were narrowed to typed exceptions.
- **CA1308** kept `ToLowerInvariant` where lowercase is the domain contract
  (hex checksums, Linux usernames, grub.cfg display paths) — suppressed with reason.
- **CA2000** `FileStream`-owns-`SafeFileHandle` false positives — disposed on failure
  paths, then suppressed.
- **CA1848 (LoggerMessage)** suppressed by policy in `.editorconfig` (low value for a
  desktop installer). **CA2007/CA1515** off for the WPF app; **CA1707/CA2007/CA1515/
  CA1062/CA1819/CA1034** off for test projects.


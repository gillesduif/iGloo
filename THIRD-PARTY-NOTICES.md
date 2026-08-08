# Third-Party Notices

iGloo is distributed under the GPL-3.0-or-later (see [LICENSE](LICENSE)). It also
includes links against or redistributes the third-party components below. Each
remains under its own license, held by its own authors.

> This file is maintained by hand. If you add or bump a dependency, update it in
> the same change.

## Bundled runtime dependencies (NuGet)

| Package | Version | License |
|---|---|---|
| CommunityToolkit.Mvvm | 8.2.2 | MIT |
| Microsoft.Data.Sqlite | 8.0.4 | MIT |
| Microsoft.Extensions.Hosting | 8.0.0 | MIT |
| Microsoft.Extensions.Http | 8.0.0 | MIT |
| Microsoft.Extensions.Logging.Abstractions | 8.0.0 | MIT |
| System.Management | 8.0.0 | MIT |
| System.Security.Cryptography.ProtectedData | 8.0.0 | MIT |
| Serilog.Extensions.Hosting | 8.0.0 | Apache-2.0 |
| Serilog.Sinks.File | 5.0.0 | Apache-2.0 |
| Svg.Skia | 4.9.0 | MIT |
| BouncyCastle.Cryptography | 2.3.1 | MIT |

## Test-only dependencies (not shipped)

| Package | Version | License |
|---|---|---|
| xunit | 2.7.0 | Apache-2.0 |
| xunit.runner.visualstudio | 2.5.7 | Apache-2.0 |
| Microsoft.NET.Test.Sdk | 17.9.0 | MIT |
| FluentAssertions | 6.12.0 | Apache-2.0 (v6) |

> Note: FluentAssertions changed to a commercial license in v8. This project pins
> v6, which is Apache-2.0. Do not bump it past v7 without revisiting licensing.

## Bundled assets

### Microsoft Fluent 3D emoji icons
`src/Igloo.App/Assets/Fluent3D/*.png` - from Microsoft's **fluentui-emoji**
project, MIT licensed. © Microsoft.
<https://github.com/microsoft/fluentui-emoji>

### WPF Fluent theme
The application merges the WPF Fluent (`Fluent.Dark.xaml`) theme that ships with
the .NET Desktop runtime. It is part of .NET and covered by the .NET license.

### Stylish GRUB theme
`distros/_shared/grub-theme/stylish/` and the archives built from it
(`grub-theme-stylish-*.tar.gz` in the agent payload) - from vinceliuice's
**grub2-themes** project, GPL-3.0 licensed. © vinceliuice and contributors.
Upstream commit and assembly notes are in `distros/_shared/grub-theme/README.md`.
<https://github.com/vinceliuice/grub2-themes>

## Distribution logos and trademarks

`distros/*/logo/*` contains the logos of the supported Linux distributions
(Fedora, Debian, Ubuntu, Linux Mint, KDE Neon, Garuda, Deepin, and others).

**These logos are trademarks of their respective projects and are NOT covered by
this repository's GPL-3.0 license.** They are included solely to identify each
distribution in the picker. Each project's trademark/logo-usage policy governs
their use; some restrict modification or commercial use independently of the
software license.

<!-- TODO(maintainer): before publishing, confirm each distro's logo/trademark
     policy permits redistribution here, or switch to linking/first-run download.
     This is the item most likely to draw a takedown request. -->

If you maintain one of these projects and want your logo changed or removed,
please open an issue and we'll act promptly.

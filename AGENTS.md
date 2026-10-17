# Repository notes for agents

Follow existing project contracts and keep unrelated working-tree edits intact. The documentation vault under `docs/` distinguishes observed implementation, tests, simulations and production evidence.

## One mascot components

- `src/Igloo.Mascot.Core` targets `net8.0` and must stay free of WPF, web hosting and installer authority. Its wing profile comes from `docs/assets/Tux.svg` using `scripts/generate_mascot_samples.py` and the checked-in numeric resource.
- `src/Igloo.Mascot.Wpf` targets `net8.0-windows`. Its `Themes/Generic.xaml` is generated from the checked-in SVG with `scripts/generate_mascot_xaml.py`; rerun that script after changing the art, then verify the 25 required artwork ids, clips and filters in generated XAML and a rendered pose. The template removes converted `BlurEffect` at runtime because the local performance probe found it costly; the procedural wing glare uses clipped geometry. Keep the dynamic wing silhouette and glare clip in sync.
- `Igloo.One.App` currently targets .NET 10 and consumes the .NET 8 WPF library. The mascot demo is visual only. Do not route mascot actions or task-card state to disk, boot, recovery or migration execution.
- Run `dotnet test tests/Igloo.Mascot.Core.Tests/Igloo.Mascot.Core.Tests.csproj`, build `Igloo.One.App`, and capture the pose PNGs with `Igloo.One.App.exe --mascot-capture <output-directory>` after changing the rig or art. Mathematical golden tests do not replace visual or performance checks.
- Document measured behavior in `docs/06 - One/Tux Mascot.md`. Keep the known head-silhouette and narrow-wing limits visible.

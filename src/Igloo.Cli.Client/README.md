# Igloo.Cli.Client

Process client for the iGloo CLI. It locates a packaged `Igloo.Cli.exe` beside the calling application (`cli\Igloo.Cli.exe` with its `distros\` folder), verifies the executable's product identity, never searches `PATH`, starts the CLI with `invoke --machine`, frames JSON Lines requests and returns typed `CommandResult` values from `Igloo.Command.Contracts`.

This package is licensed under Apache-2.0. The CLI it launches is GPL-3.0-or-later and lives in [gillesduif/iGloo](https://github.com/gillesduif/iGloo); ship it next to your application as a separate program.

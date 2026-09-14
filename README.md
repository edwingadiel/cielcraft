# CielCraft

A crafting plugin for Final Fantasy XIV, built on [Dalamud](https://github.com/goatcorp/Dalamud).

## Status

Early scaffold — plugin loads, registers `/cielcraft`, and shows a main window and a settings window. Crafting features to come.

## Commands

- `/cielcraft` — toggle the main window
- `/cielcraft config` — toggle the settings window

## Building

Requires the .NET SDK and the Dalamud dev assemblies.

- On a machine with XIVLauncher installed, Dalamud is found automatically via the default dev path.
- Otherwise, download the [Dalamud distrib](https://goatcorp.github.io/dalamud-distrib/latest.zip), extract it, and point `DALAMUD_HOME` at the extracted folder.
- Building on macOS/Linux works too (`EnableWindowsTargeting` is set in the csproj); e.g. with Homebrew's `dotnet` and the distrib extracted to `~/Library/Application Support/CielCraftDev/Dalamud`:

```
DALAMUD_HOME="$HOME/Library/Application Support/CielCraftDev/Dalamud" dotnet build
```

### Native Raphael solver

Crafting rotations come from [Raphael](https://github.com/KonaeAkira/raphael-rs) (Apache-2.0), wrapped as a C ABI library in `native/cielcraft-raphael`. The solver crate is vendored in `native/raphael-solver` with one addition (solving from a live mid-craft state; see its README). Build it with Rust:

```
cargo build --release --manifest-path native/cielcraft-raphael/Cargo.toml
```

CI builds the Windows x64 `cielcraft_raphael.dll` and bundles it into the plugin artifact automatically; a locally built host library lets `CielCraft.Tests` exercise the real solver offline. Without the library the plugin still loads — the Raphael status shows "Native library missing" and solving is disabled.

The packaged plugin ends up in `CielCraft/bin/x64/Debug/CielCraft` (via DalamudPackager).

## Installing in game

Add the custom plugin repository in `/xlsettings` → Experimental → Custom Plugin Repositories:

```
https://raw.githubusercontent.com/edwingadiel/cielcraft/main/repo.json
```

then install CielCraft from `/xlplugins`. Releases are published by tagging (`git tag v0.7.0 && git push --tags`).

## Testing in game (dev builds)

1. In game, run `/xlsettings` → Experimental → Dev Plugin Locations.
2. Add the full path to the built `CielCraft.dll`.
3. Run `/xlplugins` → Dev Tools → Installed Dev Plugins → enable CielCraft.

## Project layout

- `CielCraft/Plugin.cs` — entry point: services, command registration, window system
- `CielCraft/Configuration.cs` — persisted plugin configuration
- `CielCraft/Windows/` — ImGui windows (main + settings)
- `CielCraft/CielCraft.json` — plugin manifest

## Reporting a problem

Run `/cielcraft report` (or the **Report** button in the main window footer, or **Copy diagnostic report** in the debug window). It copies a full diagnostic report to the clipboard and saves it under the plugin config folder. Paste it with a one-line description of what you were doing. The report contains settings, live game state, the internal state of every automation layer and the last 200 log lines; it does not contain your character name or world.

The in-game test checklist is in [docs/TESTPLAN.md](docs/TESTPLAN.md).

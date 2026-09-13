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

```
dotnet build
```

The packaged plugin ends up in `CielCraft/bin/x64/Debug/CielCraft` (via DalamudPackager).

## Testing in game

1. In game, run `/xlsettings` → Experimental → Dev Plugin Locations.
2. Add the full path to the built `CielCraft.dll`.
3. Run `/xlplugins` → Dev Tools → Installed Dev Plugins → enable CielCraft.

## Project layout

- `CielCraft/Plugin.cs` — entry point: services, command registration, window system
- `CielCraft/Configuration.cs` — persisted plugin configuration
- `CielCraft/Windows/` — ImGui windows (main + settings)
- `CielCraft/CielCraft.json` — plugin manifest

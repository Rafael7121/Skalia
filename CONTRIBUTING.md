# Contributing to SKALIA

## Building

Needs the [.NET SDK](https://dotnet.microsoft.com/download) 8 or newer. The
launcher targets .NET Framework 4.8, which ships with Windows. The plugin
references the DLLs of your Unturned Dedicated Server and never copies them.

Clone this repository into `<Unturned Dedicated Server>\Skalia` and run:

```
build.cmd
```

It builds `SKALIA.exe` and `plugins\SkaliaDeps.dll` into the repository folder.
If the repository is somewhere else, set `U3DSDir` first:

```
set U3DSDir=C:\Program Files (x86)\Steam\steamapps\common\U3DS
build.cmd
```

## Project layout

| Path | What it is |
|---|---|
| `src/Skalia.Launcher/MainForm.cs` | The window and its pages |
| `src/Skalia.Launcher/Theme.cs` | Colors and the hand-drawn controls |
| `src/Skalia.Launcher/ServerManager.cs` | Prepares the server profile, starts and stops the server, opens the game |
| `src/Skalia.Launcher/GameRules.cs` | Server rules and multipliers written to `Config.txt` |
| `src/Skalia.Launcher/GamePaths.cs` | Finds Steam and the Dedicated Server |
| `src/Skalia.Launcher/OpenModInstaller.cs` | Installs OpenMod into the Dedicated Server when it is missing |
| `src/Skalia.Launcher/L.cs` | English and Brazilian Portuguese text |
| `src/Skalia.Deps` | The SKALIA DEPS OpenMod plugin (bots, TPA, Home, Vault) |
| `DESIGN.md` | Visual rules for the launcher |

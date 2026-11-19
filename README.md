# SKALIA

Play Unturned offline with bots. SKALIA starts a private LAN server on your own
PC, using the Unturned Dedicated Server you already have from Steam, and puts
you in it with one click.

SKALIA does not include or redistribute any Unturned files. It runs on top of
your own installation.

## Requirements

- Windows 10 or 11
- Steam, running and signed in
- **Unturned** (Steam)
- **Unturned Dedicated Server**, free on Steam under *Library > Tools*
- The **SKALIA DEPS** OpenMod plugin (`SkaliaDeps.dll`) in the `plugins` folder
  next to `SKALIA.exe`. It provides TPA, Home, Vault and the bots. It is not part
  of this repository.

## How to use

1. Open `SKALIA.exe`. It finds the Dedicated Server through your Steam libraries.
2. Pick a map, a difficulty and how much loot and how many zombies you want.
   Bots, world rules and server options are under **Advanced settings**.
3. Click **PLAY**. SKALIA prepares the server and loads the map.
4. When the button turns into **JOIN GAME**, click it. Unturned opens through
   Steam and connects to `127.0.0.1`. If Steam asks, choose to play without
   BattlEye.
5. Click **Save and stop server** when you are done, so your world is saved.

The world, saves and server config live in
`<Unturned Dedicated Server>\Servers\Skalia`. Launcher settings are saved in
`skalia.json` next to `SKALIA.exe`.

To connect by hand from Unturned (*Play > Connect*), use host `127.0.0.1`, port
`27025` (or the port set in SKALIA) and the password, if you set one.

## Building

Needs the [.NET SDK](https://dotnet.microsoft.com/download) 8 or newer. The
launcher targets .NET Framework 4.8, which ships with Windows.

```
build.cmd
```

or

```
dotnet build src/Skalia.Launcher/Skalia.Launcher.csproj -c Release
```

The output is `src/Skalia.Launcher/bin/Release/net48/SKALIA.exe`.

## Project layout

| Path | What it is |
|---|---|
| `src/Skalia.Launcher/MainForm.cs` | The window and its pages |
| `src/Skalia.Launcher/Theme.cs` | Colors and the hand-drawn controls |
| `src/Skalia.Launcher/ServerManager.cs` | Prepares the server profile, starts and stops the server, opens the game |
| `src/Skalia.Launcher/GameRules.cs` | Server rules and multipliers written to `Config.txt` |
| `src/Skalia.Launcher/GamePaths.cs` | Finds Steam and the Dedicated Server |
| `src/Skalia.Launcher/L.cs` | English and Brazilian Portuguese text |
| `DESIGN.md` | Visual rules for the launcher |

## Languages

English by default. Brazilian Portuguese can be picked in the sidebar.

## License

MIT, see [LICENSE](LICENSE).

SKALIA is a fan project. It is not affiliated with or endorsed by Smartly
Dressed Games. Unturned is a trademark of Smartly Dressed Games.

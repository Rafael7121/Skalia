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
| `src/Skalia.Deps/src/Bots` | The bots (see [How the bots work](#how-the-bots-work)) |
| `DESIGN.md` | Visual rules for the launcher |
| `KNOWN_ISSUES.md` | What is untested, reported or missing |
| `docs/LOGS.md` | How to read the bot lines of the server log |
| `tools/recordings` | Python scripts that compare bots and players in the recordings |

## Rules for bot behaviour

These hold for every change to the bots:

- **No wallhack.** A bot knows only what it sees (120 degrees across, line of
  sight, the same as a player's screen) or hears (shots, steps). It never reads
  another player's position to decide anything it could not have seen.
- **Measured, not invented.** A new behaviour copies what real players do in
  the recordings (see [Recordings](#recordings)), or follows a tester's precise
  description of a situation. When there is no data, say so instead of guessing
  numbers.
- **Root causes.** Fix why the bot did something, not the one place it showed.
- **Say where it came from.** Each change carries a comment with the version
  and the evidence: the recording or log line, the test, the numbers measured.
  Example from `SkaliaBotPvpFlankY.cs`:

  ```csharp
  // 0.1.0-y17.28 (the tester, 20/11: "...dar a volta e tentar achar um
  // flanko melhor"). Where the players were after the last round of a fight
  // (rec3/predict.py, 71-76 ends of a fight): above the other one by more
  // than 4m and standing still: half of them within 5m of the spot 10 and
  // 20s later...
  ```

- **The game's own rules.** Bots send input packets like a client and use the
  game's checks (stamina, doors that need a key, the reach of a shot). Check the
  decompiled game code before assuming how something works.

## How the bots work

Each bot is a fake client inside the server process
(`SkaliaBotAuthenticationService`, `SkaliaBotTransportConnection`). Its brain is
the partial class `SkaliaBotIdleSimulation`, split into one file per subject.
The newer files end in the version letter that added them (`...Y.cs`,
`...X11.cs`); the comment at the top of each file says what it does and why.

Every tick the behaviour tree (`BuildBehaviorTree` in
`SkaliaBotIdleSimulation.cs`) picks the first branch that applies:

1. Hazard: out of a spot that is hurting it
2. Out of a radiation zone in time
3. Water
4. Forced movement: a ladder under way, a flight, getting unstuck
5. The utility selector: Mega Zombie hunt or flight, getting combat gear, low
   health flight, zombie hordes, burning zombies, combat, refilling, loot,
   exploring

The fight against players runs beside the tree from `TickPvp`: vision and
hearing find a man, then the fight, cover, hunt and memory take over.

| Subject | Files |
|---|---|
| Seeing players | `SkaliaBotPvpBotsY`, `SkaliaBotPvpCameraY`, `SkaliaBotPvpSpotY` |
| Hearing shots | `SkaliaBotHearingY`, `SkaliaBotShotSectorY` |
| Aim and shooting | `SkaliaBotPvpY`, `SkaliaBotPvpAimY`, `SkaliaBotPvpHandY`, `SkaliaBotPvpRecoilY`, `SkaliaBotPvpSprayY`, `SkaliaBotPvpSpreadY`, `SkaliaBotPvpFeelY` |
| Cover | `SkaliaBotPvpCoverY`, `SkaliaBotPvpFightCoverY`, `SkaliaBotPvpLookCoverY` |
| After losing him | `SkaliaBotPvpHuntY`, `SkaliaBotPvpFlankY`, `SkaliaBotPvpWayInY`, `SkaliaBotPvpMemoryY`, `SkaliaBotPvpChaseY` |
| Melee, no ammo, healing in a fight | `SkaliaBotPvpMeleeY`, `SkaliaBotPvpNoAmmoY`, `SkaliaBotPvpHealY` |
| Moving like a player | `SkaliaBotPlayerHabits`, `SkaliaBotMoveFeelY`, `SkaliaBotPlayerFeel`, `SkaliaBotStaminaX18` |
| Routes and getting unstuck | `SkaliaBotUnityNavmesh`, `SkaliaBotRouteX9`, `SkaliaBotRouteX11`, `SkaliaBotShortcutLinks`, `SkaliaBotPassageLinks`, `SkaliaBotRampLinks`, `SkaliaBotLadder*` |
| Map knowledge | `SkaliaBotObjectMap`, `SkaliaBotStructureMap` (buildings, doors, keys), `SkaliaBotTerrainMap`, `SkaliaBotVantageMap` |
| Loot and gear | `SkaliaBotFullLootY` (the top gear of the map, read from its loot tables), `SkaliaBotTryhardGear`, `SkaliaBotTryhardLoot`, `SkaliaBotLootWorthX13`, `SkaliaBotWeaponSlots`, `SkaliaBotWearX11` |
| Radiation and key doors | `SkaliaBotRadiationY`, `SkaliaBotKeysY` |
| Zombies | `SkaliaBotThreat`, `SkaliaBotHordeX8`, `SkaliaBotHordeCommitY`, `SkaliaBotZombieCrowdX14`, `SkaliaBotZombieFootworkX13`, `SkaliaBotMegaX8` |
| Recorder | `SkaliaPlayerRecorder` |

Settings live under `serverBot:` in `src/Skalia.Deps/config.yaml`.

## Testing a build

- **Never copy the DLL while the server is running.** OpenMod notices the new
  file and reloads the plugin in the middle of the game. Close the server first
  and check that the end of the log says `plugins unloaded`.
- **Back up** the DLL and any config before replacing them.
- **Versions** go in `InformationalVersion` of `Skalia.Deps.csproj` and must be
  SemVer (`0.1.0-y17.28`); the log prints it at load, so every log can be tied
  to a build.
- After loading, check the log for the lines the change should produce (see
  [docs/LOGS.md](docs/LOGS.md)), then play a session with the recorder on.

## Recordings

The recorder writes one CSV per player per session to
`Servers/<server>/SkaliaRecordings`, a row every 0.08s while something changes
and once a second while standing still. Bots are recorded too (files with
`_BOT_` in the name, removed after 7 days).

| Command | What it does |
|---|---|
| `/gravar` | Show the recorder status |
| `/gravar ligar` / `/gravar parar` | Start / stop recording |
| `/gravar bots ligar` / `/gravar bots parar` | Also record / stop recording the bots |
| `/gravar marca <note>` | Write a note into your own recording, at that moment |

The columns:

| Column | Meaning |
|---|---|
| `t` | Seconds since the file started (the start time is in the file name) |
| `x`, `y`, `z`, `yaw`, `pitch` | Position and view |
| `postura`, `no_chao` | Stance (`STAND`, `SPRINT`, `CROUCH`, `PRONE`, `SWIM`, `DRIVING`...) and grounded |
| `frente`, `lado`, `vertical` | Movement keys: forward/back, left/right, up/down |
| `pulo`, `correr`, `agachar`, `deitar` | Jump, sprint, crouch and prone keys |
| `ataque1`, `ataque2` | Primary and secondary use (trigger, aim) |
| `arma` | Item in hand |
| `vida`, `folego`, `comida`, `agua`, `sangrando` | Health, stamina, food, water, bleeding |
| `dia` | 1 by day, 0 at night |
| `zumbi_m`, `zumbi_alvo`, `zumbis_15m`, `zumbis_seguindo`, `zumbis` | Nearest zombie, whether it targets this player, zombies within 15m, following, and up to 4 within 40m |
| `frente_obst` | What stands ahead at knee, waist and head height, up to 4m |
| `teto` | 1 with a roof (or anything) overhead |
| `mira` | What the crosshair is on, up to 60m |
| `item_perto` | The nearest visible item on the ground within 8m |
| `evento` | Shots (`atirou`), hits given and taken (`acertou`, `levou`), kills, doors, items picked up or used, notes |

The file names carry the player's Steam ID, so keep raw recordings out of the
repository. `tools/recordings` has scripts that compare bots and players; see
its README.

## Reporting a bot problem

Write down:

1. The time (from your clock; the server log uses the same one), the bot's name
   and the map.
2. What the bot did and what a player would have done there.
3. The bot's log lines from that minute (see [docs/LOGS.md](docs/LOGS.md)).
   Remove other players' names and Steam IDs before posting them.
4. If the recorder was on, which recording files cover it.

Known problems are listed in [KNOWN_ISSUES.md](KNOWN_ISSUES.md).

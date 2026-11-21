# SKALIA DEPS

OpenMod plugin that runs inside the SKALIA server. The launcher installs it on
every start and it cannot be turned off.

## What it does

- **Bots**: local fake clients that join the server, loot, fight zombies and
  players, heal and move the way players do. Their behaviour is tuned from
  recordings of real play sessions. Registered bots live in
  `Servers/Skalia/OpenMod/plugins/Skalia.Deps/bots.json`.
- **Player commands**: `/tpa`, `/home`, `/homes`, `/renomear`, `/vault`,
  `/sort`, `/cv` (vote to skip the night or clear the weather).
- **Admin commands**: `/god`, `/heal`, `/spy`, `/clear`, `/admin` and the bot
  commands (`/addbot`, `/callbots`, `/kickbots`, `/playbots`, `/stopbots`...).
- **Server rules**: spawn loadouts per map, extra zombies per region, Mega
  Zombie rewards, ground cleanup, automatic reload, extra loot above the game's
  limit.
- **Launcher bridge**: files in the server's profile folder. `skalia.loading`
  holds the progress (0 to 100) while the bots' navmesh is built, and
  `skalia.ready` appears once the map and the navmesh are done. The bots join
  only after that. `skalia.stop`, written by the launcher, saves and shuts the
  server down.
- **Development tools** used to record players and tune the bots (`/gravar`,
  `/malhanova`, `/malhateste`, `/passagem`, `/irpara`, `/navbot`, `/navid`,
  `/zumbis`).

Commands follow OpenMod permissions. The bots run entirely inside the server
process: they do not use Steam accounts and are not published to the Steam
server list.

## Configuration

`config.yaml` holds every setting with a comment above it. Everything is read
when the plugin loads. The launcher writes these keys before it starts the
server:

| Key | What it does |
|---|---|
| `serverBot:autoCallOnStart` | Bring the registered bots in once the server is ready |
| `serverBot:botLogs` | `true` logs the bots' Information and Debug lines; `false` keeps only warnings and errors (see [docs/LOGS.md](../../docs/LOGS.md)) |
| `serverBot:modifiers:*` | Bot difficulty, 32 multipliers where 1 is the recorded behaviour (see [docs/MODIFIERS.md](../../docs/MODIFIERS.md)) |
| `tpa:enabled`, `home:enabled`, `vault:enabled` | `false` turns off `/tpa`, `/home` with `/homes` and `/renomear`, or `/vault`; the command then says it is off on this server |
| `automaticReload:enabled` | `false` turns off the automatic reload when a magazine runs dry |
| `lootAmount:spawnFactor` | Extra loot above the game's own limit |

A missing switch counts as `true`. Player-facing
messages are in `translations.yaml` (Brazilian Portuguese, embedded in the DLL)
and `translations.en.yaml` (English). The launcher uses the one that matches its
own language; add new keys to both files.

## Building

Needs the .NET SDK 8 or newer and an installed Unturned Dedicated Server. The
game's DLLs are only referenced from it, never copied.

```
dotnet build -c Release -p:U3DSDir="C:\Program Files (x86)\Steam\steamapps\common\U3DS"
```

If this repository is cloned into `U3DS\Skalia`, `U3DSDir` can be left out.
The output is `bin/Release/netstandard2.1/SkaliaDeps.dll`. Put it in the
launcher's `plugins` folder.

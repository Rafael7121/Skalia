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
- **Launcher bridge**: tells the launcher when the map is ready and shuts the
  server down cleanly when the launcher asks.
- **Development tools** used to record players and tune the bots (`/gravar`,
  `/malhanova`, `/malhateste`, `/passagem`, `/irpara`, `/navbot`, `/navid`,
  `/zumbis`).

Commands follow OpenMod permissions. The bots run entirely inside the server
process: they do not use Steam accounts and are not published to the Steam
server list.

## Configuration

`config.yaml` holds every setting with a comment above it. The launcher writes
`serverBot:autoCallOnStart` and `lootAmount:spawnFactor` itself. Player-facing
messages are in `translations.yaml` (Brazilian Portuguese).

## Building

Needs the .NET SDK 8 or newer and an installed Unturned Dedicated Server. The
game's DLLs are only referenced from it, never copied.

```
dotnet build -c Release -p:U3DSDir="C:\Program Files (x86)\Steam\steamapps\common\U3DS"
```

If this repository is cloned into `U3DS\Skalia`, `U3DSDir` can be left out.
The output is `bin/Release/netstandard2.1/SkaliaDeps.dll`. Put it in the
launcher's `plugins` folder.

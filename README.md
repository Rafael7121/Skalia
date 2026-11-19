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
- The **SKALIA DEPS** plugin (`SkaliaDeps.dll`) in a `plugins` folder next to
  `SKALIA.exe`. It provides TPA, Home, Vault and the bots. Its source is in
  [`src/Skalia.Deps`](src/Skalia.Deps).

## How to use

1. Open `SKALIA.exe`. It finds the Dedicated Server through your Steam libraries.
   The first time you click PLAY it also downloads and installs
   [OpenMod](https://openmod.github.io/openmod-docs/) into the Dedicated Server
   if it is not there yet.
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

## Languages

English by default. Brazilian Portuguese can be picked in the sidebar.

## License

MIT, see [LICENSE](LICENSE).

SKALIA is a fan project. It is not affiliated with or endorsed by Smartly
Dressed Games. Unturned is a trademark of Smartly Dressed Games.

## SKALIA DEPS commands

Typed in the game chat. Commands follow OpenMod permissions; the admin, bot and
development ones are meant for admins. The bot commands also work in the server
console.

### Players

| Command | What it does |
|---|---|
| `/tpa <player>` | Ask to teleport to a player |
| `/tpa a [player]` / `/tpa r [player]` | Accept / refuse a teleport request |
| `/tpa c` | Cancel your request |
| `/home [slot]` | Teleport to one of your claimed beds |
| `/homes` | List your beds and their names |
| `/renomear <bed> <new name>` | Rename one of your beds |
| `/vault` | Open your private, persistent storage |
| `/sort` | Sort your inventory |
| `/cv d` / `/cv wn` | Vote to skip the night / clear rain, storm or snow (alias `/callvote`) |
| `/kit` | Says that kits are not available (alias `/kits`) |
| `/statsui` | Show or hide the stats HUD (needs the Workshop interface pack) |

### Admin

| Command | What it does |
|---|---|
| `/admin <player\|SteamID>` | Give the Admin role (without the bot commands) |
| `/unadmin <player\|SteamID>` | Remove that Admin role |
| `/god [player] [on\|off]` | Toggle invulnerability |
| `/heal [player]` | Restore every status to 100% |
| `/clear [player]` | Empty a player's inventory |
| `/clearg` | Remove every item players dropped on the ground |
| `/clearv` | Remove every vehicle on the map |
| `/spy <player>` | Request a screenshot from a player |

### Bots

| Command | What it does |
|---|---|
| `/addbot <steamid64> <name>` | Register a new bot |
| `/callbot <name>` / `/callbots` | Bring one bot / every bot into the server |
| `/playbot <name>` / `/playbots` | Let one bot / every bot play |
| `/stopbot <name>` / `/stopbots` | Put one bot / every bot on standby |
| `/kickbots` | Remove every bot from the server |
| `/chat <bot> <message>` | Make a bot say something in chat |

### Development

Tools used to record players and tune the bots.

| Command | What it does |
|---|---|
| `/gravar` | Show the player recorder status |
| `/gravar ligar` / `/gravar parar` | Start / stop recording players |
| `/gravar bots ligar` / `/gravar bots parar` | Also record / stop recording the bots |
| `/gravar marca <note>` | Write a note into your own recording |
| `/navbot true [bot]` / `/navbot false` | Follow a bot's navigation on screen |
| `/navid` | Show the navmesh and spawn region where you stand |
| `/irpara <x> <y> <z>` | Teleport to coordinates from the bot logs |
| `/passagem a` / `/passagem b` | Mark the two ends of a passage for the bots |
| `/passagem lista` / `/passagem remover <n>` / `/passagem cancelar` | List, remove or cancel passages |
| `/malhanova [on\|off]` | Switch bot routes to the new navigation mesh |
| `/malhateste [half side in m]` | Build a test mesh around you and compare it with the zombies' mesh |
| `/zumbis limpar [radius]` | Clear zombies around you |
| `/zumbis pausar [radius]` | Keep clearing zombies around you (toggle) |

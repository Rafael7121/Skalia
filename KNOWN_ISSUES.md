# Known issues

What is known not to work, not yet tested, or not built. Kept up to date with
each bot version (`InformationalVersion` in `src/Skalia.Deps/Skalia.Deps.csproj`).

Last update: 0.1.0-y17.38.

## Built but never run in a game

The last version played on the test server is y17.19. Everything after it
builds and loads, but no one has played against it yet:

| Version | What changed |
|---|---|
| y17.20 | The aim follows a moving target with the players' delay |
| y17.21 | Regrabbing a ladder at its foot; cover checks made consistent (the whole chest, not one ray) |
| y17.22 | Getting stuck again and again gives up the trip or the loot point |
| y17.23-y17.24 | "Full loot" is the top gear of the loaded map, read from its loot tables; arena maps take any gun; drum magazines are picked up when found |
| y17.25 | Radiation zones entered protected (gas mask, filters, suit) and left in time |
| y17.26 | The radiation suit is carried and worn only where the zone needs it; doors that need a key open only with the key, and a missing key becomes a goal |
| y17.27 | Movement measured against players: searching an area sprints outdoors, sprint and walk lengths, looks to the side while sprinting, hops in fight sprints |
| y17.28 | A player lost from sight up high and standing still is flanked by a route he does not see |
| y17.29 | y17.27's sprint and walk lengths timed by the recording's own clock |
| y17.30 | The bots' Information and Debug log lines only with `serverBot:botLogs: true` |
| y17.31 | `tpa:enabled`, `home:enabled` (/home, /homes, /renomear), `vault:enabled` and `automaticReload:enabled`, all true when missing; a disabled command answers that it is off on this server |
| y17.32 | `administration:hidden_action_complete` reads "Concluído" (its text was broken by a wrong encoding) |
| y17.33 | The new navmesh starts with the map; `skalia.ready` and the automatic bot call wait for it, `skalia.loading` holds the share done for the launcher; a failed, off or missing mesh does not hold the server |
| y17.34 | `serverBot:modifiers`: difficulty multipliers of the recorded bases for vision, field of view, hearing, reaction, aim speed, accuracy, courage and cover; 1 is the recorded behaviour |
| y17.35 | The modifiers take any decimal within the launcher's sliders (0.25-1.75, fieldOfView 0.5-1.5), clamped outside them; one that is not a number counts as 1 |
| y17.36 | 13 more modifiers: shotOrigin, openFireDelay (no longer under reactionTime), burstLength, burstPause, strafing, repositioning, sprinting, persistence, chase, healSpeed, hordeCourage, meleeHeadshots, swingSpeed |
| y17.37 | 11 more modifiers: footsteps, shotAwareness, lookAround, hipFire, zombieAimSpeed, fightHops, hops, staminaReserve, gunSilence, coveredLoot, hordeEscape |
| y17.38 | `automaticMessages` off by default, with neutral messages (the old ones were another server's map tips) |

Play a session with the recorder on (`/gravar ligar`, `/gravar bots ligar`)
before adding more on top.

## Reported, fix not confirmed

- **Bots moving oddly** ("movimentação bugando"): reported in a test, not
  pinned down to one case.
- **A bot stuck inside a house** (House_08 on Washington): not investigated.
- **A bot stuck on the way** (ToxicSniper case): y17.22 is meant to fix it;
  not tested.
- **Reaction to a heard shot and the search for cover**: changed up to y17.19
  from the testers' reports; only 17 recorded cases after y17.11, too few to say
  whether it now matches the players.

## Rules resting on few recorded cases

These behaviours copy the players, but from too few cases to be sure of the
numbers. More recordings of real fights (everyone playing to kill, recorder on)
are the best way to improve them:

| Situation | Cases |
|---|---|
| Reaction to a shot heard from someone unseen | 17 (after y17.11) |
| Where a player goes after a fight, when above the other one and still | 18 |
| Low health in the middle of a fight | 15 |
| Hit from off the screen: found the shooter or not | 7 to 17 |
| The aim's first move onto a man | 24 |

## Only tested on Washington

The loot top gear, radiation zones and key doors read everything from the map
that is loaded, so they are meant to work on any map, but no other map has been
played. Arena mode has not been played either.

## Not built

- Doors sealed with rubble (opened with the Rescue Saw or explosives).
- Keys that only an NPC or a quest gives.
- Going after the one top-gear piece still missing, by the loot tables that
  give it.
- The Mega Zombie's drop as a source of top gear.
- Driving vehicles.
- Building or raiding bases.

# Bot difficulty modifiers

`serverBot:modifiers` in the plugin's `config.yaml`. The launcher writes these
keys from its difficulty profiles (Default, Hard, Tryhard) and its sliders.
The code is in `src/Skalia.Deps/src/Bots/SkaliaBotModifiers.cs`.

## How they work

Each modifier multiplies or divides a number the bots already use. Nearly all
of those numbers were measured on recordings of real players (see
[CONTRIBUTING.md](../CONTRIBUTING.md#recordings)). A few come from the game
itself or from the tester's own description. A modifier never adds a new
behaviour.

- **1, or a missing key, leaves the bots exactly as recorded.** Every use
  multiplies or divides by exactly 1, and a share stays the same share. The
  Default profile is all 1.
- **Range:** 0.25 to 1.75, except `fieldOfView`, which is 0.5 to 1.5. Any
  decimal with a dot is accepted. A value outside the range is clamped, with a
  warning in the log. A value that is not a number counts as 1, also with a
  warning.
- **Direction:** bigger means more of the thing named. The exceptions are
  `reactionTime` and `openFireDelay`, where smaller is quicker.
- **Shares** (a percentage of the time or of the cases) never go past 100%.
- **When:** read once, when the plugin loads. The values are logged at load.

## Perception

| Key | What it scales | Base |
|---|---|---|
| `visionRange` | How far a man is seen, and the zombie sight radius. Above 1, only the 250 m cap grows: a man is never engaged past the gun's range. | 250 m (players opened fire at 6 to 261 m); zombies 50 m |
| `fieldOfView` | Half the width of the screen the bot "sees". | 60° (the game's default view, 121° across at 16:9) |
| `hearing` | How far a shot is heard (never past the gun's own sound), and how close a shot counts as aimed at the bot. | 100 m; 20 m |
| `footsteps` | How far a man's steps are heard, scaled by his volume as the game does. | 32 m (the game's footstep range) |
| `shotOrigin` | Error turning toward a shot from off the screen (divided), and the share that found the shooter (multiplied). | 25/35/47/85/127°; 4 of 7 |
| `shotAwareness` | Share that turned toward a shot heard off the screen, and how long they looked that way. | 64%; 0.1 to 14.7 s |
| `lookAround` | How often it glances to the side on the way. | 4.8 a minute sprinting, 15.6 walking |

## Aim and shooting

| Key | What it scales | Base |
|---|---|---|
| `reactionTime` | Reaction to a hit, the delay before the hand moves, and the turn to a heard shot. Smaller is quicker. | 0.8 to 2.1 s (hit seen), 0.18 to 1.15 s (hand) |
| `openFireDelay` | Time from seeing a man to the first round. Smaller is sooner. | 0 to 7 s (median 2.2 s; 1.2 s within 15 m) |
| `aimSpeed` | Length of the move onto a man, its corrections, and the lag following him. | 0.21 to 1.72 s; 0.08 to 1.04 s; 0 to 1.07 s |
| `accuracy` | Spread of the aim (divided). | 90% of rounds on the body out to 100 m (the tester's figure) |
| `burstLength` | How long the trigger is held. | 0.08 to 1.1 s |
| `burstPause` | Time between trigger pulls. | 0.12 to 1.5 s |
| `hipFire` | Share fired from the hip up close. | 29% at a man within 15 m; 25% at a zombie within 6 m |

## Movement

| Key | What it scales | Base |
|---|---|---|
| `strafing` | Share of a fight spent stepping A/D or backing off, by range. | 25 to 78% |
| `sprinting` | Sprint runs (multiplied) and the walks between them (divided). | runs 0.2 to 34 s (median 1.7 s); walks 0.1 to 5.9 s |
| `fightHops` | Share of the fight's sprints with hops. | 43% |
| `hops` | How often it hops on the way. | one every 4 to 10 s |
| `staminaReserve` | Stamina it waits for before sprinting again after running out. | 50 (players' median 54) |

## Hunting

| Key | What it scales | Base |
|---|---|---|
| `repositioning` | Rounds fired from one spot before moving (divided). | median 9 (4 to 21) |
| `persistence` | How long a fight is remembered, and how long it watches where the man was. | 5 min; 30/45/60 s |
| `chase` | Share that went straight to where the man was last seen. | 2 of 12 (at most the 7 of 12 that moved) |
| `gunSilence` | How long the gun stays quiet after a shot from someone unseen. | 40 to 120 s |

## Survival

| Key | What it scales | Base |
|---|---|---|
| `courage` | Health at which it runs or heals (divided). | 40 (65 bleeding); 35 in a fight |
| `healSpeed` | Wait before the bandage at low health in a fight (divided). | 0 to 1.6 s |
| `cover` | Share crouched or prone while shooting, and how far it looks for cover. | crouched 8 to 31%, prone 0 to 32%, by range; 10 m watching, 6 m in a fight |

## Zombies

| Key | What it scales | Base |
|---|---|---|
| `hordeCourage` | How many zombies it fights instead of running (multiplied), and the health needed for it (divided). | 4 (5 with a stunning blade), 6 for a big group; 50 health |
| `hordeEscape` | Distance of the nearest zombie at which it runs from a big group. | 3.6 m (players' p75) |
| `meleeHeadshots` | Share of blade blows aimed at the head. | 96% |
| `swingSpeed` | Minimum time between blows (divided). Never shorter than the swing's own animation. | 0.44 s (players' p25) |
| `zombieAimSpeed` | Time in the sights before the first round at a zombie (divided). | 0.3 to 0.8 s (0.25 to 0.5 s up close) |

## Loot

| Key | What it scales | Base |
|---|---|---|
| `coveredLoot` | How long it only loots under a roof after a fight. | 60 s (players: 47% under a roof after a fight, 30% otherwise) |

## Keys with no effect

These are read and logged, but no recorded number in the bots can be scaled
for them. A value other than 1 gives a warning in the log.

| Key | Why |
|---|---|
| `fightDistance` | The bot fights a man wherever its gun reaches him. There is no recorded preferred distance. |
| `looting` | A man seen always comes before loot. There is no loot weight to scale. |
| `nightVision` | Night does not limit the bot's sight. The players' longer fights at night (median 118 m against 57 m) are opening distances, not sight. |
| `headshots` | Against players the bot aims at the chest, where the players' rounds went. Against zombies it already always aims at the head. |
| `recoilControl` | The recoil pulled back between rounds (0.04 s) is not a recorded number, and only 8% is left after 0.1 s. |
| `flanking` | A rule (the man was more than 4 m above and standing still), not a recorded share. |
| `fleeWhenHurt` | After the bandage the bot runs only when there is no cover within 12 m. There is no share to scale. |

## Adding a modifier

Only for a number that comes from the recordings, the game or the tester's
own description. Add the property and its `Read` call in
`SkaliaBotModifiers.cs`, use it where the number is used so that 1 changes
nothing, add the key to `config.yaml`, and add a row here.

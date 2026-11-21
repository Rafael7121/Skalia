# Reading the bot log

The bots write to the OpenMod log of the server,
`Servers/<server>/OpenMod/logs/openmod-<date>.log`. A bot line looks like:

```
[2026-11-18 22:29:44 INF][SkaliaDeps.SkaliaBotAuthenticationService] [Vultox] PvP: perdi TheGhostSniper; fico olhando para onde ele estava a 84m por 54s.
```

Only with `botLogs: true` under `serverBot:` in the plugin's `config.yaml`
(the launcher's Developer > Bot logs); off, which is the default, the bots
write only warnings and errors.

The time, the level, the bot's name in brackets, then the message (in
Portuguese, the language of the test server). Positions are `(x, y, z)` with a
comma as the decimal mark; `/irpara <x> <y> <z>` takes you there.

To follow one bot:

```
findstr /C:"[Vultox]" openmod-2026-11-18-22-24-48.log
```

The version that was loaded is printed when the plugin starts; search for
`0.1.0-y`.

The lines below are from tests, with the human players' names replaced by
`Jogador`.

## Every 30 seconds

```
[Vultox] Estado: "Explore" ha 0s; area Seattle; posicao (-311,6, 38,8, 15,1); destino (-304,4, 38,5, -115,9) (131.2m); vida 92 comida 73 agua 70 ...
[Vultox] Ultimos 30s: ataques 0, abates 0, coletas 0, loots rejeitados 0, golpes recebidos 0 (0 de dano), rotas falhas 0, recuperacoes 0, trocas de objetivo 1. ...
```

What it is doing (`Estado`), where, where to, and its health; then what
happened in the last 30s. Many `rotas falhas` or `recuperacoes` mean it is
having trouble moving.

## What it decides to do

```
[blazex] Objetivo "Patrol" -> "Loot" (rotina): coletar Cobra #99 (equipamento de combate); destino (217,4, 39,8, -428,8); vida 19; stamina 100; ...
```

Every change of objective, with the reason. The objectives are `Loot`,
`Patrol` (searching an area), `Travel`, `Explore`, `Combat` (zombies), `Flee`
(also used for the moves of a fight against a player), `EscapeSwarm`,
`EscapeHazard`, `ExitWater`, `Ladder`, `OpenDoor`, `Unstuck`.

## Fights against players (`PvP:`)

```
[Vultox] PvP: vi o jogador Jogador a 78m; virando e mirando, primeiro tiro em 0.3s (arma na mao Bandage).
[LilJuninho13] PvP: luta com AlemaoGamer acabou (sem bala, deixei ele para tras): 3 apertos de gatilho, 0 acertos, 0 de dano recebido; vida 100.
[Vultox] PvP: perdi TheGhostSniper; fico olhando para onde ele estava a 84m por 54s.
[Vultox] Matei TheGhostSniper a 30m em (-251,3, 38,8, -25,5); indo ver o que ele deixou antes de seguir.
```

Seen, the end of the fight with its numbers, lost from sight and what it does
next (watch, go straight, go around, flank from y17.28: `Dando a volta para o
flanco`), killed.

## Shots heard

```
[TheGhostSniper] Ouvi tiro de Jogador a 81m fora da tela; olhando para o setor 132 graus +-33, medio em 1.4s por 1.3s.
[TheGhostSniper] PvP: tiro passou por mim a 36m de quem nao vejo; setor 321 graus +-47, medio; saindo da linha.
[Vultox] PvP: ouvi tiro a 60m de quem nao vejo, e daqui um homem daquele lado me ve de 11 de 15 pontos; setor 256 graus +-47, medio; saindo da linha.
```

A shot out of view turns the head to the side it believes (`setor`, with an
error like the players'); a round passing close, or a shot from a side that
can see the bot, sends it to cover.

## Moving and getting stuck

```
[LilJuninho13] Bot parado ha 20s em (74,4, 26,2, -117,3) com objetivo "Travel"; iniciando manobra de recuperacao.
[Vultox] Travado numa rota da malha nova em (-318,7, 38,8, 91,8) rumo a (-317,3, 38,8, 92,1): nada no caminho na altura do corpo (degrau ou chao); pisando em 'Office_3/Nav' ...
```

Stuck reports name the position and what is under or in front of the bot; use
`/irpara` to see the spot and `/navid` to see the navigation mesh there.

## Once per map load (y17.24 and later)

- `Topo deste mapa (tabelas de loot): ...` the gear a bot needs to count as
  fully equipped on this map; the same line ends with the radiation zones (and
  the protection each one needs) and the doors that need a key.
- `Portas com chave em <map>: ...` how many doors need a key, and the keys.

## Radiation and keys (y17.25 and later)

Lines start with `Radiacao:` or `Chaves:`, for example
`Chaves: <door> em <position> esta trancada; precisa de <key>.`

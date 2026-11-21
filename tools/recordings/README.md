# Recording analysis

Scripts that compare the bots with real players in the recordings the plugin
writes (`Servers/<server>/SkaliaRecordings`, see CONTRIBUTING.md). They are the
measurements behind the movement and flank changes of y17.27-y17.29; run them
on new recordings to check a bot version or to measure something new.

Needs Python 3.8 or newer, no packages. Each script takes one or more
recording folders:

```
python movement.py "C:\...\U3DS\Servers\Skalia\SkaliaRecordings"
```

The first run reads every CSV and leaves `skrec-cache.pkl` in the folder (when
it can write there); later runs read the cache until a file changes. Files with
`_BOT_` in the name count as bots, the rest as players, so pass only folders
whose bots are this plugin's.

| Script | What it prints |
|---|---|
| `movement.py` | Outdoors with nothing after them: sprint, walk, still and low shares of the time moving; how long the sprints and the walks between them last; looks more than 60 degrees off the way; jumps a minute |
| `fights.py` | In a fight (5s before to 2s after a round at another recorded person or a hit taken): postures, A/D, and the hops of sprints of 2s or more |
| `after_fight.py` | Where a player is 10, 20 and 30s after the last round of a fight, split by being more than 4m above the other one and by moving or still |

`skrec.py` holds the loader and the helpers (nearest row at a time, quiet
moments, fight moments, aim error) for new scripts.

Notes:

- Rows come every 0.08-0.1s; time a stretch by the rows' `t`, not by counting
  rows.
- The recorder's clock jitters: while sprinting at 7 m/s a row now and then
  reads 12-16 m/s. Only treat a jump past about 25 m/s as a teleport.
- Directions taken over two rows at walking speed are noisy; the look counts of
  `movement.py` are for comparing bots and players with the same method, not
  absolute numbers.
- A fight needs both people recorded: rounds at someone who was not recorded
  are not seen.

"""Where a player is 10, 20 and 30s after the last round of a fight.

    python after_fight.py <SkaliaRecordings folder> [more folders]

A fight ends after 15s with no round at or from another recorded person.
Split by the player being more than 4m above the nearest other one, and by
moving (over 2 m/s) or still at that moment. Deaths and respawns are left out.
"""
import collections
import math
import sys

import skrec


def path_ok(rec, t0, t1):
    prev = None
    for r in rec.between(t0, t1):
        if r['vida'] <= 0 or r['post'] == 'DRIVING':
            return False
        if prev and math.hypot(r['x'] - prev['x'], r['z'] - prev['z']) > 8:
            return False
        prev = r
    return True


def main(folders):
    recs = skrec.load(folders)
    moments = skrec.pvp_moments(recs)
    out = collections.defaultdict(list)
    for rec in recs:
        if rec.bot:
            continue
        W = moments[rec.key]
        ends = [W[i] for i in range(len(W)) if i == len(W) - 1 or W[i + 1] - W[i] >= 15]
        for t in ends:
            r0, rb = rec.at(t), rec.at(t - 2)
            if not r0 or not rb:
                continue
            nearest, best = None, 1e9
            for o in recs:
                if o is rec or o.key[0] != rec.key[0] or o.name == rec.name:
                    continue
                ro = o.at(t, 0.5)
                if ro:
                    d = math.hypot(ro['x'] - r0['x'], ro['z'] - r0['z'])
                    if d < best:
                        nearest, best = ro, d
            if not nearest or best > 200:
                continue
            high = 'alto' if r0['y'] - nearest['y'] > 4 else 'nivel'
            moving = 'movendo' if skrec.speed(rb, r0) > 2 else 'parado'
            for dt in (10, 20, 30):
                r1 = rec.at(t + dt)
                if r1 and path_ok(rec, t, t + dt):
                    out[(high, moving, dt)].append(math.hypot(r1['x'] - r0['x'], r1['z'] - r0['z']))
    for key in sorted(out):
        L = out[key]
        print('%-5s %-7s +%2ds  n=%3d  deslocamento p10/25/50/75/90 %s m  ficou a menos de 5m %2.0f%%' % (
            key[0], key[1], key[2], len(L), skrec.quantiles(L), 100 * sum(d < 5 for d in L) / len(L)))


if __name__ == '__main__':
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    main(sys.argv[1:])

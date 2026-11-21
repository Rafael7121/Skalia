"""How bots and players move in a fight.

    python fights.py <SkaliaRecordings folder> [more folders]

A fight window is 5s before to 2s after a round at another recorded person
or a hit taken from a gun. Postures, A/D, and the hops of the sprints of 2s
or more (share of sprints with a hop, the first hop, the time between hops).
"""
import bisect
import collections
import sys

import skrec


def main(folders):
    recs = skrec.load(folders)
    moments = skrec.pvp_moments(recs)
    share = collections.defaultdict(collections.Counter)
    hops = collections.defaultdict(lambda: dict(runs=0, hopping=0, first=[], gaps=[]))
    for rec in recs:
        W = moments[rec.key]
        if not W:
            continue
        group = 'bots' if rec.bot else 'jogadores'

        def in_fight(t):
            j = bisect.bisect_left(W, t - 5)
            return j < len(W) and W[j] <= t + 2
        run = None
        for p, r in zip(rec.rows, rec.rows[1:]):
            dt = r['t'] - p['t']
            fight = in_fight(r['t']) and 0.05 < dt <= 0.3 and r['vida'] > 0 and r['post'] not in ('DRIVING', 'SWIM', 'CLIMB', 'SITTING')
            v = skrec.speed(p, r) if fight else 0
            if fight and v <= 25:
                c = share[group]
                c['n'] += 1
                c['parado' if v < 0.3 else r['post']] += 1
                c['AD'] += abs(r['la']) > 0.5 and v >= 0.3
            sprinting = fight and v > 2 and (r['post'] == 'SPRINT' or (run and r['jump']))
            if sprinting:
                if not run:
                    run = dict(t0=r['t'], t1=r['t'], js=[])
                run['t1'] = r['t']
                if r['jump'] and not p['jump']:
                    run['js'].append(r['t'])
            elif run and r['t'] - run['t1'] > 0.4:
                if run['t1'] - run['t0'] >= 2:
                    H = hops[group]
                    H['runs'] += 1
                    if run['js']:
                        H['hopping'] += 1
                        H['first'].append(run['js'][0] - run['t0'])
                        H['gaps'] += [b - a for a, b in zip(run['js'], run['js'][1:])]
                run = None
    for group in ('jogadores', 'bots'):
        c = share[group]
        n = c['n'] or 1
        print('== %s, %.1f min em luta' % (group, n / 600))
        print('  ' + '  '.join('%s %2.0f%%' % (k, 100 * c[k] / n) for k in ('parado', 'SPRINT', 'STAND', 'CROUCH', 'PRONE')) + '  A/D %2.0f%%' % (100 * c['AD'] / n))
        H = hops[group]
        print('  corridas de 2s ou mais: %d, com pulo %2.0f%%; 1o pulo %s s; entre pulos %s s' % (
            H['runs'], 100 * H['hopping'] / (H['runs'] or 1), skrec.quantiles(H['first']), skrec.quantiles(H['gaps'])))


if __name__ == '__main__':
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    main(sys.argv[1:])

"""Bots against players moving outdoors with nothing after them.

    python movement.py <SkaliaRecordings folder> [more folders]

Per group (bots, players): sprint / walk / still / low shares of the time
moving, the length of the sprints and of the walks between them, the looks
more than 60 degrees off the way, and jumps a minute.
"""
import collections
import math
import re
import sys

import skrec

ROW = 0.08  # seconds a row stands for while moving


def main(folders):
    recs = skrec.load(folders)
    runs = collections.defaultdict(lambda: collections.defaultdict(list))
    looks = collections.defaultdict(lambda: collections.Counter())
    for rec in recs:
        group = 'bots' if rec.bot else 'jogadores'
        calm = skrec.calm_test(rec)
        labels = []
        for p, r in zip(rec.rows, rec.rows[1:]):
            dt = r['t'] - p['t']
            ok = (0.05 < dt <= 0.3 and r['vida'] > 0 and r['z15'] == 0
                  and r['post'] not in ('DRIVING', 'SWIM', 'CLIMB', 'SITTING') and calm(r['t']))
            v = skrec.speed(p, r) if ok else 0
            # the recorder's clock jitters (12-16 m/s for a row now and then while
            # sprinting at 7); only a jump past 25 m/s is a teleport
            if not ok or v > 25:
                labels.append(('X', False, r['t']))
            elif v < 0.3:
                labels.append(('.', r['teto'] == '0', r['t']))
            elif r['post'] == 'SPRINT':
                labels.append(('S', r['teto'] == '0', r['t']))
            elif r['post'] == 'STAND':
                labels.append(('w', r['teto'] == '0', r['t']))
            else:
                labels.append(('c', r['teto'] == '0', r['t']))
        s = ''.join(x[0] for x in labels)
        # a walk of up to 0.3s inside a sprint is the same sprint; a run
        # counts as outdoors by its first row (a tree over the head for a
        # moment does not cut it)
        s = re.sub(r'(?<=S)w{1,3}(?=S)', lambda m: 'S' * len(m.group()), s)
        for m in re.finditer(r'S+|w+|\.+|c+', s):
            if labels[m.start()][1]:
                runs[group][m.group()[0]].append(labels[m.end() - 1][2] - labels[m.start()][2] + ROW)
        # looks: the way over two rows; an episode starts when the view goes
        # past 60 degrees off it
        looking = False
        for pp, r in zip(rec.rows, rec.rows[2:]):
            dt = r['t'] - pp['t']
            ok = (0.1 < dt <= 0.5 and r['vida'] > 0 and r['z15'] == 0 and r['teto'] == '0'
                  and r['post'] in ('SPRINT', 'STAND') and calm(r['t']))
            if not ok or skrec.speed(pp, r) < 1:
                looking = False
                continue
            kind = 'S' if r['post'] == 'SPRINT' else 'w'
            looks[group]['rows' + kind] += 1
            way = math.degrees(math.atan2(r['x'] - pp['x'], r['z'] - pp['z']))
            off = abs(skrec.ang(r['yaw'] - way)) > 60
            if off and not looking:
                looks[group]['look' + kind] += 1
            looking = off
        for p, r in zip(rec.rows, rec.rows[1:]):
            if r['jump'] and not p['jump'] and r['teto'] == '0' and skrec.speed(p, r) > 1 and calm(r['t']) and r['z15'] == 0:
                looks[group]['jump'] += 1
    for group in ('jogadores', 'bots'):
        R = runs[group]
        total = sum(sum(R[c]) for c in 'Sw.c') or 1
        print('== %s, %.0f min ao ar livre, calmo' % (group, total / 60))
        for c, label in (('S', 'correndo'), ('w', 'andando'), ('.', 'parado'), ('c', 'agachado/deitado')):
            print('  %-17s %3.0f%%  trechos p10/25/50/75/90 %s s' % (label, 100 * sum(R[c]) / total, skrec.quantiles(R[c])))
        L = looks[group]
        for kind, label in (('S', 'correndo'), ('w', 'andando')):
            minutes = L['rows' + kind] / 750 or 1
            print('  olhadas >60 graus fora do rumo %s: %.1f/min' % (label, L['look' + kind] / minutes))
        print('  pulos por minuto andando: %.1f' % (L['jump'] / ((L['rowsS'] + L['rowsw']) / 750 or 1)))


if __name__ == '__main__':
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    main(sys.argv[1:])

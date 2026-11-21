"""Loader and helpers for the SKALIA DEPS recordings (SkaliaRecordings/*.csv).

One CSV per player (or bot, with _BOT_ in the name) per session, a row every
0.08s while something changes. The loader keeps the columns the scripts here
use and caches them next to the folder it read (skrec-cache.pkl).

Usage from a script:
    import skrec
    R = skrec.load(sys.argv[1:])   # one or more SkaliaRecordings folders
"""
import bisect
import csv
import datetime
import glob
import math
import os
import pickle
import re

NAME = re.compile(r'(\d{4}-\d\d-\d\d_\d\d-\d\d-\d\d)_+(BOT_)?(.*)_\d+\.csv$')


class Rec:
    """One recording: rows (dicts) sorted by time, plus who and whether a bot."""

    def __init__(self, key, name, bot, rows):
        self.key, self.name, self.bot, self.rows = key, name, bot, rows
        self.t = [r['t'] for r in rows]

    def at(self, t, tol=0.3):
        """The row nearest to t, within tol seconds, or None."""
        i = bisect.bisect_left(self.t, t)
        best = None
        for j in (i - 1, i):
            if 0 <= j < len(self.t) and abs(self.t[j] - t) <= tol:
                if best is None or abs(self.t[j] - t) < abs(self.t[best] - t):
                    best = j
        return self.rows[best] if best is not None else None

    def between(self, t0, t1):
        i = bisect.bisect_left(self.t, t0)
        while i < len(self.rows) and self.rows[i]['t'] <= t1:
            yield self.rows[i]
            i += 1


def _row(t0, r):
    return dict(
        t=t0 + float(r['t']), x=float(r['x']), y=float(r['y']), z=float(r['z']),
        yaw=float(r['yaw']), pitch=float(r['pitch']), post=r['postura'],
        fr=float(r['frente'] or 0), la=float(r['lado'] or 0), jump=r['pulo'] == '1',
        a1=r['ataque1'] == '1', arma=r['arma'], vida=float(r['vida'] or 0),
        fol=float(r['folego'] or 0), dia=r['dia'] == '1', z15=int(r['zumbis_15m'] or 0),
        teto=r.get('teto', ''), ev=r['evento'] or '')


def load_folder(folder):
    cache = os.path.join(folder, 'skrec-cache.pkl')
    files = sorted(glob.glob(os.path.join(folder, '*.csv')))
    stamp = [(os.path.basename(f), os.path.getsize(f)) for f in files]
    if os.path.exists(cache):
        with open(cache, 'rb') as fh:
            saved = pickle.load(fh)
        if saved.get('stamp') == stamp:
            return saved['recs']
    recs = []
    for f in files:
        m = NAME.search(os.path.basename(f))
        if not m:
            continue
        t0 = datetime.datetime.strptime(m.group(1), '%Y-%m-%d_%H-%M-%S').timestamp()
        rows = []
        with open(f, encoding='utf-8', errors='replace') as fh:
            text = fh.read().replace('\0', '')
        for r in csv.DictReader(text.splitlines()):
            try:
                rows.append(_row(t0, r))
            except (ValueError, KeyError, TypeError):
                pass
        if rows:
            recs.append(Rec((folder, os.path.basename(f)), m.group(3), bool(m.group(2)), rows))
    try:
        with open(cache, 'wb') as fh:
            pickle.dump({'stamp': stamp, 'recs': recs}, fh)
    except OSError:
        pass  # a read-only folder: no cache
    return recs


def load(folders):
    out = []
    for folder in folders:
        out += load_folder(folder)
    return out


def ang(a):
    """An angle folded into -180..180."""
    return (a + 540) % 360 - 180


def speed(a, b):
    dt = b['t'] - a['t']
    return math.hypot(b['x'] - a['x'], b['z'] - a['z']) / dt if dt > 0 else 0.0


def calm_test(rec, before=15, after=5):
    """True at t when no shot, hit or zombie hit happened from t-before to t+after."""
    ev = [r['t'] for r in rec.rows if 'atirou' in r['ev'] or 'levou' in r['ev'] or 'acertou' in r['ev']]

    def calm(t):
        i = bisect.bisect_left(ev, t - before)
        return not (i < len(ev) and ev[i] <= t + after)
    return calm


def aim_error(r, o):
    """Degrees between r's aim and o's chest."""
    y = math.radians(r['yaw'])
    p = math.radians(r['pitch'] - 90)
    d = (math.sin(y) * math.cos(p), -math.sin(p), math.cos(y) * math.cos(p))
    v = (o['x'] - r['x'], o['y'] + 1.2 - (r['y'] + 1.5), o['z'] - r['z'])
    n = math.sqrt(sum(c * c for c in v)) or 1
    return math.degrees(math.acos(max(-1, min(1, sum(d[i] * v[i] for i in range(3)) / n)))), n


def pvp_moments(recs, max_meters=150, max_degrees=10):
    """Per recording, the times it fired at another recorded person (aim within
    max_degrees, within max_meters) or took a hit from a gun."""
    out = {}
    for rec in recs:
        times = []
        # Recordings before the recorder's 0.2.0 have no "atirou" event: the
        # trigger with a gun in hand stands for the shot.
        events = any('atirou' in r['ev'] for r in rec.rows)
        for r in rec.rows:
            if 'levou GUN' in r['ev']:
                times.append(r['t'])
                continue
            if not ('atirou' in r['ev'] if events else (r['a1'] and r['arma'])):
                continue
            for o in recs:
                if o is rec or o.key[0] != rec.key[0] or o.name == rec.name or not (o.t[0] <= r['t'] <= o.t[-1]):
                    continue
                orow = o.at(r['t'])
                if orow and orow['vida'] > 0:
                    e, d = aim_error(r, orow)
                    if e < max_degrees and d < max_meters:
                        times.append(r['t'])
                        break
        out[rec.key] = sorted(times)
    return out


def quantiles(values, ps=(.1, .25, .5, .75, .9)):
    v = sorted(values)
    if not v:
        return '-'
    return '/'.join('%.1f' % v[int(p * (len(v) - 1))] for p in ps)

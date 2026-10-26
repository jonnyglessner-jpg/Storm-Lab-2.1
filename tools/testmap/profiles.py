"""Vertical alignment of every road on the test map.

Each stroke (a continuous road, see roads.py) is resampled at about 4 m and given a profile:
- county gravel follows the land closely (20 m smoothing), as Kansas section roads do, with a crown of 3 %;
- paved county roads are smoother (40 m), state routes much smoother (100 m), both crowned at 2 %;
- over every bridged stream the road holds a deck height (water level + clearance) across the span, and a span
  grows to cover the whole dip where the profile rides more than 2.5 m above the ground, up to a cap;
- junctions are shared: roads are profiled in rank order (state, paved, gravel) and each one is pinned to the
  heights already set at its junctions, blending into its own profile over 60-200 m;
- state routes are superelevated 4 % through their curves and marked no-passing on curves, near bridges, near
  junctions with other state routes, and wherever a crest hides an oncoming car within 300 m.
"""
import math

import numpy as np
from scipy import ndimage
from shapely.geometry import LineString

import map_config as cfg
from roads import crossings
from terrain import sample

STEP = 4.0
SMOOTH_M = {"state": 100.0, "paved": 40.0, "gravel": 20.0}
BLEND_M = {"state": 200.0, "paved": 120.0, "gravel": 60.0}
CROWN = {"state": 0.02, "paved": 0.02, "gravel": 0.03}
RANK = {"state": 3, "paved": 2, "gravel": 1}
MAX_GRADE = {"state": 0.045, "paved": 0.07, "gravel": 0.08}
SUPER_E = 0.04
NO_PASS_SIGHT_M = 300.0
EYE = 1.08


def densify(pts, junctions, step=STEP):
    """Subdivide every segment to <= step, keeping every original vertex. Returns points and remapped junctions."""
    out, idx = [pts[0]], [0]
    for a, b in zip(pts[:-1], pts[1:]):
        n = max(1, int(math.ceil(np.linalg.norm(b - a) / step)))
        for k in range(1, n + 1):
            out.append(a + (b - a) * k / n)
        idx.append(len(out) - 1)
    return np.array(out), [(n, idx[v]) for n, v in junctions]


def arclen(p):
    return np.concatenate([[0], np.cumsum(np.linalg.norm(np.diff(p, axis=0), axis=1))])


def headings(p):
    d = np.gradient(p, axis=0)
    return np.unwrap(np.arctan2(d[:, 1], d[:, 0]))


def gsmooth(v, sigma_pts):
    return ndimage.gaussian_filter1d(v, max(sigma_pts, 0.01), mode="nearest")


def stream_level(st, q):
    p = np.asarray(st["pts"])
    j = int(np.argmin(np.linalg.norm(p - q, axis=1)))
    return float(st["level"][j])


def bridge_spans(pts, s, terrain, prof, streams):
    """Spans [s0, s1] over every bridged stream the road crosses, with the deck floor for each."""
    spans = []
    for sc, k, ang in crossings(pts, streams):
        st = streams[k]
        river = st["kind"] == "river"
        bank = 20.0 if river else 8.0
        half = max(10.0, (st["width"] / 2 + bank) / max(math.sin(math.radians(ang)), 0.5))
        q = np.array(LineString(pts).interpolate(sc).coords[0])
        deck = stream_level(st, q) + (4.5 if river else 3.0)
        spans.append([sc - half, sc + half, deck, river, k])
    return spans


def profile(stk, dem_s, streams):
    cls = stk["cls"]
    pts, junc = densify(np.asarray(stk["pts"], float), stk["junctions"])
    s = arclen(pts)
    t = sample(dem_s, pts)
    sig = SMOOTH_M[cls] / STEP
    p = gsmooth(t, sig)
    spans = bridge_spans(pts, s, t, p, streams)
    for _ in range(2):
        floor = np.full(len(s), -1e9)
        for s0, s1, deck, river, k in spans:
            floor[(s >= s0) & (s <= s1)] = np.maximum(floor[(s >= s0) & (s <= s1)], deck)
        p = gsmooth(t, sig)
        for _ in range(6):
            p = gsmooth(np.maximum(p, floor), sig * 0.5)
        p = np.maximum(p, floor)
        # grow each span over the dip it rides above
        for sp in spans:
            cap = 400.0 if sp[3] else 120.0
            mid = 0.5 * (sp[0] + sp[1])
            high = (p - t) > 2.5
            i = int(np.argmin(np.abs(s - mid)))
            lo = hi = i
            while lo > 0 and high[lo - 1] and s[i] - s[lo - 1] < cap / 2:
                lo -= 1
            while hi < len(s) - 1 and high[hi + 1] and s[hi + 1] - s[i] < cap / 2:
                hi += 1
            sp[0], sp[1] = min(sp[0], s[lo]), max(sp[1], s[hi])
    p = gsmooth(grade_limit(p, s, MAX_GRADE[cls] * 0.9), 2.0)
    p = np.maximum(p, floor)
    # merge overlapping spans
    spans.sort()
    merged = []
    for sp in spans:
        if merged and sp[0] <= merged[-1][1] + 10:
            merged[-1][1] = max(merged[-1][1], sp[1])
            merged[-1][2] = max(merged[-1][2], sp[2])
            merged[-1][3] = merged[-1][3] or sp[3]
        else:
            merged.append(list(sp))
    return dict(cls=cls, name=stk["name"], pts=pts, s=s, h=p, terrain=t, junctions=junc, spans=merged)


def grade_limit(h, s, g):
    """Hold the grade under g: fill the dips first (culvert fills over draws), then cut what still exceeds it."""
    h = h.copy()
    ds = np.diff(s)
    for k in range(1, len(h)):
        h[k] = max(h[k], h[k - 1] - g * ds[k - 1])
    for k in range(len(h) - 2, -1, -1):
        h[k] = max(h[k], h[k + 1] - g * ds[k])
    for k in range(1, len(h)):
        h[k] = min(h[k], h[k - 1] + g * ds[k - 1])
    for k in range(len(h) - 2, -1, -1):
        h[k] = min(h[k], h[k + 1] + g * ds[k])
    return h


def pin(prof, node_h):
    """Blend the profile onto the junction heights already fixed by roads of higher (or equal, earlier) rank."""
    s, h = prof["s"], prof["h"]
    B = BLEND_M[prof["cls"]]
    pins = sorted((s[v], node_h[n] - h[v]) for n, v in prof["junctions"] if n in node_h)
    if pins:
        corr = np.zeros_like(h)
        ps = np.array([q for q, _ in pins])
        for k, (sk, dk) in enumerate(pins):
            left = sk - ps[k - 1] if k > 0 else np.inf
            right = ps[k + 1] - sk if k + 1 < len(ps) else np.inf
            for lim, sel in ((min(B, left / 2), s <= sk), (min(B, right / 2), s > sk)):
                x = np.abs(s[sel] - sk) / max(lim, 1e-3)
                w = np.where(x < 1, 1 - x * x * (3 - 2 * x), 0.0)
                corr[sel] += dk * w
        prof["h"] = h + corr
    for n, v in prof["junctions"]:
        node_h.setdefault(n, float(prof["h"][v]))


def cross_section(prof):
    """Crown and superelevation: per point the crown c, the blend f toward a single slope, and that slope."""
    cls = prof["cls"]
    pts, s = prof["pts"], prof["s"]
    n = len(s)
    c = np.full(n, CROWN[cls])
    f = np.zeros(n)
    slope = np.zeros(n)
    if cls == "state" and n > 8:
        th = headings(pts)
        kappa = gsmooth(np.gradient(th) / STEP, 20.0 / STEP)
        curve = (np.abs(kappa) > 1 / 2500.0).astype(float)
        f = np.clip(gsmooth(curve, 40.0 / STEP) * 1.6, 0, 1)
        slope = -np.sign(kappa) * SUPER_E
    prof["crown"], prof["superf"], prof["superslope"] = c, f, slope


def lateral(prof, i, d):
    """Road surface height at point index i (array ok), lateral offset d (left of travel positive)."""
    return prof["h"][i] + (1 - prof["superf"][i]) * (-prof["crown"][i] * np.abs(d)) + prof["superf"][i] * prof["superslope"][i] * d


def no_passing(prof, state_nodes):
    """No-passing flags for a state route (double yellow), else None."""
    if prof["cls"] != "state":
        return None
    s, h, n = prof["s"], prof["h"], len(prof["s"])
    flag = np.zeros(n, bool)
    th = headings(prof["pts"])
    kappa = np.abs(gsmooth(np.gradient(th) / STEP, 20.0 / STEP))
    w = int(150 / STEP)
    for i in np.flatnonzero(kappa > 1 / 2500.0):
        flag[max(0, i - w):i + w] = True
    for s0, s1, *_ in prof["spans"]:
        flag[(s > s0 - 100) & (s < s1 + 100)] = True
    for nd, v in prof["junctions"]:
        if nd in state_nodes:
            flag[(np.abs(s - s[v]) < 120)] = True
    m = int(NO_PASS_SIGHT_M / STEP)
    for i in range(0, n - m):
        seg = h[i:i + m + 1]
        line = np.linspace(seg[0] + EYE, seg[-1] + EYE, m + 1)
        if (seg[1:-1] > line[1:-1]).any():
            flag[i:i + m + 1] = True
    return flag


def all_profiles(net, dem, streams):
    dem_s = ndimage.gaussian_filter(dem, 1.2)
    order = sorted(range(len(net["strokes"])), key=lambda k: -RANK[net["strokes"][k]["cls"]])
    node_h = {}
    profs = [None] * len(order)
    for k in order:
        pr = profile(net["strokes"][k], dem_s, streams)
        pin(pr, node_h)
        cross_section(pr)
        profs[k] = pr
    state_nodes = {}
    for pr in profs:
        if pr["cls"] == "state":
            for n, _ in pr["junctions"]:
                state_nodes[n] = state_nodes.get(n, 0) + 1
    state_nodes = {n for n, c in state_nodes.items() if c > 1}
    for pr in profs:
        pr["nopass"] = no_passing(pr, state_nodes)
    return profs, node_h


def grade20(p):
    s, h = p["s"], p["h"]
    j = np.searchsorted(s, s + 20.0).clip(0, len(s) - 1)
    ok = s[j] - s > 10
    return np.abs(h[j] - h)[ok] / (s[j] - s)[ok]


def summary(profs):
    lines = []
    for cls in ("state", "paved", "gravel"):
        g = [grade20(p).max() for p in profs if p["cls"] == cls and len(p["s"]) > 8]
        dev = [np.abs(p["h"] - p["terrain"]).max() for p in profs if p["cls"] == cls]
        nb = sum(len(p["spans"]) for p in profs if p["cls"] == cls)
        lines.append(f"{cls}: steepest grade {100 * max(g):.1f} %, most cut/fill {max(dev):.1f} m, bridges {nb}")
    np_frac = [p["nopass"].mean() for p in profs if p["nopass"] is not None]
    lines.append(f"state routes no-passing: {100 * np.mean(np_frac):.0f} % of their length")
    return lines

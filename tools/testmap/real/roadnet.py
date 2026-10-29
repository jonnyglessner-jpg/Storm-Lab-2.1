"""Stage 2 of a real-data map (dixie, midwest): the road network, a close recreation of the real one.

The plains map's roads are designed on the section grid. Here the network follows the USGS National Transportation
Dataset, generalised the way a game map needs it:

- What is kept: every interstate, US and state route (and the Natchez Trace Parkway) as state-class roads; the
  arterials without a route number as paved roads; named rural roads that run at least 800 m (paved when they run 6 km
  or more, else gravel), so the hills keep their ridge roads, the delta its farm roads and the Illinois grid its
  section-line roads; and the named streets of the map's one town. Private drives, the streets of the other towns
  (which land.py smoothed away) and short stubs are left out.
- Dixie: one road over the Mississippi, as in life, the Natchez-Vidalia bridge (US 84 / 65 / 425). Anything else
  that would cross the river is cut at the bank.
- Divided highways become one two-lane road: the network is drawn into a 10 m raster (highways 50 m wide, so both
  carriageways merge), thinned to its centre line and traced back into lines. Then the lines are simplified
  (Douglas-Peucker, 4 m), junctions closer than 45 m merged, skeleton whiskers, bubbles and interchange loops removed,
  forks squared up, and short dead ends pruned (longer ones stay: hill roads end at the last house). There is no
  grade separation: an interstate meets the roads it crossed over at a junction.
- Every road keeps the name and class of the real road under it (I-57, US 45, IL 33, ...); strokes, corner rounding
  and the checks are the plains map's (roads.py).

Writes build/network.json, updates the big river's bridge length in build/streams.json (dixie), and
docs/testmap/<map>/network.png. Run after land.py: python -I tools/testmap/real/roadnet.py --map dixie|midwest
"""
import json
import math
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import _paths  # noqa: F401,E402
import numpy as np
import shapely
from PIL import Image, ImageDraw
from scipy import ndimage
from shapely.geometry import LineString, Point

import map_config as cfg
import roads as R

STEP = 10.0
N = int(round(cfg.SIZE_M / STEP))
WIDTH = {"state": 50.0, "paved": 14.0, "gravel": 12.0}
RANK = {"state": 3, "paved": 2, "gravel": 1}
MIN_RURAL_M = 800.0
PAVED_RURAL_M = 6000.0
DEAD_END_KEEP_M = 1200.0
TOWN_DEAD_END_M = 100.0
DESIGN = dict(R.DESIGN, sparse_bridges={}, corner_radius={"gravel": 60.0, "paved": 80.0, "state": 250.0},
              bridge_da_km2=100.0)
ROUTE = re.compile(r"^(Interstate|I-|US Hwy|State Hwy|State Rte|Hwy|LA Hwy|IL Hwy|Natchez Trace)", re.I)
REAL = cfg.SPEC["real"]
SKIP = re.compile(r"(private|driveway|pvt|access|parking|alley|service|ramp|^NA |unnamed)", re.I)


def log(msg):
    print(msg, flush=True)


# --------------------------------------------------------------------------------------------------------- selection

def route_label(r):
    """The route numbers on an NTD segment as signed: "I-57,I-70", "US 84,US 425", "IL 33" (business routes left out)."""
    out = []
    st = {"MI": "MS", "LO": "LA"}.get(r.get("state", ""), r.get("state", ""))      # (cuts made before fetch.ABBR)
    for key, fmt in (("interstate", "I-{}"), ("us_route", "US {}"), ("state_route", st + " {}")):
        for t in str(r.get(key) or "").replace(";", ",").split(","):
            t = t.strip()
            if t and t.lower() != "bus" and any(c.isdigit() for c in t):
                out.append(fmt.format(t.lstrip("I-").strip()))
    return ",".join(dict.fromkeys(out))


def classify(r):
    """Class for one NTD segment, or None to leave it out. Rural / town is decided later."""
    name = r.get("name") or ""
    if r.get("tnmfrc") in (1, 2, 3):
        if r.get("us_route") or r.get("state_route") or r.get("interstate") or ROUTE.match(name):
            return "state"
        return "paved"
    if r.get("tnmfrc") == 4 and name and not SKIP.search(name):
        return "local"
    return None


def load_roads():
    raw = json.load(open(cfg.RAW / "tran_roads.json"))
    natchez = np.load(cfg.OUT / "town.npy")
    cov = np.load(cfg.OUT / "cover.npy")
    others = ndimage.binary_dilation(ndimage.uniform_filter((cov == 50).astype(np.float32), 25) > 0.10, iterations=10) & ~natchez
    segs = []
    for r in raw:
        cls = classify(r)
        if cls is None:
            continue
        for ln in r["lines"]:
            a = np.asarray(ln, float)
            if len(a) < 2:
                continue
            mid = a[len(a) // 2]
            i, j = int(np.clip(mid[0] / 10, 0, natchez.shape[1] - 1)), int(np.clip(mid[1] / 10, 0, natchez.shape[0] - 1))
            town = bool(natchez[j, i])
            other = bool(others[j, i])
            if cls == "paved" and other:
                continue                       # Vidalia's and Ferriday's own arterials
            if cls == "local" and other:
                continue
            segs.append(dict(name=r.get("name") or "", cls=cls, town=town, pts=a,
                             route=route_label(r)))
    # rural local roads: chain by name, keep the long ones
    by = {}
    for s in segs:
        if s["cls"] == "local" and not s["town"]:
            by.setdefault(s["name"], []).append(s)
    for name, ss in by.items():
        lines = shapely.line_merge(shapely.MultiLineString([s["pts"] for s in ss]))
        parts = list(getattr(lines, "geoms", [lines]))
        long = {k for k, p in enumerate(parts) if p.length >= MIN_RURAL_M}
        for s in ss:
            ls = LineString(s["pts"])
            k = int(np.argmin([p.distance(ls.centroid) for p in parts]))
            s["cls"] = None if k not in long else ("paved" if parts[k].length >= PAVED_RURAL_M else "gravel")
    for s in segs:
        if s["cls"] == "local":          # the town's streets
            s["cls"] = "paved"
    segs = [s for s in segs if s["cls"]]
    log(f"kept {len(segs)} road segments: " + ", ".join(f"{c} {sum(LineString(s['pts']).length for s in segs if s['cls'] == c) / 1000:.0f} km"
                                                          for c in ("state", "paved", "gravel")))
    return segs


# ------------------------------------------------------------------------------------------------------ skeleton

def draw(segs, water):
    """Roads into a 10 m raster (row 0 = south); dixie: the Mississippi is cut out except under the one bridge."""
    img = Image.new("L", (N, N), 0)
    d = ImageDraw.Draw(img)
    for cls in ("gravel", "paved", "state"):
        w = max(1, int(round(WIDTH[cls] / STEP)))
        for s in segs:
            if s["cls"] != cls:
                continue
            pts = [(x / STEP, z / STEP) for x, z in s["pts"]]
            d.line(pts, fill=1, width=w)
            for p in (pts[0], pts[-1]):
                d.ellipse([p[0] - w / 2, p[1] - w / 2, p[0] + w / 2, p[1] + w / 2], fill=1)
    m = np.asarray(img).astype(bool)
    if not REAL["big_river"]:
        return m, np.zeros_like(m)
    river = ndimage.binary_dilation(water, iterations=2)
    bridge = bridge_corridor(segs)
    # the twin spans and their approaches, within 1.5 km of the water, drawn as one 140 m band (one road)
    near = ndimage.distance_transform_edt(~water[::4, ::4]) * 4 * STEP < 1500.0
    near = np.repeat(np.repeat(near, 4, 0), 4, 1)[:N, :N]
    bridge = ndimage.binary_dilation(bridge & near, iterations=4)
    m |= bridge
    m &= ~(river & ~bridge)
    return m, bridge


def bridge_corridor(segs):
    """The big river's one bridge (dixie: Natchez-Vidalia): its route's segments, drawn 60 m wide."""
    img = Image.new("L", (N, N), 0)
    d = ImageDraw.Draw(img)
    for s in segs:
        if s["cls"] == "state" and REAL["bridge_route"] in s["route"].split(","):
            d.line([(x / STEP, z / STEP) for x, z in s["pts"]], fill=1, width=6)
    return np.asarray(img).astype(bool)


def trace(sk):
    """Skeleton pixels -> node positions (pixel units) and edges as pixel paths."""
    # nodes: ends and junctions by the crossing number (0 -> 1 steps round the 8 neighbours), so the staircase
    # corners of a thinned diagonal line are not taken for junctions
    p = np.pad(sk.astype(np.int8), 1)
    ring = [p[:-2, 1:-1], p[:-2, 2:], p[1:-1, 2:], p[2:, 2:], p[2:, 1:-1], p[2:, :-2], p[1:-1, :-2], p[:-2, :-2]]
    cn = sum(((ring[k] == 0) & (ring[(k + 1) % 8] == 1)).astype(np.int8) for k in range(8))
    nb = sum(r.astype(np.int8) for r in ring)
    nodepx = sk & ((cn != 2) | (nb >= 4))
    lab, n = ndimage.label(nodepx, structure=np.ones((3, 3)))
    cent = np.array(ndimage.center_of_mass(nodepx, lab, np.arange(1, n + 1)))
    H, W = sk.shape
    seen = np.zeros_like(sk)
    edges = []
    offs = [(-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1)]
    js, is_ = np.nonzero(nodepx)
    for j0, i0 in zip(js, is_):
        a = lab[j0, i0]
        for dj, di in offs:
            j, i = j0 + dj, i0 + di
            if not (0 <= j < H and 0 <= i < W) or not sk[j, i] or nodepx[j, i] or seen[j, i]:
                continue
            path = [(j0, i0), (j, i)]
            seen[j, i] = True
            pj, pi = j0, i0
            while True:
                nxt = None
                for ej, ei in offs:
                    jj, ii = j + ej, i + ei
                    if (jj, ii) == (pj, pi) or not (0 <= jj < H and 0 <= ii < W) or not sk[jj, ii]:
                        continue
                    if nodepx[jj, ii]:
                        if lab[jj, ii] != a or len(path) > 2:
                            nxt = (jj, ii)
                            break
                        continue
                    if not seen[jj, ii]:
                        nxt = (jj, ii)
                        break
                if nxt is None:
                    break
                path.append(nxt)
                if nodepx[nxt]:
                    break
                seen[nxt] = True
                pj, pi = j, i
                j, i = nxt
            end = path[-1]
            if nodepx[end]:
                b = lab[end]
                edges.append((a - 1, b - 1, np.array(path, float)))
    return cent, edges


def rdp(p, tol):
    keep = np.zeros(len(p), bool)
    keep[0] = keep[-1] = True
    stack = [(0, len(p) - 1)]
    while stack:
        a, b = stack.pop()
        if b <= a + 1:
            continue
        d = p[b] - p[a]
        L = np.hypot(*d)
        q = p[a + 1:b] - p[a]
        dist = np.abs(q[:, 0] * d[1] - q[:, 1] * d[0]) / L if L > 1e-6 else np.hypot(q[:, 0], q[:, 1])
        i = int(np.argmax(dist))
        if dist[i] > tol:
            keep[a + 1 + i] = True
            stack += [(a, a + 1 + i), (a + 1 + i, b)]
    return p[keep]


def chaikin(p, n=2):
    """Corner-cutting smoothing of a polyline, ends kept."""
    for _ in range(n):
        if len(p) < 3:
            return p
        q = [p[0]]
        for a, b in zip(p[:-1], p[1:]):
            q += [0.75 * a + 0.25 * b, 0.25 * a + 0.75 * b]
        q.append(p[-1])
        p = np.array(q)
    return p


def graph(segs, water):
    from skimage.morphology import skeletonize
    m, bridge = draw(segs, water)
    sk = skeletonize(m)
    cent, edges = trace(sk)
    nodes = (cent[:, ::-1] + 0.5) * STEP                       # (x, z) metres
    E = []
    for a, b, path in edges:
        pts = (path[:, ::-1] + 0.5) * STEP
        pts[0], pts[-1] = nodes[a], nodes[b]
        E.append([a, b, pts])
    log(f"skeleton: {len(nodes)} nodes, {len(E)} edges")
    return nodes, E, bridge


def clean_graph(nodes, E):
    """Merge close nodes, drop whiskers and self-loops, snap ends to the map edge, prune short dead ends, then
    join edges through every node of degree two."""
    # merge nodes joined by edges shorter than 45 m (union-find): skeleton junctions come in clusters
    parent = list(range(len(nodes)))

    def find(k):
        while parent[k] != k:
            parent[k] = parent[parent[k]]
            k = parent[k]
        return k
    for a, b, p in E:
        if a != b and LineString(p).length < 45.0:
            parent[find(a)] = find(b)
    groups = {}
    for k in range(len(nodes)):
        groups.setdefault(find(k), []).append(k)
    pos = {g: nodes[ks].mean(0) for g, ks in groups.items()}
    out = []
    for a, b, p in E:
        ga, gb = find(a), find(b)
        if ga == gb and LineString(p).length < 90.0:
            continue
        p = p.copy()
        p[0], p[-1] = pos[ga], pos[gb]
        out.append([ga, gb, p])
    E = out
    # map edge: an end within 40 m of the edge goes onto it
    for g, q in pos.items():
        for ax in (0, 1):
            if q[ax] < 40:
                q[ax] = 0.0
            elif q[ax] > cfg.SIZE_M - 40:
                q[ax] = cfg.SIZE_M
    for e in E:
        e[2][0], e[2][-1] = pos[e[0]], pos[e[1]]

    def degree():
        d = {}
        for a, b, _ in E:
            d[a] = d.get(a, 0) + 1
            d[b] = d.get(b, 0) + 1
        return d

    def on_edge(g):
        q = pos[g]
        return q[0] <= 1 or q[1] <= 1 or q[0] >= cfg.SIZE_M - 1 or q[1] >= cfg.SIZE_M - 1
    # whiskers (skeleton spurs under 80 m), repeatedly; then dead ends shorter than DEAD_END_KEEP_M (in Natchez,
    # TOWN_DEAD_END_M: its short streets and cul-de-sacs stay), once
    town = np.load(cfg.OUT / "town.npy")

    def in_town(g):
        q = pos[g]
        return bool(town[int(np.clip(q[1] / 10, 0, town.shape[0] - 1)), int(np.clip(q[0] / 10, 0, town.shape[1] - 1))])
    for lim, rounds in ((80.0, 5), (DEAD_END_KEEP_M, 1)):
        for _ in range(rounds):
            d = degree()
            keep = [e for e in E if not ((d[e[0]] == 1 and not on_edge(e[0])) or (d[e[1]] == 1 and not on_edge(e[1])))
                    or LineString(e[2]).length >= (min(lim, TOWN_DEAD_END_M) if in_town(e[0]) or in_town(e[1]) else lim)]
            if len(keep) == len(E):
                break
            E = keep
    # bubbles: two edges between the same two junctions (the carriageways of a wide median, a block drawn twice):
    # keep the shorter; self-loops: short ones go, long ones get a node at their middle
    best = {}
    for e in E:
        if e[0] == e[1]:
            continue
        key = (min(e[0], e[1]), max(e[0], e[1]))
        if key not in best or LineString(e[2]).length < LineString(best[key][2]).length:
            best[key] = e
    loops = [e for e in E if e[0] == e[1] and LineString(e[2]).length >= 400.0]
    E = list(best.values())
    for a, _, p in loops:
        g = max(pos) + 1
        mid = len(p) // 2
        pos[g] = p[mid].copy()
        E += [[a, g, p[:mid + 1]], [g, a, p[mid:]]]
    # join through degree-2 nodes
    while True:
        d = degree()
        twos = [g for g, c in d.items() if c == 2]
        done = False
        for g in twos:
            es = [k for k, e in enumerate(E) if e[0] == g or e[1] == g]
            if len(es) != 2 or es[0] == es[1]:
                continue
            e1, e2 = E[es[0]], E[es[1]]
            p1 = e1[2] if e1[1] == g else e1[2][::-1]
            o1 = e1[0] if e1[1] == g else e1[1]
            p2 = e2[2] if e2[0] == g else e2[2][::-1]
            o2 = e2[1] if e2[0] == g else e2[0]
            if o1 == g or o2 == g:
                continue
            E = [e for k, e in enumerate(E) if k not in es] + [[o1, o2, np.concatenate([p1, p2[1:]])]]
            done = True
            break
        if not done:
            break
    used = sorted({g for e in E for g in e[:2]})
    idx = {g: k for k, g in enumerate(used)}
    nodes2 = np.array([pos[g] for g in used])
    E = [[idx[a], idx[b], p] for a, b, p in E]
    return nodes2, E


def label(E, segs):
    """Class and name of the real road under each edge: the highest class covering at least 40 % of it. A real road
    counts at a sample point when it runs within 30 m (a highway: its carriageways are off the merged centre line) or
    15 m (any other road); the samples keep off the edge's ends, where the roads it meets are near."""
    lines = [LineString(s["pts"]) for s in segs]
    tree = shapely.STRtree(lines)
    out = []
    for a, b, p in E:
        ln = LineString(p)
        q = [ln.interpolate(t, normalized=True) for t in np.linspace(0.15, 0.85, 12)]
        votes = {}
        for pt in q:
            for k in tree.query(pt, predicate="dwithin", distance=30.0):
                s = segs[k]
                if s["cls"] != "state" and lines[k].distance(pt) > 15.0:
                    continue
                key = (s["cls"], s["route"] or s["name"])
                votes[key] = votes.get(key, 0) + 1
        if not votes:
            out.append(("gravel", ""))
            continue
        best = None
        for (cls, name), v in sorted(votes.items(), key=lambda kv: (-RANK[kv[0][0]], -kv[1])):
            if v >= 0.4 * len(q):
                best = (cls, name)
                break
        out.append(best or max(votes.items(), key=lambda kv: kv[1])[0])
    return out


def pretty(name):
    """A route label ("US 61,US 84") as shown: "US 61 / US 84"; a street name as it is."""
    return (name or "").replace(",", " / ")


SMOOTH_M = {"state": 25.0, "paved": 12.0, "gravel": 10.0}
MIN_R = {25.0: 60.0, 12.0: 20.0, 10.0: 18.0}      # smallest bend radius by smoothing (state, paved, gravel)
ACUTE_DEG = 35.0
ACUTE_BACK_M = 60.0


def smooth_line(p, sigma_m, step=5.0):
    """Resample to step and smooth with a gaussian along the line, the ends held where they are."""
    if len(p) < 2:
        return p
    p = R.resample(np.asarray(p, float), step)
    if len(p) < 4:
        return p
    q = ndimage.gaussian_filter1d(p, sigma_m / step, axis=0, mode="nearest")
    t = np.linspace(0, 1, len(p))[:, None]
    q += (p[0] - q[0]) * (1 - t) + (p[-1] - q[-1]) * t
    # then ease every bend tighter than MIN_R toward the chord of its neighbours, ends held
    rmin = MIN_R.get(sigma_m, 20.0)
    for _ in range(60):
        r = R.min_radius(q) if len(q) > 2 else np.inf
        if r >= rmin:
            break
        a, b, c = q[:-2], q[1:-1], q[2:]
        ab, bc, ca = (np.linalg.norm(b - a, axis=1), np.linalg.norm(c - b, axis=1), np.linalg.norm(a - c, axis=1))
        cr = np.abs((b - a)[:, 0] * (c - a)[:, 1] - (b - a)[:, 1] * (c - a)[:, 0])
        rr = ab * bc * ca / (2 * cr + 1e-9)
        tight = ndimage.binary_dilation(rr < rmin, iterations=2)
        q[1:-1][tight] = 0.5 * q[1:-1][tight] + 0.25 * (a[tight] + c[tight])
    return q


def fix_acute(nodes, edges, roads):
    """Where two legs leave a junction less than ACUTE_DEG apart (a fork the skeleton drew as a Y), the lesser leg is
    cut back ACUTE_BACK_M and joined square onto the other, which gets a new junction there."""
    nodes = [np.asarray(q, float) for q in nodes]
    fixed = 0
    for _ in range(400):
        at = R.legs_at(edges)
        done = False
        for n, legs in at.items():
            if len(legs) < 3:
                continue
            dirs = [R.leg_dir(edges[k], end, 25.0) for k, end in legs]
            pair = None
            for i in range(len(legs)):
                for j in range(i + 1, len(legs)):
                    if float(np.dot(dirs[i], dirs[j])) > math.cos(math.radians(ACUTE_DEG)) and legs[i][0] != legs[j][0]:
                        pair = (i, j)
                        break
                if pair:
                    break
            if not pair:
                continue
            (ka, ea), (kb, eb) = legs[pair[0]], legs[pair[1]]
            la, lb = (LineString(edges[k]["pts"]).length for k in (ka, kb))
            ra, rb = (RANK[roads[edges[k]["road"]]["cls"]] for k in (ka, kb))
            (km, em, Lm), (kj, ej) = ((kb, eb, lb), (ka, ea)) if (ra, la) > (rb, lb) else ((ka, ea, la), (kb, eb))
            if Lm < ACUTE_BACK_M + 30:
                continue
            pm = edges[km]["pts"] if em == 0 else edges[km]["pts"][::-1]
            sm = np.concatenate([[0], np.cumsum(np.linalg.norm(np.diff(pm, axis=0), axis=1))])
            rest = pm[np.searchsorted(sm, ACUTE_BACK_M):]
            pj = edges[kj]["pts"] if ej == 0 else edges[kj]["pts"][::-1]
            lj = LineString(pj)
            sq = lj.project(Point(rest[0]))
            if sq < 15 or sq > lj.length - 15:
                continue
            Q = np.array(lj.interpolate(sq).coords[0])
            m = len(nodes)
            nodes.append(Q)
            sj = np.concatenate([[0], np.cumsum(np.linalg.norm(np.diff(pj, axis=0), axis=1))])
            cut = int(np.searchsorted(sj, sq))
            first = np.vstack([pj[:cut], Q])
            second = np.vstack([Q, pj[cut:]])
            other_j = edges[kj]["b"] if ej == 0 else edges[kj]["a"]
            other_m = edges[km]["b"] if em == 0 else edges[km]["a"]
            new = [dict(road=edges[kj]["road"], a=n, b=m, pts=first), dict(road=edges[kj]["road"], a=m, b=other_j, pts=second),
                   dict(road=edges[km]["road"], a=m, b=other_m, pts=smooth_line(np.vstack([Q, rest]), 10.0))]
            new[2]["pts"][0], new[2]["pts"][-1] = Q, nodes[other_m]
            edges = [e for k, e in enumerate(edges) if k not in (km, kj)] + new
            fixed += 1
            done = True
            break
        if not done:
            break
    log(f"acute forks squared up: {fixed}")
    return np.array(nodes), edges


def drop_ramp_loops(E, labels, short=500.0, alt=900.0):
    """Interchange loops and ramps: a highway edge under `short` m whose ends are also joined by other highway edges
    within `alt` m goes (the map has no grade separation, so a cloverleaf would be a roundabout)."""
    import heapq
    L = [LineString(p).length for _, _, p in E]
    st = [k for k, (c, _) in enumerate(labels) if c == "state"]
    adj = {}
    for k in st:
        a, b, _ = E[k]
        adj.setdefault(a, []).append((b, k))
        adj.setdefault(b, []).append((a, k))
    gone = set()
    for k in sorted(st, key=lambda k: -L[k]):
        if L[k] >= short:
            continue
        a, b, _ = E[k]
        dist, heap = {a: 0.0}, [(0.0, a)]
        found = False
        while heap:
            d, u = heapq.heappop(heap)
            if u == b:
                found = True
                break
            if d > dist.get(u, 1e18) or d > alt:
                continue
            for v, kk in adj.get(u, []):
                if kk == k or kk in gone:
                    continue
                nd = d + L[kk]
                if nd < dist.get(v, 1e18) and nd <= alt:
                    dist[v] = nd
                    heapq.heappush(heap, (nd, v))
        if found:
            gone.add(k)
    log(f"interchange loops and ramps dropped: {len(gone)}")
    return [e for k, e in enumerate(E) if k not in gone], [l for k, l in enumerate(labels) if k not in gone]


def network(segs, water, streams):
    nodes, E, bridge = graph(segs, water)
    nodes, E = clean_graph(nodes, E)
    labels = label(E, segs)
    E, labels = drop_ramp_loops(E, labels)
    roads, edges = [], []
    rid = {}
    for (a, b, p), (cls, name) in zip(E, labels):
        p = smooth_line(rdp(p, 3.0), SMOOTH_M[cls])
        p[0], p[-1] = nodes[a], nodes[b]
        key = (cls, name)
        if key not in rid:
            rid[key] = len(roads)
            roads.append(dict(name=pretty(name) or ("Rd" if cls != "state" else "Hwy"), cls=cls))
        edges.append(dict(road=rid[key], a=a, b=b, pts=p))
    nodes, edges = fix_acute(nodes, edges, roads)
    deg = np.zeros(len(nodes), int)
    for e in edges:
        deg[e["a"]] += 1
        deg[e["b"]] += 1
    stks = R.strokes(roads, nodes, edges)
    radii = []
    for st in stks:
        radii += R.round_corners(st, deg, DESIGN)
    return nodes, stks, deg, radii


def mississippi_crossing(stks, streams, water):
    """The bridge: how far each road runs over or beside the river (to set the span); returns the crossing count."""
    k = next((i for i, s in enumerate(streams) if s.get("polygon")), None)
    if k is None:
        return 0
    river = LineString(streams[k]["pts"])
    n = 0
    for st in stks:
        ln = LineString(st["pts"])
        if not ln.intersects(river):
            continue
        n += 1
        x = ln.intersection(river)
        g = x if x.geom_type == "Point" else list(x.geoms)[0]
        s0 = ln.project(g)
        # walk out both ways until the road is off the water and the low batture: 150 m past the last wet cell
        ss = np.arange(max(0.0, s0 - 4000), min(ln.length, s0 + 4000), 10.0)
        P = np.array([ln.interpolate(s).coords[0] for s in ss])
        wet = water[np.clip((P[:, 1] / cfg.CELL).astype(int), 0, cfg.GRID - 1), np.clip((P[:, 0] / cfg.CELL).astype(int), 0, cfg.GRID - 1)]
        if wet.any():
            w = ss[wet]
            half = max(s0 - w.min(), w.max() - s0) + 150.0
            streams[k]["width"] = float(2 * half)
            streams[k]["span_cap"] = 1600.0
            log(f"Mississippi crossing on {st['name']}: river {w.max() - w.min():.0f} m wide on the road, span {2 * half:.0f} m")
    return n


def preview(stks, streams, water, path):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.colors import LightSource
    fig, ax = plt.subplots(figsize=(20, 20))
    dem = np.load(cfg.OUT / "dem.npy", mmap_mode="r")[::16, ::16]
    img = LightSource(315, 35).shade(np.asarray(dem), cmap=plt.cm.gist_earth, vert_exag=4, blend_mode="soft")
    w = water[::16, ::16] > 0
    img[w, :3] = (0.3, 0.45, 0.7)
    ax.imshow(img, extent=(0, cfg.SIZE_M, 0, cfg.SIZE_M), origin="lower")
    for st in streams:
        p = np.asarray(st["pts"])
        if R.bridged(st) and not st.get("polygon"):
            ax.plot(p[:, 0], p[:, 1], color="#1060ff", lw=1.8 if st["kind"] == "river" else 0.8)
    col = {"state": ("#d01010", 2.0), "paved": ("#202020", 1.1), "gravel": ("#f4f0e0", 0.7)}
    named = set()
    for st in stks:
        c, lw = col[st["cls"]]
        ax.plot(st["pts"][:, 0], st["pts"][:, 1], color=c, lw=lw)
        if st["cls"] == "state" and st["name"] not in named and len(st["pts"]) > 50:
            named.add(st["name"])
            p = st["pts"][len(st["pts"]) // 3]
            ax.text(p[0], p[1], st["name"], color="#a00000", fontsize=11, weight="bold")
    ax.set_xlim(0, cfg.SIZE_M)
    ax.set_ylim(0, cfg.SIZE_M)
    ax.set_title(f"{cfg.SPEC['title']}: roads from the USGS National Transportation Dataset, generalised (red interstate, "
                 "US and state routes, black paved, white gravel; blue bridged streams)")
    path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(path, dpi=60, bbox_inches="tight")


def main():
    streams = json.load(open(cfg.OUT / "streams.json"))
    for st in streams:
        st["_line"] = LineString(st["pts"])
    wk = np.load(cfg.OUT / "water.npz")["kind"]
    water10 = wk[(np.arange(N) * STEP / cfg.CELL).astype(int)][:, (np.arange(N) * STEP / cfg.CELL).astype(int)] == 1
    segs = load_roads()
    nodes, stks, deg, radii = network(segs, water10, streams)
    n = mississippi_crossing(stks, streams, wk == 1)
    if REAL["big_river"]:
        log(f"roads over the {REAL['big_river']}: {n}")
    for st in streams:
        st["_line"] = LineString(st["pts"])
    for ln in R.report(nodes, stks, deg, radii, streams):
        log(ln)
    json.dump([{k: v for k, v in s.items() if k != "_line"} for s in streams], open(cfg.OUT / "streams.json", "w"))
    preview(stks, streams, wk, cfg.PREVIEW / "network.png")
    json.dump(dict(nodes=np.round(nodes, 3).tolist(), degree=deg.tolist(),
                   strokes=[dict(cls=st["cls"], name=st["name"], pts=np.round(st["pts"], 3).tolist(),
                                 junctions=[[int(n), int(v)] for n, v in st["junctions"]]) for st in stks]),
              open(cfg.OUT / "network.json", "w"))


if __name__ == "__main__":
    main()

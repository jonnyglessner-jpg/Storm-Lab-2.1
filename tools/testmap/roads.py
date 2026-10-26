"""Stage 2 of the test map: the road network, laid out the way Kansas roads are.

- Section-line roads every mile both ways (gravel county roads, 24 ft top, crowned), the whole grid.
- Paved county roads (22 ft blacktop) on a few section lines, and a river road along each side of the Arkansas, which
  is only bridged every 4-6 miles as in life. Section roads that reach the river between bridges end at a T on the
  river road instead of at the bank.
- Two east-west and two north-south state routes (two 12 ft lanes, 6 ft paved shoulders). They hold section lines and
  move between them only on a reverse curve: two circular arcs (radius 1,200 m, design speed 65 mph and up) joined
  by a tangent, starting and ending on section corners. The section road the route leaves stops at the next corner,
  so it meets the route at a right-angle crossing, not a sliver of a fork.
- Every crossing of a stream that drains more than 40 km2 is a bridge.
- The network is checked for dead ends (none away from the map edge), junctions closer than 150 m and the angle at
  every junction, and the report says so.

Writes build/network.json and docs/testmap/network.png. Run after terrain.py: python -I tools/testmap/roads.py
"""
import json
import math
import sys
from pathlib import Path

import numpy as np
import shapely
import shapely.ops
from shapely.geometry import LineString, Point

sys.path.insert(0, str(Path(__file__).resolve().parent))
import map_config as cfg
from terrain import resample

MILE = cfg.MILE
S = cfg.SIZE_M
CURVE_R = 1200.0
NEAR_NODE = 150.0             # curved roads keep at least this far from section corners

# Cross-sections, metres: half the width of the travelled way + paved shoulder, then the gravel edge outside it.
CLASSES = {
    "state": dict(half=3.66 + 1.83, edge=0.9, rank=3),
    "paved": dict(half=3.35, edge=0.9, rank=2),
    "gravel": dict(half=3.66, edge=0.0, rank=1),
}


def line_at(i):
    return (i + 0.5) * MILE


# --------------------------------------------------------------------------------------------- state route geometry

def reverse_curve(dx, dz, R=CURVE_R):
    """Deflection theta and tangent length L of a symmetric reverse curve covering dx along and dz across."""
    lo, hi = 1e-4, math.pi / 2 - 1e-3
    for _ in range(80):
        th = 0.5 * (lo + hi)
        L = (dx - 2 * R * math.sin(th)) / math.cos(th)
        d = 2 * R * (1 - math.cos(th)) + L * math.sin(th)
        if L < 0 or d > dz:
            hi = th
        else:
            lo = th
    th = 0.5 * (lo + hi)
    return th, (dx - 2 * R * math.sin(th)) / math.cos(th)


def route_points(spec, step=5.0):
    """A state route as a dense polyline in the map frame, plus the grid-line intervals it occupies.
    spec: dict(axis='x'|'z', legs=[(line, from_mile, to_mile), ...]) - consecutive legs are joined by a reverse curve
    from the corner at the end of one leg to the corner at the start of the next."""
    ew = spec["axis"] == "x"            # runs east-west: along x, the section line fixes z
    pts, covered = [], []
    legs = spec["legs"]
    for k, (line, a, b) in enumerate(legs):
        ua = 0.0 if k == 0 else line_at(a)
        ub = S if k == len(legs) - 1 else line_at(b)
        n = max(2, int((ub - ua) / step) + 1)
        u = np.linspace(ua, ub, n)
        w = np.full(n, line_at(line))
        pts.append(np.stack([u, w], 1) if ew else np.stack([w, u], 1))
        # the section road keeps the line outside the route, from the next corner on
        lo = ua if k == 0 else line_at(a - 1)
        hi = ub if k == len(legs) - 1 else line_at(b + 1)
        covered.append(("z" if ew else "x", line, lo, hi))   # (fixed axis, line index, from, to along the line)
        if k + 1 < len(legs):
            nline, na, _ = legs[k + 1]
            dx = line_at(na) - line_at(b)
            dz = line_at(nline) - line_at(line)
            th, L = reverse_curve(dx, abs(dz))
            sgn = 1.0 if dz > 0 else -1.0
            seg = []
            # first arc: centre on the side we turn to
            c = np.array([line_at(b), line_at(line) + sgn * CURVE_R])
            for t in np.linspace(0, th, max(3, int(CURVE_R * th / step))):
                seg.append(c + np.array([CURVE_R * math.sin(t), -sgn * CURVE_R * math.cos(t)]))
            p1 = seg[-1]
            d = np.array([math.cos(th), sgn * math.sin(th)])
            p2 = p1 + d * L
            for t in np.linspace(0, 1, max(2, int(L / step)))[1:-1]:
                seg.append(p1 + d * L * t)
            c2 = np.array([line_at(na), line_at(nline) - sgn * CURVE_R])
            for t in np.linspace(th, 0, max(3, int(CURVE_R * th / step))):
                seg.append(c2 + np.array([-CURVE_R * math.sin(t), sgn * CURVE_R * math.cos(t)]))
            seg = np.array(seg)
            assert np.linalg.norm(seg[len(seg) // 2] - seg[len(seg) // 2 - 1]) < 4 * step
            assert np.allclose(p2, c2 + np.array([-CURVE_R * math.sin(th), sgn * CURVE_R * math.cos(th)]), atol=0.5)
            pts.append(seg if ew else seg[:, ::-1])
    pts = np.concatenate(pts)
    keep = np.concatenate([[True], np.linalg.norm(np.diff(pts, axis=0), axis=1) > 0.05])
    return pts[keep], covered


def min_radius(pts):
    a, b, c = pts[:-2], pts[1:-1], pts[2:]
    ab, bc, ca = (np.linalg.norm(b - a, axis=1), np.linalg.norm(c - b, axis=1), np.linalg.norm(a - c, axis=1))
    cross = np.abs((b - a)[:, 0] * (c - a)[:, 1] - (b - a)[:, 1] * (c - a)[:, 0])
    r = ab * bc * ca / (2 * cross + 1e-9)
    return float(r.min()) if len(r) else float("inf")


# ------------------------------------------------------------------------------------------------------- network

DESIGN = {
    # state routes: (name, axis, legs). Legs are (section line index, first mile, last mile); lines are at
    # (index + 0.5) miles from the west / south edge.
    "state": [
        ("K-19", "x", [(12, 0, 24), (13, 27, 49)]),
        ("K-156", "x", [(38, 0, 49)]),
        ("K-283", "z", [(10, 0, 29), (9, 32, 49)]),
        ("K-183", "z", [(36, 0, 49)]),
    ],
    "paved_x": [25],           # paved county roads on these E-W section lines
    "paved_z": [23],           # ... and N-S
    # rivers the county grid only bridges every so often (state routes always bridge); the section roads between
    # bridges stop at the last corner before the river, so the grid there steps along the valley
    "sparse_bridges": {"Arkansas River": 6.5, "Pawnee River": 3.2},     # km between county bridges
    "min_bridge_angle": 55.0,  # county bridges only where the road crosses the river this squarely or better
    "bridge_da_km2": 100.0,    # creeks draining more than this are bridged; smaller ones run through culverts
    "corner_radius": {"gravel": 150.0, "paved": 250.0, "state": 400.0},
}


def build_network(streams, design=DESIGN):
    roads = []      # dict(name, cls, pts)
    covered = []
    for name, axis, legs in design["state"]:
        pts, cov = route_points(dict(axis=axis, legs=legs))
        roads.append(dict(name=name, cls="state", pts=pts))
        covered += cov

    # the section grid, minus what the state routes took over
    for axis in ("x", "z"):
        for i in range(cfg.MAP_MILES):
            cls = "paved" if i in design["paved_x" if axis == "x" else "paved_z"] else "gravel"
            fixed = "z" if axis == "x" else "x"
            cuts = sorted((lo, hi) for f, ln, lo, hi in covered if f == fixed and ln == i)
            u0 = 0.0
            for lo, hi in cuts + [(S, S)]:
                if lo - u0 > 1.0:
                    a = np.array([[u0, line_at(i)], [lo, line_at(i)]])
                    roads.append(dict(name=f"{'Rd' if axis == 'x' else 'Ave'} {i}", cls=cls,
                                      pts=a if axis == "x" else a[:, ::-1]))
                u0 = max(u0, hi)
    return roads


def node_graph(roads):
    """Split every road at every crossing. Returns nodes (N x 2) and edges dict(road, a, b, s0, s1, pts)."""
    lines = [LineString(r["pts"]) for r in roads]
    tree = shapely.STRtree(lines)
    hits = {i: [] for i in range(len(lines))}
    pairs = tree.query(lines, predicate="intersects")
    for i, j in zip(*pairs):
        if i >= j:
            continue
        x = lines[i].intersection(lines[j])
        pts = [g for g in getattr(x, "geoms", [x]) if g.geom_type == "Point"]
        for g in pts:
            hits[i].append((g.x, g.y))
            hits[j].append((g.x, g.y))
    nodes, nid = [], {}

    def node(p):
        k = (round(p[0] / 0.5), round(p[1] / 0.5))
        for dk in ((0, 0), (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1)):
            kk = (k[0] + dk[0], k[1] + dk[1])
            if kk in nid:
                return nid[kk]
        nid[k] = len(nodes)
        nodes.append(p)
        return nid[k]

    edges = []
    for i, ln in enumerate(lines):
        cut = sorted({round(ln.project(Point(p)), 3) for p in hits[i]} | {0.0, round(ln.length, 3)})
        cut = [c for k, c in enumerate(cut) if k == 0 or c - cut[k - 1] > 0.5]
        cut[-1] = ln.length
        for s0, s1 in zip(cut[:-1], cut[1:]):
            seg = shapely.ops.substring(ln, s0, s1)
            pts = np.array(seg.coords)
            a, b = node(tuple(pts[0])), node(tuple(pts[-1]))
            edges.append(dict(road=i, a=a, b=b, s0=s0, s1=s1, pts=pts))
    return np.array(nodes), edges


def on_edge(p, tol=1.0):
    return p[0] < tol or p[1] < tol or p[0] > S - tol or p[1] > S - tol


def prune(nodes, edges):
    """Drop edges that end in a dead end away from the map edge, until none are left."""
    alive = [True] * len(edges)
    while True:
        deg = np.zeros(len(nodes), int)
        for e, ok in zip(edges, alive):
            if ok:
                deg[e["a"]] += 1
                deg[e["b"]] += 1
        dead = {n for n in range(len(nodes)) if deg[n] == 1 and not on_edge(nodes[n])}
        if not dead:
            break
        for k, e in enumerate(edges):
            if alive[k] and (e["a"] in dead or e["b"] in dead):
                alive[k] = False
    return [e for e, ok in zip(edges, alive) if ok]


def bridged(st, design=DESIGN):
    return st["kind"] == "river" or (st["kind"] == "creek" and st["da"] >= design["bridge_da_km2"])


def crossings(edge_pts, streams, design=DESIGN):
    """Where a line crosses bridged streams: list of (s along the line, stream index, crossing angle in degrees,
    90 = square)."""
    ln = LineString(edge_pts)
    out = []
    for k, st in enumerate(streams):
        if not bridged(st, design):
            continue
        sl = st["_line"]
        if not ln.intersects(sl):
            continue
        x = ln.intersection(sl)
        for g in getattr(x, "geoms", [x]):
            if g.geom_type != "Point":
                continue
            s = ln.project(g)
            t1 = tangent_at(ln, s)
            t2 = tangent_at(sl, sl.project(g))
            ang = math.degrees(math.acos(min(1.0, abs(float(np.dot(t1, t2))))))
            out.append((s, k, ang))
    return out


def tangent_at(ln, s, d=8.0):
    a = np.array(ln.interpolate(max(0.0, s - d)).coords[0])
    b = np.array(ln.interpolate(min(ln.length, s + d)).coords[0])
    v = b - a
    return v / (np.linalg.norm(v) + 1e-9)


def sparse_river_bridges(roads, edges, streams, design):
    """Edges to drop: gravel crossings of the big rivers other than one about every N km (square ones only)."""
    drop = set()
    for name, km in design["sparse_bridges"].items():
        rk = next((k for k, st in enumerate(streams) if st["name"] == name), None)
        if rk is None:
            continue
        rl = streams[rk]["_line"]
        cand = []      # (position along the river, edge, class, worst crossing angle, crossings on the edge)
        for k, e in enumerate(edges):
            cr = crossings(e["pts"], [streams[rk]], design)
            if cr:
                pos = rl.project(LineString(e["pts"]).interpolate(cr[0][0]))
                cand.append((pos, k, roads[e["road"]]["cls"], min(c[2] for c in cr), len(cr)))
        cand.sort()
        # state routes and paved county roads always get their bridge
        fixed = [pos for pos, k, c, a, n in cand if c != "gravel"]
        last = -1e9
        keep = {k for pos, k, c, a, n in cand if c != "gravel"}
        for pos, k, c, ang, n in cand:
            if c != "gravel" or ang < design["min_bridge_angle"] or n > 1:
                continue
            if all(abs(pos - q) >= km * 800 for q in fixed) and pos - last >= km * 1000:
                keep.add(k)
                last = pos
        drop |= {k for pos, k, c, a, n in cand if k not in keep}
    return drop


# ------------------------------------------------------------------------------------------------------- strokes

def legs_at(edges):
    at = {}
    for k, e in enumerate(edges):
        at.setdefault(e["a"], []).append((k, 0))
        at.setdefault(e["b"], []).append((k, 1))
    return at


def leg_dir(e, end, d=20.0):
    p = e["pts"] if end == 0 else e["pts"][::-1]
    ln = LineString(p)
    q = np.array(ln.interpolate(min(d, ln.length * 0.5)).coords[0])
    v = q - p[0]
    return v / (np.linalg.norm(v) + 1e-9)


def strokes(roads, nodes, edges):
    """Chain edges into strokes: through deg-2 nodes (same class), and through junctions along the same road when it
    goes straight on. Returns dicts of cls, name, pts and junctions = [(node, vertex index)], ends included."""
    at = legs_at(edges)
    pair = {}                      # (edge, end) -> (edge, end) continuing on the other side of the node
    for n, legs in at.items():
        if len(legs) == 2:
            (k1, e1), (k2, e2) = legs
            if roads[edges[k1]["road"]]["cls"] == roads[edges[k2]["road"]]["cls"]:
                pair[(k1, e1)] = (k2, e2)
                pair[(k2, e2)] = (k1, e1)
        elif len(legs) > 2:
            for i in range(len(legs)):
                for j in range(i + 1, len(legs)):
                    (k1, e1), (k2, e2) = legs[i], legs[j]
                    if edges[k1]["road"] == edges[k2]["road"] and \
                            np.dot(leg_dir(edges[k1], e1), leg_dir(edges[k2], e2)) < -0.86:
                        pair[(k1, e1)] = (k2, e2)
                        pair[(k2, e2)] = (k1, e1)
    seen = set()
    chains = []
    for k0 in range(len(edges)):
        if k0 in seen:
            continue
        # walk back to the start of the chain (or once round a loop)
        k, end = k0, 0
        for _ in range(100000):
            if (k, end) not in pair:
                break
            k2, e2 = pair[(k, end)]
            if k2 == k0:
                break
            k, end = k2, 1 - e2
        chain = []
        while True:
            seen.add(k)
            chain.append((k, end))      # edge k traversed from `end` to the other end
            nxt = pair.get((k, 1 - end))
            if nxt is None or nxt[0] in seen:
                break
            k, end = nxt
        chains.append(chain)
    res = []
    for chain in chains:
        pts, junc = [], []
        for k, end in chain:
            e = edges[k]
            p = e["pts"] if end == 0 else e["pts"][::-1]
            n0 = e["a"] if end == 0 else e["b"]
            junc.append((n0, 0 if not pts else len(pts) - 1))
            pts.extend(p.tolist() if not pts else p[1:].tolist())
        k, end = chain[-1]
        junc.append((edges[k]["b"] if end == 0 else edges[k]["a"], len(pts) - 1))
        names = []
        for k, _ in chain:
            nm = roads[edges[k]["road"]]["name"]
            if nm not in names:
                names.append(nm)
        res.append(dict(cls=roads[edges[chain[0][0]]["road"]]["cls"], name=" / ".join(names), pts=np.array(pts),
                        junctions=junc))
    return res


def round_corners(stk, deg, design=DESIGN):
    """Replace the corner at every deg-2 node where a stroke turns with a circular curve. Returns the radii used."""
    R0 = design["corner_radius"][stk["cls"]]
    pts = stk["pts"]
    seg = np.linalg.norm(np.diff(pts, axis=0), axis=1)
    s = np.concatenate([[0], np.cumsum(seg)])
    keep_j, cuts = [], []          # cuts: (s of the corner, tangent length, arc points, radius)
    for n, vi in stk["junctions"]:
        if deg[n] != 2 or vi == 0 or vi == len(pts) - 1:
            keep_j.append((n, vi))
            continue
        a = pts[vi] - pts[vi - 1]
        b = pts[vi + 1] - pts[vi]
        a = a / (np.linalg.norm(a) + 1e-9)
        b = b / (np.linalg.norm(b) + 1e-9)
        th = math.acos(float(np.clip(np.dot(a, b), -1, 1)))
        if th < math.radians(5):
            continue
        others = [s[v] for m, v in stk["junctions"] if v != vi]
        room = min(abs(s[vi] - o) for o in others) * 0.45
        T = min(R0 * math.tan(th / 2), room)
        R = T / math.tan(th / 2)
        P1 = pts[vi] - a * T
        side = 1.0 if a[0] * b[1] - a[1] * b[0] > 0 else -1.0
        c = P1 + side * np.array([-a[1], a[0]]) * R
        a0 = math.atan2((P1 - c)[1], (P1 - c)[0])
        arc = np.array([c + R * np.array([math.cos(t), math.sin(t)])
                        for t in np.linspace(a0, a0 + side * th, max(4, int(R * th / 5)))])
        cuts.append((s[vi], T, arc, R))
    if not cuts:
        stk["junctions"] = keep_j
        return []
    new, si = [], []
    for k in range(len(pts)):
        if not any(abs(s[k] - sc) < T for sc, T, _, _ in cuts):
            new.append(pts[k])
            si.append(s[k])
    for sc, T, arc, _ in cuts:
        for k, q in enumerate(arc):
            new.append(q)
            si.append(sc - T + 2 * T * k / (len(arc) - 1))
    order = np.argsort(si, kind="stable")
    stk["pts"] = np.array(new)[order]
    sn = np.array(si)[order]
    stk["junctions"] = [(n, int(np.argmin(np.abs(sn - s[vi])))) for n, vi in keep_j]
    return [R for _, _, _, R in cuts]


def design(streams, design_spec=DESIGN):
    for st in streams:
        st["_line"] = LineString(st["pts"])
    roads = build_network(streams, design_spec)
    nodes, edges = node_graph(roads)
    drop = sparse_river_bridges(roads, edges, streams, design_spec)
    edges = prune(nodes, [e for k, e in enumerate(edges) if k not in drop])
    deg = np.zeros(len(nodes), int)
    for e in edges:
        deg[e["a"]] += 1
        deg[e["b"]] += 1
    stks = strokes(roads, nodes, edges)
    radii = []
    for st in stks:
        radii += round_corners(st, deg, design_spec)
    return nodes, stks, deg, radii


def report(nodes, stks, deg, radii, streams):
    lines = []
    used = np.flatnonzero(deg > 0)
    dead = [n for n in used if deg[n] == 1 and not on_edge(nodes[n])]
    lines.append(f"strokes: {len(stks)}; junctions: {int((deg >= 3).sum())}; dead ends away from the map edge: "
                 f"{len(dead)}; corners rounded: {len(radii)} (radius {min(radii, default=0):.0f}-"
                 f"{max(radii, default=0):.0f} m)")
    from scipy.spatial import cKDTree
    J = nodes[deg >= 3]
    d, _ = cKDTree(J).query(J, k=2)
    lines.append(f"closest junctions {d[:, 1].min():.0f} m apart; closer than {NEAR_NODE:.0f} m: "
                 f"{int((d[:, 1] < NEAR_NODE).sum())}")
    dirs = {}
    for st in stks:
        p = st["pts"]
        for n, vi in st["junctions"]:
            if deg[n] < 3:
                continue
            for sgn in (-1, 1):
                j = vi + sgn * 4
                if 0 <= j < len(p):
                    v = p[j] - p[vi]
                    dirs.setdefault(n, []).append(math.degrees(math.atan2(v[1], v[0])) % 360)
    worst = min(float(np.diff(sorted(a) + [sorted(a)[0] + 360]).min()) for a in dirs.values() if len(a) >= 2)
    lines.append(f"smallest angle between two legs at a junction: {worst:.0f} deg")
    by = {}
    for st in stks:
        by[st["cls"]] = by.get(st["cls"], 0) + LineString(st["pts"]).length
    lines.append("road length: " + ", ".join(f"{c} {v / MILE:.0f} mi" for c, v in sorted(by.items())))
    rmin = {}
    for st in stks:
        r = min_radius(resample(st["pts"], 10.0)) if len(st["pts"]) > 2 else float("inf")
        rmin[st["cls"]] = min(rmin.get(st["cls"], float("inf")), r)
    lines.append("tightest curve: " + ", ".join(f"{c} {v:.0f} m" for c, v in sorted(rmin.items())))
    nb = {}
    for st in stks:
        for s, k, ang in crossings(st["pts"], streams):
            key = streams[k]["name"] if streams[k]["kind"] == "river" else "creek"
            nb[key] = nb.get(key, 0) + 1
    lines.append("bridges: " + ", ".join(f"{k} {v}" for k, v in sorted(nb.items())))
    return lines


def preview(stks, streams, path):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.colors import LightSource
    fig, ax = plt.subplots(figsize=(20, 20))
    dem = np.load(cfg.OUT / "dem.npy", mmap_mode="r")[::16, ::16]
    ax.imshow(LightSource(315, 35).shade(np.asarray(dem), cmap=plt.cm.gist_earth, vert_exag=6, blend_mode="soft"),
              extent=(0, S, 0, S), origin="lower")
    for st in streams:
        p = np.asarray(st["pts"])
        if bridged(st):
            ax.plot(p[:, 0], p[:, 1], color="#1060ff", lw=2.5 if st["kind"] == "river" else 1.0)
    col = {"state": ("#d01010", 2.2), "paved": ("#202020", 1.4), "gravel": ("#f0f0f0", 0.6)}
    for st in stks:
        c, w = col[st["cls"]]
        ax.plot(st["pts"][:, 0], st["pts"][:, 1], color=c, lw=w)
        if st["cls"] == "state":
            p = st["pts"][len(st["pts"]) // 7]
            ax.text(p[0], p[1], st["name"], color="#d01010", fontsize=14, weight="bold")
    ax.set_xlim(0, S)
    ax.set_ylim(0, S)
    ax.set_title("Plains test map: roads (red state routes, black paved county, white gravel; blue bridged streams)")
    fig.savefig(path, dpi=60, bbox_inches="tight")


def main():
    streams = json.load(open(cfg.OUT / "streams.json"))
    nodes, stks, deg, radii = design(streams)
    for ln in report(nodes, stks, deg, radii, streams):
        print(ln)
    cfg.PREVIEW.mkdir(parents=True, exist_ok=True)
    preview(stks, streams, cfg.PREVIEW / "network.png")
    json.dump(dict(nodes=np.round(nodes, 3).tolist(), degree=deg.tolist(),
                   strokes=[dict(cls=st["cls"], name=st["name"], pts=np.round(st["pts"], 3).tolist(),
                                 junctions=[[int(n), int(v)] for n, v in st["junctions"]]) for st in stks]),
              open(cfg.OUT / "network.json", "w"))


if __name__ == "__main__":
    main()

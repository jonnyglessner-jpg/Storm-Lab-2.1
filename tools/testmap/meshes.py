"""Road, junction, bridge and water geometry for the test map, cut into per-tile meshes.

Road surfaces are ribbons along each stroke's profile: the travelled way, shoulders and gravel edge, then a skirt
that dips 0.4 m below the ground so no seam shows against the terrain. Junctions:
- where one road is clearly the major one and goes straight through (a state route past a section road), the major
  stays continuous with its markings, and each minor road ends at the major's edge in a "mouth" with curb-return
  curves (radius 10-15 m) on both corners;
- where the roads are equals (two section roads, two state routes), every leg stops short and a pad fills the
  junction, its corners rounded the same way.
Bridges keep the road ribbon as their deck and add parapets, the deck slab and fascia, abutments and piers.

Vertices are written relative to their tile's south-west corner (x east, y up = elevation - datum, z north).
Unity's front faces are those whose (b - a) x (c - a) points at the viewer, which every writer here respects.
"""
import math
import struct

import numpy as np

import map_config as cfg
from profiles import lateral
from profiles import RANK
from roads import CLASSES

MAT = ["road_state", "road_state_nopass", "road_county", "road_gravel", "plain_asphalt", "plain_gravel", "concrete",
       "water"]
M_STATE, M_NOPASS, M_COUNTY, M_GRAVEL, M_PASPHALT, M_PGRAVEL, M_CONCRETE, M_WATER = range(8)
FILLET = {"state": 15.0, "paved": 12.0, "gravel": 10.0}
SKIRT_OUT, SKIRT_DOWN = 0.8, 0.4
CHUNK_M = 300.0
REPEAT = 12.192


def outer(cls):
    c = CLASSES[cls]
    return c["half"] + c["edge"]


class TileMeshes:
    """Geometry buckets: (tile c, r, layer) -> material -> (verts list, uvs list, tris list)."""

    def __init__(self, datum):
        self.datum = datum
        self.b = {}

    def bucket(self, x, z, layer):
        c = int(np.clip(x // cfg.TILE_M, 0, cfg.TILES - 1))
        r = int(np.clip(z // cfg.TILE_M, 0, cfg.TILES - 1))
        return (c, r, layer)

    def add(self, key, mat, V, UV, T, want=None):
        """V: (n, 3) map-frame x, height, z. T: (m, 3) indices. want: (m, 3) direction each face should point (or
        None for up). Faces are flipped to point that way."""
        V = np.asarray(V, np.float64)
        T = np.asarray(T, np.int64).reshape(-1, 3)
        if not len(T):
            return
        a, b, c = V[T[:, 0]], V[T[:, 1]], V[T[:, 2]]
        nrm = np.cross(b - a, c - a)
        w = np.array([0, 1.0, 0]) if want is None else np.asarray(want)
        flip = (nrm * w).sum(-1) < 0
        T[flip] = T[flip][:, [0, 2, 1]]
        d = self.b.setdefault(key, {}).setdefault(mat, [[], [], [], 0])
        c0, r0 = key[0], key[1]
        L = V.copy()
        L[:, 0] -= c0 * cfg.TILE_M
        L[:, 2] -= r0 * cfg.TILE_M
        L[:, 1] -= self.datum
        d[0].append(L.astype(np.float32))
        d[1].append(np.asarray(UV, np.float32))
        d[2].append((T + d[3]).astype(np.int32))
        d[3] += len(V)

    def write(self, outdir):
        outdir.mkdir(parents=True, exist_ok=True)
        stats = {}
        for (c, r, layer), mats in self.b.items():
            with open(outdir / f"{layer}_{c}_{r}.bin", "wb") as f:
                f.write(struct.pack("<i", len(mats)))
                for m, (vs, uvs, ts, n) in sorted(mats.items()):
                    V = np.concatenate(vs)
                    UV = np.concatenate(uvs)
                    T = np.concatenate(ts)
                    f.write(struct.pack("<iii", m, len(V), T.size))
                    f.write(V.tobytes())
                    f.write(UV.tobytes())
                    f.write(T.astype(np.int32).tobytes())
                    stats[layer] = stats.get(layer, 0) + len(V)
        return stats


# ---------------------------------------------------------------------------------------------------------- frames

def frames(pts):
    d = np.gradient(pts, axis=0)
    t = d / (np.linalg.norm(d, axis=1, keepdims=True) + 1e-12)
    return t, np.stack([-t[:, 1], t[:, 0]], 1)


def interp_point(prof, sq):
    """Position, tangent, normal and profile index (fractional) at arc length sq."""
    s = prof["s"]
    i = int(np.clip(np.searchsorted(s, sq) - 1, 0, len(s) - 2))
    f = (sq - s[i]) / max(s[i + 1] - s[i], 1e-9)
    return i, f


def sample_prof(prof, sq):
    """A cross-section row at arc length sq: centre xz, tangent, left normal and a height function of d."""
    i, f = interp_point(prof, sq)
    p = prof["pts"][i] * (1 - f) + prof["pts"][i + 1] * f
    t = prof["pts"][i + 1] - prof["pts"][i]
    t = t / (np.linalg.norm(t) + 1e-12)
    n = np.array([-t[1], t[0]])

    def height(d):
        return lateral(prof, i, d) * (1 - f) + lateral(prof, i + 1, d) * f
    return p, t, n, height


# ---------------------------------------------------------------------------------------------------------- ribbons

def ribbon_material(prof, i, on_bridge):
    cls = prof["cls"]
    if cls == "state":
        return M_NOPASS if prof["nopass"] is not None and prof["nopass"][i] else M_STATE
    if cls == "paved":
        return M_COUNTY
    return M_CONCRETE if on_bridge else M_GRAVEL


def offsets(cls):
    w = outer(cls)
    half = CLASSES[cls]["half"]
    d = [-(w + SKIRT_OUT), -w] + ([-half] if CLASSES[cls]["edge"] > 0 else []) + [0.0] + \
        ([half] if CLASSES[cls]["edge"] > 0 else []) + [w, w + SKIRT_OUT]
    return np.array(d), w


def simplify(prof, tol=0.03, max_gap=32.0):
    """Rows worth keeping: Douglas-Peucker on the centre line and both edge heights, plus every material change."""
    if "keep" in prof:
        return prof["keep"]
    w = outer(prof["cls"])
    n = len(prof["s"])
    idx = np.arange(n)
    X = np.stack([prof["pts"][:, 0], prof["pts"][:, 1], lateral(prof, idx, w), lateral(prof, idx, -w)], 1)
    keep = np.zeros(n, bool)
    keep[0] = keep[-1] = True
    stack = [(0, n - 1)]
    s = prof["s"]
    while stack:
        a, b = stack.pop()
        if b - a < 2:
            continue
        t = (s[a + 1:b] - s[a]) / max(s[b] - s[a], 1e-9)
        lin = X[a][None, :] + (X[b] - X[a])[None, :] * t[:, None]
        err = np.abs(X[a + 1:b] - lin).max(1)
        k = int(np.argmax(err))
        if err[k] > tol or s[b] - s[a] > max_gap:
            m = a + 1 + k if err[k] > tol else (a + b) // 2
            keep[m] = True
            stack += [(a, m), (m, b)]
    if prof["nopass"] is not None:
        ch = np.flatnonzero(np.diff(prof["nopass"].astype(int)) != 0)
        keep[ch] = keep[ch + 1] = True
    for s0, s1, *_ in prof["spans"]:
        keep[np.searchsorted(s, [s0, s1]).clip(0, n - 1)] = True
    prof["keep"] = np.flatnonzero(keep)
    return prof["keep"]


def add_ribbon(tm, prof, s0, s1):
    """Ribbon of the road surface from arc length s0 to s1, cut into chunks by tile and material."""
    s = prof["s"][simplify(prof)]
    inner = s[(s > s0 + 0.05) & (s < s1 - 0.05)]
    ss = np.concatenate([[s0], inner, [s1]])
    if len(ss) < 2 or s1 - s0 < 0.2:
        return
    D, w = offsets(prof["cls"])
    spans = prof["spans"]
    rows = []
    for sq in ss:
        p, t, n, height = sample_prof(prof, sq)
        i, _ = interp_point(prof, sq)
        hs = height(np.clip(D, -w, w))
        hs = np.where(np.abs(D) > w, hs - SKIRT_DOWN, hs)
        V = np.stack([p[0] + n[0] * D, hs, p[1] + n[1] * D], 1)
        on_b = any(a <= sq <= b for a, b, *_ in spans)
        mat = ribbon_material(prof, i, on_b)
        u = np.clip((D + w) / (2 * w), 0, 1)
        rows.append((V, u, sq / REPEAT, mat))
    # cut into pieces where the material changes or a piece gets long; neighbouring pieces share their end row
    cur, s_start = [rows[0]], ss[0]
    for k in range(1, len(rows)):
        cur.append(rows[k])
        if rows[k][3] != cur[0][3] or ss[k] - s_start > CHUNK_M:
            emit_rows(tm, cur, cur[0][3])
            cur, s_start = [rows[k]], ss[k]
    if len(cur) >= 2:
        emit_rows(tm, cur, cur[0][3])


def emit_rows(tm, seg, mat):
    m = len(seg[0][0])
    V = np.concatenate([r[0] for r in seg])
    UV = np.concatenate([np.stack([r[1], np.full(m, r[2])], 1) for r in seg])
    T = []
    for j in range(len(seg) - 1):
        for k in range(m - 1):
            a, b = j * m + k, j * m + k + 1
            c, d = (j + 1) * m + k, (j + 1) * m + k + 1
            T += [(a, c, b), (b, c, d)]
    mid = seg[len(seg) // 2][0][m // 2]
    tm.add(tm.bucket(mid[0], mid[2], "roads"), mat, V, UV, T)


# --------------------------------------------------------------------------------------------------------- junctions

def junction_index(profs, degree):
    at = {}
    for k, pr in enumerate(profs):
        for n, v in pr["junctions"]:
            if degree[n] >= 2:
                at.setdefault(n, []).append((k, v))
    out = {}
    for n, lst in at.items():
        legs = []
        for k, v in lst:
            pr = profs[k]
            for sign in (-1, 1):
                if (sign < 0 and v == 0) or (sign > 0 and v == len(pr["s"]) - 1):
                    continue
                sq = pr["s"][v] + sign * min(12.0, (pr["s"][-1] - pr["s"][v]) if sign > 0 else pr["s"][v])
                p, _, _, _ = sample_prof(pr, sq)
                u = p - pr["pts"][v]
                u = u / (np.linalg.norm(u) + 1e-12)
                legs.append(dict(k=k, v=v, sign=sign, u=u, w=outer(pr["cls"]), rank=RANK[pr["cls"]], cls=pr["cls"]))
        if len(legs) >= 2 and not (len(legs) == 2 and legs[0]["k"] == legs[1]["k"]):
            out[n] = legs
    return out


def perp(u):
    return np.array([-u[1], u[0]])


def line_cross(p, a, q, b):
    """Intersection of lines p + t a and q + s b."""
    m = np.array([[a[0], -b[0]], [a[1], -b[1]]])
    if abs(np.linalg.det(m)) < 1e-9:
        return None
    t, _ = np.linalg.solve(m, q - p)
    return p + a * t


def fillet(C, ua, ub, rc):
    """Fillet of radius rc in the corner at C between rays C + t ua and C + t ub. Returns tangent distance and arc
    points from the ua side to the ub side."""
    phi = math.acos(float(np.clip(np.dot(ua, ub), -1, 1)))
    if phi > math.radians(172) or phi < math.radians(8):
        return 0.0, np.array([C])
    td = rc / math.tan(phi / 2)
    Ta, Tb = C + ua * td, C + ub * td
    bis = ua + ub
    bis /= np.linalg.norm(bis)
    O = C + bis * rc / math.sin(phi / 2)
    a0 = math.atan2(*(Ta - O)[::-1])
    a1 = math.atan2(*(Tb - O)[::-1])
    da = (a1 - a0 + math.pi) % (2 * math.pi) - math.pi
    n = max(3, int(abs(da) * rc / 2.0))
    arc = np.array([O + rc * np.array([math.cos(a0 + da * t), math.sin(a0 + da * t)]) for t in np.linspace(0, 1, n)])
    return td, arc


def surface_height(profs, leg, q):
    """Height of a leg's road surface at map point q (projected onto the leg)."""
    pr = profs[leg["k"]]
    v = leg["v"]
    along = float(np.dot(q - pr["pts"][v], leg["u"]))
    sq = float(np.clip(pr["s"][v] + leg["sign"] * along, 0, pr["s"][-1]))
    p, t, n, height = sample_prof(pr, sq)
    d = float(np.dot(q - p, n))
    w = outer(pr["cls"])
    return float(height(np.clip(d, -w, w)))


def plan_junctions(profs, degree, nodes):
    """Trim lengths per (prof, vertex, sign) and the polygons to fill, from every junction."""
    trims = {}
    polys = []      # (points xz, heights, material)
    J = junction_index(profs, degree)
    for n, legs in J.items():
        node = np.asarray(nodes[n])
        top = max(l["rank"] for l in legs)
        tops = [l for l in legs if l["rank"] == top]
        through = len(tops) == 2 and tops[0]["k"] == tops[1]["k"] and tops[0]["v"] == tops[1]["v"]
        if through:
            major = tops[0]
            W = major["w"]
            t = tops[0]["u"]
            for l in legs:
                if l["rank"] == top:
                    continue
                u = l["u"]
                side = perp(t) if np.dot(perp(t), u) > 0 else -perp(t)
                edge_p = node + side * W
                pts_poly, hts = [], []
                trim = 0.0
                ends = []
                for sgn in (1, -1):                       # left then right edge of the minor leg
                    o = perp(u) * l["w"] * sgn
                    C = line_cross(node + o, u, edge_p, t)
                    if C is None:
                        continue
                    tdir = t if np.dot(t, perp(u) * sgn) > 0 else -t
                    td, arc = fillet(C, u, tdir, FILLET[l["cls"]])
                    trim = max(trim, float(np.dot(C + u * td - node, u)))
                    ends.append((sgn, arc))
                trim += 0.5
                trims[(l["k"], l["v"], l["sign"])] = trim
                left_end = node + u * trim + perp(u) * l["w"]
                right_end = node + u * trim - perp(u) * l["w"]
                poly = [left_end]
                for sgn, arc in ends:
                    if sgn == 1:
                        poly += list(arc)
                for sgn, arc in ends:
                    if sgn == -1:
                        poly += list(arc[::-1])
                poly.append(right_end)
                P = np.array(poly)
                H = []
                for q in P:
                    off_major = abs(float(np.dot(q - node, side)))
                    if off_major <= W + 0.05:
                        H.append(surface_height(profs, major, q))
                    else:
                        H.append(min(surface_height(profs, l, q), 1e9))
                polys.append((P, np.array(H), mouth_material(l["cls"]), "mouth"))
        else:
            legs = sorted(legs, key=lambda l: math.atan2(l["u"][1], l["u"][0]))
            m = len(legs)
            fil = []
            trim = [max(l2["w"] for l2 in legs if l2 is not l) + 1.0 for l in legs]
            for i in range(m):
                a, b = legs[i], legs[(i + 1) % m]
                C = line_cross(node + perp(a["u"]) * a["w"], a["u"], node - perp(b["u"]) * b["w"], b["u"])
                cross = a["u"][0] * b["u"][1] - a["u"][1] * b["u"][0]
                if C is None or cross <= 0.02:
                    fil.append(None)
                    continue
                td, arc = fillet(C, a["u"], b["u"], min(FILLET[a["cls"]], FILLET[b["cls"]]))
                fil.append(arc)
                trim[i] = max(trim[i], float(np.dot(arc[0] - node, a["u"])))
                trim[(i + 1) % m] = max(trim[(i + 1) % m], float(np.dot(arc[-1] - node, b["u"])))
            trim = [t + 0.5 for t in trim]
            poly = []
            for i, l in enumerate(legs):
                trims[(l["k"], l["v"], l["sign"])] = trim[i]
                poly.append(node + l["u"] * trim[i] - perp(l["u"]) * l["w"])
                poly.append(node + l["u"] * trim[i] + perp(l["u"]) * l["w"])
                if fil[i] is not None:
                    poly += list(fil[i])
            P = np.array(poly)
            H = np.array([surface_height(profs, nearest_leg(legs, node, q), q) for q in P])
            polys.append((P, H, M_PASPHALT if top >= 2 else M_PGRAVEL, "pad", node,
                          float(np.mean([surface_height(profs, l, node) for l in legs]))))
    return trims, polys


def nearest_leg(legs, node, q):
    """The leg whose centre line passes closest to q, among those q is not behind."""
    def score(l):
        r = q - node
        return abs(float(perp(l["u"]) @ r)) + (1e3 if float(l["u"] @ r) < -1.0 else 0.0)
    return min(legs, key=score)


def mouth_material(cls):
    return M_PASPHALT if cls != "gravel" else M_PGRAVEL


def ear_clip(P):
    """Triangulate a simple polygon (n, 2). Returns index triples."""
    idx = list(range(len(P)))
    area = sum(P[i][0] * P[(i + 1) % len(P)][1] - P[(i + 1) % len(P)][0] * P[i][1] for i in range(len(P)))
    ccw = area > 0
    tris = []
    guard = 0
    while len(idx) > 3 and guard < 10000:
        guard += 1
        n = len(idx)
        for k in range(n):
            i0, i1, i2 = idx[k - 1], idx[k], idx[(k + 1) % n]
            a, b, c = P[i0], P[i1], P[i2]
            cr = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
            if (cr > 1e-9) != ccw:
                continue
            inside = False
            for j in idx:
                if j in (i0, i1, i2):
                    continue
                p = P[j]
                d1 = (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0])
                d2 = (c[0] - b[0]) * (p[1] - b[1]) - (c[1] - b[1]) * (p[0] - b[0])
                d3 = (a[0] - c[0]) * (p[1] - c[1]) - (a[1] - c[1]) * (p[0] - c[0])
                if ccw and d1 > 0 and d2 > 0 and d3 > 0 or (not ccw) and d1 < 0 and d2 < 0 and d3 < 0:
                    inside = True
                    break
            if not inside:
                tris.append((i0, i1, i2))
                idx.pop(k)
                break
        else:
            break
    if len(idx) == 3:
        tris.append(tuple(idx))
    return tris


def add_polys(tm, polys):
    for item in polys:
        P, H, mat, kind = item[0], item[1], item[2], item[3]
        if kind == "pad":
            node, hc = item[4], item[5]
            V = np.vstack([[node[0], hc, node[1]], np.stack([P[:, 0], H, P[:, 1]], 1)])
            m = len(P)
            T = [(0, 1 + i, 1 + (i + 1) % m) for i in range(m)]
        else:
            V = np.stack([P[:, 0], H, P[:, 1]], 1)
            T = ear_clip(P)
        V[:, 1] += 0.01                      # a centimetre over the ribbons they meet, against z-fighting at seams
        UV = V[:, [0, 2]] / 8.0
        tm.add(tm.bucket(V[0, 0], V[0, 2], "roads"), mat, V, UV, T)


# -------------------------------------------------------------------------------------------------------- roads all

def build_roads(tm, profs, degree, nodes):
    trims, polys = plan_junctions(profs, degree, nodes)
    for k, pr in enumerate(profs):
        cuts = []
        for n, v in pr["junctions"]:
            for sign in (-1, 1):
                t = trims.get((k, v, sign))
                if t is not None:
                    sv = pr["s"][v]
                    cuts.append((sv, sv + t) if sign > 0 else (sv - t, sv))
        cuts.sort()
        a = 0.0
        for c0, c1 in cuts + [(pr["s"][-1], pr["s"][-1])]:
            if c0 > a:
                add_ribbon(tm, pr, a, c0)
            a = max(a, c1)
    add_polys(tm, polys)
    return len(polys)


# --------------------------------------------------------------------------------------------------------- bridges

def box(center, along, across, hl, hw, h0, h1):
    """Corners of an oriented box: horizontal centre (x, z), unit along/across, from height h0 to h1."""
    c = np.array([center[0], 0, center[1]])
    A = np.array([along[0], 0, along[1]]) * hl
    B = np.array([across[0], 0, across[1]]) * hw
    pts = []
    for y in (h0, h1):
        for sa, sb in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            p = c + A * sa + B * sb
            pts.append([p[0], y, p[2]])
    return np.array(pts)


def add_box(tm, key, corners, mat):
    """Six faces of a box from box(); outward-facing."""
    V, T, want = [], [], []
    ctr = corners.mean(0)
    faces = [(0, 1, 2, 3), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    for f in faces:
        base = len(V)
        q = corners[list(f)]
        V += list(q)
        out = q.mean(0) - ctr
        T += [(base, base + 1, base + 2), (base, base + 2, base + 3)]
        want += [out, out]
    V = np.array(V)
    UV = np.zeros((len(V), 2))
    for i in range(0, len(V), 4):
        q = V[i:i + 4]
        ax = q[1] - q[0]
        ay = q[3] - q[0]
        la, lb = np.linalg.norm(ax) + 1e-9, np.linalg.norm(ay) + 1e-9
        UV[i:i + 4] = [[0, 0], [la / 4, 0], [la / 4, lb / 4], [0, lb / 4]]
    tm.add(key, mat, V, UV, T, want=np.array(want))


def build_bridges(tm, profs, ground):
    """ground(x, z) -> terrain height (after grading), for pier footings."""
    count = 0
    for pr in profs:
        w = outer(pr["cls"])
        for s0, s1, deck, river, k in pr["spans"]:
            count += 1
            a, b = max(0.0, s0 - 3.0), min(pr["s"][-1], s1 + 3.0)
            ss = np.linspace(a, b, max(2, int((b - a) / 4.0) + 1))
            rows = [sample_prof(pr, q) for q in ss]
            key = tm.bucket(rows[len(rows) // 2][0][0], rows[len(rows) // 2][0][1], "bridges")
            # parapets (inner face, top, outer face) on both sides, and the slab fascia + soffit over the span
            for sgn in (1, -1):
                prof_d = [(w - 0.3) * sgn, (w - 0.3) * sgn, (w + 0.15) * sgn, (w + 0.15) * sgn]
                V, UV, T, want = [], [], [], []
                for j, (p, t, n, height) in enumerate(rows):
                    he = float(height(w * sgn))
                    ys = [he, he + 0.85, he + 0.85, he - 1.1 if s0 <= ss[j] <= s1 else he - 0.3]
                    for d, y in zip(prof_d, ys):
                        V.append([p[0] + n[0] * d, y, p[1] + n[1] * d])
                        UV.append([ss[j] / 4.0, y / 4.0])
                m = 4
                for j in range(len(rows) - 1):
                    n_out = np.array([rows[j][2][0], 0, rows[j][2][1]]) * sgn
                    for c, wv in ((0, -n_out), (1, np.array([0, 1.0, 0])), (2, n_out)):
                        a0, a1 = j * m + c, j * m + c + 1
                        b0, b1 = (j + 1) * m + c, (j + 1) * m + c + 1
                        T += [(a0, b0, a1), (a1, b0, b1)]
                        want += [wv, wv]
                tm.add(key, M_CONCRETE, V, UV, T, want=np.array(want))
            # soffit under the deck
            V, UV, T = [], [], []
            for j, (p, t, n, height) in enumerate(rows):
                if not (s0 <= ss[j] <= s1):
                    continue
                for d in (-(w + 0.15), w + 0.15):
                    y = float(height(np.clip(d, -w, w))) - 1.1
                    V.append([p[0] + n[0] * d, y, p[1] + n[1] * d])
                    UV.append([ss[j] / 4.0, d / 4.0])
            for j in range(len(V) // 2 - 1):
                T += [(2 * j, 2 * j + 1, 2 * j + 2), (2 * j + 1, 2 * j + 3, 2 * j + 2)]
            if T:
                tm.add(key, M_CONCRETE, V, UV, T, want=np.tile([0, -1.0, 0], (len(T), 1)))
            # abutments and piers
            span = s1 - s0
            npier = int(span // (28 if span < 600 else 80))      # long river crossings: longer spans between piers
            stations = [s0, s1] + [s0 + span * (i + 1) / (npier + 1) for i in range(npier)]
            for q in stations:
                p, t, n, height = sample_prof(pr, q)
                top = float(height(0.0)) - 1.1
                g = ground(p[0], p[1]) - 1.5
                if top - g < 0.3:
                    continue
                thick = 0.6 if q in (s0, s1) else 0.45
                add_box(tm, key, box(p, t, n, thick, w if q in (s0, s1) else w * 0.8, g, top), M_CONCRETE)
    return count


# ----------------------------------------------------------------------------------------------------------- water

def build_water(tm, streams):
    n = 0
    for st in streams:
        if st["width"] <= 0 or st.get("polygon"):         # (polygon: water drawn from its area, real/places.py)
            continue
        p = np.asarray(st["pts"])
        lvl = np.asarray(st["level"])
        if len(p) < 2:
            continue
        t, nrm = frames(p)
        hw = st["width"] / 2
        s = np.concatenate([[0], np.cumsum(np.linalg.norm(np.diff(p, axis=0), axis=1))])
        for a in range(0, len(p) - 1, 20):
            b = min(len(p), a + 21)
            seg = slice(a, b)
            L = p[seg] + nrm[seg] * hw
            R = p[seg] - nrm[seg] * hw
            V = np.empty((2 * (b - a), 3))
            V[0::2] = np.stack([L[:, 0], lvl[seg], L[:, 1]], 1)
            V[1::2] = np.stack([R[:, 0], lvl[seg], R[:, 1]], 1)
            UV = np.empty((2 * (b - a), 2))
            UV[0::2] = np.stack([np.zeros(b - a), s[seg] / 20.0], 1)
            UV[1::2] = np.stack([np.full(b - a, hw / 10.0), s[seg] / 20.0], 1)
            T = []
            for j in range(b - a - 1):
                T += [(2 * j, 2 * j + 1, 2 * j + 2), (2 * j + 1, 2 * j + 3, 2 * j + 2)]
            mid = p[(a + b) // 2]
            if 0 <= mid[0] < cfg.SIZE_M and 0 <= mid[1] < cfg.SIZE_M:
                tm.add(tm.bucket(mid[0], mid[1], "water"), M_WATER, V, UV, T)
                n += 1
    return n

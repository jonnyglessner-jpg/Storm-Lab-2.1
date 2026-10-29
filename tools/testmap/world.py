"""Stage 3 of the test map: everything on the ground, written out for the Unity builder.

1. Road profiles (profiles.py) and the terrain graded to them: under the road and its shoulders the ground sits
   12 cm below the surface; beyond, a 1:4 foreslope, a ditch (0.5-0.9 m by class), a 1:3 backslope, or a 1:4 fill
   slope where the road rides above the land. Junction pads and farmyard pads are flattened, bridge spans left open.
2. Streams carved to hold their water ribbons (rivers and named-size creeks), except where a road fill crosses.
3. Farmsteads: about one per two sections, set back off a county road on a driveway, with a house, metal sheds,
   grain bins and a cedar shelterbelt on the north and west.
4. Land cover, from the 2024 Cropland Data Layer read per quarter section, so the mix and where it sits are real
   (grass on the breaks and sandhills, irrigated circles near the river) while the fields line up with the game's
   section grid: quarter, half and strip fields, centre pivots with dry corners, grass on steep ground, waterways in
   the draws, mown road ditches, sand bars and riparian grass.
5. Trees (cottonwood galleries on the rivers, elm and cedar in the creeks and draws, shelterbelts), power lines.
6. Meshes (meshes.py), then per-tile heightmaps, splat maps, meshes and object lists in tests/StormLabTest/
   TestMapData, plus previews in docs/testmap.

Run after terrain.py and roads.py: python -I tools/testmap/world.py
"""
import json
import math
import pickle
import shutil
import struct
import sys
import time
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage
from scipy.spatial import cKDTree
from shapely.geometry import LineString

sys.path.insert(0, str(Path(__file__).resolve().parent))
import map_config as cfg
import meshes as M
import profiles as P
from roads import bridged
from terrain import CDL_STEP, sample
from textures import LAYERS

rng = np.random.default_rng(2026)
T0 = time.time()
DITCH = {"state": 0.9, "paved": 0.7, "gravel": 0.5, "drive": 0.0}
L_STUBBLE, L_GREEN, L_FALLOW, L_RANGE, L_GRASS, L_SORGHUM, L_SAND, L_DIRT = range(8)


def log(msg):
    print(f"[{time.time() - T0:6.0f} s] {msg}", flush=True)


def cell_centres(idx):
    """Flat grid indices -> map-frame x, z of those samples."""
    j, i = np.divmod(idx, cfg.GRID)
    return i * cfg.CELL, j * cfg.CELL


# ---------------------------------------------------------------------------------------------------------- grading

def road_seeds(profs):
    """Every profile point outside a bridge span, as a seed carrying what grading needs."""
    cols = {k: [] for k in ("x", "z", "tx", "tz", "h", "c", "f", "sl", "w", "D", "rank", "prof", "end")}
    for k, pr in enumerate(profs):
        s = pr["s"]
        inside = np.zeros(len(s), bool)
        for s0, s1, *_ in pr["spans"]:
            inside |= (s > s0 + 0.5) & (s < s1 - 0.5)
        t, _ = M.frames(pr["pts"])
        sel = np.flatnonzero(~inside)
        # seeds next to a gap (bridge, stroke end) mark the end of the road prism
        end = np.zeros(len(s), bool)
        end[0] = end[-1] = True
        edges = np.flatnonzero(np.diff(inside.astype(int)) != 0)
        end[edges] = end[np.minimum(edges + 1, len(s) - 1)] = True
        cols["x"].append(pr["pts"][sel, 0])
        cols["z"].append(pr["pts"][sel, 1])
        cols["tx"].append(t[sel, 0])
        cols["tz"].append(t[sel, 1])
        cols["h"].append(pr["h"][sel])
        cols["c"].append(pr["crown"][sel])
        cols["f"].append(pr["superf"][sel])
        cols["sl"].append(pr["superslope"][sel])
        cols["w"].append(np.full(len(sel), M.outer(pr["cls"])))
        cols["D"].append(np.full(len(sel), DITCH[pr["cls"]]))
        cols["rank"].append(np.full(len(sel), P.RANK[pr["cls"]]))
        cols["prof"].append(np.full(len(sel), k))
        cols["end"].append(end[sel])
    return {k: np.concatenate(v) for k, v in cols.items()}


def rasterise(seeds):
    """Seed id per grid cell (higher rank wins), -1 where none."""
    grid = np.full((cfg.GRID, cfg.GRID), -1, np.int32)
    order = np.argsort(seeds["rank"], kind="stable")
    ci = np.clip(np.rint(seeds["x"][order] / cfg.CELL).astype(int), 0, cfg.GRID - 1)
    cj = np.clip(np.rint(seeds["z"][order] / cfg.CELL).astype(int), 0, cfg.GRID - 1)
    grid[cj, ci] = order
    return grid


def grade_roads(dem, seeds, nodes_xy, node_r, reach=45.0):
    """Returns the graded DEM, the distance from each cell to its nearest road's edge (clearance, metres, capped at
    reach) and a mask of cells inside a road's flat zone."""
    grid = rasterise(seeds)
    dist, ind = ndimage.distance_transform_edt(grid < 0, sampling=cfg.CELL, return_indices=True)
    near = np.flatnonzero(dist.ravel() <= reach)
    log(f"grading {len(near):,} cells near roads")
    sid = grid[ind[0].ravel()[near], ind[1].ravel()[near]]
    del ind
    x, z = cell_centres(near)
    sx, sz = seeds["x"][sid], seeds["z"][sid]
    tx, tz = seeds["tx"][sid], seeds["tz"][sid]
    rx, rz = x - sx, z - sz
    d = rx * -tz + rz * tx                     # left of travel positive
    a = rx * tx + rz * tz
    w = seeds["w"][sid]
    beyond = seeds["end"][sid] & (np.abs(a) > 2.0)
    ad = np.where(beyond, np.hypot(d, a), np.abs(d))
    # lateral surface height at the clamped offset
    dc = np.clip(d, -w, w)
    hsurf = seeds["h"][sid] + (1 - seeds["f"][sid]) * (-seeds["c"][sid] * np.abs(dc)) + seeds["f"][sid] * seeds["sl"][sid] * dc
    hedge = hsurf - 0.12
    # junction pads: flat out to the pad radius
    tree = cKDTree(nodes_xy)
    nd, ni = tree.query(np.stack([x, z], 1), distance_upper_bound=60.0)
    in_pad = nd < node_r[np.minimum(ni, len(node_r) - 1)]
    F = w + 1.5
    D = seeds["D"][sid]
    flat = (ad <= F) | in_pad
    bottom_end = F + 4 * D + 1.5
    E = hedge - (ad - F) / 4.0
    Pd = np.where(ad <= bottom_end, hedge - np.minimum(D, (ad - F) / 4.0), hedge - D + (ad - bottom_end) / 3.0)
    t0 = dem.ravel()[near]
    new = np.where(flat, hedge, np.clip(t0, E, Pd))
    out = dem.copy()
    out.ravel()[near] = new.astype(np.float32)
    clear = np.full(dem.shape, reach, np.float16)
    clear.ravel()[near] = np.clip(np.where(beyond, np.hypot(d, a), np.abs(d)) - w, 0, reach).astype(np.float16)
    flatmask = np.zeros(dem.shape, bool)
    flatmask.ravel()[near] = flat
    return out, clear, flatmask


def carve_streams(dem, streams, flatmask):
    """Lower the ground along streams with water so the water ribbon sits in a channel. Road fills stay."""
    sel = [s for s in streams if s["width"] > 0]
    xs, zs, lv, hw = [], [], [], []
    for st in sel:
        pts = np.asarray(st["pts"])
        dense = []
        for a, b, la, lb in zip(pts[:-1], pts[1:], st["level"][:-1], st["level"][1:]):
            n = max(1, int(np.linalg.norm(b - a) / 4.0))
            t = np.arange(n) / n
            dense.append(np.column_stack([a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, la + (lb - la) * t]))
        q = np.concatenate(dense)
        xs.append(q[:, 0])
        zs.append(q[:, 1])
        lv.append(q[:, 2])
        hw.append(np.full(len(q), st["width"] / 2))
    x, z, lv, hw = map(np.concatenate, (xs, zs, lv, hw))
    ok = (x >= 0) & (z >= 0) & (x <= cfg.SIZE_M) & (z <= cfg.SIZE_M)
    x, z, lv, hw = x[ok], z[ok], lv[ok], hw[ok]
    grid = np.full(dem.shape, -1, np.int32)
    order = np.argsort(hw)
    grid[np.rint(z[order] / cfg.CELL).astype(int), np.rint(x[order] / cfg.CELL).astype(int)] = order
    dist, ind = ndimage.distance_transform_edt(grid < 0, sampling=cfg.CELL, return_indices=True)
    near = np.flatnonzero(dist.ravel() <= 80.0)
    sid = grid[ind[0].ravel()[near], ind[1].ravel()[near]]
    del ind
    d = dist.ravel()[near]
    h = hw[sid]
    target = np.where(d <= 0.8 * h, lv[sid] - np.where(h > 20, 1.4, 0.8),
                      np.where(d <= h, lv[sid] - 0.3, lv[sid] - 0.3 + (d - h) / 2.0))
    t0 = dem.ravel()[near]
    keep = flatmask.ravel()[near]
    new = np.where(keep, t0, np.minimum(t0, target))
    out = dem.copy()
    out.ravel()[near] = new.astype(np.float32)
    water = np.full(dem.shape, 1e3, np.float16)
    water.ravel()[near] = (d - h).astype(np.float16)     # metres from the water's edge (negative: in the water)
    return out, water


# ------------------------------------------------------------------------------------------------------- farmsteads

def plan_farmsteads(profs, dem, water_edge, clear):
    """Sites beside county roads: about one per two sections, away from streams, junctions and steep ground."""
    sites = []
    taken = cKDTree(np.zeros((1, 2)) - 1e6)
    pos = []
    junction_pts = np.array([pr["pts"][v] for pr in profs for n, v in pr["junctions"]])
    jt = cKDTree(junction_pts)
    for k, pr in enumerate(profs):
        if pr["cls"] == "state":
            continue
        L = pr["s"][-1]
        s = rng.uniform(300, 1800)
        while s < L - 200:
            i = int(np.searchsorted(pr["s"], s))
            p = pr["pts"][i]
            t, n = M.frames(pr["pts"][max(0, i - 2):i + 3])
            n = n[min(2, len(n) - 1)]
            t = t[min(2, len(t) - 1)]
            side = rng.choice([-1, 1])
            c = p + n * side * 85.0
            ok = 1500 < c[0] < cfg.SIZE_M - 1500 and 1500 < c[1] < cfg.SIZE_M - 1500
            if ok:
                ok = jt.query(p)[0] > 180 and (not pos or cKDTree(pos).query(c)[0] > 900)
            if ok:
                gi, gj = int(c[0] / cfg.CELL), int(c[1] / cfg.CELL)
                patch = dem[gj - 9:gj + 10, gi - 9:gi + 10]
                wpatch = water_edge[gj - 30:gj + 31, gi - 30:gi + 31]
                cpatch = clear[gj - 6:gj + 7, gi - 6:gi + 7]
                ok = patch.size and patch.max() - patch.min() < 4.0 and float(wpatch.min()) > 60 and \
                    float(cpatch.min()) > 20
            if ok:
                pos.append(c)
                sites.append(dict(prof=k, s=float(s), road=p, t=t, n=n * side, c=c, w=M.outer(pr["cls"])))
            s += rng.uniform(1600, 4200)
    return sites


def farmstead_layout(site, dem):
    """Buildings, trees and a driveway for one farmstead. Yard axes: fwd = away from the road, rt = along it."""
    c, fwd = site["c"], site["n"]
    rt = np.array([fwd[1], -fwd[0]])
    pad = float(sample(dem, c[None, :])[0])
    B, trees = [], []

    def at(f, r):
        return c + fwd * f + rt * r
    yaw = math.degrees(math.atan2(fwd[0], fwd[1]))
    colours = [(0.88, 0.88, 0.86), (0.78, 0.79, 0.80), (0.74, 0.70, 0.62), (0.62, 0.20, 0.16), (0.55, 0.57, 0.58)]
    roofs = [(0.35, 0.36, 0.38), (0.55, 0.20, 0.16), (0.62, 0.64, 0.66), (0.25, 0.27, 0.30)]
    # house near the road side, sheds behind, bins to one side
    hx = rng.uniform(-25, 10)
    B.append(dict(kind="house", p=at(-28, hx), yaw=yaw + rng.choice([0, 90]), w=rng.uniform(9, 13), l=rng.uniform(13, 18),
                  h=rng.uniform(3.0, 5.5), roof=rng.uniform(0.25, 0.5), wall=colours[rng.integers(0, 3)],
                  roofc=roofs[rng.integers(0, 4)]))
    for kk in range(rng.integers(1, 4)):
        B.append(dict(kind="shed", p=at(rng.uniform(5, 30), -30 + kk * 30 + rng.uniform(-4, 4)), yaw=yaw,
                      w=rng.uniform(14, 22), l=rng.uniform(18, 36), h=rng.uniform(4.5, 7.5), roof=0.2,
                      wall=colours[rng.integers(0, 5)], roofc=roofs[rng.integers(0, 4)]))
    nb = int(rng.choice([0, 0, 2, 3, 4, 5]))
    for kk in range(nb):
        dia = rng.uniform(6, 11)
        B.append(dict(kind="bin", p=at(-5 + kk * (dia + 1.5) - 10, 38 + rng.uniform(-2, 2)), yaw=0.0, w=dia, l=dia,
                      h=dia * rng.uniform(0.9, 1.4), roof=0.35, wall=(0.70, 0.72, 0.73), roofc=(0.62, 0.64, 0.66)))
    if rng.random() < 0.2:
        B.append(dict(kind="windmill", p=at(-10, rng.uniform(20, 40)), yaw=0.0, w=2, l=2, h=12, roof=0, wall=(0.6, 0.6, 0.6),
                      roofc=(0.6, 0.6, 0.6)))
    # shelterbelt: rows on the north and west sides of the yard (or the back if the road is there)
    for side_v in (np.array([0, 1.0]), np.array([-1.0, 0])):
        if np.dot(side_v, -fwd) > 0.7:          # the road is on that side
            side_v = fwd.copy()
        tang = np.array([side_v[1], -side_v[0]])
        for row in range(rng.integers(2, 4)):
            off = 62 + row * 6
            for q in np.arange(-70, 71, rng.uniform(4.5, 6.5)):
                p = c + side_v * (off + rng.normal(0, 0.6)) + tang * (q + rng.normal(0, 0.8))
                trees.append((2 if row < 2 else 1, p, rng.uniform(0.75, 1.15)))
    for _ in range(rng.integers(3, 9)):            # yard shade trees
        p = at(rng.uniform(-45, -10), rng.uniform(-50, 50))
        trees.append((1 if rng.random() < 0.6 else 0, p, rng.uniform(0.6, 1.0)))
    road_edge = site["road"] + site["n"] * (site["w"] + 0.3)
    drive_end = at(-40, hx + 14)
    return dict(c=c, fwd=fwd, pad=pad, buildings=B, trees=trees, drive=(road_edge, drive_end))


def grade_farmsteads(dem, farms, profs):
    for fm in farms:
        c = fm["c"]
        gi, gj = int(c[0] / cfg.CELL), int(c[1] / cfg.CELL)
        r = 14
        jj, ii = np.mgrid[gj - r:gj + r + 1, gi - r:gi + r + 1]
        x, z = ii * cfg.CELL, jj * cfg.CELL
        rel = np.stack([x - c[0], z - c[1]], -1)
        f = rel @ fm["fwd"]
        rr = rel @ np.array([fm["fwd"][1], -fm["fwd"][0]])
        box = np.maximum(np.abs(f - 5) / 55.0, np.abs(rr) / 60.0)
        w = np.clip((1.25 - box) / 0.25, 0, 1)
        pad = float(np.median(dem[gj - 6:gj + 7, gi - 6:gi + 7]))
        fm["pad"] = pad
        dem[jj, ii] = dem[jj, ii] * (1 - w) + pad * w
        # driveway: a straight ramp from the road edge to the pad, 6 m wide, at grade over the ditch
        a, b = fm["drive"]
        k = fm["prof_k"]
        ha = float(np.interp(fm["s"], profs[k]["s"], profs[k]["h"]))
        fm["drive_h"] = (ha - 0.03, pad)
        L = np.linalg.norm(b - a)
        u = (b - a) / L
        n = np.array([-u[1], u[0]])
        for q in np.arange(0, L, cfg.CELL * 0.5):
            for o in np.arange(-6, 6.1, cfg.CELL * 0.5):
                p = a + u * q + n * o
                i2, j2 = int(round(p[0] / cfg.CELL)), int(round(p[1] / cfg.CELL))
                h = ha + (pad - ha) * q / L - 0.1
                wgt = 1.0 if abs(o) < 4 else max(0.0, 1 - (abs(o) - 4) / 2)
                dem[j2, i2] = dem[j2, i2] * (1 - wgt) + h * wgt


def drive_meshes(tm, farms):
    for fm in farms:
        a, b = fm["drive"]
        ha, hb = fm["drive_h"]
        L = np.linalg.norm(b - a)
        u = (b - a) / L
        n = np.array([-u[1], u[0]])
        V, UV, T = [], [], []
        steps = max(2, int(L / 4))
        for k in range(steps + 1):
            t = k / steps
            p = a + u * L * t
            h = ha + (hb - ha) * t
            for o in (-2.6, 2.6):
                q = p + n * o
                V.append([q[0], h + 0.02, q[1]])
                UV.append([q[0] / 8, q[1] / 8])
        for k in range(steps):
            T += [(2 * k, 2 * k + 1, 2 * k + 2), (2 * k + 1, 2 * k + 3, 2 * k + 2)]
        tm.add(tm.bucket(a[0], a[1], "roads"), M.M_PGRAVEL, V, UV, T)


# ------------------------------------------------------------------------------------------------------- land cover

CDL_GROUP = {}
for code in (24, 26, 27, 28, 21, 205, 225, 236, 238, 22, 23, 29):
    CDL_GROUP[code] = L_STUBBLE
for code in (1, 5, 6, 12, 13, 36, 41, 42, 43, 44, 31, 33):
    CDL_GROUP[code] = L_GREEN
CDL_GROUP[61] = L_FALLOW
CDL_GROUP[4] = L_SORGHUM
for code in (37, 58, 59, 60, 111, 121, 141, 142, 143, 152, 176, 190, 195, 131):
    CDL_GROUP[code] = L_RANGE


def quarter_sections(cdl):
    """Per quarter section (100 x 100): main and second land-cover group from the CDL, by area."""
    n = int(round(cfg.SIZE_M / CDL_STEP))
    lut = np.full(256, -1, np.int16)
    for code, g in CDL_GROUP.items():
        lut[code] = g
    grp = lut[cdl]
    c = (np.arange(n) + 0.5) * CDL_STEP
    q = np.minimum((c / (cfg.MILE / 2)).astype(int), 99)
    QJ, QI = np.meshgrid(q, q, indexing="ij")
    counts = np.zeros((100, 100, 8))
    for g in range(8):
        m = grp == g
        np.add.at(counts[:, :, g], (QJ[m], QI[m]), 1)
    order = np.argsort(-counts, axis=2)
    main = order[:, :, 0]
    second = order[:, :, 1]
    frac2 = np.take_along_axis(counts, second[:, :, None], 2)[:, :, 0] / np.maximum(counts.sum(2), 1)
    return main, second, frac2


def field_plan(main, second, frac2, farms):
    """Per quarter section: layout kind and classes. kinds: 0 whole, 1 halves (N-S split), 2 halves (E-W split),
    3 strips (E-W), 4 pivot, 5 pivot with two crops."""
    kind = np.zeros((100, 100), np.int8)
    a = main.astype(np.int8).copy()
    b = second.astype(np.int8).copy()
    corner = np.where(rng.random((100, 100)) < 0.45, L_RANGE, L_FALLOW).astype(np.int8)
    u = rng.random((100, 100))
    crop = a != L_RANGE
    pivot_p = np.select([a == L_GREEN, a == L_SORGHUM, a == L_STUBBLE, a == L_FALLOW], [0.85, 0.3, 0.12, 0.05], 0.0)
    kind[crop & (u < pivot_p)] = 4
    two = (kind == 4) & (rng.random((100, 100)) < 0.25)
    kind[two] = 5
    b[two] = np.where(rng.random(two.sum()) < 0.5, L_STUBBLE, L_SORGHUM)
    rest = crop & (kind == 0)
    v = rng.random((100, 100))
    kind[rest & (frac2 > 0.25) & (v < 0.45)] = 1
    kind[rest & (frac2 > 0.25) & (v >= 0.45) & (v < 0.8)] = 2
    strips = rest & np.isin(a, [L_STUBBLE, L_FALLOW]) & (rng.random((100, 100)) < 0.12)
    kind[strips] = 3
    b[strips] = np.where(a[strips] == L_STUBBLE, L_FALLOW, L_STUBBLE)
    b = np.where(b == a, np.where(a == L_FALLOW, L_STUBBLE, L_FALLOW), b).astype(np.int8)
    for fm in farms:
        qi, qj = int(fm["c"][0] / (cfg.MILE / 2)), int(fm["c"][1] / (cfg.MILE / 2))
        if kind[qj, qi] >= 4:
            kind[qj, qi] = 0
    # a little colour variety within each field: a fraction of a neighbouring cover mixed in
    mixc = rng.choice([L_FALLOW, L_RANGE, L_DIRT, L_GRASS], size=(100, 100)).astype(np.int8)
    mixw = rng.uniform(0.0, 0.25, (100, 100)).astype(np.float32)
    return dict(kind=kind, a=a, b=b, corner=corner, mixc=mixc, mixw=mixw)


def landcover_tile(c, r, plan, dem, clear, water_edge, draw_dist, farms_in_tile):
    """Splat weights (8, R, R) for one tile; texel (j, i) centre at ((i + .5), (j + .5)) * CELL from the tile."""
    R = cfg.SPLAT_RES
    x = c * cfg.TILE_M + (np.arange(R) + 0.5) * cfg.CELL
    z = r * cfg.TILE_M + (np.arange(R) + 0.5) * cfg.CELL
    X, Z = np.meshgrid(x, z)
    Q = cfg.MILE / 2
    qi = np.minimum((X / Q).astype(int), 99)
    qj = np.minimum((Z / Q).astype(int), 99)
    lx, lz = X - qi * Q, Z - qj * Q
    kind = plan["kind"][qj, qi]
    A, B = plan["a"][qj, qi], plan["b"][qj, qi]
    cls = A.copy()
    cls = np.where((kind == 1) & (lz > Q / 2), B, cls)
    cls = np.where((kind == 2) & (lx > Q / 2), B, cls)
    cls = np.where((kind == 3) & ((lz // 110).astype(int) % 2 == 1), B, cls)
    rad = np.hypot(lx - Q / 2, lz - Q / 2)
    piv = kind >= 4
    inside = rad < Q / 2 - 12
    cls = np.where(piv & ~inside, plan["corner"][qj, qi], cls)
    cls = np.where((kind == 5) & inside & (lx > Q / 2), B, cls)
    # ground under the cover
    sl = slice(r * (cfg.TILE_RES - 1), r * (cfg.TILE_RES - 1) + R)
    sc = slice(c * (cfg.TILE_RES - 1), c * (cfg.TILE_RES - 1) + R)
    h = dem[sl, sc]
    gy, gx = np.gradient(h, cfg.CELL)
    slope = np.hypot(gx, gy)
    crop = cls != L_RANGE
    cls = np.where(crop & (slope > 0.07), L_RANGE, cls)
    dd = draw_dist[sl, sc]
    cls = np.where(crop & (dd < 18), L_GRASS, cls)
    W = np.zeros((8, R, R), np.float32)
    for k in range(8):
        W[k] = cls == k
    mixw = plan["mixw"][qj, qi]
    mixc = plan["mixc"][qj, qi]
    for k in range(8):
        W[k] = W[k] * (1 - mixw) + (mixc == k) * mixw
    # road verges: mown grass in the ditches, a little range grass
    cl = clear[sl, sc].astype(np.float32)
    verge = np.clip((14.0 - cl) / 4.0, 0, 1)[None]
    vmix = np.zeros((8, 1, 1), np.float32)
    vmix[L_GRASS], vmix[L_RANGE], vmix[L_DIRT] = 0.6, 0.3, 0.1
    W = W * (1 - verge) + vmix * verge
    under = np.clip((1.5 - cl) / 1.5, 0, 1)[None]          # under the road surface itself: gravel-coloured dirt
    umix = np.zeros((8, 1, 1), np.float32)
    umix[L_DIRT] = 1
    W = W * (1 - under) + umix * under
    # streams: sand in the channel, grass on the banks
    we = water_edge[sl, sc].astype(np.float32)
    bank = np.clip((40.0 - we) / 20.0, 0, 1)[None]
    bmix = np.zeros((8, 1, 1), np.float32)
    bmix[L_GRASS], bmix[L_RANGE] = 0.55, 0.45
    W = W * (1 - bank) + bmix * bank
    sand = np.clip((6.0 - we) / 6.0, 0, 1)[None]
    smix = np.zeros((8, 1, 1), np.float32)
    smix[L_SAND] = 1
    W = W * (1 - sand) + smix * sand
    # farmyards
    for fm in farms_in_tile:
        rel_x, rel_z = X - fm["c"][0], Z - fm["c"][1]
        f = rel_x * fm["fwd"][0] + rel_z * fm["fwd"][1]
        rr = rel_x * fm["fwd"][1] - rel_z * fm["fwd"][0]
        yard = np.clip((1.15 - np.maximum(np.abs(f - 5) / 55, np.abs(rr) / 60)) / 0.15, 0, 1)
        lawn = (f < -12) * yard
        dirt = (f >= -12) * yard
        W = W * (1 - yard[None])
        W[L_GRASS] += lawn
        W[L_DIRT] += dirt * 0.8
        W[L_GRASS] += dirt * 0.2
    W = ndimage.gaussian_filter(W, (0, 0.6, 0.6))
    W /= np.maximum(W.sum(0, keepdims=True), 1e-6)
    return W


# ----------------------------------------------------------------------------------------------------------- trees

def stream_trees(streams, clear, water_edge):
    out = []
    noise = ndimage.zoom(rng.random((64, 64)), 160, order=1)          # clumps and gaps, ~1 km features
    for st in streams:
        pts = np.asarray(st["pts"])
        if len(pts) < 2:
            continue
        if st["kind"] == "river":
            band, per_m, types = (110.0 if st["name"].startswith("Ark") else 55.0), 0.16, (0, 0, 0, 1, 1, 2)
        elif st["kind"] == "creek":
            band, per_m, types = min(45.0, 12 + 1.2 * math.sqrt(st["da"])), 0.07 * (st["da"] / 100) ** 0.3, (0, 1, 1, 1, 2, 3)
        else:
            if st["da"] < 15:
                continue
            band, per_m, types = 12.0, 0.012, (1, 2, 2, 3)
        seg = np.linalg.norm(np.diff(pts, axis=0), axis=1)
        s = np.concatenate([[0], np.cumsum(seg)])
        n = rng.poisson(per_m * s[-1])
        sq = rng.uniform(0, s[-1], n)
        x = np.interp(sq, s, pts[:, 0])
        z = np.interp(sq, s, pts[:, 1])
        k = np.clip(np.searchsorted(s, sq) - 1, 0, len(pts) - 2)
        t = pts[k + 1] - pts[k]
        t /= np.linalg.norm(t, axis=1, keepdims=True) + 1e-9
        nrm = np.stack([-t[:, 1], t[:, 0]], 1)
        off = (st["width"] / 2 + 3 + rng.random(n) ** 1.5 * band) * rng.choice([-1, 1], n)
        x, z = x + nrm[:, 0] * off, z + nrm[:, 1] * off
        ok = (x > 5) & (z > 5) & (x < cfg.SIZE_M - 5) & (z < cfg.SIZE_M - 5)
        x, z = x[ok], z[ok]
        gi, gj = (x / cfg.CELL).astype(int), (z / cfg.CELL).astype(int)
        nz = noise[(z / cfg.SIZE_M * (noise.shape[0] - 1)).astype(int), (x / cfg.SIZE_M * (noise.shape[1] - 1)).astype(int)]
        keep = (clear[gj, gi] > 7) & (water_edge[gj, gi] > 1.5) & (rng.random(len(x)) < 0.25 + 0.95 * nz)
        for xx, zz in zip(x[keep], z[keep]):
            out.append((int(rng.choice(types)), np.array([xx, zz]), float(rng.uniform(0.7, 1.25))))
    return out


def windbreaks(profs, clear):
    """A few field windbreaks: a row of cedars or elms along the north side of an east-west section road."""
    out = []
    for pr in profs:
        if pr["cls"] != "gravel" or len(pr["pts"]) < 3:
            continue
        d = pr["pts"][-1] - pr["pts"][0]
        if abs(d[0]) < abs(d[1]) * 3:
            continue
        for s0 in np.arange(0, pr["s"][-1] - cfg.MILE, cfg.MILE):
            if rng.random() > 0.04:
                continue
            typ = 2 if rng.random() < 0.6 else 1
            for s in np.arange(s0 + 150, s0 + cfg.MILE - 150, rng.uniform(5, 8)):
                i = int(np.searchsorted(pr["s"], s))
                p = pr["pts"][min(i, len(pr["pts"]) - 1)] + np.array([0, 28.0 + rng.normal(0, 0.7)])
                gi, gj = int(p[0] / cfg.CELL), int(p[1] / cfg.CELL)
                if 0 <= gi < cfg.GRID and 0 <= gj < cfg.GRID and clear[gj, gi] > 8:
                    out.append((typ, p, float(rng.uniform(0.8, 1.1))))
    return out


# ----------------------------------------------------------------------------------------------------------- power

def power_lines(profs, farms, clear, ground):
    """Distribution lines along the state routes, paved roads and every third county road (plus any road with
    farmsteads): poles about every 100 m, 15-18 m off the centre line, skipped where they'd stand in a road or
    a stream."""
    by_prof = {}
    for fm in farms:
        by_prof[fm["prof_k"]] = by_prof.get(fm["prof_k"], 0) + 1
    lines = []
    for k, pr in enumerate(profs):
        if not (pr["cls"] != "gravel" or k % 3 == 0 or by_prof.get(k, 0) >= 1):
            continue
        three = pr["cls"] != "gravel"
        off = 18.0 if pr["cls"] == "state" else 15.0
        side = 1.0 if (k % 2 == 0) else -1.0
        t, n = M.frames(pr["pts"])
        poles = []
        for s in np.arange(20.0, pr["s"][-1] - 20.0, 100.0):
            placed = None
            for jit in (0, 12, -12, 24, -24):
                q = s + jit
                if any(a - 5 < q < b + 5 for a, b, *_ in pr["spans"]):
                    continue
                i = int(np.searchsorted(pr["s"], q))
                i = min(i, len(pr["s"]) - 1)
                p = pr["pts"][i] + n[i] * off * side
                gi, gj = int(p[0] / cfg.CELL), int(p[1] / cfg.CELL)
                if not (0 < gi < cfg.GRID - 1 and 0 < gj < cfg.GRID - 1):
                    continue
                if clear[gj, gi] < 5:
                    continue
                placed = p
                break
            if placed is not None:
                poles.append([float(placed[0]), float(ground(placed[0], placed[1])), float(placed[1])])
            elif len(poles) >= 2:
                lines.append(dict(three=three, poles=poles))
                poles = []
        if len(poles) >= 2:
            lines.append(dict(three=three, poles=poles))
    return lines


# ---------------------------------------------------------------------------------------------------------- export

def write_tiles(dem, datum, plan, clear, water_edge, draw_dist, farms):
    out = cfg.UNITY_DATA
    (out / "heights").mkdir(parents=True, exist_ok=True)
    (out / "splat").mkdir(parents=True, exist_ok=True)
    hmin = float(np.floor(dem.min() - datum - 2))
    hmax = float(np.ceil(dem.max() - datum + 2))
    for r in range(cfg.TILES):
        for c in range(cfg.TILES):
            sl = slice(r * (cfg.TILE_RES - 1), r * (cfg.TILE_RES - 1) + cfg.TILE_RES)
            sc = slice(c * (cfg.TILE_RES - 1), c * (cfg.TILE_RES - 1) + cfg.TILE_RES)
            h = (dem[sl, sc] - datum - hmin) / (hmax - hmin)
            (out / "heights" / f"h_{c}_{r}.raw").write_bytes(np.clip(h * 65535, 0, 65535).astype("<u2").tobytes())
            tf = [f for f in farms if c * cfg.TILE_M - 200 < f["c"][0] < (c + 1) * cfg.TILE_M + 200
                  and r * cfg.TILE_M - 200 < f["c"][1] < (r + 1) * cfg.TILE_M + 200]
            W = landcover_tile(c, r, plan, dem, clear, water_edge, draw_dist, tf)
            for half in range(2):
                img = (np.clip(W[4 * half:4 * half + 4], 0, 1) * 255 + 0.5).astype(np.uint8).transpose(1, 2, 0)
                Image.fromarray(img[::-1], "RGBA").save(out / "splat" / f"s_{c}_{r}_{half}.png")
        log(f"tiles row {r} written")
    return hmin, hmax


def write_objects(trees, farms, lines, datum, ground):
    out = cfg.UNITY_DATA
    tiles = {}
    for typ, p, sc in trees:
        c = int(np.clip(p[0] // cfg.TILE_M, 0, cfg.TILES - 1))
        r = int(np.clip(p[1] // cfg.TILE_M, 0, cfg.TILES - 1))
        tiles.setdefault((c, r), []).append((typ, p[0], p[1], sc, rng.uniform(0, 360)))
    (out / "trees").mkdir(parents=True, exist_ok=True)
    for (c, r), lst in tiles.items():
        a = np.array(lst, np.float32)
        a[:, 1] = (a[:, 1] - c * cfg.TILE_M) / cfg.TILE_M          # normalised position within the tile
        a[:, 2] = (a[:, 2] - r * cfg.TILE_M) / cfg.TILE_M
        (out / "trees" / f"t_{c}_{r}.bin").write_bytes(a.tobytes())
    half = cfg.SIZE_M / 2
    fj = []
    for fm in farms:
        for b in fm["buildings"]:
            fj.append(dict(kind=b["kind"], x=float(b["p"][0] - half), y=float(fm["pad"] - datum),
                           z=float(b["p"][1] - half), yaw=float(b["yaw"]), w=float(b["w"]), l=float(b["l"]),
                           h=float(b["h"]), roof=float(b["roof"]), wall=list(b["wall"]), roofc=list(b["roofc"])))
    json.dump(dict(items=fj), open(out / "buildings.json", "w"))
    lj = [dict(three=ln["three"], poles=[round(v, 2) for p in ln["poles"] for v in (p[0] - half, p[1] - datum, p[2] - half)])
          for ln in lines]
    json.dump(dict(lines=lj), open(out / "power.json", "w"))


def write_skirt(dem, datum, hmin, hmax):
    """One terrain three map widths across, 2049 samples, under and around the map. Inside the map it is dropped
    25 m so it never shows through; along the map edge it meets the map's own edge heights."""
    import tifffile
    a = tifffile.imread(cfg.RAW / "skirt.tif").astype(np.float32)[::-1]
    a[~np.isfinite(a) | (a < 0)] = np.nanmedian(a)
    n = 2049
    span = 3 * cfg.SIZE_M
    src = (cfg.SKIRT_RES - 1) / (n - 1)
    idx = np.arange(n) * src
    sk = ndimage.map_coordinates(a, np.meshgrid(idx, idx, indexing="ij"), order=1)
    pos = np.arange(n) / (n - 1) * span - cfg.SIZE_M            # map-frame metres of each skirt sample
    inside = (pos > 1.0) & (pos < cfg.SIZE_M - 1.0)
    I, J = np.meshgrid(inside, inside)
    sk[I & J] -= 25.0
    # snap the samples nearest the map edge to the map's edge heights, slightly low
    edge = (np.abs(pos) < span / (n - 1)) | (np.abs(pos - cfg.SIZE_M) < span / (n - 1))
    ji = np.flatnonzero((pos >= -span / (n - 1)) & (pos <= cfg.SIZE_M + span / (n - 1)))
    for jj in ji:
        for ii in ji:
            if not (edge[jj] or edge[ii]):
                continue
            x, z = np.clip(pos[ii], 0, cfg.SIZE_M), np.clip(pos[jj], 0, cfg.SIZE_M)
            sk[jj, ii] = sample(dem, np.array([[x, z]]))[0] - 0.6
    lo, hi = float(np.floor(sk.min() - datum - 1)), float(np.ceil(sk.max() - datum + 1))
    h = (sk - datum - lo) / (hi - lo)
    (cfg.UNITY_DATA / "skirt.raw").write_bytes(np.clip(h * 65535, 0, 65535).astype("<u2").tobytes())
    # land cover: a random quarter-section patchwork in the same proportions
    R = 2048
    qn = int(span / (cfg.MILE / 2))
    probs = np.array([0.20, 0.11, 0.15, 0.36, 0.0, 0.12, 0.0, 0.0])
    qcls = rng.choice(8, size=(qn + 1, qn + 1), p=probs / probs.sum())
    tex = (np.arange(R) + 0.5) / R * span
    qi = (tex / (cfg.MILE / 2)).astype(int)
    cls = qcls[np.ix_(qi, qi)]
    W = np.stack([(cls == k).astype(np.float32) for k in range(8)])
    W = 0.6 * W
    W[L_RANGE] += 0.4          # seen only from far off: soften the patchwork toward range grass
    W = ndimage.gaussian_filter(W, (0, 0.5, 0.5))
    W /= W.sum(0, keepdims=True)
    for half in range(2):
        img = (W[4 * half:4 * half + 4] * 255 + 0.5).astype(np.uint8).transpose(1, 2, 0)
        Image.fromarray(img[::-1], "RGBA").save(cfg.UNITY_DATA / "splat" / f"skirt_{half}.png")
    return dict(res=n, size=span, origin=-cfg.SIZE_M - cfg.SIZE_M / 2, hmin=lo, hmax=hi)


def spawn_points(profs, datum):
    """Named places to start the car: on a state route junction, a river bridge, a gravel road in the breaks."""
    half = cfg.SIZE_M / 2
    out = []
    for pr in profs:
        if pr["cls"] == "state" and len(out) < 4:
            i = len(pr["s"]) // 3
            t, _ = M.frames(pr["pts"][i - 1:i + 2])
            out.append(dict(name=f"{pr['name']}", x=float(pr["pts"][i, 0] - half), y=float(pr["h"][i] - datum + 0.3),
                            z=float(pr["pts"][i, 1] - half), yaw=float(math.degrees(math.atan2(t[1, 0], t[1, 1])))))
    for pr in profs:
        for s0, s1, deck, river, k in pr["spans"]:
            if river and len(out) < 6:
                s = s0 - 120
                i = int(np.searchsorted(pr["s"], s))
                t, _ = M.frames(pr["pts"][i - 1:i + 2])
                out.append(dict(name=f"{pr['name']} river bridge", x=float(pr["pts"][i, 0] - half),
                                y=float(pr["h"][i] - datum + 0.3), z=float(pr["pts"][i, 1] - half),
                                yaw=float(math.degrees(math.atan2(t[1, 0], t[1, 1])))))
                break
    return out


def routes(profs, datum, step=25.0):
    """Centre lines (world x, y, z every ~25 m, flattened) of the state routes, the paved roads and the three longest gravel
    roads, for the drive test's autopilot and the HUD. The real-data maps take 10 m: their curves are much tighter."""
    half = cfg.SIZE_M / 2
    gravel = sorted((p for p in profs if p["cls"] == "gravel"), key=lambda p: -p["s"][-1])[:3]
    out = []
    for pr in [p for p in profs if p["cls"] != "gravel"] + gravel:
        idx = np.unique(np.searchsorted(pr["s"], np.arange(0, pr["s"][-1], step)).clip(0, len(pr["s"]) - 1))
        pts = [round(v, 2) for i in idx for v in (float(pr["pts"][i, 0] - half), float(pr["h"][i] - datum),
                                                  float(pr["pts"][i, 1] - half))]
        out.append(dict(name=pr["name"], cls=pr["cls"], pts=pts))
    return out


def preview(plan, dem, profs, farms, path):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.colors import LightSource
    colours = np.array([(196, 168, 112), (78, 106, 44), (120, 96, 70), (140, 128, 86), (86, 106, 46), (150, 80, 42),
                        (190, 172, 140), (126, 114, 98)]) / 255.0
    step = 8
    R = cfg.SPLAT_RES // step
    img = np.zeros((cfg.TILES * R, cfg.TILES * R, 3))
    for r in range(cfg.TILES):
        for c in range(cfg.TILES):
            for half in range(2):
                a = np.asarray(Image.open(cfg.UNITY_DATA / "splat" / f"s_{c}_{r}_{half}.png"))[::-1][::step, ::step] / 255.0
                img[r * R:(r + 1) * R, c * R:(c + 1) * R] += np.tensordot(a, colours[4 * half:4 * half + 4], 1)
    h = dem[::step * 1, ::step * 1][:cfg.TILES * R, :cfg.TILES * R]
    shade = LightSource(315, 40).hillshade(h, vert_exag=6, dx=cfg.CELL * step, dy=cfg.CELL * step)
    img = img * (0.55 + 0.6 * shade[..., None])
    fig, ax = plt.subplots(figsize=(24, 24))
    ax.imshow(np.clip(img, 0, 1), origin="lower", extent=(0, cfg.SIZE_M, 0, cfg.SIZE_M))
    for pr in profs:
        ax.plot(pr["pts"][:, 0], pr["pts"][:, 1], color={"state": "#e02020", "paved": "#202020", "gravel": "#e8e0d0"}[pr["cls"]],
                lw={"state": 1.8, "paved": 1.2, "gravel": 0.5}[pr["cls"]])
    if farms:
        fx = np.array([f["c"] for f in farms])
        ax.plot(fx[:, 0], fx[:, 1], "s", ms=2.5, color="#2050ff")
    ax.set_title("Plains test map: land cover (from the 2024 CDL, laid on the section grid), roads, farmsteads")
    fig.savefig(path, dpi=55, bbox_inches="tight")


def main():
    streams = json.load(open(cfg.OUT / "streams.json"))
    for st in streams:
        st["_line"] = LineString(st["pts"])
    net = json.load(open(cfg.OUT / "network.json"))
    nodes = np.array(net["nodes"])
    degree = np.array(net["degree"])
    dem = np.load(cfg.OUT / "dem.npy")
    datum = float(np.round(np.median(dem[::20, ::20]) / 10) * 10)
    log(f"datum {datum:.0f} m")

    profs, node_h = P.all_profiles(net, dem, streams)
    for ln in P.summary(profs):
        log(ln)
    seeds = road_seeds(profs)
    jn = [n for n in range(len(nodes)) if degree[n] >= 3]
    nodes_xy = nodes[jn]
    node_r = np.full(len(jn), 22.0)
    graded, clear, flatmask = grade_roads(dem, seeds, nodes_xy, node_r)
    del seeds
    log("roads graded")
    graded, water_edge = carve_streams(graded, streams, flatmask)
    log("streams carved")
    sites = plan_farmsteads(profs, graded, water_edge, clear)
    farms = []
    for st in sites:
        fm = farmstead_layout(st, graded)
        fm.update(prof_k=st["prof"], s=st["s"], road=st["road"])
        farms.append(fm)
    grade_farmsteads(graded, farms, profs)
    log(f"{len(farms)} farmsteads")
    # draws: distance to the smaller NHD streams, for grassed waterways
    dgrid = np.ones(graded.shape, bool)
    for st in streams:
        if st["kind"] == "draw" or (st["kind"] == "creek" and not bridged(st)):
            p = np.asarray(st["pts"])
            ok = (p[:, 0] >= 0) & (p[:, 1] >= 0) & (p[:, 0] < cfg.SIZE_M) & (p[:, 1] < cfg.SIZE_M)
            dgrid[(p[ok, 1] / cfg.CELL).astype(int), (p[ok, 0] / cfg.CELL).astype(int)] = False
    draw_dist = ndimage.distance_transform_edt(dgrid, sampling=cfg.CELL).astype(np.float16)
    del dgrid

    def ground(x, z):
        return float(sample(graded, np.array([[x, z]]))[0])

    trees = stream_trees(streams, clear, water_edge) + windbreaks(profs, clear)
    for fm in farms:
        trees += fm["trees"]
    log(f"{len(trees):,} trees")
    lines = power_lines(profs, farms, clear, ground)
    log(f"{len(lines)} power lines, {sum(len(l['poles']) for l in lines):,} poles")

    tm = M.TileMeshes(datum)
    pads = M.build_roads(tm, profs, degree, nodes)
    drive_meshes(tm, farms)
    nb = M.build_bridges(tm, profs, ground)
    nw = M.build_water(tm, streams)
    log(f"meshes: {pads} junction pads, {nb} bridges, {nw} water pieces")

    main_g, second, frac2 = quarter_sections(np.load(cfg.OUT / "cdl.npy"))
    plan = field_plan(main_g, second, frac2, farms)
    if cfg.UNITY_DATA.exists():
        shutil.rmtree(cfg.UNITY_DATA)
    cfg.UNITY_DATA.mkdir(parents=True)
    stats = tm.write(cfg.UNITY_DATA / "meshes")
    log(f"mesh vertices: {stats}")
    hmin, hmax = write_tiles(graded, datum, plan, clear, water_edge, draw_dist, farms)
    write_objects(trees, farms, lines, datum, ground)
    skirt = write_skirt(graded, datum, hmin, hmax)
    meta = dict(sizeM=cfg.SIZE_M, tiles=cfg.TILES, tileM=cfg.TILE_M, tileRes=cfg.TILE_RES, splatRes=cfg.SPLAT_RES,
                datum=datum, hmin=hmin, hmax=hmax, layers=LAYERS, materials=M.MAT, skirt=skirt,
                scenarioOriginKm=(cfg.SIZE_M / 2) / 1000.0 - (cfg.SCENARIO_MILES * cfg.MILE / 2) / 1000.0,
                spawns=spawn_points(profs, datum), routes=routes(profs, datum),
                source=dict(terrain="USGS 3DEP bare earth via The National Map, resampled to 7.86 m",
                            streams="USGS NHDPlus medium resolution", landcover="USDA NASS Cropland Data Layer 2024",
                            centre_utm14n=[cfg.E0 + cfg.SIZE_M / 2, cfg.N0 + cfg.SIZE_M / 2]))
    json.dump(meta, open(cfg.UNITY_DATA / "meta.json", "w"), indent=1)
    np.save(cfg.OUT / "dem_final.npy", graded)
    pickle.dump(dict(farms=[{k: v for k, v in f.items() if k != "trees"} for f in farms]), open(cfg.OUT / "farms.pkl", "wb"))
    cfg.PREVIEW.mkdir(parents=True, exist_ok=True)
    preview(plan, graded, profs, farms, cfg.PREVIEW / "landcover.png")
    log("done")


if __name__ == "__main__":
    main()

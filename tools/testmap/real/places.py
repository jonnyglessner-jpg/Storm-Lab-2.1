"""Stage 3 of a real-data map (dixie, midwest): everything on the ground, written out for the Unity builder in the
plains map's format.

1. Road profiles, grading, junction pads, bridges and stream ribbons: the plains map's (profiles.py, world.py,
   meshes.py). Junction heights far from a road's own profile are blended in over a grade-keeping length
   (profiles.ADAPTIVE_BLEND). Roads over lakes ride 1.5 m above the water. Dixie: the Natchez-Vidalia bridge clears
   the river by 26 m and runs on long spans over the batture.
2. The water polygons (land.py): the ground under them is carved 1-6 m below the water level and a flat water
   surface is laid at the level, run by run on the height grid; rivers are muddy brown, lakes and ponds dark.
3. The town (Natchez, Effingham): downtown, two- and three-storey brick rows on the street line; houses along every
   other street; stores with parking along the highways; a few churches. Lots are skipped where the ground under
   them falls more than 2.5 m or a road, a stream or another building is in the way.
4. The countryside: a house or a mobile home every few hundred metres along the roads, with a shed and yard trees;
   farm headquarters (shop, bins) on the delta floodplain (dixie) or on any farm road (midwest). Yards are graded flat.
5. Forest: a canopy shell over the woods' interior (a 31 m roof at crown height, 18-26 m, with a crown texture,
   closed by vertical walls in the understory's shade) and
   real trees where the shell would show its edge: the front row along every road, the forest edges within 45 m of a
   road, and the town's wooded lots. Dixie: pines in the hills, oaks and sweetgums on the floodplain, bald cypress
   round the lakes. Midwest: oak-hickory timber, a few planted pines.
6. Land cover splats from WorldCover (pasture, tilled and young crops on a field patchwork, forest floor, wetland,
   lawn in town and yards, sand bars, mown verges), power lines, the horizon skirt (3DEP 1 arc-second), the spawn
   points and routes, meta.json, and previews in docs/testmap/<map>.

Run after land.py, roadnet.py and groundtex.py: python -I tools/testmap/real/places.py --map dixie|midwest
"""
import json
import math
import shutil
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import _paths  # noqa: F401,E402
import numpy as np
from PIL import Image
from scipy import ndimage
from scipy.spatial import cKDTree
from shapely.geometry import LineString

import map_config as cfg
import meshes as M
import profiles as P
import world as W
from groundtex import CANOPY_M, CANOPY_SIDE_M, LAYERS
from land import COVER_STEP, NCOV, GeoTiles, WC_BARE, WC_BUILT, WC_CROP, WC_GRASS, WC_SHRUB, WC_TREE, WC_WATER, WC_WETLAND
from terrain import sample

REAL = cfg.SPEC["real"]
rng = np.random.default_rng({"dixie": 1817, "midwest": 1818}.get(cfg.MAP, 0))     # (the years of statehood)
T0 = time.time()
L_PASTURE, L_TILLED, L_YOUNG, L_FOREST, L_LAWN, L_WETLAND, L_SAND, L_DIRT = range(8)
M.MAT += ["river", "canopy_hard", "canopy_pine", "canopy_side"]
M_RIVER, M_CANOPY_HARD, M_CANOPY_PINE, M_CANOPY_SIDE = range(len(M.MAT) - 4, len(M.MAT))
DOWNTOWN = (cfg.SIZE_M / 2 + REAL["downtown"][0], cfg.SIZE_M / 2 + REAL["downtown"][1])
DOWNTOWN_R = REAL["downtown_r"]
TREE_PINE, TREE_OAK, TREE_CYPRESS, TREE_SHRUB = range(4)
CANOPY_G = 4                                            # canopy shell grid: every 4th height sample (31 m)
SHELL_CLEAR_M = 22.0                                    # the shell starts this far from a road's edge ...
CANOPY_FRINGE_M = 6.0                                   # the walls rise this far above the roof: the crowns' ragged tops
EDGE_TREES_M = 45.0                                     # ... and real trees stand in front of it, and on forest edges this near a road


def log(msg):
    print(f"[{time.time() - T0:6.0f} s] {msg}", flush=True)


def cells(x, z):
    return (np.clip((np.asarray(z) / cfg.CELL).astype(int), 0, cfg.GRID - 1),
            np.clip((np.asarray(x) / cfg.CELL).astype(int), 0, cfg.GRID - 1))


def cov_at(cov, x, z):
    return cov[np.clip((np.asarray(z) / COVER_STEP).astype(int), 0, NCOV - 1),
               np.clip((np.asarray(x) / COVER_STEP).astype(int), 0, NCOV - 1)]


# ------------------------------------------------------------------------------------------------------------ water

def carve_water(dem, kind, level, flatmask):
    """Lower the ground under the water polygons below their level: 1 m at the edge to 6 m (river) or 3 m (lakes)
    50 m in. Road fills stay."""
    wet = kind > 0
    d = ndimage.distance_transform_edt(wet, sampling=cfg.CELL)
    deep = np.where(np.isin(kind, (1, 3)), 6.0, 3.0)
    target = level - 1.0 - (deep - 1.0) * np.clip(d / 50.0, 0, 1)
    out = np.where(wet & ~flatmask, np.minimum(dem, target), dem).astype(np.float32)
    edge = ndimage.distance_transform_edt(~wet, sampling=cfg.CELL) - np.where(wet, d, 0)
    return out, edge.astype(np.float16)


def outside_water(streams, kind):
    """The pieces of each stream that run outside the water polygons (a wide bayou is drawn by its polygon)."""
    wet = (kind > 0).astype(np.float32)
    out = []
    for st in streams:
        p, lv = np.asarray(st["pts"]), np.asarray(st["level"])
        dry = sample(wet, p) < 0.5
        ch = np.flatnonzero(np.diff(np.concatenate([[0], dry.astype(np.int8), [0]])))
        for a, b in zip(ch[0::2], ch[1::2]):
            if b - a >= 3:
                out.append(dict(st, pts=p[a:b], level=lv[a:b]))
    return out


def water_meshes(tm, kind, level):
    """Flat water over every wet cell (and one cell round it, so the terrain cuts the shoreline), as quads along each
    row's runs, cut by tile. Heights from the level field, lifted 5 cm."""
    wet = ndimage.binary_dilation(kind > 0, iterations=1)
    idx = ndimage.distance_transform_edt(kind == 0, return_distances=False, return_indices=True)
    lv = level[tuple(idx)]
    kk = kind[tuple(idx)]
    del idx
    count = 0
    for r in range(cfg.TILES):
        for c in range(cfg.TILES):
            j0, i0 = r * (cfg.TILE_RES - 1), c * (cfg.TILE_RES - 1)
            for mat, sel in ((M_RIVER, (1, 3)), (M.M_WATER, (2,))):
                V, UV, T = [], [], []
                sub = wet[j0:j0 + cfg.TILE_RES - 1, i0:i0 + cfg.TILE_RES - 1] & \
                    np.isin(kk[j0:j0 + cfg.TILE_RES - 1, i0:i0 + cfg.TILE_RES - 1], sel)
                if not sub.any():
                    continue
                for jj in np.flatnonzero(sub.any(1)):
                    row = sub[jj].astype(np.int8)
                    ch = np.flatnonzero(np.diff(np.concatenate([[0], row, [0]])))
                    for a, b in zip(ch[0::2], ch[1::2]):
                        # runs longer than 32 cells are split so the level can follow the river's slope
                        for a2 in range(a, b, 32):
                            b2 = min(b, a2 + 32)
                            J, I = j0 + jj, i0 + a2
                            xs = np.array([I, i0 + b2]) * cfg.CELL
                            zs = np.array([J, J + 1]) * cfg.CELL
                            base = len(V)
                            for z in zs:
                                for x in xs:
                                    jy, ix = cells(x, z)
                                    V.append([x, float(lv[jy, ix]) + 0.05, z])
                                    UV.append([x / 40.0, z / 40.0])
                            T += [(base, base + 2, base + 1), (base + 1, base + 2, base + 3)]
                if T:
                    tm.add((c, r, "water"), mat, V, UV, T)
                    count += 1
    return count


# --------------------------------------------------------------------------------------------------------- buildings

def footprint_ok(x, z, fw, fl, yaw, clear, water_edge, dem, taken, min_clear=2.0, max_fall=2.5):
    """Corners and centre of a building: off the roads and the water, on ground that falls less than max_fall."""
    a = math.radians(yaw)
    fwd = np.array([math.sin(a), math.cos(a)])
    rt = np.array([fwd[1], -fwd[0]])
    pts = np.array([[x, z]] + [[x, z] + fwd * sf * fl / 2 + rt * sr * fw / 2 for sf in (-1, 1) for sr in (-1, 1)])
    if (pts < 50).any() or (pts > cfg.SIZE_M - 50).any():
        return None
    j, i = cells(pts[:, 0], pts[:, 1])
    if float(clear[j, i].astype(np.float32).min()) < min_clear or float(water_edge[j, i].astype(np.float32).min()) < 6:
        return None
    h = sample(dem, pts)
    if h.max() - h.min() > max_fall:
        return None
    if taken.near(x, z, 0.5 * math.hypot(fw, fl)):
        return None
    return float(h.min()), float(h.max() - h.min())


class Taken:
    """Building footprints placed so far, as circles."""

    def __init__(self):
        self.p, self.r, self.tree, self.dirty = [], [], None, 0

    def near(self, x, z, r):
        if self.dirty:
            self.tree = cKDTree(np.array(self.p)) if self.p else None
            self.dirty = 0
        if self.tree is None:
            return False
        for k in self.tree.query_ball_point([x, z], r + 50.0):
            if math.hypot(self.p[k][0] - x, self.p[k][1] - z) < r + self.r[k] + 1.5:
                return True
        return False

    def add(self, x, z, r):
        self.p.append((x, z))
        self.r.append(r)
        self.dirty = 1


WALLS = [(0.92, 0.91, 0.88), (0.86, 0.84, 0.78), (0.78, 0.80, 0.80), (0.82, 0.76, 0.64), (0.70, 0.74, 0.70),
         (0.88, 0.86, 0.80), (0.64, 0.66, 0.70)]
BRICK = [(0.52, 0.26, 0.20), (0.60, 0.34, 0.26), (0.46, 0.24, 0.20), (0.70, 0.62, 0.52), (0.82, 0.78, 0.70)]
ROOFS = [(0.26, 0.27, 0.29), (0.36, 0.33, 0.30), (0.20, 0.22, 0.25), (0.45, 0.30, 0.24), (0.58, 0.60, 0.62)]
METAL = [(0.74, 0.76, 0.77), (0.60, 0.20, 0.16), (0.30, 0.42, 0.34), (0.86, 0.86, 0.84)]


def building(kind, x, z, yaw, w, l, h, roof, wall, roofc, base, fall):
    return dict(kind=kind, x=x, z=z, yaw=yaw, w=w, l=l, h=h + fall, roof=roof, wall=wall, roofc=roofc, y=base)


def town(profs, natchez, clear, water_edge, dem, taken):
    """The town's buildings, lot by lot along both sides of every road inside it."""
    out, trees, lots = [], [], []
    for k, pr in enumerate(profs):
        pts, s = pr["pts"], pr["s"]
        inside = natchez[np.clip((pts[:, 1] / COVER_STEP).astype(int), 0, NCOV - 1),
                         np.clip((pts[:, 0] / COVER_STEP).astype(int), 0, NCOV - 1)]
        if not inside.any():
            continue
        t, n = M.frames(pts)
        w = M.outer(pr["cls"])
        for side in (-1, 1):
            q = rng.uniform(5, 20)
            while q < s[-1] - 5:
                i = int(np.searchsorted(s, q))
                i = min(i, len(s) - 1)
                if not inside[i]:
                    q += 25
                    continue
                p = pts[i]
                dd = math.hypot(p[0] - DOWNTOWN[0], p[1] - DOWNTOWN[1])
                face = math.degrees(math.atan2(-n[i][0] * side, -n[i][1] * side))     # front faces the road
                u = rng.random()
                if dd < DOWNTOWN_R and pr["cls"] != "state" or dd < DOWNTOWN_R * 0.6:
                    fw, fl = rng.uniform(8, 14), rng.uniform(18, 30)
                    setback = w + 2.5 + fl / 2
                    kind, h, roof = "store", rng.uniform(7, 12.5), 0.0
                    wall, roofc = BRICK[rng.integers(len(BRICK))], ROOFS[2]
                    step = fw + rng.uniform(0, 1.5)
                elif pr["cls"] == "state" and u < 0.55:
                    fw, fl = rng.uniform(25, 70), rng.uniform(25, 55)
                    setback = w + rng.uniform(30, 55) + fl / 2
                    kind, h, roof = "store", rng.uniform(5.5, 8.5), 0.0
                    wall, roofc = [(0.80, 0.78, 0.72), (0.72, 0.70, 0.66), (0.62, 0.56, 0.48)][rng.integers(3)], ROOFS[4]
                    step = fw + rng.uniform(20, 60)
                else:
                    fw, fl = rng.uniform(9, 13), rng.uniform(12, 18)
                    setback = w + rng.uniform(9, 16) + fl / 2
                    kind, h, roof = "house", rng.uniform(3.0, 4.2) if rng.random() < 0.75 else rng.uniform(5.8, 7.0), rng.uniform(0.35, 0.55)
                    wall, roofc = WALLS[rng.integers(len(WALLS))], ROOFS[rng.integers(len(ROOFS))]
                    step = rng.uniform(20, 30)
                    if rng.random() < 0.012:
                        kind, fw, fl, h, roof, wall = "church", 14.0, 26.0, 8.0, 0.75, (0.94, 0.93, 0.90)
                        setback = w + 14 + fl / 2
                c = p + n[i] * side * setback
                ok = footprint_ok(c[0], c[1], fw, fl, face, clear, water_edge, dem, taken,
                                  min_clear=1.5 if kind == "store" and dd < DOWNTOWN_R else 4.0)
                if ok:
                    base, fall = ok
                    taken.add(c[0], c[1], 0.5 * math.hypot(fw, fl))
                    out.append(building(kind if kind != "church" else "house", c[0], c[1], face, fw, fl, h, roof, wall, roofc, base, fall))
                    if kind == "church":
                        st = c - np.array([math.sin(math.radians(face)), math.cos(math.radians(face))]) * (fl / 2 - 2)
                        out.append(building("steeple", st[0], st[1], face, 3.4, 3.4, 15.0, 1.6, wall, ROOFS[0], base, fall))
                    if kind == "store" and pr["cls"] == "state" and dd >= DOWNTOWN_R * 0.6:
                        lots.append((p + n[i] * side * (w + 3), c - n[i] * side * fl / 2, fw, face))
                    if kind == "house":
                        for _ in range(rng.integers(0, 3)):
                            tp = c + n[i] * side * rng.uniform(-fl, fl * 1.5) + t[i] * rng.uniform(-step / 2, step / 2)
                            trees.append((TREE_OAK if rng.random() < 0.8 else TREE_PINE, tp, float(rng.uniform(0.7, 1.2))))
                q += step
    log(f"{REAL['town']}: {sum(1 for b in out if b['kind'] == 'house')} houses, {sum(1 for b in out if b['kind'] == 'store')} "
        f"stores, {sum(1 for b in out if b['kind'] == 'steeple')} churches, {len(lots)} parking lots")
    return out, trees, lots


def countryside(profs, natchez, clear, water_edge, dem, taken, flood):
    """Homes along the rural roads; a farm headquarters now and then where farms are (flood: the delta floodplain,
    or everywhere)."""
    homes = []
    junction_pts = np.array([pr["pts"][v] for pr in profs for n, v in pr["junctions"]])
    jt = cKDTree(junction_pts)
    for k, pr in enumerate(profs):
        pts, s = pr["pts"], pr["s"]
        if len(s) < 3:
            continue
        t, n = M.frames(pts)
        w = M.outer(pr["cls"])
        q = rng.uniform(100, 900)
        while q < s[-1] - 100:
            i = min(int(np.searchsorted(s, q)), len(s) - 1)
            p = pts[i]
            gap = rng.uniform(300, 1300) if pr["cls"] != "gravel" else rng.uniform(450, 1800)
            ci, cj = int(p[0] / COVER_STEP), int(p[1] / COVER_STEP)
            if natchez[min(cj, NCOV - 1), min(ci, NCOV - 1)] or jt.query(p)[0] < 70:
                q += gap
                continue
            side = rng.choice([-1, 1])
            farm = bool(flood[cells(p[0], p[1])]) and rng.random() < REAL["farm_p"]
            setback = w + (rng.uniform(45, 70) if farm else rng.uniform(22, 40))
            c = p + n[i] * side * setback
            if not (150 < c[0] < cfg.SIZE_M - 150 and 150 < c[1] < cfg.SIZE_M - 150):
                q += gap
                continue
            face = math.degrees(math.atan2(-n[i][0] * side, -n[i][1] * side))
            mobile = not farm and rng.random() < 0.4
            fw, fl = (4.3, rng.uniform(17, 23)) if mobile else (rng.uniform(9, 13), rng.uniform(13, 18))
            ok = footprint_ok(c[0], c[1], fw + 20, fl + 20, face, clear, water_edge, dem, taken, min_clear=10.0, max_fall=4.0)
            if ok:
                taken.add(c[0], c[1], 45.0 if farm else 25.0)
                homes.append(dict(prof_k=k, s=float(q), road=p, n=n[i] * side, t=t[i], c=c, face=face, farm=farm,
                                  mobile=mobile, fw=fw, fl=fl, w=w))
            q += gap
    log(f"countryside: {len(homes)} homes ({sum(h['farm'] for h in homes)} farm headquarters, "
        f"{sum(h['mobile'] for h in homes)} mobile homes)")
    return homes


def grade_yards(dem, homes, profs):
    """Flatten each yard to a pad, and lay its driveway from the road edge to the pad."""
    for hm in homes:
        c = hm["c"]
        fwd = -hm["n"]
        gi, gj = int(c[0] / cfg.CELL), int(c[1] / cfg.CELL)
        r = 10 if hm["farm"] else 6
        jj, ii = np.mgrid[gj - r:gj + r + 1, gi - r:gi + r + 1]
        x, z = ii * cfg.CELL, jj * cfg.CELL
        rel = np.stack([x - c[0], z - c[1]], -1)
        f, rr = rel @ fwd, rel @ np.array([fwd[1], -fwd[0]])
        lim = 40.0 if hm["farm"] else 22.0
        box = np.maximum(np.abs(f) / lim, np.abs(rr) / lim)
        wgt = np.clip((1.25 - box) / 0.25, 0, 1)
        pad = float(np.median(dem[gj - 2:gj + 3, gi - 2:gi + 3]))
        hm["pad"] = pad
        dem[jj, ii] = dem[jj, ii] * (1 - wgt) + pad * wgt
        a = hm["road"] + hm["n"] * (hm["w"] + 0.3)
        b = c + hm["n"] * (fl_half(hm) + 3)
        k = hm["prof_k"]
        ha = float(np.interp(hm["s"], profs[k]["s"], profs[k]["h"]))
        hm["drive"] = (a, b)
        hm["drive_h"] = (ha - 0.03, pad)
        L = np.linalg.norm(b - a)
        if L < 1:
            continue
        u = (b - a) / L
        nn = np.array([-u[1], u[0]])
        for q in np.arange(0, L, cfg.CELL * 0.5):
            for o in np.arange(-5, 5.1, cfg.CELL * 0.5):
                p = a + u * q + nn * o
                i2, j2 = int(round(p[0] / cfg.CELL)), int(round(p[1] / cfg.CELL))
                h = ha + (pad - ha) * q / L - 0.1
                wg = 1.0 if abs(o) < 3.5 else max(0.0, 1 - (abs(o) - 3.5) / 1.5)
                dem[j2, i2] = dem[j2, i2] * (1 - wg) + h * wg


def fl_half(hm):
    return 6.0 if not hm["farm"] else 10.0


def home_buildings(hm):
    """The buildings and trees of one rural home site."""
    c, face = hm["c"], hm["face"]
    a = math.radians(face)
    fwd = np.array([math.sin(a), math.cos(a)])      # toward the road
    rt = np.array([fwd[1], -fwd[0]])
    B, trees = [], []
    base = hm["pad"]
    if hm["farm"]:
        B.append(building("house", *(c + fwd * 18), face, rng.uniform(10, 13), rng.uniform(14, 18), rng.uniform(3.2, 4.0),
                          rng.uniform(0.3, 0.45), WALLS[rng.integers(len(WALLS))], ROOFS[rng.integers(len(ROOFS))], base, 0.0))
        for kk in range(rng.integers(1, 3)):
            p = c - fwd * 10 + rt * (-25 + kk * 32)
            B.append(building("shed", *p, face + 180, rng.uniform(18, 26), rng.uniform(24, 40), rng.uniform(5.5, 7.5), 0.2,
                              METAL[rng.integers(len(METAL))], ROOFS[4], base, 0.0))
        for kk in range(int(rng.choice([0, 2, 3, 4]))):
            dia = rng.uniform(8, 12)
            B.append(building("bin", *(c - fwd * 30 + rt * (30 + kk * (dia + 1.5))), 0.0, dia, dia, dia * rng.uniform(1.0, 1.4),
                              0.35, (0.72, 0.74, 0.75), (0.64, 0.66, 0.68), base, 0.0))
    else:
        if hm["mobile"]:
            B.append(building("house", *c, face + 90, hm["fw"], hm["fl"], 2.9, 0.12,
                              [(0.92, 0.91, 0.88), (0.84, 0.86, 0.84), (0.80, 0.76, 0.66)][rng.integers(3)], ROOFS[4], base, 0.0))
        else:
            B.append(building("house", *c, face + rng.choice([0, 90]), hm["fw"], hm["fl"], rng.uniform(3.0, 4.2),
                              rng.uniform(0.35, 0.55), WALLS[rng.integers(len(WALLS))], ROOFS[rng.integers(len(ROOFS))], base, 0.0))
        if rng.random() < 0.6:
            p = c - fwd * rng.uniform(14, 22) + rt * rng.uniform(-14, 14)
            B.append(building("house", *p, face, rng.uniform(4, 8), rng.uniform(5, 10), rng.uniform(2.6, 3.4), 0.25,
                              METAL[rng.integers(len(METAL))], ROOFS[4], base, 0.0))
    for _ in range(rng.integers(3, 9)):
        p = c + fwd * rng.uniform(-30, 20) + rt * rng.uniform(-35, 35)
        if np.linalg.norm(p - c) > 12:
            trees.append((TREE_OAK if rng.random() < 0.65 else TREE_PINE, p, float(rng.uniform(0.7, 1.15))))
    return B, trees


def parking_meshes(tm, lots, ground):
    """Asphalt in front of the stores on the US routes, from the road edge to the store front."""
    for a, b, fw, face in lots:
        L = np.linalg.norm(b - a)
        if L < 4:
            continue
        u = (b - a) / L
        n = np.array([-u[1], u[0]])
        hw = fw / 2 + 4
        V, UV = [], []
        for q in (0.0, L):
            p = a + u * q
            h = ground(p[0], p[1]) + 0.08
            for o in (-hw, hw):
                r = p + n * o
                V.append([r[0], h, r[1]])
                UV.append([r[0] / 8, r[1] / 8])
        tm.add(tm.bucket(a[0], a[1], "roads"), M.M_PASPHALT, V, UV, [(0, 1, 2), (1, 3, 2)])


def drive_meshes(tm, homes):
    W.drive_meshes(tm, [dict(drive=h["drive"], drive_h=h["drive_h"]) for h in homes if "drive" in h])


# ------------------------------------------------------------------------------------------------------------ forest

def smooth_noise(shape, feature_cells, seed):
    g = np.random.default_rng(seed).random((shape[0] // feature_cells + 2, shape[1] // feature_cells + 2))
    return ndimage.zoom(g, feature_cells, order=1)[:shape[0], :shape[1]]


def lowland(dem):
    """The floodplain: ground whose surroundings (300 m opening) lie below REAL["lowland_m"]; none on the midwest map."""
    if REAL["lowland_m"] is None:
        return np.zeros(dem.shape, bool)
    base = ndimage.grey_opening(dem[::8, ::8], size=(31, 31))
    return ndimage.zoom((base < REAL["lowland_m"]).astype(np.float32), 8, order=1)[:cfg.GRID, :cfg.GRID] > 0.5


def forest_plan(forest, clear, water_edge, yards, dem, town):
    """Forest mask, where the canopy shell goes (not in the town, whose trees stand one by one), the pine share and the
    floodplain (dixie: bottomland forest, cypress round the lakes)."""
    f = ndimage.gaussian_filter(forest.astype(np.float32), 1.0) > 0.5
    f &= (clear.astype(np.float32) > 7) & (water_edge.astype(np.float32) > 3) & ~yards
    shell = ndimage.binary_erosion(f, iterations=2) & (clear.astype(np.float32) > SHELL_CLEAR_M) & ~town
    plain = lowland(dem)
    lo, hi = REAL["pine"]
    pine = np.where(plain, 0.04, lo + (hi - lo) * smooth_noise(forest.shape, 160, 3)).astype(np.float16)
    return f, shell, pine, plain


def edge_trees(f, shell, pine, plain, clear, water_kind, town, spacing=8.0):
    """Trees on a jittered grid where the forest shows but the shell does not: the front row of the woods along the
    roads (in front of the shell's edge), the forest edges within EDGE_TREES_M of a road, and the town's wooded lots
    and ravines."""
    cl = clear.astype(np.float32)
    band = f & ~shell & ((cl < EDGE_TREES_M) | town)
    lake = ndimage.binary_dilation(water_kind == 2, iterations=3)
    step = spacing / cfg.CELL
    gx = np.arange(0, cfg.GRID - 1, step)
    out = []
    for z0 in range(0, len(gx), 256):
        zz = gx[z0:z0 + 256]
        X, Z = np.meshgrid(gx, zz)
        X = X + rng.uniform(-0.45, 0.45, X.shape) * step
        Z = Z + rng.uniform(-0.45, 0.45, Z.shape) * step
        j, i = np.clip(Z.astype(int), 0, cfg.GRID - 1), np.clip(X.astype(int), 0, cfg.GRID - 1)
        m = band[j, i]
        j, i, X, Z = j[m], i[m], X[m], Z[m]
        u = rng.random(len(j))
        typ = np.where(u < pine[j, i].astype(np.float32), TREE_PINE, TREE_OAK)
        typ = np.where(lake[j, i] & plain[j, i] & (rng.random(len(j)) < 0.7), TREE_CYPRESS, typ)
        typ = np.where(rng.random(len(j)) < 0.08, TREE_SHRUB, typ)
        sc = rng.uniform(0.75, 1.3, len(j))
        for t, x, z, s in zip(typ, X * cfg.CELL, Z * cfg.CELL, sc):
            out.append((int(t), np.array([x, z]), float(s)))
    return out


def canopy_meshes(tm, graded, shell, pine, plain, clear):
    """The canopy shell, tile by tile on a 31 m grid: a roof at crown height over the shell (a grid cell with all four
    corners in the shell, or the half of it three corners span), split into pine and hardwood by the pine share, and a
    vertical wall round it from the ground to a ragged fringe of crowns above the roof (alpha-tested). (A roof that sloped to the ground
    over a grid cell read as a grassy hill from the road.)"""
    G = CANOPY_G
    cov = ndimage.uniform_filter(shell.astype(np.float32), G)[::G, ::G]
    cov = ndimage.gaussian_filter(cov, 0.6)
    gh = graded[::G, ::G]
    ht = np.where(plain[::G, ::G], 25.0, 21.0) * (0.85 + 0.3 * smooth_noise(gh.shape, 6, 9)) + rng.normal(0, 1.2, gh.shape)
    # near a road no roof corner is drawn within 16 m of a road edge, so the shell never spans a road (its corners are
    # 31 m apart), and the shell starts SHELL_CLEAR_M from the road
    cl = clear[::G, ::G].astype(np.float32)
    inside = (cov > 0.15) & (cl >= SHELL_CLEAR_M) & (cl >= 16.0)
    top = gh + ht
    foot = gh - 3.0                                   # the wall's foot, below the ground between grid samples
    pn = pine[::G, ::G].astype(np.float32) > 0.5
    per = (cfg.TILE_RES - 1) // G
    count = 0
    for r in range(cfg.TILES):
        for c in range(cfg.TILES):
            j0, i0 = r * per, c * per
            ins = inside[j0:j0 + per + 1, i0:i0 + per + 1]
            if not ins.any():
                continue
            hh, ff = top[j0:j0 + per + 1, i0:i0 + per + 1], foot[j0:j0 + per + 1, i0:i0 + per + 1]
            pp = pn[j0:j0 + per, i0:i0 + per]
            ny, nx = hh.shape
            X = (i0 + np.arange(nx)) * G * cfg.CELL
            Z = (j0 + np.arange(ny)) * G * cfg.CELL
            XX, ZZ = np.meshgrid(X, Z)
            V = np.stack([XX.ravel(), hh.ravel(), ZZ.ravel()], 1)
            UV = np.stack([XX.ravel() / CANOPY_M, ZZ.ravel() / CANOPY_M], 1)
            # cell corners: a (j, i), b (j+1, i), d (j, i+1), e (j+1, i+1)
            A, B, D, E = ins[:-1, :-1], ins[1:, :-1], ins[:-1, 1:], ins[1:, 1:]
            jj, ii = np.mgrid[0:ny - 1, 0:nx - 1]
            a = jj * nx + ii
            b, d, e = a + nx, a + 1, a + nx + 1
            full = A & B & D & E
            # triangles of the roof: two per full cell, one per cell with three corners in (the three it has)
            tris, tpine = [], []
            for sel, t in ((full, (a, b, d)), (full, (d, b, e)),
                           (~full & A & B & D, (a, b, d)), (~full & B & D & E, (d, b, e)),
                           (~full & A & B & E & ~D, (a, b, e)), (~full & A & D & E & ~B, (a, e, d))):
                tris.append(np.stack([t[0][sel], t[1][sel], t[2][sel]], 1))
                tpine.append(pp[sel])
            T = np.concatenate(tris)
            if not len(T):
                continue
            TP = np.concatenate(tpine)
            for mat, sel in ((M_CANOPY_PINE, TP), (M_CANOPY_HARD, ~TP)):
                if not sel.any():
                    continue
                used = np.unique(T[sel])
                remap = np.full(len(V), -1)
                remap[used] = np.arange(len(used))
                tm.add((c, r, "canopy"), mat, V[used], UV[used], remap[T[sel]])
            count += len(T)
            # the walls: every roof edge that only one roof triangle has, dropped to the foot, facing away from it
            edges = np.concatenate([T[:, [0, 1]], T[:, [1, 2]], T[:, [2, 0]]])
            third = np.concatenate([T[:, 2], T[:, 0], T[:, 1]])
            key = np.sort(edges, 1)
            _, first, n = np.unique(key[:, 0] * len(V) + key[:, 1], return_index=True, return_counts=True)
            k = first[n == 1]
            p, q, o = edges[k, 0], edges[k, 1], third[k]
            if not len(k):
                continue
            P0, P1, PO = V[p], V[q], V[o]
            along = P1 - P0
            out = np.cross(along, [0.0, 1.0, 0.0])
            out[:, 1] = 0
            flip = ((out * (P0 - PO)).sum(1) < 0)
            out[flip] *= -1
            F0 = np.stack([P0[:, 0], ff.ravel()[p], P0[:, 2]], 1)
            F1 = np.stack([P1[:, 0], ff.ravel()[q], P1[:, 2]], 1)
            m = len(k)
            T0, T1 = P0 + [0, CANOPY_FRINGE_M, 0], P1 + [0, CANOPY_FRINGE_M, 0]      # the crowns' fringe above the roof
            WV = np.concatenate([T0, T1, F1, F0])
            u0 = (P0[:, 0] + P0[:, 2]) / CANOPY_SIDE_M
            u1 = u0 + np.hypot(along[:, 0], along[:, 2]) / CANOPY_SIDE_M
            WUV = np.concatenate([np.stack([u0, np.ones(m)], 1), np.stack([u1, np.ones(m)], 1),
                                  np.stack([u1, np.zeros(m)], 1), np.stack([u0, np.zeros(m)], 1)])
            idx = np.arange(m)
            WT = np.concatenate([np.stack([idx, idx + m, idx + 2 * m], 1), np.stack([idx, idx + 2 * m, idx + 3 * m], 1)])
            tm.add((c, r, "canopy"), M_CANOPY_SIDE, WV, WUV, WT, want=np.concatenate([out, out]))
    return count


# --------------------------------------------------------------------------------------------------------- land cover

def field_patchwork(seed=7):
    """Per 10 m cover cell: which crop look a delta field has (tilled or young crop), on irregular blocks of 200-500 m."""
    g = np.random.default_rng(seed)
    out = np.zeros((NCOV, NCOV), np.uint8)
    bx = np.cumsum(g.uniform(20, 50, 400)).astype(int)
    bz = np.cumsum(g.uniform(15, 40, 600)).astype(int)
    cx = np.searchsorted(bx, np.arange(NCOV))
    cz = np.searchsorted(bz, np.arange(NCOV))
    pick = g.random((600, 400)) < 0.62
    out[:] = np.where(pick[np.ix_(cz, cx)], L_TILLED, L_YOUNG)
    return out


def landcover_tile(c, r, cov, fields, natchez, dem, clear, water_edge, kind, forest_f, yard_mask):
    R = cfg.SPLAT_RES
    x = c * cfg.TILE_M + (np.arange(R) + 0.5) * cfg.CELL
    z = r * cfg.TILE_M + (np.arange(R) + 0.5) * cfg.CELL
    X, Z = np.meshgrid(x, z)
    ci = np.clip((X / COVER_STEP).astype(int), 0, NCOV - 1)
    cj = np.clip((Z / COVER_STEP).astype(int), 0, NCOV - 1)
    wc = cov[cj, ci]
    town = natchez[cj, ci]
    sl = slice(r * (cfg.TILE_RES - 1), r * (cfg.TILE_RES - 1) + R)
    sc = slice(c * (cfg.TILE_RES - 1), c * (cfg.TILE_RES - 1) + R)
    h = dem[sl, sc]
    gy, gx = np.gradient(h, cfg.CELL)
    slope = np.hypot(gx, gy)
    we = water_edge[sl, sc].astype(np.float32)
    cl = clear[sl, sc].astype(np.float32)
    W = np.zeros((8, R, R), np.float32)
    cls = np.full((R, R), L_PASTURE, np.int8)
    cls[wc == WC_CROP] = fields[cj, ci][wc == WC_CROP]
    cls[(wc == WC_CROP) & (slope > 0.12)] = L_PASTURE
    cls[wc == WC_TREE] = L_FOREST
    cls[wc == WC_WETLAND] = L_WETLAND
    cls[(wc == WC_WATER) | (wc == WC_BARE)] = np.where(we[(wc == WC_WATER) | (wc == WC_BARE)] < 400, L_SAND, L_WETLAND)
    cls[(wc == WC_BUILT) & town] = L_LAWN
    for k in range(8):
        W[k] = cls == k
    # shrub: young pines and briars on cut-over land
    shrub = (wc == WC_SHRUB)[None]
    mix = np.zeros((8, 1, 1), np.float32)
    mix[L_PASTURE], mix[L_FOREST] = 0.6, 0.4
    W = np.where(shrub, mix, W)
    # forest that the edge cleaning took away (yards, verges) is not forest floor
    fo = forest_f[sl, sc]
    W[L_FOREST] *= np.clip(fo.astype(np.float32) * 1.5, 0.3, 1)
    # town: lawn with a little bare ground
    tmix = np.zeros((8, 1, 1), np.float32)
    tmix[L_LAWN], tmix[L_DIRT], tmix[L_FOREST] = 0.8, 0.12, 0.08
    W = np.where((town & (wc == WC_BUILT))[None], tmix, W)
    # verges: mown grass; under the road surface: dirt
    verge = np.clip((14.0 - cl) / 4.0, 0, 1)[None]
    vmix = np.zeros((8, 1, 1), np.float32)
    vmix[L_LAWN], vmix[L_PASTURE], vmix[L_DIRT] = 0.6, 0.3, 0.1
    W = W * (1 - verge) + vmix * verge
    under = np.clip((1.5 - cl) / 1.5, 0, 1)[None]
    umix = np.zeros((8, 1, 1), np.float32)
    umix[L_DIRT] = 1
    W = W * (1 - under) + umix * under
    # banks: sand at the water, then grass and brush
    bank = np.clip((25.0 - we) / 15.0, 0, 1)[None]
    bmix = np.zeros((8, 1, 1), np.float32)
    bmix[L_PASTURE], bmix[L_FOREST], bmix[L_WETLAND] = 0.4, 0.3, 0.3
    W = W * (1 - bank) + bmix * bank
    sand = np.clip((5.0 - we) / 5.0, 0, 1)[None]
    smix = np.zeros((8, 1, 1), np.float32)
    smix[L_SAND] = 1
    W = W * (1 - sand) + smix * sand
    # yards
    y = yard_mask[sl, sc][None].astype(np.float32)
    ymix = np.zeros((8, 1, 1), np.float32)
    ymix[L_LAWN], ymix[L_DIRT] = 0.8, 0.2
    W = W * (1 - y) + ymix * y
    W = ndimage.gaussian_filter(W, (0, 0.6, 0.6))
    W /= np.maximum(W.sum(0, keepdims=True), 1e-6)
    return W


def write_tiles(dem, datum, args):
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
            Wt = landcover_tile(c, r, *args)
            for half in range(2):
                img = (np.clip(Wt[4 * half:4 * half + 4], 0, 1) * 255 + 0.5).astype(np.uint8).transpose(1, 2, 0)
                Image.fromarray(img[::-1], "RGBA").save(out / "splat" / f"s_{c}_{r}_{half}.png")
        log(f"tiles row {r} written")
    return hmin, hmax


def write_objects(trees, buildings, lines, datum):
    out = cfg.UNITY_DATA
    tiles = {}
    for typ, p, sc in trees:
        if not (0 <= p[0] < cfg.SIZE_M and 0 <= p[1] < cfg.SIZE_M):
            continue
        c = int(np.clip(p[0] // cfg.TILE_M, 0, cfg.TILES - 1))
        r = int(np.clip(p[1] // cfg.TILE_M, 0, cfg.TILES - 1))
        tiles.setdefault((c, r), []).append((typ, p[0], p[1], sc, rng.uniform(0, 360)))
    (out / "trees").mkdir(parents=True, exist_ok=True)
    for (c, r), lst in tiles.items():
        a = np.array(lst, np.float32)
        a[:, 1] = (a[:, 1] - c * cfg.TILE_M) / cfg.TILE_M
        a[:, 2] = (a[:, 2] - r * cfg.TILE_M) / cfg.TILE_M
        (out / "trees" / f"t_{c}_{r}.bin").write_bytes(a.tobytes())
    half = cfg.SIZE_M / 2
    fj = [dict(kind=b["kind"], x=float(b["x"] - half), y=float(b["y"] - datum), z=float(b["z"] - half), yaw=float(b["yaw"]),
               w=float(b["w"]), l=float(b["l"]), h=float(b["h"]), roof=float(b["roof"]), wall=list(b["wall"]),
               roofc=list(b["roofc"])) for b in buildings]
    json.dump(dict(items=fj), open(out / "buildings.json", "w"))
    lj = [dict(three=ln["three"], poles=[round(v, 2) for p in ln["poles"] for v in (p[0] - half, p[1] - datum, p[2] - half)])
          for ln in lines]
    json.dump(dict(lines=lj), open(out / "power.json", "w"))


def write_skirt(dem, datum, cov_full):
    """The horizon skirt from the 3DEP 1 arc-second tiles, three map widths across, dropped 25 m inside the map and
    meeting the map's own edge heights; its land cover from WorldCover, coarse."""
    tiles = GeoTiles(sorted((cfg.RAW / "dem1").glob("*.tif")), nodata=-999999.0)
    n = 2049
    span = 3 * cfg.SIZE_M
    step = span / (n - 1)
    sk = np.empty((n, n), np.float32)
    from pyproj import Transformer
    t = Transformer.from_crs(cfg.EPSG, 4269, always_xy=True)
    xs = cfg.E0 - cfg.SIZE_M + np.arange(n) * step
    for j0 in range(0, n, 256):
        zz = cfg.N0 - cfg.SIZE_M + np.arange(j0, min(n, j0 + 256)) * step
        X, Z = np.meshgrid(xs, zz)
        lon, lat = t.transform(X.ravel(), Z.ravel())
        sk[j0:j0 + 256] = tiles.sample(np.asarray(lon), np.asarray(lat)).reshape(X.shape)
    sk[~np.isfinite(sk)] = np.nanmedian(sk)
    pos = np.arange(n) * step - cfg.SIZE_M
    inside = (pos > 1.0) & (pos < cfg.SIZE_M - 1.0)
    I, J = np.meshgrid(inside, inside)
    sk[I & J] -= 25.0
    ji = np.flatnonzero((pos >= -step) & (pos <= cfg.SIZE_M + step))
    edge = (np.abs(pos) < step) | (np.abs(pos - cfg.SIZE_M) < step)
    JJ, II = np.meshgrid(ji, ji, indexing="ij")
    m = edge[JJ] | edge[II]
    JJ, II = JJ[m], II[m]
    q = np.stack([np.clip(pos[II], 0, cfg.SIZE_M), np.clip(pos[JJ], 0, cfg.SIZE_M)], 1)
    sk[JJ, II] = sample(dem, q) - 0.6
    lo, hi = float(np.floor(sk.min() - datum - 1)), float(np.ceil(sk.max() - datum + 1))
    (cfg.UNITY_DATA / "skirt.raw").write_bytes(np.clip((sk - datum - lo) / (hi - lo) * 65535, 0, 65535).astype("<u2").tobytes())
    # land cover: WorldCover sampled at the skirt texels
    R = 2048
    wc = GeoTiles(sorted((cfg.RAW / "worldcover").glob("*.tif")))
    t4 = Transformer.from_crs(cfg.EPSG, 4326, always_xy=True)
    xs = cfg.E0 - cfg.SIZE_M + (np.arange(R) + 0.5) / R * span
    cls = np.empty((R, R), np.int32)
    for j0 in range(0, R, 256):
        zz = cfg.N0 - cfg.SIZE_M + (np.arange(j0, min(R, j0 + 256)) + 0.5) / R * span
        X, Z = np.meshgrid(xs, zz)
        lon, lat = t4.transform(X.ravel(), Z.ravel())
        cls[j0:j0 + 256] = wc.sample(np.asarray(lon), np.asarray(lat), order=0, fill=WC_TREE).reshape(X.shape)
    Wk = np.zeros((8, R, R), np.float32)
    Wk[L_FOREST] = cls == WC_TREE
    Wk[L_PASTURE] = np.isin(cls, (WC_GRASS, WC_SHRUB, WC_BUILT))
    Wk[L_TILLED] = cls == WC_CROP
    Wk[L_WETLAND] = np.isin(cls, (WC_WETLAND, WC_WATER))
    Wk[L_SAND] = cls == WC_BARE
    Wk = ndimage.gaussian_filter(Wk, (0, 0.6, 0.6))
    Wk /= np.maximum(Wk.sum(0, keepdims=True), 1e-6)
    for half in range(2):
        img = (Wk[4 * half:4 * half + 4] * 255 + 0.5).astype(np.uint8).transpose(1, 2, 0)
        Image.fromarray(img[::-1], "RGBA").save(cfg.UNITY_DATA / "splat" / f"skirt_{half}.png")
    return dict(res=n, size=span, origin=-cfg.SIZE_M - cfg.SIZE_M / 2, hmin=lo, hmax=hi)


# ------------------------------------------------------------------------------------------------------------ spawns

def spawn_points(profs, datum):
    """Where the car can start (keys 1-9): downtown first, then the big river's bridge (dixie), the highways listed in
    REAL["spawns"], and river bridges."""
    half = cfg.SIZE_M / 2

    def at(pr, i, name):
        i = int(np.clip(i, 1, len(pr["s"]) - 2))
        t, _ = M.frames(pr["pts"][i - 1:i + 2])
        return dict(name=name, x=float(pr["pts"][i, 0] - half), y=float(pr["h"][i] - datum + 0.3),
                    z=float(pr["pts"][i, 1] - half), yaw=float(math.degrees(math.atan2(t[1, 0], t[1, 1]))))
    out = []
    # downtown: the town street nearest the town's centre (Main Street in Natchez)
    best = None
    for pr in profs:
        if pr["cls"] == "paved" and (cfg.MAP != "dixie" or "Main St" in pr["name"]):
            d = np.hypot(pr["pts"][:, 0] - DOWNTOWN[0], pr["pts"][:, 1] - DOWNTOWN[1])
            i = int(np.argmin(d))
            if best is None or d[i] < best[0]:
                best = (d[i], pr, i)
    if best:
        out.append(at(best[1], best[2], f"{REAL['town']}, {best[1]['name'] or 'downtown'}"))
    for pr in profs:
        for s0, s1, deck, river, k in pr["spans"]:
            if s1 - s0 > 600:        # the Mississippi: start on the Louisiana side, facing the bridge
                i0, i1 = np.searchsorted(pr["s"], [s0 - 400, s1 + 400])
                west = i0 if pr["pts"][min(i0, len(pr["s"]) - 1), 0] < pr["pts"][min(i1, len(pr["s"]) - 1), 0] else i1
                sp = at(pr, west, "Natchez-Vidalia bridge (Louisiana side)")
                if west == i1:
                    sp["yaw"] = (sp["yaw"] + 180.0) % 360.0
                out.append(sp)
    seen = set()
    for key, label in REAL["spawns"]:
        cand = [pr for pr in profs if pr["cls"] == "state" and key in pr["name"] and len(pr["s"]) > 200]
        if cand and label not in seen:
            pr = max(cand, key=lambda p: p["s"][-1])
            out.append(at(pr, len(pr["s"]) // 3, label))
            seen.add(label)
    for pr in profs:
        for s0, s1, deck, river, k in pr["spans"]:
            if river and s1 - s0 <= 600 and len(out) < 9:
                i = int(np.searchsorted(pr["s"], s0 - 120))
                out.append(at(pr, i, f"{pr['name']} river bridge"))
                break
        if len(out) >= 9:
            break
    return out[:9]


# ----------------------------------------------------------------------------------------------------------- preview

COLOURS = np.array([(96, 124, 52), (92, 80, 66), (88, 104, 60), (52, 66, 34), (90, 120, 50), (70, 80, 52),
                    (192, 176, 144), (146, 122, 90)]) / 255.0


def preview(dem, profs, buildings, kind, path, title, window=None, step=8):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.colors import LightSource
    R = cfg.SPLAT_RES // step
    img = np.zeros((cfg.TILES * R, cfg.TILES * R, 3))
    for r in range(cfg.TILES):
        for c in range(cfg.TILES):
            for half in range(2):
                a = np.asarray(Image.open(cfg.UNITY_DATA / "splat" / f"s_{c}_{r}_{half}.png"))[::-1][::step, ::step] / 255.0
                img[r * R:(r + 1) * R, c * R:(c + 1) * R] += np.tensordot(a, COLOURS[4 * half:4 * half + 4], 1)
    h = dem[::step, ::step][:cfg.TILES * R, :cfg.TILES * R]
    shade = LightSource(315, 40).hillshade(h, vert_exag=4, dx=cfg.CELL * step, dy=cfg.CELL * step)
    img = img * (0.5 + 0.65 * shade[..., None])
    w = kind[::step, ::step][:cfg.TILES * R, :cfg.TILES * R]
    img[np.isin(w, (1, 3))] = (0.45, 0.38, 0.28)
    img[w == 2] = (0.18, 0.28, 0.34)
    fig, ax = plt.subplots(figsize=(22, 22))
    ax.imshow(np.clip(img, 0, 1), origin="lower", extent=(0, cfg.SIZE_M, 0, cfg.SIZE_M))
    for pr in profs:
        ax.plot(pr["pts"][:, 0], pr["pts"][:, 1], color={"state": "#e02020", "paved": "#202020", "gravel": "#e8e0d0"}[pr["cls"]],
                lw={"state": 1.6, "paved": 0.9, "gravel": 0.5}[pr["cls"]] * (3 if window else 1))
    if buildings:
        b = np.array([[q["x"], q["z"]] for q in buildings])
        ax.plot(b[:, 0], b[:, 1], "s", ms=1.5 if not window else 3, color="#f0f0f0", mec="#303030", mew=0.3)
    if window:
        ax.set_xlim(window[0], window[1])
        ax.set_ylim(window[2], window[3])
    else:
        ax.set_xlim(0, cfg.SIZE_M)
        ax.set_ylim(0, cfg.SIZE_M)
    ax.set_title(title)
    fig.savefig(path, dpi=55, bbox_inches="tight")
    plt.close(fig)


# -------------------------------------------------------------------------------------------------------------- main

def main():
    streams = json.load(open(cfg.OUT / "streams.json"))
    for st in streams:
        st["_line"] = LineString(st["pts"])
    net = json.load(open(cfg.OUT / "network.json"))
    nodes, degree = np.array(net["nodes"]), np.array(net["degree"])
    dem = np.load(cfg.OUT / "dem.npy")
    wz = np.load(cfg.OUT / "water.npz")
    kind, level = wz["kind"], wz["level"]
    cov = np.load(cfg.OUT / "cover.npy")
    natchez = np.load(cfg.OUT / "town.npy")
    forest = np.load(cfg.OUT / "forest.npy").astype(np.float32) > 0.5
    datum = float(np.round(np.median(dem[::20, ::20]) / 10) * 10)
    log(f"datum {datum:.0f} m")

    P.ADAPTIVE_BLEND = True
    # roads that cross a lake or a river polygon (causeways, the dam roads) ride 1.5 m above its water
    over_water = np.where(kind > 0, np.maximum(dem, level + 1.5), dem).astype(np.float32)
    profs, _ = P.all_profiles(net, over_water, streams)
    del over_water
    for ln in P.summary(profs):
        log(ln)
    seeds = W.road_seeds(profs)
    jn = [k for k in range(len(nodes)) if degree[k] >= 3]
    graded, clear, flatmask = W.grade_roads(dem, seeds, nodes[jn], np.full(len(jn), 22.0))
    del seeds
    log("roads graded")
    graded, poly_edge = carve_water(graded, kind, level, flatmask)
    ribbons = outside_water([s for s in streams if s["width"] > 0 and not s.get("polygon")], kind)
    graded, rib_edge = W.carve_streams(graded, ribbons, flatmask)
    water_edge = np.minimum(poly_edge, rib_edge)
    del poly_edge, rib_edge
    log("water carved")

    flood = lowland(dem) if REAL["lowland_m"] is not None else np.ones(dem.shape, bool)
    taken = Taken()
    town_b, town_trees, lots = town(profs, natchez, clear, water_edge, graded, taken)
    homes = countryside(profs, natchez, clear, water_edge, graded, taken, flood)
    del flood
    grade_yards(graded, homes, profs)
    buildings, trees = list(town_b), list(town_trees)
    for hm in homes:
        b, t = home_buildings(hm)
        buildings += b
        trees += t
    yards = np.zeros(graded.shape, bool)
    for hm in homes:
        j, i = cells(hm["c"][0], hm["c"][1])
        r = 6 if hm["farm"] else 4
        yards[max(0, j - r):j + r + 1, max(0, i - r):i + r + 1] = True
    for b in town_b:
        j, i = cells(b["x"], b["z"])
        yards[max(0, j - 2):j + 3, max(0, i - 2):i + 3] = True

    def ground(x, z):
        return float(sample(graded, np.array([[x, z]]))[0])

    town_cells = natchez[np.clip((np.arange(cfg.GRID) * cfg.CELL / COVER_STEP).astype(int), 0, NCOV - 1)][
        :, np.clip((np.arange(cfg.GRID) * cfg.CELL / COVER_STEP).astype(int), 0, NCOV - 1)]
    f, shell, pine, plain = forest_plan(forest, clear, water_edge, yards, graded, town_cells)
    trees += edge_trees(f, shell, pine, plain, clear, kind, town_cells)
    del town_cells
    log(f"{len(trees):,} trees; canopy shell over {shell.mean() * 100:.0f} % of the map")
    lines = W.power_lines(profs, [dict(prof_k=h["prof_k"]) for h in homes], clear, ground)
    log(f"{len(lines)} power lines, {sum(len(l['poles']) for l in lines):,} poles")

    tm = M.TileMeshes(datum)
    pads = M.build_roads(tm, profs, degree, nodes)
    drive_meshes(tm, homes)
    parking_meshes(tm, lots, ground)
    nb = M.build_bridges(tm, profs, ground)
    nw = M.build_water(tm, ribbons) + water_meshes(tm, kind, level)
    ncan = canopy_meshes(tm, graded, shell, pine, plain, clear)
    log(f"meshes: {pads} junction pads, {nb} bridges, {nw} water pieces, {ncan:,} canopy triangles")

    if cfg.UNITY_DATA.exists():
        shutil.rmtree(cfg.UNITY_DATA)
    cfg.UNITY_DATA.mkdir(parents=True)
    log(f"mesh vertices: {tm.write(cfg.UNITY_DATA / 'meshes')}")
    fields = field_patchwork()
    hmin, hmax = write_tiles(graded, datum, (cov, fields, natchez, graded, clear, water_edge, kind, f, yards))
    write_objects(trees, buildings, lines, datum)
    skirt = write_skirt(graded, datum, cov)
    spawns = spawn_points(profs, datum)
    meta = dict(sizeM=cfg.SIZE_M, tiles=cfg.TILES, tileM=cfg.TILE_M, tileRes=cfg.TILE_RES, splatRes=cfg.SPLAT_RES,
                datum=datum, hmin=hmin, hmax=hmax, layers=LAYERS, materials=M.MAT, skirt=skirt,
                scenarioOriginKm=(cfg.SIZE_M / 2) / 1000.0 - (cfg.SCENARIO_MILES * cfg.MILE / 2) / 1000.0,
                spawns=spawns, routes=W.routes(profs, datum, step=10.0), title=cfg.SPEC["title"],
                source=dict(terrain="USGS 3DEP 1/3 arc-second (lidar-derived), resampled to 7.86 m",
                            streams="USGS NHDPlus High Resolution", water="USGS NHDPlus HR areas and waterbodies, wet where the lidar is",
                            roads="USGS National Transportation Dataset, generalised",
                            landcover="ESA WorldCover 2021 v200 (10 m)",
                            centre_utm15n=[cfg.E0 + cfg.SIZE_M / 2, cfg.N0 + cfg.SIZE_M / 2]))
    json.dump(meta, open(cfg.UNITY_DATA / "meta.json", "w"), indent=1)
    np.save(cfg.OUT / "dem_final.npy", graded)
    cfg.PREVIEW.mkdir(parents=True, exist_ok=True)
    preview(graded, profs, buildings, kind, cfg.PREVIEW / "landcover.png",
            f"{cfg.SPEC['title']}: land cover (WorldCover on the 3DEP hillshade), roads, buildings")
    preview(graded, profs, buildings, kind, cfg.PREVIEW / "town.png",
            REAL["town"] + (" and the bridge to Vidalia" if cfg.MAP == "dixie" else ""),
            window=(DOWNTOWN[0] - 5250, DOWNTOWN[0] + 3750, DOWNTOWN[1] - 4500, DOWNTOWN[1] + 4500), step=2)
    log("done")


if __name__ == "__main__":
    main()

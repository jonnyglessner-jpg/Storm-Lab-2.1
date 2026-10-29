"""Stage 1 of a real-data map (dixie, midwest): the real ground, its water and its cover.

- DEM: the USGS 3DEP 1/3 arc-second tiles resampled (bilinear) onto the map grid (GRID x GRID, row 0 = south).
- Cleanup: on flat ground the narrow fills of the real roads and railways are clipped as on the plains map
  (terrain.clean); in hills, where the same filter would shave real knife-edge ridges, the ground is kept. Dixie: the
  Mississippi River levees are kept everywhere (the delta's horizon); NHD has no levee lines there, so they are found
  in the DEM (ridges 3.5 m and more above the floodplain, 1.5 km and longer).
- Towns: every built-up area in WorldCover except the map's one town (Natchez, Effingham) is smoothed into the land
  around it. The town keeps its ground; roadnet.py keeps its streets and places.py lays out its buildings.
- Water: NHD area polygons (rivers) and waterbodies (lakes, reservoirs, ponds of 0.4 ha and up), wet where the lidar's
  hydro-flattened surface says there is water at the time of the survey (sand bars stay dry ground). Dixie: the
  Mississippi fills its polygon (high water, the storm season). Each piece of water gets a level from that flattened
  surface.
- Streams: NHDPlus HR flowlines draining 6 km2 or more, moved onto the DEM's channel as on the plains map; rivers are
  wider and always bridged. Dixie: the Mississippi is in the list too (kind "river", polygon = true), so the road tools
  see its crossing, but its water is the polygon, not a ribbon.
- Land cover: ESA WorldCover 2021 (10 m) resampled onto a 10 m map grid; forest fraction per height cell.

Writes build/dem_raw.npy, dem.npy, water.npz, streams.json, cover.npy (10 m), forest.npy, town.npy, levees.json.
Run after fetch.py: python -I tools/testmap/real/land.py --map dixie|midwest [--fresh]
(--fresh: mosaic and resample again, not reuse)
"""
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import _paths  # noqa: F401,E402
import numpy as np
import tifffile
from PIL import Image, ImageDraw
from pyproj import Transformer
from scipy import ndimage

import map_config as cfg
from terrain import clean, resample, sample, snap, water_level

T0 = time.time()
REAL = cfg.SPEC["real"]
BIG = REAL["big_river"]                           # the Mississippi (dixie), or None
DOWNTOWN = (cfg.SIZE_M / 2 + REAL["downtown"][0], cfg.SIZE_M / 2 + REAL["downtown"][1])   # the town's centre
COVER_STEP = 10.0
NCOV = int(round(cfg.SIZE_M / COVER_STEP))       # 8047
# WorldCover classes
WC_TREE, WC_SHRUB, WC_GRASS, WC_CROP, WC_BUILT, WC_BARE, WC_WATER, WC_WETLAND = 10, 20, 30, 40, 50, 60, 80, 90
RIVER_DA_KM2 = 500.0          # named streams draining more than this are rivers: wide, sand bars, always bridged
CREEK_DA_KM2 = 40.0
MIN_DA_KM2 = 6.0
POND_KM2 = 0.004
SIDE_CHANNELS = ("The Canal", "The Narrows")      # of the Mississippi, inside its polygon


def log(msg):
    print(f"[{time.time() - T0:6.0f} s] {msg}", flush=True)


# ----------------------------------------------------------------------------------------------------------- rasters

class GeoTiles:
    """Geographic GeoTIFF tiles (pixel is area), sampled at lon / lat."""

    def __init__(self, paths, nodata=None):
        self.t = []
        for p in paths:
            with tifffile.TiffFile(p) as tf:
                page = tf.pages[0]
                a = page.asarray()
                sx, sy, _ = page.tags["ModelPixelScaleTag"].value
                tie = page.tags["ModelTiepointTag"].value
            if nodata is not None and a.dtype.kind == "f":
                a = np.where(a == nodata, np.nan, a)
            self.t.append((a, tie[3], tie[4], sx, sy))

    def sample(self, lon, lat, order=1, fill=np.nan):
        out = np.full(lon.shape, fill, np.float32 if order else np.int32)
        done = np.zeros(lon.shape, bool)
        for a, x0, y0, sx, sy in self.t:
            c = (lon - x0) / sx - 0.5
            r = (y0 - lat) / sy - 0.5
            m = (c >= 0) & (r >= 0) & (c <= a.shape[1] - 1) & (r <= a.shape[0] - 1) & ~done
            if not m.any():
                continue
            done |= m
            if order:
                out[m] = ndimage.map_coordinates(a, [r[m], c[m]], order=1, mode="nearest")
            else:
                out[m] = a[np.rint(r[m]).astype(int), np.rint(c[m]).astype(int)]
        return out


def grid_lonlat(n, step, rows, crs=4269):
    """lon, lat of map-grid points (i * step, j * step) for j in rows."""
    t = Transformer.from_crs(cfg.EPSG, crs, always_xy=True)
    x = cfg.E0 + np.arange(n) * step
    z = cfg.N0 + np.asarray(rows) * step
    X, Z = np.meshgrid(x, z)
    lon, lat = t.transform(X.ravel(), Z.ravel())
    return np.asarray(lon).reshape(X.shape), np.asarray(lat).reshape(X.shape)


def mosaic():
    tiles = GeoTiles(sorted((cfg.RAW / "dem13").glob("*.tif")), nodata=-999999.0)
    dem = np.empty((cfg.GRID, cfg.GRID), np.float32)
    for j0 in range(0, cfg.GRID, 512):
        rows = np.arange(j0, min(j0 + 512, cfg.GRID))
        lon, lat = grid_lonlat(cfg.GRID, cfg.CELL, rows)
        dem[rows] = tiles.sample(lon, lat)
    bad = ~np.isfinite(dem) | (dem < -20) | (dem > 1000)
    if bad.any():
        log(f"filling {bad.sum()} nodata samples")
        idx = ndimage.distance_transform_edt(bad, return_distances=False, return_indices=True)
        dem = dem[tuple(idx)]
    return dem


def worldcover():
    wc = GeoTiles(sorted((cfg.RAW / "worldcover").glob("*.tif")))
    cov = np.zeros((NCOV, NCOV), np.uint8)
    for j0 in range(0, NCOV, 1024):
        rows = np.arange(j0, min(j0 + 1024, NCOV))
        # cell centres of the 10 m grid
        t = Transformer.from_crs(cfg.EPSG, 4326, always_xy=True)
        x = cfg.E0 + (np.arange(NCOV) + 0.5) * COVER_STEP
        z = cfg.N0 + (rows + 0.5) * COVER_STEP
        X, Z = np.meshgrid(x, z)
        lon, lat = t.transform(X.ravel(), Z.ravel())
        cov[rows] = wc.sample(np.asarray(lon).reshape(X.shape), np.asarray(lat).reshape(X.shape), order=0, fill=0).astype(np.uint8)
    return cov


def to_cells(a, step):
    """A 10 m raster resampled (bilinear) onto the height grid."""
    zoom = cfg.CELL / step
    idx = (np.arange(cfg.GRID) * zoom - 0.5).clip(0, a.shape[0] - 1)
    out = np.empty((cfg.GRID, cfg.GRID), np.float32)
    for j0 in range(0, cfg.GRID, 1024):
        jj = idx[j0:j0 + 1024]
        out[j0:j0 + 1024] = ndimage.map_coordinates(a.astype(np.float32), np.meshgrid(jj, idx, indexing="ij"), order=1)
    return out


def rasterise(polys, step, n, value=1):
    """Polygons (lists of rings in the map frame) burnt into an n x n uint8 raster of cell size step, row 0 = south."""
    img = Image.new("L", (n, n), 0)
    d = ImageDraw.Draw(img)
    for rings in polys:
        for k, ring in enumerate(rings):
            pts = [(x / step, z / step) for x, z in ring]
            if len(pts) >= 3:
                d.polygon(pts, fill=value if k == 0 else 0)
    return np.asarray(img)


def burn_lines(lines, step, n, width_m):
    img = Image.new("L", (n, n), 0)
    d = ImageDraw.Draw(img)
    w = max(1, int(round(width_m / step)))
    for ln in lines:
        pts = [(x / step, z / step) for x, z in ln]
        if len(pts) >= 2:
            d.line(pts, fill=1, width=w)
    return np.asarray(img).astype(bool)


# ------------------------------------------------------------------------------------------------------- the ground

def relief(dem, sigma_m=120.0):
    s = sigma_m / cfg.CELL
    m = ndimage.gaussian_filter(dem, s)
    v = ndimage.gaussian_filter(dem * dem, s) - m * m
    return np.sqrt(np.maximum(v, 0))


def ground(dem, levee):
    """Clean the narrow man-made relief where the land is flat; keep the hills and the levees as they are."""
    r = relief(dem)
    flat = np.clip((5.0 - r) / 3.0, 0, 1).astype(np.float32)        # 1 below 2 m local relief, 0 above 5 m
    flat = ndimage.gaussian_filter(flat, 4)
    soft = ndimage.gaussian_filter(dem, 1.0)
    out = soft * (1 - flat) + clean(dem) * flat
    keep = ndimage.gaussian_filter(levee.astype(np.float32), 3)
    out = out * (1 - keep) + soft * keep
    log(f"cleaned: {flat.mean() * 100:.0f} % of the map treated as flat; levees kept on {levee.mean() * 100:.2f} %")
    return out.astype(np.float32)


def town_mask(cov, water):
    """The town (Natchez, Effingham): where the streets are town-dense (USGS transportation, interstates left out: 5 km
    of road a km2 or more over 500 m, or 3 km where WorldCover is built up), connected to downtown; dixie: only on the
    Mississippi side of the river. (Built-up alone is not enough: WorldCover counts an interstate's pavement and
    median as built up, and the town would run out along it.)"""
    built = ndimage.uniform_filter((cov == WC_BUILT).astype(np.float32), 31) > 0.12     # 300 m windows, 12 % built
    img = Image.new("L", (NCOV, NCOV), 0)
    d = ImageDraw.Draw(img)
    for r in json.load(open(cfg.RAW / "tran_roads.json")):
        if r.get("tnmfrc") == 1 or r.get("interstate"):
            continue
        for ln in r["lines"]:
            if len(ln) >= 2:
                d.line([(x / COVER_STEP, z / COVER_STEP) for x, z in ln], fill=1, width=1)
    km_per_km2 = ndimage.uniform_filter(np.asarray(img).astype(np.float32), 51) * (1000.0 / COVER_STEP)
    dens = (km_per_km2 > 5.0) | (built & (km_per_km2 > 3.0))
    lab, n = ndimage.label(dens)
    ci, cj = int(DOWNTOWN[0] / COVER_STEP), int(DOWNTOWN[1] / COVER_STEP)
    near = lab[cj - 60:cj + 61, ci - 60:ci + 61]
    ids = [k for k in np.unique(near) if k]
    town = np.isin(lab, ids)
    # cut it at the river: only what lies east of the water at each row
    if BIG:
        yy, xx = np.nonzero(town)
        rx = river_x(water, yy * COVER_STEP)
        town[yy[xx * COVER_STEP < rx], xx[xx * COVER_STEP < rx]] = False
    lab, _ = ndimage.label(ndimage.binary_dilation(town, iterations=8))
    town = lab == lab[cj, ci] if lab[cj, ci] else lab == np.argmax(np.bincount(lab.ravel())[1:]) + 1
    log(f"{REAL['town']}: {town.sum() * COVER_STEP ** 2 / 1e6:.1f} km2")
    return town


def river_x(water_kind, z):
    """East edge of the Mississippi (kind 1) at map z (array), metres; 0 where the river is not found in that row."""
    j = np.clip((np.asarray(z) / cfg.CELL).astype(int), 0, cfg.GRID - 1)
    rows = {}
    out = np.zeros(len(j))
    for k, jj in enumerate(j):
        if jj not in rows:
            w = np.flatnonzero(water_kind[jj] == 1)
            rows[jj] = (w.max() * cfg.CELL) if len(w) else 0.0
        out[k] = rows[jj]
    return out


def flatten_towns(dem, cov, keep):
    """Every other town is smoothed into the land around it (plains terrain.flatten_towns, 10 m cover)."""
    dev = ((cov == WC_BUILT) & ~keep).astype(np.float32)
    town = ndimage.uniform_filter(dev, 25) > 0.10
    town = ndimage.binary_dilation(town, iterations=10)
    w = to_cells(ndimage.gaussian_filter(town.astype(np.float32), 8), COVER_STEP)
    big = ndimage.grey_closing(dem, size=(15, 15))
    filled = np.minimum(big, dem + 3.0)
    smooth = ndimage.gaussian_filter(filled, 50.0 / cfg.CELL)
    log(f"other towns: {town.mean() * 100:.1f} % of the map smoothed")
    return (dem * (1 - w) + smooth * w).astype(np.float32)


def detect_levees(dem):
    """The Mississippi River levees, which NHD does not carry here: ridges 3.5 m or more above the ground within
    ~150 m either side, on the floodplain (ground below 32 m), in pieces at least 1.5 km long. Returns the levee
    mask on the height grid (crest and slopes) and its centre lines in the map frame."""
    k = 4
    d = dem[: (cfg.GRID // k) * k, : (cfg.GRID // k) * k].reshape(cfg.GRID // k, k, cfg.GRID // k, k).max(axis=(1, 3))
    step = cfg.CELL * k
    base = ndimage.grey_opening(d, size=(9, 9))                      # ground under anything narrower than ~280 m
    tophat = d - base
    plain = ndimage.grey_opening(d, size=(41, 41)) < 32.0
    ridge = (tophat > 3.5) & plain
    lab, n = ndimage.label(ridge, structure=np.ones((3, 3)))
    keep = np.zeros(n + 1, bool)
    for i, sl in enumerate(ndimage.find_objects(lab), start=1):
        h = (sl[0].stop - sl[0].start) * step
        w = (sl[1].stop - sl[1].start) * step
        keep[i] = max(h, w) >= 1500.0
    lev = keep[lab]
    lev = ndimage.binary_closing(lev, iterations=3) & (tophat > 1.0)
    lines = []
    lab2, n2 = ndimage.label(lev, structure=np.ones((3, 3)))
    for i, sl in enumerate(ndimage.find_objects(lab2), start=1):
        jj, ii = np.nonzero(lab2[sl] == i)
        if len(jj) < 10:
            continue
        # order the crest cells along the component's long axis: a centre line good enough for a preview
        x = (ii + sl[1].start + 0.5) * step
        z = (jj + sl[0].start + 0.5) * step
        u = np.array([x - x.mean(), z - z.mean()])
        ax = np.linalg.svd(u, full_matrices=False)[0][:, 0]
        t = ax @ u
        o = np.argsort(t)
        bins = np.arange(t.min(), t.max() + 200, 200)
        idx = np.digitize(t[o], bins)
        pts = [[float(x[o][idx == b].mean()), float(z[o][idx == b].mean())] for b in np.unique(idx)]
        if len(pts) >= 2:
            lines.append(pts)
    full = ndimage.zoom(ndimage.binary_dilation(lev, iterations=2).astype(np.uint8), k, order=0)
    mask = np.zeros((cfg.GRID, cfg.GRID), bool)
    mask[:full.shape[0], :full.shape[1]] = full[:cfg.GRID, :cfg.GRID] > 0
    log(f"levees: {len(lines)} pieces, {sum(np.linalg.norm(np.diff(np.array(l), axis=0), axis=1).sum() for l in lines) / 1000:.0f} km")
    return mask, lines


# --------------------------------------------------------------------------------------------------------- water

def polys_of(items, keep):
    """NHD polygon records -> list of ring lists (exterior first), for the records keep() accepts."""
    out = []
    for it in items:
        if keep(it) and it["kind"] in ("Polygon", "MultiPolygon"):
            # coords() flattened multipolygons; a ring that does not sit inside the previous exterior starts a polygon
            cur = []
            for ring in it["rings"]:
                if cur and not _inside(ring[0], cur[0]):
                    out.append(cur)
                    cur = []
                cur.append(ring)
            if cur:
                out.append(cur)
    return out


def _inside(p, ring):
    x, z = p
    r = np.asarray(ring)
    xi, zi = r[:, 0], r[:, 1]
    xj, zj = np.roll(xi, 1), np.roll(zi, 1)
    c = ((zi > z) != (zj > z)) & (x < (xj - xi) * (z - zi) / (zj - zi + 1e-12) + xi)
    return bool(c.sum() % 2)


def water(dem_raw, flow):
    """Water kind per height cell (0 none, 1 Mississippi, 2 lake or pond, 3 other river) and the water level."""
    areas = json.load(open(cfg.RAW / "nhd_areas.json"))
    bodies = json.load(open(cfg.RAW / "nhd_waterbodies.json"))
    n = cfg.GRID
    # the Mississippi: area polygons (stream / river) that the Mississippi's own flowlines run through
    river_polys = polys_of(areas, lambda a: a["ftype"] == 460)
    riv = rasterise(river_polys, cfg.CELL, n).astype(bool)
    msl = burn_lines([ln for f in flow if BIG and f["name"] == BIG for ln in f["lines"]], cfg.CELL, n, 30)
    lab, _ = ndimage.label(riv)
    ids = np.unique(lab[msl & riv])
    # the polygons touching the Mississippi's line, but only within 2.5 km of it: the Homochitto's and the Buffalo's
    # own river polygons join the Mississippi's at their mouths
    k = 8
    near = ndimage.distance_transform_edt(~msl[::k, ::k]) * k * cfg.CELL < 2500.0
    near = np.repeat(np.repeat(near, k, 0), k, 1)[:n, :n]
    miss = np.isin(lab, ids[ids > 0]) & near
    other_river = riv & ~miss
    lakes = rasterise(polys_of(bodies, lambda b: b["ftype"] in (390, 436) and _area(b) >= POND_KM2 * 1e6), cfg.CELL, n).astype(bool)
    log(f"water polygons: {BIG or 'big river'} {miss.mean() * 100:.2f} %, other rivers {other_river.mean() * 100:.2f} %, "
        f"lakes and ponds {lakes.mean() * 100:.2f} % of the map")
    kind = np.zeros((n, n), np.uint8)
    level = np.full((n, n), np.nan, np.float32)
    # the rivers: the lidar's hydro-flattened surface is the lowest ground in the polygon nearby (it steps by a metre
    # or two between survey reaches, so the test is local); cells within 0.5 m of it are water, the rest (bars) land
    # The Mississippi itself is filled bank to bank (NHD's polygon): the lidar caught it at low water, with bars a
    # kilometre wide, and the storm season is its high-water season
    for mask, k in ((miss, 1), (other_river, 3)):
        if not mask.any():
            continue
        lowf = _local_low(dem_raw, mask)
        wet = mask if k == 1 else mask & (dem_raw <= lowf + 0.5)
        wet = ndimage.binary_opening(wet, iterations=1)
        kind[wet] = k
        flat = mask & (dem_raw <= lowf + 0.5)              # the level comes from the flattened water, not the bars
        level[wet] = _wet_level(dem_raw, flat if k == 1 else wet)[wet] + (0.5 if k == 1 else 0.0)
    # lakes and ponds: one level each, the median of the flattened surface inside
    lab, nl = ndimage.label(lakes & (kind == 0))
    if nl:
        med = ndimage.median(dem_raw, lab, np.arange(1, nl + 1))
        lv = np.concatenate([[np.nan], med]).astype(np.float32)[lab]
        wet = (lab > 0) & (dem_raw <= lv + 0.3)
        kind[wet] = 2
        level[wet] = lv[wet]
    log(f"water: {BIG or 'big river'} {(kind == 1).mean() * 100:.2f} %, lakes and ponds {(kind == 2).mean() * 100:.2f} %, "
        f"other rivers {(kind == 3).mean() * 100:.2f} %; {nl} lakes and ponds")
    return kind, level


def _area(b):
    r = np.asarray(b["rings"][0])
    return 0.5 * abs(np.dot(r[:, 0], np.roll(r[:, 1], 1)) - np.dot(r[:, 1], np.roll(r[:, 0], 1)))


def _coarse(a, k, fn):
    n = (a.shape[0] // k) * k
    return fn(a[:n, :n].reshape(n // k, k, n // k, k), axis=(1, 3))


def _up(c, k, shape, order=1):
    idx = (np.arange(shape[0]) / k - 0.5).clip(0, c.shape[0] - 1)
    out = np.empty(shape, np.float32)
    for j0 in range(0, shape[0], 1024):
        out[j0:j0 + 1024] = ndimage.map_coordinates(c, np.meshgrid(idx[j0:j0 + 1024], idx, indexing="ij"), order=order)
    return out


def _local_low(dem, mask, k=8, window_m=600.0):
    """The lowest ground of the mask within about window_m, everywhere in the mask."""
    v = np.where(mask, dem, np.inf)
    lo = _coarse(v, k, np.min)
    lo = ndimage.minimum_filter(lo, size=int(window_m / (k * cfg.CELL)) | 1)
    fin = np.isfinite(lo)
    if not fin.all():
        idx = ndimage.distance_transform_edt(~fin, return_distances=False, return_indices=True)
        lo = lo[tuple(idx)]
    return _up(lo.astype(np.float32), k, mask.shape, order=1)


def _wet_level(dem, wet, k=8, sigma_m=300.0):
    """A smooth water surface over the wet cells: the normalised gaussian of their (flat) ground."""
    v = _coarse(np.where(wet, dem, 0.0), k, np.sum)
    w = _coarse(wet.astype(np.float32), k, np.sum)
    sg = sigma_m / (k * cfg.CELL)
    m = ndimage.gaussian_filter(v, sg) / np.maximum(ndimage.gaussian_filter(w, sg), 1e-6)
    m[w.sum() == 0] = 0
    return _up(m.astype(np.float32), k, wet.shape)


# ------------------------------------------------------------------------------------------------------- streams

def load_flow():
    return json.load(open(cfg.RAW / "nhd_flowlines.json"))


def chain_named(flow, name):
    """One downstream polyline for a named stream: its reaches joined end to start by the NHDPlus links."""
    rs = [f for f in flow if f["name"] == name and f["lines"]]
    by_hs = {f["hs"]: f for f in rs}
    heads = [f for f in rs if not any(g["dn"] == f["hs"] for g in rs)]
    best = None
    for h in heads:
        pts, cur, seen = [], h, set()
        while cur is not None and cur["hs"] not in seen:
            seen.add(cur["hs"])
            ln = max(cur["lines"], key=len)
            pts.extend(ln if not pts else ln[1:])
            cur = by_hs.get(cur["dn"])
        if best is None or len(pts) > len(best):
            best = pts
    return np.array(best, float) if best else None


def streams(dem, flow, kind):
    sm = ndimage.gaussian_filter(dem, 12.0 / cfg.CELL)
    low = ndimage.grey_erosion(dem, size=(3, 3))
    out = []
    names = {}
    for f in flow:
        if f["name"]:
            names[f["name"]] = max(names.get(f["name"], 0.0), f["da"])
    # NHDPlus's drainage areas in the delta carry whole basins through side channels and bayous (The Narrows has
    # the Mississippi's 2.6 million km2), so a river is a named stream whose reaches run mostly through NHD's river
    # polygons, or one draining RIVER_DA_KM2 or more on the Mississippi side of the river (the hills)
    wetf = (kind > 0).astype(np.float32)
    polyf = ndimage.binary_dilation(kind == 3, iterations=4).astype(np.float32)
    share, first = {}, {}
    for f in flow:
        if not f["name"]:
            continue
        for ln in f["lines"]:
            a = np.asarray(ln, float)
            if len(a) >= 2:
                v = sample(polyf, a)
                t = share.setdefault(f["name"], [0.0, 0])
                t[0] += float(v.sum())
                t[1] += len(v)
                first.setdefault(f["name"], []).append(a[0])
    rivers = []
    for nm, da in names.items():
        if nm == BIG:
            rivers.append(nm)
            continue
        if nm in SIDE_CHANNELS or nm not in first:
            continue
        poly = share[nm]
        x, z = np.mean(first[nm], axis=0)
        hills = x > river_x(kind, np.array([z]))[0] + 500 if BIG else True
        if poly[0] / max(poly[1], 1) > 0.5 or (hills and da >= RIVER_DA_KM2):
            rivers.append(nm)
    log("rivers: " + ", ".join(f"{nm} ({names[nm]:,.0f} km2)" for nm in sorted(rivers, key=lambda k: -names[k])))
    for nm in rivers:
        pts = chain_named(flow, nm)
        if pts is None or len(pts) < 2:
            continue
        if nm == BIG:
            pts = resample(pts, 20.0)
            out.append(dict(name=nm, kind="river", da=names[nm], width=0.0, polygon=True, clear=26.0,
                            pts=pts, level=np.zeros(len(pts))))
            continue
        width = float(np.clip(6 + 0.8 * np.sqrt(names[nm]), 20, 60))
        p = snap(pts, sm, search=150.0)
        out.append(dict(name=nm, kind="river", da=names[nm], width=width, pts=p, level=water_level(p, low, 1.4)))
    for f in flow:
        if f["name"] in rivers or f["name"] in SIDE_CHANNELS or f["da"] < MIN_DA_KM2 or f["ftype"] not in (460, 336):
            continue
        k = "creek" if f["da"] >= CREEK_DA_KM2 and f["order"] >= 3 else "draw"
        for ln in f["lines"]:
            a = np.asarray(ln, float)
            if len(a) < 2:
                continue
            # reaches inside a lake or the river are drawn by the polygon
            if (sample(wetf, a) > 0.5).mean() > 0.5:
                continue
            p = snap(a, sm, search=90.0 if k == "creek" else 60.0)
            w = 4.0 + 0.9 * np.sqrt(min(f["da"], 400.0)) if k == "creek" else 0.0
            out.append(dict(name=f["name"], kind=k, da=f["da"], width=float(min(w, 22.0)), pts=p,
                            level=water_level(p, low, 0.6)))
    inside = lambda p: ((p[:, 0] > -500) & (p[:, 0] < cfg.SIZE_M + 500) & (p[:, 1] > -500) & (p[:, 1] < cfg.SIZE_M + 500)).any()
    return [s for s in out if inside(s["pts"])]


# --------------------------------------------------------------------------------------------------------- preview

COVER_RGB = {WC_TREE: (52, 82, 40), WC_SHRUB: (110, 120, 70), WC_GRASS: (130, 150, 80), WC_CROP: (150, 125, 95),
             WC_BUILT: (150, 150, 150), WC_BARE: (200, 185, 150), WC_WATER: (60, 90, 110), WC_WETLAND: (90, 110, 90)}


def preview(dem, cov, kind, st, levee_lines, town, path, step=8):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.colors import LightSource
    h = dem[::step, ::step]
    n = h.shape[0]
    ci = np.clip((np.arange(n) * step * cfg.CELL / COVER_STEP).astype(int), 0, NCOV - 1)
    c = cov[np.ix_(ci, ci)]
    rgb = np.zeros(c.shape + (3,))
    for k, col in COVER_RGB.items():
        rgb[c == k] = np.array(col) / 255
    shade = LightSource(315, 40).hillshade(h, vert_exag=4, dx=cfg.CELL * step, dy=cfg.CELL * step)
    img = rgb * (0.45 + 0.7 * shade[..., None])
    w = kind[::step, ::step]
    img[w == 1] = (0.45, 0.38, 0.28)
    img[w == 2] = (0.20, 0.32, 0.40)
    img[w == 3] = (0.40, 0.36, 0.28)
    tn = town[np.ix_(ci, ci)]
    img[tn] = img[tn] * 0.6 + 0.4 * np.array([0.85, 0.3, 0.3])
    fig, ax = plt.subplots(figsize=(20, 20))
    ax.imshow(np.clip(img, 0, 1), origin="lower", extent=(0, cfg.SIZE_M, 0, cfg.SIZE_M))
    for s in st:
        p = np.asarray(s["pts"])
        if s["kind"] == "river" and not s.get("polygon"):
            ax.plot(p[:, 0], p[:, 1], color="#2060ff", lw=2.0)
            q = p[len(p) // 2]
            ax.text(q[0], q[1], s["name"], color="#1030a0", fontsize=11)
        elif s["kind"] == "creek":
            ax.plot(p[:, 0], p[:, 1], color="#3080ff", lw=0.7)
    for ln in levee_lines:
        p = np.asarray(ln)
        ax.plot(p[:, 0], p[:, 1], color="#ffd000", lw=1.5)
    ax.set_xlim(0, cfg.SIZE_M)
    ax.set_ylim(0, cfg.SIZE_M)
    ax.set_title(f"{cfg.SPEC['title']}, stage 1: WorldCover land cover on the 3DEP hillshade; water (NHD, where the lidar "
                 f"is wet), rivers and creeks (NHDPlus HR), levees (yellow), {REAL['town']} (red)")
    path.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(path, dpi=60, bbox_inches="tight")


# ------------------------------------------------------------------------------------------------------------ main

def main():
    cfg.OUT.mkdir(parents=True, exist_ok=True)
    fresh = "--fresh" in sys.argv or not (cfg.OUT / "dem_raw.npy").exists() or not (cfg.OUT / "cover.npy").exists()
    if fresh:
        dem_raw = mosaic()
        np.save(cfg.OUT / "dem_raw.npy", dem_raw)
    else:
        dem_raw = np.load(cfg.OUT / "dem_raw.npy")
    log(f"mosaic {dem_raw.shape}, {dem_raw.min():.1f}..{dem_raw.max():.1f} m")
    flow = load_flow()
    kind, level = water(dem_raw, flow)
    if REAL["levees"]:
        levee, levee_lines = detect_levees(dem_raw)
    else:
        levee, levee_lines = np.zeros(dem_raw.shape, bool), []
    json.dump(levee_lines, open(cfg.OUT / "levees.json", "w"))
    dem = ground(dem_raw, levee)
    if fresh:
        cov = worldcover()
        np.save(cfg.OUT / "cover.npy", cov)
    else:
        cov = np.load(cfg.OUT / "cover.npy")
    log("cover: " + ", ".join(f"{c} {(cov == c).mean() * 100:.1f} %" for c in np.unique(cov)))
    town = town_mask(cov, kind)
    np.save(cfg.OUT / "town.npy", town)
    dem = flatten_towns(dem, cov, town)
    np.save(cfg.OUT / "dem.npy", dem)
    forest = to_cells(ndimage.uniform_filter((cov == WC_TREE).astype(np.float32), 3), COVER_STEP)
    np.save(cfg.OUT / "forest.npy", forest.astype(np.float16))
    st = streams(dem, flow, kind)
    # the Mississippi's level along its line: the water level of the nearest river cells
    for s in st:
        if s.get("polygon"):
            idx = ndimage.distance_transform_edt(kind != 1, return_distances=False, return_indices=True)
            s["level"] = sample(level[tuple(idx)], s["pts"])
            del idx
    np.savez_compressed(cfg.OUT / "water.npz", kind=kind, level=np.nan_to_num(level, nan=0.0).astype(np.float32))
    json.dump([dict(s, pts=np.round(s["pts"], 2).tolist(), level=np.round(np.asarray(s["level"]), 2).tolist()) for s in st],
              open(cfg.OUT / "streams.json", "w"))
    kinds = {k: sum(1 for s in st if s["kind"] == k) for k in ("river", "creek", "draw")}
    log(f"streams: {kinds}")
    preview(dem, cov, kind, st, levee_lines, town, cfg.PREVIEW / "land.png")


if __name__ == "__main__":
    main()

"""Stage 1 of the test map: the real ground and its streams.

- Mosaics the 3DEP chunks onto the map grid (GRID x GRID samples, row 0 = south edge, column 0 = west edge).
- Cleans what a bare-earth DEM keeps of the real roads, railways and lagoons, so the designed roads don't run past
  phantom embankments.
- Builds the streams from NHDPlus: the Arkansas and Pawnee rivers as single chained polylines, the rest as reaches,
  each moved onto the DEM's real channel (NHD medium resolution is off by up to ~100 m) with a water level that only
  falls downstream.

Writes build/dem.npy (float32, metres NAVD88), build/cdl.npy (land cover codes, 30 m) and build/streams.json. Run: python -I tools/testmap/terrain.py
"""
import json
import sys
import time
from pathlib import Path

import numpy as np
import tifffile
from scipy import ndimage

sys.path.insert(0, str(Path(__file__).resolve().parent))
import map_config as cfg

CHUNK = 2048
RIVERS = {"Arkansas River": dict(width=70.0, bed=1.2), "Pawnee River": dict(width=22.0, bed=1.5)}
CREEK_DA_KM2 = 40.0           # streams draining more than this get a bridge and a tree line
MIN_DA_KM2 = 6.0              # smaller NHD reaches are left to the terrain


def mosaic():
    n = (cfg.GRID - 1) // CHUNK
    dem = np.empty((cfg.GRID, cfg.GRID), np.float32)
    for cj in range(n):
        for ci in range(n):
            a = tifffile.imread(cfg.RAW / "dem" / f"dem_{ci}_{cj}.tif").astype(np.float32)
            assert a.shape == (CHUNK + 1, CHUNK + 1), a.shape
            dem[cj * CHUNK:(cj + 1) * CHUNK + 1, ci * CHUNK:(ci + 1) * CHUNK + 1] = a[::-1]
    bad = ~np.isfinite(dem) | (dem < 0) | (dem > 3000)
    if bad.any():
        print(f"filling {bad.sum()} nodata samples")
        idx = ndimage.distance_transform_edt(bad, return_distances=False, return_indices=True)
        dem = dem[tuple(idx)]
    return dem


def clean(dem):
    """Remove narrow man-made relief. Embankments, levees, dams and the old roads' crowns stand up out of the land
    around them by a metre or more over a width of tens of metres; natural relief at that width in these plains is
    channels (downward) and the odd dune. So clip what rises above a 60 m-scale smooth surface, and soften the narrow
    pits and ditches with a light blur."""
    sm = ndimage.gaussian_filter(dem, 60.0 / cfg.CELL)
    dem = np.minimum(dem, sm + np.maximum(0.35, 0.25 * (dem - sm).clip(0)))  # cap ridges, keep a little of real swells
    pits = ndimage.grey_closing(dem, size=(5, 5))                            # fill pits narrower than ~40 m
    dem = np.minimum(pits, dem + 1.2)                                        # ... but no deeper than lagoon berms
    return ndimage.gaussian_filter(dem, 1.0).astype(np.float32)


CDL_STEP = 30.0
DEVELOPED = (122, 123, 124)   # CDL developed low / medium / high intensity: the real towns


def cdl_map():
    """The 2024 Cropland Data Layer resampled (nearest) onto a 30 m grid in the map frame, row 0 = south."""
    from pyproj import Transformer
    with tifffile.TiffFile(cfg.RAW / "cdl_2024.tif") as t:
        page = t.pages[0]
        a = page.asarray()
        sx, sy, _ = page.tags["ModelPixelScaleTag"].value
        tie = page.tags["ModelTiepointTag"].value
    n = int(round(cfg.SIZE_M / CDL_STEP))
    c = (np.arange(n) + 0.5) * CDL_STEP
    xx, zz = np.meshgrid(c + cfg.E0, c + cfg.N0)
    X, Y = Transformer.from_crs(cfg.EPSG, 5070, always_xy=True).transform(xx.ravel(), zz.ravel())
    col = np.clip(((np.asarray(X) - tie[3]) / sx).astype(int), 0, a.shape[1] - 1)
    row = np.clip(((tie[4] - np.asarray(Y)) / sy).astype(int), 0, a.shape[0] - 1)
    return a[row, col].reshape(n, n)


def flatten_towns(dem, cdl):
    """The real towns (Kinsley, Jetmore, Spearville, ...) keep their street grids, lots and lagoons in a bare-earth
    DEM. The map has no towns, so smooth them into the land around."""
    dev = np.isin(cdl, DEVELOPED).astype(np.float32)
    town = ndimage.uniform_filter(dev, 11) > 0.12
    town = ndimage.binary_dilation(town, iterations=6)
    feather = ndimage.gaussian_filter(town.astype(np.float32), 4)
    zoom = cfg.CELL / CDL_STEP
    idx = (np.arange(cfg.GRID) * zoom - 0.5).clip(0, feather.shape[0] - 1)
    w = ndimage.map_coordinates(feather, np.meshgrid(idx, idx, indexing="ij"), order=1).astype(np.float32)
    big = ndimage.grey_closing(dem, size=(15, 15))
    filled = np.minimum(big, dem + 3.0)
    smooth = ndimage.gaussian_filter(filled, 50.0 / cfg.CELL)
    print(f"towns: {town.mean() * 100:.1f} % of the map smoothed")
    return (dem * (1 - w) + smooth * w).astype(np.float32)


# ---------------------------------------------------------------------------------------------------------- streams

def load_reaches():
    d = json.load(open(cfg.RAW / "nhd_flowlines.geojson"))
    out = []
    for f in d["features"]:
        p = f["properties"]
        g = f["geometry"]
        lines = [g["coordinates"]] if g["type"] == "LineString" else g["coordinates"]
        for line in lines:
            a = np.array(line, float)[:, :2]
            a[:, 0] -= cfg.E0
            a[:, 1] -= cfg.N0
            out.append(dict(name=p.get("GNIS_NAME") or "", order=p.get("StreamOrde") or 0,
                            da=p.get("TotDASqKM") or 0.0, pts=a))
    return out


def chain(reaches):
    """Join reaches end to start into the longest downstream path (NHDPlus lines run downstream)."""
    key = lambda p: (round(p[0], 1), round(p[1], 1))
    starts = {}
    for r in reaches:
        starts.setdefault(key(r["pts"][0]), []).append(r)
    ends = {key(r["pts"][-1]) for r in reaches}
    best = None
    for head in [r for r in reaches if key(r["pts"][0]) not in ends] or reaches:
        path, cur, seen = [head["pts"]], head, {id(head)}
        while True:
            nxt = [r for r in starts.get(key(cur["pts"][-1]), []) if id(r) not in seen]
            if not nxt:
                break
            cur = max(nxt, key=lambda r: r["da"])
            seen.add(id(cur))
            path.append(cur["pts"][1:])
        pts = np.concatenate(path)
        if best is None or len(pts) > len(best):
            best = pts
    return best


def resample(pts, step):
    seg = np.linalg.norm(np.diff(pts, axis=0), axis=1)
    s = np.concatenate([[0], np.cumsum(seg)])
    n = max(2, int(s[-1] / step) + 1)
    t = np.linspace(0, s[-1], n)
    return np.stack([np.interp(t, s, pts[:, 0]), np.interp(t, s, pts[:, 1])], 1)


def sample(grid, pts):
    """Bilinear sample of a GRID x GRID array at map-frame points."""
    x = np.clip(pts[:, 0] / cfg.CELL, 0, cfg.GRID - 1.001)
    z = np.clip(pts[:, 1] / cfg.CELL, 0, cfg.GRID - 1.001)
    return ndimage.map_coordinates(grid, [z, x], order=1, mode="nearest")


def normals(pts):
    t = np.gradient(pts, axis=0)
    t /= np.linalg.norm(t, axis=1, keepdims=True) + 1e-9
    return np.stack([-t[:, 1], t[:, 0]], 1)


def snap(pts, smooth_dem, search, step=20.0, smooth=6.0):
    """Move a stream line onto the lowest ground across its path, then smooth the offsets along it."""
    pts = resample(pts, step)
    nrm = normals(pts)
    offs = np.linspace(-search, search, int(2 * search / 5) + 1)
    h = np.stack([sample(smooth_dem, pts + o * nrm) for o in offs], 1)
    h += 0.002 * np.abs(offs)[None, :]                       # ties go to the NHD line
    best = offs[np.argmin(h, 1)]
    best = ndimage.gaussian_filter1d(ndimage.median_filter(best, 5), smooth, mode="nearest")
    return pts + best[:, None] * nrm


def water_level(pts, low, bed):
    """Surface along the stream: the channel's low ground (low = DEM eroded by a cell), never rising downstream."""
    lo = sample(low, pts)
    lvl = np.minimum.accumulate(ndimage.gaussian_filter1d(lo, 8, mode="nearest"))
    return lvl + bed * 0.4


def streams(dem):
    reaches = [r for r in load_reaches() if r["da"] >= MIN_DA_KM2 or r["name"] in RIVERS]
    sm = ndimage.gaussian_filter(dem, 12.0 / cfg.CELL)
    low = ndimage.grey_erosion(dem, size=(3, 3))
    out = []
    for name, spec in RIVERS.items():
        rs = [r for r in reaches if r["name"] == name]
        if not rs:
            continue
        pts = snap(chain(rs), sm, search=150.0)
        out.append(dict(name=name, kind="river", da=max(r["da"] for r in rs), width=spec["width"],
                        pts=pts, level=water_level(pts, low, spec["bed"])))
    for r in reaches:
        if r["name"] in RIVERS:
            continue
        kind = "creek" if r["da"] >= CREEK_DA_KM2 else "draw"
        pts = snap(r["pts"], sm, search=90.0 if kind == "creek" else 60.0)
        width = 4.0 + 0.9 * np.sqrt(r["da"]) if kind == "creek" else 0.0
        out.append(dict(name=r["name"], kind=kind, da=r["da"], width=float(width), pts=pts,
                        level=water_level(pts, low, 0.6)))
    # keep only what is on the map
    inside = lambda p: ((p[:, 0] > -500) & (p[:, 0] < cfg.SIZE_M + 500) & (p[:, 1] > -500) & (p[:, 1] < cfg.SIZE_M + 500)).any()
    return [s for s in out if inside(s["pts"])]


def main():
    t0 = time.time()
    cfg.OUT.mkdir(parents=True, exist_ok=True)
    dem = mosaic()
    print(f"mosaic {dem.shape}, {dem.min():.1f}..{dem.max():.1f} m ({time.time() - t0:.0f} s)")
    np.save(cfg.OUT / "dem_raw.npy", dem)
    dem = clean(dem)
    cdl = cdl_map()
    np.save(cfg.OUT / "cdl.npy", cdl)
    dem = flatten_towns(dem, cdl)
    np.save(cfg.OUT / "dem.npy", dem)
    print(f"cleaned ({time.time() - t0:.0f} s)")
    st = streams(dem)
    json.dump([dict(s, pts=s["pts"].round(2).tolist(), level=np.round(s["level"], 2).tolist()) for s in st],
              open(cfg.OUT / "streams.json", "w"))
    kinds = {k: sum(1 for s in st if s["kind"] == k) for k in ("river", "creek", "draw")}
    print(f"streams: {kinds} ({time.time() - t0:.0f} s)")


if __name__ == "__main__":
    main()

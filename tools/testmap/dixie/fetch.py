"""Download the source data for the Dixie map (Natchez, MS) and cut it down to the map.

Everything comes from The National Map's staged products on S3 (prd-tnm.s3.amazonaws.com) and ESA's WorldCover
bucket, so no ArcGIS service has to be up:

- USGS 3DEP 1/3 arc-second DEM tiles n32w092 and n32w091 (about 10 m, lidar-derived here) -> dem13/
- USGS 3DEP 1 arc-second DEM tiles over three map widths for the horizon skirt -> dem1/
- USGS NHDPlus High Resolution for HU4 0804-0807 (flowlines with drainage area, the river and lake polygons, and
  NHD's levee lines) -> nhd_*.json, in the map frame
- USGS National Transportation Dataset, Mississippi and Louisiana (the real highways the designed roads follow)
  -> tran_roads.json, in the map frame
- ESA WorldCover 2021 v200, 10 m (tile N30W093) for land cover: tree cover, cropland, grassland, built-up, wetland.
  The plains map uses USDA's Cropland Data Layer; its server is not needed here and the Delta's crop type matters
  little in the storm season (bare tilled ground in March and April)

Downloads are cached in data/raw/testmap/dixie (about 3 GB, the zips can be deleted after the cut). Run:
python -I tools/testmap/dixie/fetch.py
"""
import json
import sqlite3
import sys
import time
import urllib.request
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import _paths  # noqa: F401,E402
import numpy as np
import shapely
from pyproj import Transformer

import map_config as cfg

TNM = "https://prd-tnm.s3.amazonaws.com/StagedProducts"
WORLDCOVER = "https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/ESA_WorldCover_10m_2021_v200_N30W093_Map.tif"
DEM13 = ["n32w092", "n32w091"]
DEM1 = [f"n{la}w{lo:03d}" for la in (31, 32, 33) for lo in (93, 92, 91)]
HU4 = ["0804", "0805", "0806", "0807"]
STATES = ["Mississippi", "Louisiana"]
PAD = 3000.0                       # metres of data kept around the map


def download(url, path, tries=5):
    if path.exists():
        return path
    path.parent.mkdir(parents=True, exist_ok=True)
    part = path.with_suffix(path.suffix + ".part")
    for k in range(tries):
        try:
            have = part.stat().st_size if part.exists() else 0
            req = urllib.request.Request(url, headers={"Range": f"bytes={have}-"} if have else {})
            with urllib.request.urlopen(req, timeout=300) as r, open(part, "ab" if have else "wb") as f:
                if have and r.status != 206:          # the server ignored the range: start again
                    f.truncate(0)
                while True:
                    b = r.read(1 << 22)
                    if not b:
                        break
                    f.write(b)
            part.rename(path)
            print(f"  {path.name}: {path.stat().st_size / 1e6:.0f} MB", flush=True)
            return path
        except Exception as e:  # noqa: BLE001 - S3 drops long transfers now and then; resume
            print(f"  retry {k + 1} {path.name}: {e}", flush=True)
            time.sleep(5 * (k + 1))
    raise RuntimeError(f"failed: {url}")


# ------------------------------------------------------------------------------------------------------- geopackage

def gpkg_geom(blob):
    """GeoPackage geometry blob -> shapely geometry (the standard header, then WKB)."""
    flags = blob[3]
    env = {0: 0, 1: 32, 2: 48, 3: 48, 4: 64}[(flags >> 1) & 7]
    return shapely.from_wkb(bytes(blob[8 + env:]))


def gpkg_query(path, table, bbox_ll, cols, where=None):
    """Rows of a GeoPackage feature table whose envelope meets bbox_ll (lon0, lat0, lon1, lat1): (geometry, {col: v})."""
    con = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    gcol = con.execute("select column_name from gpkg_geometry_columns where lower(table_name)=lower(?)", (table,)).fetchone()[0]
    rtree = f"rtree_{table}_{gcol}"
    x0, y0, x1, y1 = bbox_ll
    sel = ", ".join(f't."{c}"' for c in cols)
    q = (f'select t."{gcol}"{", " + sel if cols else ""} from "{table}" t join "{rtree}" r on t.rowid = r.id '
         f"where r.maxx >= ? and r.minx <= ? and r.maxy >= ? and r.miny <= ?")
    if where:
        q += f" and ({where})"
    out = []
    for row in con.execute(q, (x0, x1, y0, y1)):
        if row[0] is None:
            continue
        out.append((gpkg_geom(row[0]), dict(zip(cols, row[1:]))))
    con.close()
    return out


def tables(path):
    con = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    t = [r[0] for r in con.execute("select table_name from gpkg_contents")]
    con.close()
    return t


def columns(path, table):
    con = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    c = [r[1] for r in con.execute(f'pragma table_info("{table}")')]
    con.close()
    return c


def srs_of(path, table):
    con = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    s = con.execute("select srs_id from gpkg_geometry_columns where lower(table_name)=lower(?)", (table,)).fetchone()[0]
    con.close()
    return int(s)


def map_bbox_ll(pad=PAD):
    t = Transformer.from_crs(cfg.EPSG, 4269, always_xy=True)
    xs, ys = [], []
    for f in np.linspace(0, 1, 9):
        for e, n in ((cfg.E0 - pad + f * (cfg.SIZE_M + 2 * pad), cfg.N0 - pad), (cfg.E0 - pad + f * (cfg.SIZE_M + 2 * pad), cfg.N0 + cfg.SIZE_M + pad),
                     (cfg.E0 - pad, cfg.N0 - pad + f * (cfg.SIZE_M + 2 * pad)), (cfg.E0 + cfg.SIZE_M + pad, cfg.N0 - pad + f * (cfg.SIZE_M + 2 * pad))):
            x, y = t.transform(e, n)
            xs.append(x)
            ys.append(y)
    return min(xs), min(ys), max(xs), max(ys)


def to_map(geom, srs):
    """Reproject a geographic geometry to the map frame (metres from the map's south-west corner), 2D."""
    t = Transformer.from_crs(srs, cfg.EPSG, always_xy=True)

    def f(c):
        x, y = t.transform(c[:, 0], c[:, 1])
        return np.column_stack([np.asarray(x) - cfg.E0, np.asarray(y) - cfg.N0])
    return shapely.transform(shapely.force_2d(geom), f)


def coords(g):
    """A (multi)line or (multi)polygon as a list of coordinate lists, rounded to 0.1 m."""
    if g.geom_type in ("LineString", "LinearRing"):
        return [np.round(np.asarray(g.coords), 1).tolist()]
    if g.geom_type == "Polygon":
        return [np.round(np.asarray(r.coords), 1).tolist() for r in [g.exterior, *g.interiors]]
    return [c for p in g.geoms for c in coords(p)]


def clip_box(pad=PAD):
    return shapely.box(-pad, -pad, cfg.SIZE_M + pad, cfg.SIZE_M + pad)


def cut_nhd():
    out = cfg.RAW / "nhd_flowlines.json"
    if out.exists():
        return
    bb = map_bbox_ll()
    box = clip_box()
    flow, areas, bodies, lines = [], [], [], []
    for h in HU4:
        z = cfg.RAW / "nhdplus" / f"NHDPLUS_H_{h}_HU4_GPKG.zip"
        with zipfile.ZipFile(z) as zf:
            name = next(n for n in zf.namelist() if n.lower().endswith(".gpkg"))
            gp = cfg.RAW / "nhdplus" / name
            if not gp.exists():
                zf.extract(name, cfg.RAW / "nhdplus")
        tb = {t.lower(): t for t in tables(gp)}
        srs = srs_of(gp, tb["nhdflowline"])
        # flowlines and their value-added attributes (drainage area, stream order, the downstream link)
        con = sqlite3.connect(f"file:{gp}?mode=ro", uri=True)
        vcols = {c.lower(): c for c in columns(gp, tb["nhdplusflowlinevaa"])}
        vaa = {}
        for row in con.execute(f'select "{vcols["nhdplusid"]}", "{vcols["totdasqkm"]}", "{vcols["streamorde"]}", '
                               f'"{vcols["hydroseq"]}", "{vcols["dnhydroseq"]}" from "{tb["nhdplusflowlinevaa"]}"'):
            vaa[row[0]] = row[1:]
        con.close()
        fcols = {c.lower(): c for c in columns(gp, tb["nhdflowline"])}
        for g, a in gpkg_query(gp, tb["nhdflowline"], bb, [fcols["nhdplusid"], fcols["gnis_name"], fcols["ftype"]]):
            g = to_map(g, srs).intersection(box)
            if g.is_empty:
                continue
            da, order, hs, dn = vaa.get(a[fcols["nhdplusid"]], (0.0, 0, 0, 0))
            flow.append(dict(id=a[fcols["nhdplusid"]], name=a[fcols["gnis_name"]] or "", ftype=a[fcols["ftype"]],
                             da=float(da or 0.0), order=int(order or 0), hs=hs, dn=dn, lines=coords(g)))
        for tname, dst in (("nhdarea", areas), ("nhdwaterbody", bodies), ("nhdline", lines)):
            if tname not in tb:
                continue
            cc = {c.lower(): c for c in columns(gp, tb[tname])}
            want = [cc[k] for k in ("gnis_name", "ftype", "fcode", "areasqkm") if k in cc]
            for g, a in gpkg_query(gp, tb[tname], bb, want):
                g = to_map(g, srs)
                if not g.is_valid:
                    g = shapely.make_valid(g)
                g = g.intersection(box)
                if g.is_empty:
                    continue
                dst.append(dict(name=a.get(cc["gnis_name"]) or "", ftype=a.get(cc["ftype"]), fcode=a.get(cc.get("fcode", "")),
                                kind=g.geom_type, rings=coords(g)))
        gp.unlink()
        print(f"NHDPlus HR {h}: {len(flow)} flowlines, {len(areas)} areas, {len(bodies)} waterbodies, {len(lines)} lines so far", flush=True)
    for name, data in (("nhd_areas", areas), ("nhd_waterbodies", bodies), ("nhd_lines", lines), ("nhd_flowlines", flow)):
        (cfg.RAW / f"{name}.json").write_text(json.dumps(data))


def cut_tran():
    out = cfg.RAW / "tran_roads.json"
    if out.exists():
        return
    bb = map_bbox_ll()
    box = clip_box()
    roads = []
    for st in STATES:
        z = cfg.RAW / "tran" / f"TRAN_{st}_State_GPKG.zip"
        with zipfile.ZipFile(z) as zf:
            name = next(n for n in zf.namelist() if n.lower().endswith(".gpkg"))
            gp = cfg.RAW / "tran" / name
            if not gp.exists():
                zf.extract(name, cfg.RAW / "tran")
        tb = {t.lower(): t for t in tables(gp)}
        rt = tb["trans_roadsegment"]
        cc = {c.lower(): c for c in columns(gp, rt)}
        want = [cc[k] for k in ("full_street_name", "name", "mtfcc", "tnmfrc", "us_route", "state_route", "interstate",
                                "county_route", "permanent_identifier") if k in cc]
        srs = srs_of(gp, rt)
        for g, a in gpkg_query(gp, rt, bb, want):
            g = to_map(g, srs).intersection(box)
            if g.is_empty:
                continue
            roads.append(dict(state=st[:2].upper(), **{k.lower(): v for k, v in a.items()}, lines=coords(g)))
        gp.unlink()
        print(f"Transportation {st}: {len(roads)} road segments so far", flush=True)
    out.write_text(json.dumps(roads))


def main():
    cfg.RAW.mkdir(parents=True, exist_ok=True)
    for t in DEM13:
        download(f"{TNM}/Elevation/13/TIFF/current/{t}/USGS_13_{t}.tif", cfg.RAW / "dem13" / f"USGS_13_{t}.tif")
    for t in DEM1:
        download(f"{TNM}/Elevation/1/TIFF/current/{t}/USGS_1_{t}.tif", cfg.RAW / "dem1" / f"USGS_1_{t}.tif")
    download(WORLDCOVER, cfg.RAW / "worldcover" / WORLDCOVER.rsplit("/", 1)[1])
    if not (cfg.RAW / "nhd_flowlines.json").exists():
        for h in HU4:
            download(f"{TNM}/Hydrography/NHDPlusHR/VPU/Current/GPKG/NHDPLUS_H_{h}_HU4_GPKG.zip",
                     cfg.RAW / "nhdplus" / f"NHDPLUS_H_{h}_HU4_GPKG.zip")
    if not (cfg.RAW / "tran_roads.json").exists():
        for st in STATES:
            download(f"{TNM}/Tran/GPKG/TRAN_{st}_State_GPKG.zip", cfg.RAW / "tran" / f"TRAN_{st}_State_GPKG.zip")
    cut_nhd()
    cut_tran()
    print("done")


if __name__ == "__main__":
    main()

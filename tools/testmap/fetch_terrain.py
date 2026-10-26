"""Download the source data for the plains test map.

- USGS 3DEP bare-earth elevation (The National Map ImageServer), resampled onto the map grid: 25 chunks of
  2049 x 2049 samples -> data/raw/testmap/dem/
- A low-resolution DEM three map widths wide for the horizon skirt -> data/raw/testmap/skirt.tif
- NHDPlus medium-resolution flowlines (stream order 2+, with drainage area) for the map -> data/raw/testmap/nhd_flowlines.geojson
- USDA NASS Cropland Data Layer 2024 (30 m, Albers) over the map -> data/raw/testmap/cdl_2024.tif. Used for where
  grass and irrigation are, not for field outlines (real fields follow the real survey grid, not the game's)

Cached: existing files are not downloaded again. Run: python -I tools/testmap/fetch_terrain.py
"""
import json
import re
from concurrent.futures import ThreadPoolExecutor
import sys
import time
import urllib.parse
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import map_config as cfg

DEM_URL = "https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage"
NHD_URL = "https://hydro.nationalmap.gov/arcgis/rest/services/nhd/MapServer/4/query"
CHUNK = 2048


def fetch(url, path, tries=4):
    for k in range(tries):
        try:
            with urllib.request.urlopen(url, timeout=300) as r:
                data = r.read()
            if data[:2] not in (b"II", b"MM"):
                raise RuntimeError(data[:300])
            path.write_bytes(data)
            return
        except Exception as e:  # noqa: BLE001 - the server drops connections now and then
            print(f"  retry {k + 1}: {e}")
            time.sleep(5 * (k + 1))
    raise RuntimeError(f"failed: {url}")


def export_dem(e0, n0, e1, n1, px, path):
    q = dict(bbox=f"{e0},{n0},{e1},{n1}", bboxSR=cfg.EPSG, imageSR=cfg.EPSG, size=f"{px},{px}", format="tiff",
             pixelType="F32", noDataInterpretation="esriNoDataMatchAny", interpolation="RSP_BilinearInterpolation",
             f="image")
    fetch(DEM_URL + "?" + urllib.parse.urlencode(q), path)


def main():
    (cfg.RAW / "dem").mkdir(parents=True, exist_ok=True)
    n = (cfg.GRID - 1) // CHUNK
    s = cfg.CELL

    def chunk(ij):
        ci, cj = ij
        path = cfg.RAW / "dem" / f"dem_{ci}_{cj}.tif"
        if path.exists():
            return
        # pixel centres on samples ci*CHUNK .. ci*CHUNK + CHUNK (shared edges), so the bbox is half a cell wider
        e0 = cfg.E0 + (ci * CHUNK - 0.5) * s
        n0 = cfg.N0 + (cj * CHUNK - 0.5) * s
        export_dem(e0, n0, e0 + (CHUNK + 1) * s, n0 + (CHUNK + 1) * s, CHUNK + 1, path)
        print(f"DEM chunk {ci},{cj}", flush=True)

    with ThreadPoolExecutor(4) as pool:
        list(pool.map(chunk, [(ci, cj) for cj in range(n) for ci in range(n)]))

    skirt = cfg.RAW / "skirt.tif"
    if not skirt.exists():
        ss = 3 * cfg.SIZE_M / (cfg.SKIRT_RES - 1)
        e0, n0 = cfg.E0 - cfg.SIZE_M - ss / 2, cfg.N0 - cfg.SIZE_M - ss / 2
        print("skirt DEM")
        export_dem(e0, n0, e0 + cfg.SKIRT_RES * ss, n0 + cfg.SKIRT_RES * ss, cfg.SKIRT_RES, skirt)

    nhd = cfg.RAW / "nhd_flowlines.geojson"
    if not nhd.exists():
        feats, off = [], 0
        pad = 2000
        while True:
            q = dict(where="StreamOrde>=2", geometry=f"{cfg.E0 - pad},{cfg.N0 - pad},{cfg.E0 + cfg.SIZE_M + pad},"
                     f"{cfg.N0 + cfg.SIZE_M + pad}", geometryType="esriGeometryEnvelope", inSR=cfg.EPSG,
                     outSR=cfg.EPSG, spatialRel="esriSpatialRelIntersects",
                     outFields="GNIS_NAME,StreamOrde,TotDASqKM,QA_MA,FTYPE,Hydroseq,DnHydroseq", returnGeometry="true",
                     f="geojson", resultOffset=off, resultRecordCount=2000)
            d = json.load(urllib.request.urlopen(NHD_URL + "?" + urllib.parse.urlencode(q), timeout=300))
            feats += d["features"]
            print(f"NHD flowlines: {len(feats)}")
            if not (d.get("exceededTransferLimit") or d.get("properties", {}).get("exceededTransferLimit")):
                break
            off += 2000
        nhd.write_text(json.dumps({"type": "FeatureCollection", "features": feats}))

    cdl = cfg.RAW / "cdl_2024.tif"
    if not cdl.exists():
        from pyproj import Transformer
        t = Transformer.from_crs(cfg.EPSG, 5070, always_xy=True)
        pad = 3000
        pts = [t.transform(e, n) for e in (cfg.E0 - pad, cfg.E0 + cfg.SIZE_M + pad)
               for n in (cfg.N0 - pad, cfg.N0 + cfg.SIZE_M + pad)]
        bbox = ",".join(f"{v:.0f}" for v in (min(p[0] for p in pts), min(p[1] for p in pts),
                                              max(p[0] for p in pts), max(p[1] for p in pts)))
        r = urllib.request.urlopen("https://nassgeodata.gmu.edu/axis2/services/CDLService/GetCDLFile?year=2024&bbox="
                                   + bbox, timeout=300).read().decode()
        url = re.search(r"<returnURL>(.*?)</returnURL>", r).group(1)
        cdl.write_bytes(urllib.request.urlopen(url, timeout=600).read())
        print("CDL 2024")


if __name__ == "__main__":
    main()

"""The test map's GPS data: the road network as a routable graph and a base map for the minimap (TestMapMinimap).

Reads build/network.json and build/streams.json (roads.py, terrain.py) and the Unity data world.py wrote (heights, land
cover splats, buildings), and writes into the Unity project's Resources (gitignored with the rest of Generated/):

- TestMapNav.json: junctions and the roads between them in world metres (x east, z north, origin at the map centre),
  each road piece with its class (state, paved, gravel), its name and its centre line. Gravel and paved strokes that turn
  a corner are split at the corner, so every piece has one name: Rd i runs east-west on the section line (i + 0.5) mi
  north of the map's south edge, Ave i north-south (i + 0.5) mi east of its west edge (TestMapWorld.GridPosition's
  names); state routes keep theirs. Also the radar site (WSR-88D KDDC, Dodge City) in the same frame.
- TestMapNavBase.png: 2048 x 2048 (39 m a pixel), north up: land cover in the splat colours, hillshade, rivers and
  creeks, farmsteads. Roads are not in it: the minimap draws them as lines.

For the real-data maps (--map dixie, --map midwest): DixieMapNav / MidwestMapNav (.json and Base.png) in
Assets/TestMap/Generated<Map>/Resources, every road piece named after its real road, the water polygons in the base map,
and the radar site from map_config (KDGX for Dixie, KILX for the Midwest).

Run after world.py (or real/places.py): python -I tools/testmap/navmap.py [--map dixie|midwest] [build dir]. The build dir defaults to data/raw/testmap/build and
falls back to Supercell Lab 2.0's (../supercell-lab-2/data/raw/testmap/build), where this map's network was made.
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, str(Path(__file__).resolve().parent))
import map_config as cfg

OUT = cfg.UNITY_GEN / "Resources"
NAME = {"dixie": "DixieMapNav", "midwest": "MidwestMapNav"}.get(cfg.MAP, "TestMapNav")
RES = 2048
CLASSES = {"state": 0, "paved": 1, "gravel": 2}
# WSR-88D KDDC (Dodge City, KS): 37.7608 N, 99.9689 W, the site about 789 m above sea level (NWS site list); the
# antenna is taken 20 m up its tower
RADAR = dict(name="KDDC", lat=37.76083, lon=-99.96889, elev=789.0 + 20.0)
if "real" in cfg.SPEC:            # the real-data maps' radar (map_config: site, and how sure it is)
    RADAR = cfg.SPEC["real"]["radar"]
# the splat layers' colours (world.py's preview)
COLOURS = np.array([(196, 168, 112), (78, 106, 44), (120, 96, 70), (140, 128, 86), (86, 106, 46), (150, 80, 42),
                    (190, 172, 140), (126, 114, 98)]) / 255.0
if "real" in cfg.SPEC:            # real/places.py's layer colours
    COLOURS = np.array([(96, 124, 52), (92, 80, 66), (88, 104, 60), (52, 66, 34), (90, 120, 50), (70, 80, 52),
                        (192, 176, 144), (146, 122, 90)]) / 255.0


def build_dir():
    if len(sys.argv) > 1:
        return Path(sys.argv[1])
    for d in (cfg.OUT, cfg.ROOT.parent / "supercell-lab-2" / "data" / "raw" / cfg.SPEC["raw"] / "build"):
        if (d / "network.json").exists():
            return d
    raise FileNotFoundError("network.json not found: run roads.py, or pass the build directory")


def rdp(p, tol):
    """Douglas-Peucker: the points of polyline p (n x 2) needed to stay within tol metres of it."""
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


def grid_name(a, b):
    """The section-line name of a straight piece from a to b (map frame)."""
    d = b - a
    m = (a + b) / 2 / cfg.MILE
    return f"Rd {round(m[1] - 0.5)}" if abs(d[0]) >= abs(d[1]) else f"Ave {round(m[0] - 0.5)}"


def graph(net):
    nodes = [list(map(float, n)) for n in net["nodes"]]
    edges = []
    for st in net["strokes"]:
        pts = np.array(st["pts"], float)
        js = sorted(st["junctions"], key=lambda j: j[1])
        for (na, ia), (nb, ib) in zip(js, js[1:]):
            seg = pts[ia:ib + 1]
            if len(seg) < 2:
                continue
            if st["cls"] == "state" or cfg.MAP != "plains":
                pieces = [(na, nb, seg, st["name"])]
            else:
                # split at the corners where a grid road turns from one section line onto the next
                cut = [0]
                for k in range(1, len(seg) - 1):
                    if grid_name(seg[k - 1], seg[k]) != grid_name(seg[k], seg[k + 1]):
                        cut.append(k)
                cut.append(len(seg) - 1)
                pieces = []
                prev = na
                for c0, c1 in zip(cut, cut[1:]):
                    end = nb if c1 == len(seg) - 1 else len(nodes)
                    if end == len(nodes):
                        nodes.append([float(seg[c1, 0]), float(seg[c1, 1])])
                    pieces.append((prev, end, seg[c0:c1 + 1], grid_name(seg[c0], seg[c1])))
                    prev = end
            for a, b, p, name in pieces:
                p = rdp(p, 1.0)
                h = cfg.SIZE_M / 2
                edges.append(dict(a=int(a), b=int(b), cls=CLASSES[st["cls"]], name=name,
                                  pts=[round(float(v), 2) for xy in p for v in (xy[0] - h, xy[1] - h)]))
    h = cfg.SIZE_M / 2
    return [round(v, 2) for n in nodes for v in (n[0] - h, n[1] - h)], edges


def radar_site(datum):
    from pyproj import Transformer
    e, n = Transformer.from_crs(4326, cfg.EPSG, always_xy=True).transform(RADAR["lon"], RADAR["lat"])
    return dict(name=RADAR["name"], x=round(e - (cfg.E0 + cfg.SIZE_M / 2), 1), z=round(n - (cfg.N0 + cfg.SIZE_M / 2), 1),
                y=round(RADAR["elev"] - datum, 1), lat=RADAR["lat"], lon=RADAR["lon"])


def base_map(meta, streams, buildings):
    T, step = cfg.TILES, 4
    R = cfg.SPLAT_RES // step
    img = np.zeros((T * R, T * R, 3))
    hgt = np.zeros((T * R, T * R))
    for r in range(T):
        for c in range(T):
            for half in range(2):
                a = np.asarray(Image.open(cfg.UNITY_DATA / "splat" / f"s_{c}_{r}_{half}.png"))[::-1][::step, ::step] / 255.0
                img[r * R:(r + 1) * R, c * R:(c + 1) * R] += np.tensordot(a, COLOURS[4 * half:4 * half + 4], 1)
            raw = np.fromfile(cfg.UNITY_DATA / "heights" / f"h_{c}_{r}.raw", "<u2").reshape(cfg.TILE_RES, cfg.TILE_RES)
            hgt[r * R:(r + 1) * R, c * R:(c + 1) * R] = raw[:-1:step, :-1:step] / 65535.0 * (meta["hmax"] - meta["hmin"]) + meta["hmin"]
    # hillshade, light from the north-west
    px = cfg.SIZE_M / (T * R)
    gz, gx = np.gradient(hgt * 6.0, px)
    lx, ly, lz = -0.5, 0.5, 0.707
    shade = np.clip((-gx * lx - gz * ly + lz) / np.sqrt(gx * gx + gz * gz + 1.0), 0, 1)
    img = np.clip(img * (0.45 + 0.75 * shade[..., None]), 0, 1)
    wz = build_dir() / "water.npz"
    if wz.exists():                  # the real-data maps' rivers and lakes (polygons, not stream lines)
        k = np.load(wz)["kind"][::step, ::step][:T * R, :T * R]
        img[k > 0] = np.array([58, 92, 120]) / 255.0
    im = Image.fromarray((img[::-1] * 255 + 0.5).astype(np.uint8)).resize((RES, RES), Image.LANCZOS)

    s = RES / cfg.SIZE_M
    dr = ImageDraw.Draw(im)
    def pix(x, z):
        return x * s, (cfg.SIZE_M - z) * s
    water = (58, 92, 120)
    for st in sorted(streams, key=lambda t: t["width"]):
        if st["kind"] == "draw" or st["width"] <= 0 or st.get("polygon"):
            continue
        w = max(1, round(st["width"] * s * 1.5))
        dr.line([pix(x, z) for x, z in st["pts"]], fill=water, width=w, joint="curve")
    h = cfg.SIZE_M / 2
    for b in buildings:
        cx, cz = pix(b["x"] + h, b["z"] + h)
        r = max(0.6, 0.5 * max(b["w"], b["l"]) * s)
        dr.rectangle([cx - r, cz - r, cx + r, cz + r], fill=(205, 200, 190))
    return im


def main():
    bd = build_dir()
    meta = json.load(open(cfg.UNITY_DATA / "meta.json"))
    net = json.load(open(bd / "network.json"))
    nodes, edges = graph(net)
    OUT.mkdir(parents=True, exist_ok=True)
    nav = dict(sizeM=cfg.SIZE_M, radar=radar_site(meta["datum"]), nodes=nodes, edges=edges)
    json.dump(nav, open(OUT / f"{NAME}.json", "w"), separators=(",", ":"))
    km = sum(np.hypot(*np.diff(np.array(e["pts"]).reshape(-1, 2), axis=0).T).sum() for e in edges) / 1000
    print(f"{len(nodes) // 2} junctions and corners, {len(edges)} road pieces, {km:.0f} km of road; radar {nav['radar']}")
    streams = json.load(open(bd / "streams.json"))
    buildings = json.load(open(cfg.UNITY_DATA / "buildings.json"))["items"]
    base_map(meta, streams, buildings).save(OUT / f"{NAME}Base.png", optimize=True)
    print(f"wrote {OUT / NAME}.json and {NAME}Base.png")


if __name__ == "__main__":
    main()

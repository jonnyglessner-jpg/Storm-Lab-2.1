"""Shared constants for the test maps (50 x 50 mile drivable worlds for testing the storms).

Three maps, picked with `--map plains|dixie|midwest` on a tool's command line (plains when left out):

- plains: USGS 3DEP bare-earth elevation of a tract of south-west Kansas between Dodge City and Larned, with the
  Arkansas and Pawnee rivers. The roads are designed, not real: a complete 1-mile section-line grid plus state routes,
  laid out to Kansas practice so every road is drivable end to end. Built by the scripts in this folder.
- dixie: the lower Mississippi valley centred on Natchez, MS: the loess bluffs and the Homochitto hills on the
  Mississippi side, the Concordia Parish delta and its levee on the Louisiana side, one bridge over the river. Built
  by the scripts in real/ (README there), which reuse the plains road, profile and mesh machinery.
- midwest: the Illinois till plain centred on Effingham, IL: corn and soybean ground on the mile grid, timber along
  the Little Wabash, the Embarras and the Kaskaskia, Lake Shelbyville's south end, I-57 and I-70. Built by real/ too.

The two real-data maps (dixie, midwest) carry their sources and rules in SPEC["real"]: the DEM, WorldCover and NHDPlus
tiles, the states, the one town kept, the big river and its bridge (dixie), the radar site and the car's start points.

Frames: NAD83 / UTM (zone 14N for plains, 15N for dixie, 16N for midwest) for the source data. Map frame: metres from the map's
south-west corner, x east, z north. Unity world: +X east, +Z north, +Y = elevation in metres, origin at the map centre.
"""
import sys
from pathlib import Path


def _pick():
    if "--map" in sys.argv:
        k = sys.argv.index("--map")
        name = sys.argv[k + 1]
        del sys.argv[k:k + 2]          # so the tools' own positional arguments stay where they were
        return name
    return "plains"


MAP = _pick()
MAPS = {
    # lat 38.07 N, lon 99.55 W
    "plains": dict(epsg=26914, centre=(451718.0, 4213067.0), raw="testmap", unity_data="TestMapData", gen="Generated",
                   preview="testmap", title="Plains test map"),
    # Natchez, MS: lat 31.5604 N, lon 91.4032 W (the bluff above Natchez-Under-the-Hill)
    "dixie": dict(epsg=26915, centre=(651550.0, 3492817.0), raw="testmap/dixie", unity_data="DixieMapData",
                  gen="GeneratedDixie", preview="testmap/dixie", title="Dixie map", real=dict(
                      town="Natchez", downtown=(250.0, 0.0), downtown_r=650.0,
                      dem13=["n32w092", "n32w091"], dem1=[f"n{a}w{o:03d}" for a in (31, 32, 33) for o in (93, 92, 91)],
                      worldcover=["N30W093"], hu4=["0804", "0805", "0806", "0807"], states=["Mississippi", "Louisiana"],
                      big_river="Mississippi River", bridge_route="US 84", levees=True,
                      lowland_m=32.0,            # ground below this (m) is the floodplain: bottomland forest, delta farms
                      pine=(0.25, 0.85), farm_p=0.18,
                      # WSR-88D KDGX (Jackson / Brandon, MS), about 160 km to the north-east. Site position and ground
                      # elevation as remembered, not yet checked against the NWS site list; antenna 20 m up
                      radar=dict(name="KDGX", lat=32.27994, lon=-89.98444, elev=151.0 + 20.0),
                      spawns=[("US 61", "US 61"), ("Natchez Trace", "Natchez Trace Parkway"), ("US 84 / US 98", "US 84 / US 98"),
                              ("LA 15", "LA 15, the delta"), ("MS 33", "MS 33")])),
    # Effingham, IL: lat 39.1200 N, lon 88.5434 W (downtown, by the courthouse)
    "midwest": dict(epsg=26916, centre=(366577.0, 4331227.0), raw="testmap/midwest", unity_data="MidwestMapData",
                    gen="GeneratedMidwest", preview="testmap/midwest", title="Midwest map", real=dict(
                        town="Effingham", downtown=(0.0, 0.0), downtown_r=450.0,
                        dem13=[f"n{a}w{o:03d}" for a in (39, 40) for o in (90, 89)],
                        dem1=[f"n{a}w{o:03d}" for a in (39, 40, 41) for o in (90, 89, 88)],
                        worldcover=["N36W090", "N39W090"], hu4=["0512", "0514", "0713", "0714"], states=["Illinois"],
                        big_river=None, bridge_route=None, levees=False, lowland_m=None,
                        pine=(0.02, 0.02), farm_p=0.55,
                        # WSR-88D KILX (Lincoln, IL; the NWS office whose area takes in Effingham County), about 135 km
                        # to the north-west. Site position and ground elevation as remembered, not yet checked against
                        # the NWS site list; antenna 20 m up
                        radar=dict(name="KILX", lat=40.15052, lon=-89.33676, elev=177.0 + 20.0),
                        spawns=[("I-57", "I-57"), ("I-70", "I-70"), ("US 45", "US 45"), ("US 40", "US 40, the National Road"),
                                ("IL 33", "IL 33"), ("IL 32", "IL 32")])),
}
if MAP not in MAPS:
    raise SystemExit(f"--map {MAP}: unknown map (one of {', '.join(MAPS)})")
SPEC = MAPS[MAP]

ROOT = Path(__file__).resolve().parents[2]
RAW = ROOT / "data" / "raw" / SPEC["raw"]
OUT = RAW / "build"                                            # large generated data (gitignored with data/raw)
UNITY_DATA = ROOT / "tests" / "StormLabTest" / SPEC["unity_data"]   # read by the Unity builder, not imported
UNITY_GEN = ROOT / "tests" / "StormLabTest" / "Assets" / "TestMap" / SPEC["gen"]
PREVIEW = ROOT / "docs" / SPEC["preview"]

EPSG = SPEC["epsg"]
MILE = 1609.344

# Map extent: 50 x 50 miles about the map's centre.
MAP_MILES = 50
SIZE_M = MAP_MILES * MILE                    # 80467.2
E0 = SPEC["centre"][0] - SIZE_M / 2
N0 = SPEC["centre"][1] - SIZE_M / 2

# Unity terrain: 10 x 10 tiles of 5 miles, 1025 height samples per side (7.86 m spacing, shared edges).
TILES = 10
TILE_M = SIZE_M / TILES                      # 8046.72
TILE_RES = 1025
GRID = TILES * (TILE_RES - 1) + 1            # 10241 samples per side over the whole map
CELL = SIZE_M / (GRID - 1)                   # 7.858 m

# Land-cover splat maps: per tile, one texel per height cell.
SPLAT_RES = 1024

# Horizon skirt: low-detail terrain around the map, 3 x 3 map widths, no roads.
SKIRT_RES = 1537                             # samples per side over 3 x SIZE_M (157 m)

# The storms' 30 x 30 mile game map sits in the middle, so storms are visible coming in from 10 miles out.
SCENARIO_MILES = 30


def map_to_world(x, z):
    return x - SIZE_M / 2, z - SIZE_M / 2


def utm_to_map(e, n):
    return e - E0, n - N0

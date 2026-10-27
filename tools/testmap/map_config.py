"""Shared constants for the test maps (50 x 50 mile drivable worlds for testing the storms).

Two maps, picked with `--map plains|dixie` on any tool's command line (plains when left out):

- plains: USGS 3DEP bare-earth elevation of a tract of south-west Kansas between Dodge City and Larned, with the
  Arkansas and Pawnee rivers. The roads are designed, not real: a complete 1-mile section-line grid plus state routes,
  laid out to Kansas practice so every road is drivable end to end. Built by the scripts in this folder.
- dixie: the lower Mississippi valley centred on Natchez, MS: the loess bluffs and the Homochitto hills on the
  Mississippi side, the Concordia Parish delta and its levee on the Louisiana side, one bridge over the river. Built
  by the scripts in dixie/ (README there), which reuse the plains road, profile and mesh machinery.

Frames: NAD83 / UTM (zone 14N for plains, 15N for dixie) for the source data. Map frame: metres from the map's
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
                  gen="GeneratedDixie", preview="testmap/dixie", title="Dixie map"),
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

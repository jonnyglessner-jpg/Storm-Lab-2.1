"""Shared constants for the plains test map (a 50 x 50 mile drivable world for testing the storms).

The terrain is real: USGS 3DEP bare-earth elevation of a tract of south-west Kansas between Dodge City and Larned,
with the Arkansas and Pawnee rivers. The roads are designed, not real: a complete 1-mile section-line grid plus
state routes, laid out to Kansas practice so every road is drivable end to end.

Frames: NAD83 / UTM zone 14N (EPSG:26914) for the source data. Map frame: metres from the map's south-west
corner, x east, z north. Unity world: +X east, +Z north, +Y = elevation in metres, origin at the map centre.
"""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
RAW = ROOT / "data" / "raw" / "testmap"
OUT = ROOT / "data" / "raw" / "testmap" / "build"        # large generated data (gitignored with data/raw)
UNITY_DATA = ROOT / "tests" / "StormLabTest" / "TestMapData"   # read by the Unity builder, not imported
PREVIEW = ROOT / "docs" / "testmap"

EPSG = 26914
MILE = 1609.344

# Map extent: 50 x 50 miles, centred at lat 38.07 N, lon 99.55 W (UTM 451718 E, 4213067 N).
MAP_MILES = 50
SIZE_M = MAP_MILES * MILE                    # 80467.2
E0 = 451718.0 - SIZE_M / 2
N0 = 4213067.0 - SIZE_M / 2

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

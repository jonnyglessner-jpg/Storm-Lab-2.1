# Plains test map

A 50 x 50 mile drivable stretch of south-west Kansas for testing the storms: real ground, a designed road grid.
Scene: `tests/StormLabTest/Assets/Scenes/TestMap.unity` (**StormLab > Test Map > Open Test Map**).

Two more maps, Dixie (50 x 50 miles round Natchez, MS) and Midwest (round Effingham, IL), are built from real data by
the scripts in [real/](real/README.md) with this folder's road, profile, mesh and GPS code (`navmap.py --map dixie|midwest`;
`map_config.py` holds all three maps' frames).

![land cover](../../docs/testmap/landcover.png)

## What is real and what is designed

| Part | Source |
| --- | --- |
| Terrain | USGS 3DEP bare-earth elevation (The National Map), resampled to 7.86 m. The tract is centred at 38.07 N, 99.55 W (UTM 14N 451718 E, 4213067 N): from just east of Dodge City to Larned, Hodgeman / Ford / Edwards / Pawnee counties. Real road and rail embankments, lagoons and the towns' street grids are smoothed out (towns found from the CDL's developed classes). |
| Streams | USGS NHDPlus medium resolution, moved onto the DEM's real channel. The Arkansas and the Pawnee are the two rivers; creeks draining more than 40 km2 get water and tree lines. |
| Land cover | USDA NASS Cropland Data Layer 2024, read per quarter section, so the mix and where it is are real (grass on the breaks and sandhills, irrigated circles in the sand prairie south-east of the Arkansas). Fields are drawn on the game's section grid: quarter, half and strip fields, centre pivots with dry corners. |
| Roads | Designed (real roads have dead ends and correction-line jogs). |
| Farmsteads, trees, power lines | Placed by rules from the reference photos. |

## Road rules

- A gravel county road on every section line, both ways (24 ft, crowned 3 %), the whole map.
- State routes K-19 and K-156 (east-west), K-283 and K-183 (north-south): two 12 ft lanes, 6 ft paved shoulders,
  crowned 2 %, superelevated 4 % in curves. They hold section lines and change line only on a reverse curve (two
  1,200 m arcs and a tangent) between section corners. The section road they leave stops at the next corner, so it
  meets the route at a right-angle crossing.
- Paved county roads on Rd 25 and Ave 23 (22 ft blacktop).
- The Arkansas and the Pawnee are bridged by every state and paved road, and by county roads only every 6.5 km
  and 3.2 km (square crossings only). Between bridges the grid steps along the valley. No road dead-ends: a
  road that would end at the bank stops at the corner before it, and any corner where a road turns is a 150 m curve.
- Creeks draining more than 100 km2 are bridged; smaller creeks and draws cross on culvert fills.
- Vertical profiles: gravel follows the land (20 m smoothing, 8 % max), paved 40 m and 7 %, state routes 100 m
  and 4.5 %. Junction heights are shared. State routes are marked no-passing on curves, near bridges, near junctions
  with other state routes and where a crest hides a car within 300 m.
- Junctions: where a state route passes a county road it stays continuous with its markings and the county road
  ends in a mouth with 10-15 m curb returns; equal roads meet on a pad with rounded corners.
- The checks run by `roads.py`: no dead ends away from the map edge, no junctions closer than 150 m (closest is
  511 m), smallest angle between legs 70 degrees.

## Build

```
python -I tools/testmap/fetch_terrain.py   # downloads (~500 MB, cached in data/raw/testmap)
python -I tools/testmap/terrain.py         # DEM mosaic and cleanup, streams, CDL resample
python -I tools/testmap/roads.py           # road network + docs/testmap/network.png
python -I tools/testmap/textures.py        # ground and road textures
python -I tools/testmap/world.py           # profiles, grading, land cover, objects, meshes -> TestMapData/
python -I tools/testmap/navmap.py          # the GPS's road graph and the map's base image -> Assets/TestMap/Generated/Resources
```

Then **StormLab > Test Map > Build World** in Unity (or headless:
`-executeMethod TestMapBuilder.BuildFromCommandLine`). The generated data (`TestMapData/`, `Assets/TestMap/Generated`,
the scene) is not in git.

Checks: **StormLab > Test Map > Run Drive Test + Screenshots** (headless: `-executeMethod
TestMapTests.RunFromCommandLine`). An autopilot drives the state routes, paved roads and longest gravel roads at
design speed; the report is `validation/reports/TestMapReport.md`, the views `Screenshots/testmap_*.png`.

## Frames

Map frame: metres from the south-west corner, x east, z north. Unity world: origin at the map centre, y =
elevation minus the datum in `meta.json` (680 m). The storms' 30 x 30 mile game map is centred on the world origin
(`worldOriginKm` = 24.14 km on both axes), so storms come in over 10 miles of map.

## Known limits

- Trees beyond 5 km are not drawn (no billboards), so far tree lines vanish; the ground colour keeps the corridors.
- The horizon skirt is coarse (118 m) and meets the map edge with a small step.
- Coordinates reach 40 km from the origin without a floating origin (HDRP renders camera-relative, physics is fine at
  that range, but very fine detail can shimmer at the far corners).

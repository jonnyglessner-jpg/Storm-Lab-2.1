# Dixie map

A 50 x 50 mile drivable stretch of the lower Mississippi valley centred on Natchez, MS (31.5604 N, 91.4032 W; UTM 15N
651550 E, 3492817 N). It is the plains test map's sibling, built with the same process and file formats: the ground,
water and land cover come from official data, the roads are a generalised recreation of the real ones, and the
buildings and trees are placed by rules. Scene: `tests/StormLabTest/Assets/Scenes/DixieMap.unity` (**StormLab > Dixie
Map > Open Dixie Map**).

The map: the loess bluffs and the Homochitto hills on the Mississippi side, the Concordia Parish delta with its oxbow
lakes, bayous and the main-line levee on the Louisiana side, and the river between them with one crossing, the
Natchez-Vidalia bridge. Natchez is the only town for now. Vidalia, Ferriday, Fayette, Washington and the rest are
smoothed out of the ground, their streets left out, and only the highways through them are kept. Small towns can be
added later. `places.py` would get the buildings, and `roadnet.py` would keep the town's streets (see `natchez_mask`).

![land cover](../../../docs/testmap/dixie/landcover.png)

## What is real and what is designed

| Part | Source |
| --- | --- |
| Terrain | USGS 3DEP 1/3 arc-second DEM (lidar-derived here), tiles n32w092 and n32w091 from The National Map's S3 staged products, resampled to 7.86 m. On flat ground the fills of real roads and railways are clipped as on the plains map. The hills and the levees are left alone. |
| Levees | Found in the DEM (NHD has no levee lines here): ridges at least 3.5 m above the floodplain and at least 1.5 km long. That catches the Mississippi main-line levee on the Louisiana side and a few smaller ones. |
| Water | USGS NHDPlus HR area and waterbody polygons: the Mississippi, oxbow lakes, and ponds of 0.4 ha and up. The Mississippi fills its polygon bank to bank, because the lidar caught it at low water and the storm season is its high-water season. Its level is taken from the lidar's hydro-flattened water surface plus 0.5 m. The other rivers and lakes are wet only where that surface says there was water. |
| Streams | NHDPlus HR flowlines, moved onto the DEM's channel as on the plains map. A stream counts as a river (wide, always bridged) when most of it runs through NHD river polygons, or when it drains 500 km2 or more on the Mississippi side. That gives the Homochitto, Buffalo, Coles Creek, Tensas, Black River and Bayou Cocodrie. NHDPlus drainage areas in the delta are unreliable (side channels carry the Mississippi's whole basin), so they are not used there. |
| Land cover | ESA WorldCover 2021 (10 m). The plains map's USDA Cropland Data Layer server could not be reached from the build machine, and crop type matters little in the storm season. Forest is 67 % of the map. |
| Roads | USGS National Transportation Dataset, generalised. See below. |
| Natchez | The town is the area where the USGS streets are town-dense (5 km of road per km2 or more) or WorldCover is built up, connected to downtown. Its streets are the real ones. Its buildings are placed lot by lot: brick rows downtown, houses elsewhere, stores with parking on the US routes, a few churches. |
| Homes, farms, trees, power lines | Placed by rules. |

## Roads

- Kept: every US and state route and the Natchez Trace Parkway (state class); arterials without a route number (paved);
  named rural roads that run 800 m or more (paved from 6 km up, else gravel); the named streets of Natchez (paved).
- Divided highways become one two-lane road. The network is drawn into a 10 m raster (highways 50 m wide), thinned to
  a centre line and traced back to lines. Junctions closer than 45 m are merged. Whiskers, bubbles and interchange
  loops are removed. Forks narrower than 35 degrees are squared up. Lines are smoothed, with a minimum radius of 60 m
  for state roads and 18-20 m for the rest.
- One road crosses the Mississippi (US 84 / 425), on a 26 m high bridge over the water and the batture. Every other
  road that reached the river stops at the bank.
- Dead ends: rural ones shorter than 1.2 km are pruned, longer ones stay (hill roads end at the last house). In
  Natchez, streets of 100 m or more stay.
- Profiles, grading, junctions and bridges use the plains map's code (`profiles.py`, `world.py`, `meshes.py`).

## Forest

Drawing 67 % forest tree by tree is out of the question. The forest interior is a canopy shell: a 31 m mesh at crown
height (18-26 m) with a crown texture, pine or hardwood. It starts 22 m from a road's edge, and no shell quad touches
a road. Real trees stand in front of it: the front row of the woods along every road, the forest edges within 45 m of
a road, and Natchez's wooded lots and ravines. That means loblolly pine in the hills, oak and sweetgum on the
floodplain, and bald cypress round the lakes, about 1.3 million trees in all. The shell has no collider and casts
shadows.

What the last run made: 3,040 km of road (1,000 km US and state routes), 53 bridges, 6,337 houses, 728 stores and 60
churches in Natchez, 1,446 rural homes (222 delta farm headquarters, 434 mobile homes), 1,228 power lines, the canopy
shell over 61 % of the map.

## Build

Python 3 with numpy, scipy, shapely, tifffile, imagecodecs (the 3DEP and WorldCover tiles are LZW / deflate
compressed), pyproj, pillow, matplotlib and scikit-image.

```
python -I tools/testmap/dixie/fetch.py        # downloads (~3 GB, cached in data/raw/testmap/dixie), cut to the map
python -I tools/testmap/dixie/land.py         # DEM, levees, water, streams, land cover, Natchez outline
python -I tools/testmap/dixie/roadnet.py      # road network + docs/testmap/dixie/network.png
python -I tools/testmap/dixie/groundtex.py    # ground, canopy and road textures
python -I tools/testmap/dixie/places.py       # profiles, grading, water, buildings, forest, meshes -> DixieMapData/
python -I tools/testmap/navmap.py --map dixie # the GPS's road graph and base map (radar KDGX)
```

Then **StormLab > Dixie Map > Build World** in Unity (or headless:
`-executeMethod TestMapBuilder.BuildDixieFromCommandLine`). The generated data (`DixieMapData/`,
`Assets/TestMap/GeneratedDixie`, the scene) is not in git.

## Known limits

- Not seen in Unity yet: the Python stages ran and their previews are in `docs/testmap/dixie`, but the world has not
  been built or driven. Expect a round of fixes on the first build (junction pads where real roads meet at odd angles,
  the canopy shell's look, tree counts).
- Divided highways (US 61, US 84 east of Natchez) are two-lane, and interchanges are at grade.
- The radar is KDGX (Jackson / Brandon, MS), about 160 km away. Its site position was taken from memory and still
  needs checking against the NWS site list.
- Crop type is a patchwork of tilled ground and young crops, not the real fields.
- Colours are picked by eye, not calibrated against photos of the region.

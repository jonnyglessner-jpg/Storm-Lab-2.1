"""Procedural textures for the Dixie map: eight tiling ground layers (with normal maps), the forest canopy seen from
above, and the plains map's road surfaces (textures.py).

The season is the Dixie storm season, March to May: pastures and hay meadows fresh green, delta fields tilled dark
grey-brown or with young cotton, corn and soybean rows, hardwoods in new leaf, pines dark, sand bars pale. Colours are
sRGB and, like the plains map's, picked by eye; they are not calibrated against photographs of the region yet.

Writes tests/StormLabTest/Assets/TestMap/GeneratedDixie/Textures/*.png. Run: python -I tools/testmap/dixie/groundtex.py
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import _paths  # noqa: F401,E402
import numpy as np

import textures as T
from textures import N, colourise, fbm, normal_map, pnoise, rows, save, specks

# terrain layer order is shared with places.py and the Unity builder
LAYERS = ["pasture", "tilled", "youngcrop", "forest", "lawn", "wetland", "sand", "dirt"]
SMOOTH = {"pasture": 0.2, "tilled": 0.12, "youngcrop": 0.18, "forest": 0.12, "lawn": 0.22, "wetland": 0.35,
          "sand": 0.15, "dirt": 0.1}


def ground_layers():
    S = (N, N)
    info = {}
    # pasture: bahia and bermuda grass in spring, clover patches, cow paths faintly
    g = fbm(S, (10, 50, 260), (0.5, 0.55, 0.45))
    clover = pnoise(S, 30)
    h = g + 0.4 * pnoise(S, 320, (1, 2))
    info["pasture"] = (colourise(g + 0.25 * clover, [(-2, (60, 84, 34)), (-0.5, (84, 112, 46)), (0.6, (106, 132, 58)),
                                                      (2, (136, 150, 76))]), h, 1.3)
    # tilled delta field: dark grey-brown alluvial clay in bedded rows (38 in) with clods and old stalks
    clods = fbm(S, (20, 80, 300), (0.6, 0.6, 0.5))
    r = rows(S, 0.97, 1.2, 0.03)
    h = clods + 1.2 * r
    col = colourise(clods * 0.8 + 0.5 * r, [(-2, (60, 52, 44)), (-0.4, (82, 72, 60)), (0.7, (104, 92, 76)),
                                             (2, (126, 114, 96))])
    col += specks(S, 0.015, 1)[..., None] * 35
    info["tilled"] = (col, h, 2.0)
    # young crop: emerging cotton / soybean rows over the same soil
    plants = (rows(S, 0.97, 4.0, 0.03) > 0.6) * np.clip(0.6 + 0.6 * pnoise(S, 200), 0, 1)
    soil = fbm(S, (20, 90, 300), (0.6, 0.5, 0.4))
    h = soil + 1.5 * plants
    col = colourise(soil, [(-2, (72, 62, 50)), (0, (96, 84, 68)), (2, (120, 108, 90))])
    green = colourise(pnoise(S, 150), [(-2, (52, 80, 30)), (2, (98, 128, 52))])
    col = col * (1 - plants[..., None]) + green * plants[..., None]
    info["youngcrop"] = (col, h, 1.6)
    # forest floor: oak and sweetgum leaf litter with pine straw and fern / palmetto patches
    lit = fbm(S, (25, 110, 400), (0.5, 0.6, 0.6))
    straw = pnoise(S, 380, (1, 6))
    ferns = np.clip(fbm(S, (6, 30), (0.7, 0.5)) - 0.6, 0, 1)
    h = lit + 0.4 * straw
    col = colourise(lit + 0.3 * straw, [(-2, (58, 44, 30)), (-0.4, (88, 66, 42)), (0.6, (112, 86, 56)), (2, (136, 110, 74))])
    col = col * (1 - ferns[..., None]) + np.array([62, 88, 40.0]) * ferns[..., None]
    info["forest"] = (col, h, 1.4)
    # lawn: mown St. Augustine / centipede, yards, ditches and road verges
    g = fbm(S, (15, 80, 300), (0.5, 0.5, 0.5))
    info["lawn"] = (colourise(g, [(-2, (56, 84, 32)), (-0.4, (76, 106, 42)), (0.8, (96, 126, 54)), (2, (118, 140, 70))]),
                    g + 0.5 * pnoise(S, 350, (1, 2)), 1.2)
    # wetland: wet olive sedge and smartweed over dark mud, standing water in the lows
    m = fbm(S, (8, 40, 200), (0.6, 0.5, 0.4))
    col = colourise(m, [(-2, (40, 44, 34)), (-0.3, (64, 72, 44)), (0.8, (92, 102, 58)), (2, (110, 116, 72))])
    info["wetland"] = (col, m, 1.0)
    # sand: Mississippi and Homochitto bars, pale tan with ripples and drift lines
    rip = np.sin(2 * np.pi * (np.arange(N)[:, None] / N * T.LAYER_M / 0.15 + 1.5 * pnoise(S, 4)))
    s = fbm(S, (6, 40, 200), (0.6, 0.3, 0.3))
    info["sand"] = (colourise(s + 0.2 * rip, [(-2, (158, 140, 112)), (0, (192, 176, 144)), (2, (218, 204, 174))]),
                    0.5 * rip + s, 1.0)
    # dirt: loess and clay in yards, gravel drives and cut banks, buff to tan
    d = fbm(S, (10, 60, 250), (0.6, 0.5, 0.4))
    peb = specks(S, 0.04, 1)
    info["dirt"] = (colourise(d, [(-2, (110, 90, 66)), (0, (146, 122, 90)), (2, (176, 154, 118))]) + peb[..., None] * 25,
                    d + peb * 1.5, 1.6)
    for name in LAYERS:
        col, h, strength = info[name]
        save(f"ground_{name}", col, alpha=np.full(S, SMOOTH[name]))
        save(f"ground_{name}_n", normal_map(h, strength) * 255)


def canopy():
    """The forest roof seen from above and from the side: rounded crowns with shaded gaps between them. One texture
    per forest type, tiling every CANOPY_M metres; the normal map gives the crowns their light and shade."""
    S = (N, N)
    yy, xx = np.mgrid[0:N, 0:N] / N
    out = {}
    for name, crown_m, pal in (
            ("canopy_hard", 9.0, [(-2, (28, 40, 20)), (-0.6, (58, 82, 36)), (0.4, (88, 116, 52)), (1.4, (122, 146, 70))]),
            ("canopy_pine", 6.5, [(-2, (18, 28, 18)), (-0.6, (36, 54, 32)), (0.4, (54, 76, 44)), (1.4, (74, 96, 58))])):
        n = int(round(CANOPY_M / crown_m))
        rng = np.random.default_rng(5 if name.endswith("hard") else 6)
        h = np.full(S, -1.0)
        for i in range(n):
            for j in range(n):
                cx, cy = (i + rng.uniform(0.15, 0.85)) / n, (j + rng.uniform(0.15, 0.85)) / n
                r = rng.uniform(0.45, 0.75) / n
                dx = (xx - cx + 0.5) % 1 - 0.5
                dy = (yy - cy + 0.5) % 1 - 0.5
                d2 = (dx * dx + dy * dy) / (r * r)
                h = np.maximum(h, np.sqrt(np.clip(1 - d2, 0, 1)) * rng.uniform(0.8, 1.2) - 0.2)
        leaves = fbm(S, (60, 250, 500), (0.5, 0.6, 0.4))
        hh = h * 2.5 + 0.4 * leaves
        # crowns darker toward their rims and mottled by leaf clusters (the normal map adds the sun's side)
        rim = np.clip(h, 0, 1) ** 0.5
        out[name] = (colourise(hh - 0.6 + 0.5 * leaves, pal) * (0.7 + 0.35 * rim[..., None]), hh)
    for name, (col, h) in out.items():
        save(name, col)
        save(name + "_n", normal_map(h, 3.0) * 255)


CANOPY_M = 48.0


def main():
    T.OUT.mkdir(parents=True, exist_ok=True)
    ground_layers()
    canopy()
    T.road_textures()
    json.dump(dict(layers=LAYERS, layer_m=T.LAYER_M, road_repeat_m=T.ROAD_REPEAT_M, canopy_m=CANOPY_M),
              open(T.OUT / "textures.json", "w"))
    print("textures written to", T.OUT)


if __name__ == "__main__":
    main()

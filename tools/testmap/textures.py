"""Procedural textures for the test map: eight tiling ground layers (with normal maps) and the road surfaces.

Every texture tiles: the noise is filtered white noise made periodic in the frequency domain. Colours are sRGB,
picked from the user's reference photos of south-west Kansas in late summer (golden wheat stubble, green corn,
rust sorghum, brown fallow, tan-olive range grass). Ground layers tile every LAYER_M metres in Unity.

Road textures: U runs across the whole road surface (0 = left edge, 1 = right edge, gravel edges included), V along
it, one repeat = ROAD_REPEAT_M (a 10 ft dash and 30 ft gap, the US two-lane standard).

Writes tests/StormLabTest/Assets/TestMap/Generated/Textures/*.png. Run: python -I tools/testmap/textures.py
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import map_config as cfg

OUT = cfg.ROOT / "tests" / "StormLabTest" / "Assets" / "TestMap" / "Generated" / "Textures"
N = 1024
LAYER_M = 12.0                 # ground layer tile size, metres
ROAD_REPEAT_M = 12.192         # 40 ft
rng = np.random.default_rng(11)

# terrain layer order is shared with world.py and the Unity builder
LAYERS = ["stubble", "greencrop", "fallow", "range", "grass", "sorghum", "sand", "dirt"]


def pnoise(shape, cutoff, aniso=(1.0, 1.0), power=0.0):
    """Periodic noise, std 1. cutoff: features per tile (gaussian low-pass); aniso stretches it (rows, cols)."""
    h, w = shape
    fy = np.fft.fftfreq(h)[:, None] * h / aniso[0]
    fx = np.fft.fftfreq(w)[None, :] * w / aniso[1]
    f = np.sqrt(fx ** 2 + fy ** 2)
    filt = np.exp(-(f / cutoff) ** 2)
    if power:
        filt *= (f + 1.0) ** -power
    n = np.real(np.fft.ifft2(np.fft.fft2(rng.normal(0, 1, shape)) * filt))
    return (n - n.mean()) / (n.std() + 1e-9)


def fbm(shape, cutoffs, weights, aniso=(1.0, 1.0)):
    return sum(w * pnoise(shape, c, aniso) for c, w in zip(cutoffs, weights))


def specks(shape, density, size):
    """Sparse bright or dark grains (pebbles, straw ends), one texel each, varying in strength."""
    m = (rng.random(shape) < density).astype(float)
    return m * (0.6 + 0.4 * np.clip(pnoise(shape, N / max(size, 1)), -1, 1))


def rows(shape, spacing_m, sharp=2.0, wobble=0.0):
    """Crop rows running along V (texture rows), spacing in metres."""
    h, w = shape
    u = np.arange(w)[None, :] / w * LAYER_M
    off = wobble * pnoise(shape, 6, (1, 8))
    s = np.cos(2 * np.pi * (u + off) / spacing_m)
    return np.sign(s) * np.abs(s) ** (1 / sharp)


def colourise(h, palette):
    """Map a field in roughly -2..2 through a list of (position, sRGB) stops."""
    pos = np.array([p for p, _ in palette])
    cols = np.array([c for _, c in palette], float)
    t = np.clip(h, pos[0], pos[-1])
    out = np.stack([np.interp(t, pos, cols[:, k]) for k in range(3)], -1)
    return out


def normal_map(height, strength):
    gy, gx = np.gradient(height)
    # periodic gradient at the edges
    gx[:, 0] = (height[:, 1] - height[:, -1]) / 2
    gx[:, -1] = (height[:, 0] - height[:, -2]) / 2
    gy[0, :] = (height[1, :] - height[-1, :]) / 2
    gy[-1, :] = (height[0, :] - height[-2, :]) / 2
    nx, ny, nz = -gx * strength, -gy * strength, np.ones_like(height)
    l = np.sqrt(nx ** 2 + ny ** 2 + nz ** 2)
    # image row 0 is the top (v = 1): flip y for OpenGL-style normals
    return np.stack([nx / l, -ny / l, nz / l], -1) * 0.5 + 0.5


def save(name, rgb, alpha=None):
    OUT.mkdir(parents=True, exist_ok=True)
    a = np.clip(rgb, 0, 255).astype(np.uint8)
    if alpha is not None:
        a = np.dstack([a, np.clip(alpha * 255, 0, 255).astype(np.uint8)])
    Image.fromarray(a).save(OUT / f"{name}.png", optimize=True)


def ground_layers():
    S = (N, N)
    info = {}
    # 0 wheat stubble: golden straw in drill rows (7.5 in) with soil showing between
    straw = fbm(S, (8, 40, 200), (0.5, 0.35, 0.4))
    r = rows(S, 0.19, 1.5, 0.02)
    soil = (r < -0.3).astype(float) * 0.8
    h = straw + 0.6 * r
    col = colourise(straw * 0.8 - soil * 1.2, [(-2, (120, 98, 70)), (-0.6, (165, 138, 92)), (0.3, (196, 168, 112)),
                                               (1.5, (222, 200, 150))])
    col += specks(S, 0.02, 1)[..., None] * 25
    info["stubble"] = (col, h, 1.2, 0.18)
    # 1 green crop: corn rows (30 in) seen from above, leaf mottling, dark gaps
    leaf = fbm(S, (30, 120, 300), (0.5, 0.5, 0.4))
    r = rows(S, 0.762, 3.0, 0.05)
    h = leaf * 0.6 + r
    col = colourise(leaf * 0.7 + r * 0.6, [(-2, (38, 52, 22)), (-0.5, (58, 82, 34)), (0.6, (84, 112, 46)),
                                           (2, (118, 140, 66))])
    info["greencrop"] = (col, h, 1.5, 0.25)
    # 2 fallow: tilled brown loam, clods and a little residue
    clods = fbm(S, (20, 90, 300), (0.6, 0.6, 0.5))
    h = clods + 0.4 * rows(S, 0.25, 1.0, 0.03)
    col = colourise(clods, [(-2, (84, 64, 46)), (-0.5, (112, 88, 64)), (0.8, (138, 112, 82)), (2, (160, 134, 100))])
    col += specks(S, 0.01, 1)[..., None] * 40
    info["fallow"] = (col, h, 2.0, 0.12)
    # 3 native range: short-grass prairie, tan cured grass with olive clumps and bare patches
    clump = fbm(S, (12, 50, 250), (0.5, 0.6, 0.5))
    blades = pnoise(S, 300, (1, 3))
    h = clump + 0.4 * blades
    col = colourise(clump + 0.3 * blades, [(-2, (98, 86, 60)), (-0.6, (128, 116, 78)), (0.3, (150, 138, 92)),
                                            (1.0, (118, 122, 72)), (2, (96, 108, 58))])
    info["range"] = (col, h, 1.4, 0.2)
    # 4 green grass: mown or irrigated, ditches and yards
    g = fbm(S, (15, 80, 300), (0.5, 0.5, 0.5))
    h = g + 0.5 * pnoise(S, 350, (1, 2))
    col = colourise(g, [(-2, (54, 70, 30)), (-0.4, (74, 94, 40)), (0.8, (96, 116, 52)), (2, (120, 132, 70))])
    info["grass"] = (col, h, 1.2, 0.22)
    # 5 sorghum (milo): rust-red heads over dark leaves, rows 30 in
    heads = fbm(S, (40, 160, 320), (0.5, 0.6, 0.5))
    r = rows(S, 0.762, 2.5, 0.05)
    h = heads + r
    col = colourise(heads + 0.4 * r, [(-2, (62, 54, 34)), (-0.6, (104, 70, 44)), (0.4, (134, 84, 52)),
                                      (2, (158, 106, 66))])
    info["sorghum"] = (col, h, 1.5, 0.22)
    # 6 sand: pale river sand with ripples
    rip = np.sin(2 * np.pi * (np.arange(N)[:, None] / N * LAYER_M / 0.12 + 1.5 * pnoise(S, 4)))
    s = fbm(S, (6, 40, 200), (0.6, 0.3, 0.3))
    h = 0.5 * rip + s
    col = colourise(s + 0.2 * rip, [(-2, (150, 132, 106)), (0, (186, 168, 136)), (2, (214, 198, 166))])
    info["sand"] = (col, h, 1.0, 0.15)
    # 7 dirt / gravel yard: packed grey-brown with pebbles
    d = fbm(S, (10, 60, 250), (0.6, 0.5, 0.4))
    peb = specks(S, 0.05, 1)
    h = d + peb * 1.5
    col = colourise(d, [(-2, (92, 82, 70)), (0, (126, 114, 98)), (2, (156, 146, 128))]) + peb[..., None] * 30
    info["dirt"] = (col, h, 1.6, 0.1)
    for name in LAYERS:
        col, h, strength, smooth = info[name]
        save(f"ground_{name}", col, alpha=np.full(S, smooth))   # alpha = smoothness
        save(f"ground_{name}_n", normal_map(h, strength) * 255)


# ----------------------------------------------------------------------------------------------------------- roads

RW, RH = 512, 1024
WHITE = np.array([228, 228, 222.0])
YELLOW = np.array([226, 180, 44.0])


def asphalt(shape=(RH, RW), base=(66, 66, 68)):
    a = np.ones(shape + (3,)) * np.array(base, float)
    a += (4 * pnoise(shape, 6) + 3 * pnoise(shape, 60) + 6 * pnoise(shape, 400))[..., None]
    return a


def gravel_surface(shape=(RH, RW)):
    g = fbm(shape, (8, 60, 400), (0.4, 0.5, 0.7))
    a = colourise(g, [(-2, (110, 100, 88)), (0, (146, 136, 120)), (2, (178, 170, 156))])
    a += specks(shape, 0.04, 1)[..., None] * 35
    return a


def stripe(img, u0, u1, colour, dash=None):
    u = (np.arange(img.shape[1]) + 0.5) / img.shape[1]
    m = ((u >= u0) & (u <= u1))[None, :]
    if dash is not None:
        v = (np.arange(img.shape[0]) + 0.5) / img.shape[0] * ROAD_REPEAT_M
        m = m & (v < dash)[:, None]
    m = np.broadcast_to(m, img.shape[:2])
    wear = 0.86 + 0.14 * np.clip(pnoise(img.shape[:2], 200), -1, 1)
    img[m] = img[m] * 0.08 + (colour[None, :] * wear[m][:, None]) * 0.92


def road(half, edge, centre, edge_lines):
    """half: paved half width; edge: gravel edge width. centre: 'dash' | 'double' | 'dash_solid' | None."""
    total = 2 * (half + edge)
    u = lambda d: (d + half + edge) / total            # metres from the centre line -> u
    img = asphalt()
    if edge > 0:
        gr = gravel_surface()
        side = (np.abs((np.arange(RW) + 0.5) / RW * total - total / 2) > half)
        img[:, side] = gr[:, side]
    # faint wheel paths in each lane
    for c in (-2.6, -1.0, 1.0, 2.6):
        uu = (np.arange(RW) + 0.5) / RW
        img += (np.exp(-((uu - u(c)) / (0.35 / total)) ** 2) * 5)[None, :, None]
    lw = 0.1  # 4 in lines
    if edge_lines:
        stripe(img, u(-3.66 - lw / 2), u(-3.66 + lw / 2), WHITE)
        stripe(img, u(3.66 - lw / 2), u(3.66 + lw / 2), WHITE)
    g = 0.09
    if centre == "dash":
        stripe(img, u(-lw / 2), u(lw / 2), YELLOW, dash=3.048)
    elif centre == "double":
        stripe(img, u(-g - lw), u(-g), YELLOW)
        stripe(img, u(g), u(g + lw), YELLOW)
    return img


def gravel_road(half):
    img = gravel_surface()
    uu = (np.arange(RW) + 0.5) / RW
    d = (uu - 0.5) * 2 * half
    # two packed wheel tracks per direction of travel merge into a pair near the crown, loose gravel windrows outside
    tracks = sum(np.exp(-((d - c) / 0.45) ** 2) for c in (-1.6, -0.1, 0.1, 1.6))
    windrow = np.exp(-((np.abs(d) - (half - 0.6)) / 0.4) ** 2)
    img += (windrow * 14)[None, :, None]
    img -= (tracks * 10)[None, :, None]
    # grass creeping in at the very edge
    edge = np.clip((np.abs(d) - (half - 0.25)) / 0.25, 0, 1)
    grass = np.array([96, 102, 60.0])
    img = img * (1 - edge[None, :, None] * 0.6) + grass * edge[None, :, None] * 0.6
    return img


def road_textures():
    save("road_state", road(3.66 + 1.83, 0.9, "dash", True))
    save("road_state_nopass", road(3.66 + 1.83, 0.9, "double", True))
    save("road_county", road(3.35, 0.9, "dash", False))
    save("road_gravel", gravel_road(3.66))
    sq = (512, 512)
    save("plain_asphalt", asphalt(sq))
    save("plain_gravel", gravel_surface(sq))
    c = np.ones(sq + (3,)) * np.array([162, 160, 152.0]) + (5 * pnoise(sq, 30) + 4 * pnoise(sq, 300))[..., None]
    save("concrete", c)
    json.dump(dict(layers=LAYERS, layer_m=LAYER_M, road_repeat_m=ROAD_REPEAT_M), open(OUT / "textures.json", "w"))


def main():
    ground_layers()
    road_textures()
    print("textures written to", OUT)


if __name__ == "__main__":
    main()

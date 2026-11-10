"""Four small towns on the plains map, one at each state-route crossing (user, 2026-11-02): Haskett (K-283 / K-19,
south-west), Ravanna (K-183 / K-19, south-east), Lanark (K-283 / K-156, north-west) and Eldora, which sits three miles
east of the K-183 / K-156 crossing on K-156 (the Pawnee runs near the crossing), so its Main Street is the section road.

Laid out after the user's Scott City references: a Main Street (the north-south state route, or in Eldora the section
road) with two blocks of brick storefronts north of the highway crossing and a traffic light at the crossing; a grid of
residential blocks about 100 x 140 m (330 x 460 ft) with 17 m lots, small gabled houses set 8-10 m back behind lawns,
driveways, sidewalks, parkway trees and yard trees; the grain elevator at the edge of town on Main; a highway strip with
the truck stop (canopy, pumps, diesel lanes, store, price sign), the motel (two storeys, porte-cochere, parking) and the
mechanic's shop (a metal building on a gravel lot with cars outside); a church with a steeple, the school with its
field, a park block and the water tower. Each town is well under four square miles.

Everything here is pure geometry in the map frame (metres from the south-west corner, x east, z north): roads.py takes
the street network, world.py the lots, buildings, trees, pads and places (and gives them heights), navmap.py the
places for the map. Nothing here reads the terrain.
"""
import math
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import map_config as cfg

MILE = cfg.MILE
DX, DZ = 140.0, 100.0          # block spacing: north-south streets 140 m apart, east-west streets 100 m apart
STREET_HALF = 4.6              # town streets: 30 ft, no curbs (grass to the edge, as on the side streets of the references)
ROW = 4.6                      # from the street's edge to the lots: a parkway strip and the sidewalk
LOT_W, LOT_D = 17.0, 44.0      # residential lots: 56 ft frontage, 145 ft deep
STATE_HALF = 3.66 + 1.83 + 0.9 # a state route's outer edge (CLASSES["state"] half + edge, roads.py)

EW_NAMES_N = ["Elm St", "Oak St", "Maple St", "Walnut St", "Cedar St", "Locust St", "Pine St", "Ash St", "Hickory St", "Birch St"]
EW_NAMES_S = ["Grant St", "Lincoln St", "Sherman St", "Garfield St", "Harrison St", "Cleveland St", "Jackson St", "Monroe St"]
NS_NAMES_W = ["Washington Ave", "Adams Ave", "Jefferson Ave", "Madison Ave", "Jackson Ave", "Tyler Ave", "Polk Ave", "Taylor Ave"]
NS_NAMES_E = ["College Ave", "Church Ave", "Antelope Ave", "Prairie Ave", "Mesa Ave", "Buffalo Ave", "Kiowa Ave", "Pawnee Ave"]

# col / row: the section line (roads.py line_at) the town's Main Street and highway run on; Main is the north-south
# state route except in Eldora, where it is the section road Ave 39 (main_street=True) and only K-156 is a state route.
# cols_w / cols_e / rows_n / rows_s: blocks of town west, east, north and south of the crossing. pop: 2020-style census
# figure for the night glow (TestMapTowns). gas / motel / mech: which end of the highway strip (west or east) and which
# side (north or south) each takes. elev: the elevator's end of Main (north or south).
TOWNS = [
    dict(key="sw", name="Haskett", col=10, row=12, ns="K-283", ew="K-19", main_street=False, seed=11, pop=1900,
         cols_w=5, cols_e=4, rows_n=7, rows_s=4, gas=("w", "n"), motel=("e", "s"), mech=("e", "n"), elev="n",
         gas_name="Prairie Stop", motel_name="Wheatland Inn", mech_name="Haskett Auto & Diesel"),
    dict(key="se", name="Ravanna", col=36, row=13, ns="K-183", ew="K-19", main_street=False, seed=23, pop=1400,
         cols_w=4, cols_e=4, rows_n=6, rows_s=4, gas=("e", "s"), motel=("w", "n"), mech=("w", "s"), elev="s",
         gas_name="Sunflower Travel Center", motel_name="Plainsman Motel", mech_name="Ravanna Repair"),
    dict(key="nw", name="Lanark", col=9, row=38, ns="K-283", ew="K-156", main_street=False, seed=37, pop=2300,
         cols_w=5, cols_e=5, rows_n=6, rows_s=5, gas=("e", "n"), motel=("w", "s"), mech=("w", "n"), elev="n",
         gas_name="Pawnee Valley Fuel", motel_name="Lanark Inn & Suites", mech_name="Lanark Tire & Service"),
    dict(key="ne", name="Eldora", col=39, row=38, ns="Ave 39", ew="K-156", main_street=True, seed=41, pop=1600,
         cols_w=4, cols_e=5, rows_n=5, rows_s=5, gas=("w", "s"), motel=("e", "n"), mech=("e", "s"), elev="s",
         gas_name="High Plains Truck Stop", motel_name="Eldora Motor Lodge", mech_name="Eldora Garage"),
]


def line_at(i):
    return (i + 0.5) * MILE


def centre(town):
    return np.array([line_at(town["col"]), line_at(town["row"])])


def street_x(town, i):
    return centre(town)[0] + i * DX


def street_z(town, j):
    return centre(town)[1] + j * DZ


def box(town, margin=0.0):
    """The town's extent (x0, z0, x1, z1): the outer streets, plus a margin."""
    c = centre(town)
    return (c[0] - town["cols_w"] * DX - margin, c[1] - town["rows_s"] * DZ - margin,
            c[0] + town["cols_e"] * DX + margin, c[1] + town["rows_n"] * DZ + margin)


def inside_mask(pts, margin=0.0):
    """Which of the map points (n x 2) lie in a town (bool array)."""
    pts = np.asarray(pts, float)
    m = np.zeros(len(pts), bool)
    for t in TOWNS:
        x0, z0, x1, z1 = box(t, margin)
        m |= (pts[:, 0] >= x0) & (pts[:, 0] <= x1) & (pts[:, 1] >= z0) & (pts[:, 1] <= z1)
    return m


def inside(p, margin=0.0):
    """Which town a map point is in (its dict), or None."""
    for t in TOWNS:
        x0, z0, x1, z1 = box(t, margin)
        if x0 <= p[0] <= x1 and z0 <= p[1] <= z1:
            return t
    return None


def block_present(town, i, j):
    """Block (i, j) exists: i counts blocks east of Main (0 is the first block east, -1 the first west), j north of the
    highway. The town is trimmed to a rounded shape at its corners, by its seed."""
    cw, ce, rn, rs = town["cols_w"], town["cols_e"], town["rows_n"], town["rows_s"]
    if not (-cw <= i < ce and -rs <= j < rn):
        return False
    rng = np.random.default_rng(town["seed"] * 1000 + (i + 50) * 97 + (j + 50))
    ex = (i + 0.5) / (ce if i >= 0 else cw)
    ez = (j + 0.5) / (rn if j >= 0 else rs)
    r = math.hypot(ex, ez)
    # along the highway and Main the town runs out to its full extent; the corners are cut back
    return r < 1.08 + 0.12 * rng.random() or abs(ex) < 0.3 or abs(ez) < 0.3


def blocks(town):
    return [(i, j) for i in range(-town["cols_w"], town["cols_e"]) for j in range(-town["rows_s"], town["rows_n"]) if block_present(town, i, j)]


def ns_name(town, i):
    if i == 0:
        return town["ns"] if not town["main_street"] else "Main St"
    return NS_NAMES_W[min(-i - 1, len(NS_NAMES_W) - 1)] if i < 0 else NS_NAMES_E[min(i - 1, len(NS_NAMES_E) - 1)]


def ew_name(town, j):
    if j == 0:
        return town["ew"]
    return EW_NAMES_N[min(j - 1, len(EW_NAMES_N) - 1)] if j > 0 else EW_NAMES_S[min(-j - 1, len(EW_NAMES_S) - 1)]


def street_segments(town):
    """The town's streets for the road network: every block edge that is not a state route (or Main in Eldora, which
    is a street), as (name, (x0, z0), (x1, z1)), each from one grid node to the next, deduplicated."""
    segs = {}
    for i, j in blocks(town):
        x0, x1 = street_x(town, i), street_x(town, i + 1)
        z0, z1 = street_z(town, j), street_z(town, j + 1)
        # the block's four edges: south, north (east-west streets j and j + 1), west, east (north-south i and i + 1)
        for jj, z in ((j, z0), (j + 1, z1)):
            if jj == 0:
                continue   # the highway
            segs[("ew", jj, i)] = (ew_name(town, jj), (x0, z), (x1, z))
        for ii, x in ((i, x0), (i + 1, x1)):
            if ii == 0 and not town["main_street"]:
                continue   # the north-south state route
            segs[("ns", ii, j)] = (ns_name(town, ii), (x, z0), (x, z1))
    return list(segs.values())


def covered(town):
    """Grid-line intervals the town takes over from the section grid: (fixed axis, line index, from, to along it)."""
    out = []
    x0, z0, x1, z1 = box(town)
    if town["main_street"]:
        out.append(("x", town["col"], z0, z1))     # Main St is the section road's line: the gravel stops at the edge
    return out


# ------------------------------------------------------------------------------------------------------------ layout

HOUSE_WALLS = [(0.93, 0.92, 0.88), (0.86, 0.84, 0.78), (0.78, 0.80, 0.84), (0.72, 0.78, 0.70), (0.80, 0.70, 0.60),
               (0.60, 0.42, 0.34), (0.88, 0.86, 0.80), (0.70, 0.66, 0.60), (0.82, 0.76, 0.62), (0.55, 0.58, 0.55)]
HOUSE_ROOFS = [(0.35, 0.36, 0.38), (0.42, 0.40, 0.38), (0.55, 0.20, 0.16), (0.50, 0.50, 0.52), (0.28, 0.30, 0.33), (0.30, 0.42, 0.36)]
BRICK = [(0.62, 0.36, 0.28), (0.74, 0.56, 0.42), (0.86, 0.80, 0.70), (0.55, 0.30, 0.25), (0.80, 0.72, 0.60), (0.68, 0.46, 0.36), (0.90, 0.88, 0.82)]
CAR_PAINT = [(0.8, 0.8, 0.8), (0.1, 0.1, 0.12), (0.5, 0.1, 0.1), (0.2, 0.3, 0.6), (0.6, 0.6, 0.62), (0.75, 0.72, 0.6), (0.3, 0.5, 0.3)]


class Layout:
    """What world.py builds: buildings (map-frame p, yaw as Unity's, w across, l along yaw, h, roof rise fraction,
    wall and roof colours, kind), trees (type, p, scale), pads (polygon points, material name, soft flatten), strips
    (sidewalks and driveways: a, b, width, material), lawns (rectangles x0, z0, x1, z1), the places (truck stop, motel,
    mechanic: positions, headings and the marked spots) and the traffic light."""

    def __init__(self, town):
        self.town = town
        self.buildings, self.trees, self.pads, self.strips, self.lawns = [], [], [], [], []
        self.places = {}
        self.rng = np.random.default_rng(town["seed"] * 7 + 3)

    def add(self, kind, p, yaw, w, l, h, roof=0.0, wall=(0.9, 0.9, 0.9), roofc=(0.3, 0.3, 0.3), tag=""):
        self.buildings.append(dict(kind=kind, p=np.asarray(p, float), yaw=float(yaw), w=float(w), l=float(l), h=float(h),
                                   roof=float(roof), wall=tuple(wall), roofc=tuple(roofc), tag=tag))

    def tree(self, typ, p, scale=1.0):
        self.trees.append((int(typ), np.asarray(p, float), float(scale)))

    def pad(self, pts, mat, flatten=True):
        self.pads.append(dict(pts=np.asarray(pts, float), mat=mat, flatten=flatten))

    def strip(self, a, b, width, mat="concrete"):
        self.strips.append((np.asarray(a, float), np.asarray(b, float), float(width), mat))


def yaw_of(fwd):
    """Unity yaw (degrees, 0 = north, 90 = east) of a map-frame direction."""
    return math.degrees(math.atan2(fwd[0], fwd[1]))


def rect(c, fwd, rt, hl, hw):
    """Corners of a rectangle about c: hl along fwd, hw along rt."""
    c, fwd, rt = np.asarray(c), np.asarray(fwd), np.asarray(rt)
    return np.array([c - fwd * hl - rt * hw, c + fwd * hl - rt * hw, c + fwd * hl + rt * hw, c - fwd * hl + rt * hw])


def road_half(town, axis, index):
    """Outer edge of the road on grid line `index` of `axis` ('ns' or 'ew'): the highways are wider than the streets."""
    if axis == "ew" and index == 0:
        return STATE_HALF
    if axis == "ns" and index == 0 and not town["main_street"]:
        return STATE_HALF
    return STREET_HALF


def lot_frontage(town, i, j, side):
    """The lot line of block (i, j) along its `side` ('n' or 's': the east-west street j + 1 or j; 'w' or 'e': the
    north-south street i or i + 1): (street centre coordinate, the setback to the lot line, inward unit vector)."""
    if side in ("n", "s"):
        jj = j + 1 if side == "n" else j
        return street_z(town, jj), road_half(town, "ew", jj) + ROW, np.array([0.0, -1.0 if side == "n" else 1.0])
    ii = i if side == "w" else i + 1
    return street_x(town, ii), road_half(town, "ns", ii) + ROW, np.array([1.0 if side == "w" else -1.0, 0.0])


def block_rect(town, i, j):
    """The block's buildable rectangle (inside the streets' reserves): x0, z0, x1, z1."""
    x0 = street_x(town, i) + road_half(town, "ns", i) + ROW
    x1 = street_x(town, i + 1) - road_half(town, "ns", i + 1) - ROW
    z0 = street_z(town, j) + road_half(town, "ew", j) + ROW
    z1 = street_z(town, j + 1) - road_half(town, "ew", j + 1) - ROW
    return x0, z0, x1, z1


def roles(town):
    """What each block is: 'res' (houses), 'downtown' (storefronts on Main), 'gas', 'motel', 'mech', 'elev' (the
    elevator), 'school', 'park', 'church' (a church on a residential block), 'tower' (the water tower on a block)."""
    present = set(blocks(town))
    role = {b: "res" for b in present}
    cw, ce, rn, rs = town["cols_w"], town["cols_e"], town["rows_n"], town["rows_s"]
    # downtown: the two blocks north of the highway and one south, either side of Main
    for i in (-1, 0):
        for j in (-1, 0, 1):
            if (i, j) in present:
                role[(i, j)] = "downtown"

    def strip_block(end, side):
        # the outermost present block along the highway at that end, on that side
        j = 0 if side == "n" else -1
        cols = range(ce - 1, 0, -1) if end == "e" else range(-cw, -1)
        for i in cols:
            if (i, j) in present and role[(i, j)] == "res":
                return (i, j)
        return None
    for key in ("gas", "motel", "mech"):
        b = strip_block(*town[key])
        if b:
            role[b] = key
    # the elevator at the end of Main, two blocks deep if there
    jj = range(rn - 1, 1, -1) if town["elev"] == "n" else range(-rs, -1)
    for j in jj:
        if (0, j) in present and role[(0, j)] == "res":
            role[(0, j)] = "elev"
            nxt = (0, j + (-1 if town["elev"] == "n" else 1))
            if nxt in present and role[nxt] == "res":
                role[nxt] = "elev"
            break
    rng = np.random.default_rng(town["seed"] * 13)
    res = [b for b in present if role[b] == "res"]
    # the school on an outer block away from the highway, the park, two churches and the water tower on inner blocks
    outer = sorted(res, key=lambda b: -(abs(b[0]) + abs(b[1])))
    for b in outer[:1]:
        role[b] = "school"
    inner = [b for b in res if role[b] == "res" and 1 <= abs(b[0]) <= 3 and 1 <= abs(b[1]) <= 3]
    rng.shuffle(inner)
    for b, r in zip(inner, ("park", "church", "church", "tower")):
        role[b] = r
    return role


def layout(town):
    L = Layout(town)
    role = roles(town)
    for (i, j), r in role.items():
        x0, z0, x1, z1 = block_rect(town, i, j)
        L.lawns.append((x0 - ROW, z0 - ROW, x1 + ROW, z1 + ROW))
        if r in ("res", "church", "tower"):
            residential(L, i, j, skip=r)
        elif r == "downtown":
            downtown(L, i, j)
        elif r == "gas":
            truck_stop(L, i, j)
        elif r == "motel":
            motel(L, i, j)
        elif r == "mech":
            mechanic(L, i, j)
        elif r == "elev":
            elevator(L, i, j)
        elif r == "school":
            school(L, i, j)
        elif r == "park":
            park(L, i, j)
    sidewalks(L, role)
    c = centre(town)
    L.places["light"] = dict(x=float(c[0]), z=float(c[1]), ns_half=road_half(town, "ns", 0), ew_half=STATE_HALF,
                             main=ns_name(town, 0), cross=town["ew"])
    L.role = role
    return L


# ----------------------------------------------------------------------------------------------------- residential

def residential(L, i, j, skip="res"):
    """Houses on the block's north and south frontages (facing the east-west streets), lots LOT_W wide. A church block
    gives up its middle lots on one side to the church; a tower block to the water tower."""
    town = L.town
    x0, z0, x1, z1 = block_rect(town, i, j)
    downtown_margin = 34.0 if role_is_downtown_neighbour(town, i, j) else 0.0
    for side in ("s", "n"):
        zs, setback, inward = lot_frontage(town, i, j, side)
        front = zs + inward[1] * setback            # the lot line
        xa, xb = x0, x1
        if i == -1:
            xb -= downtown_margin
        if i == 0:
            xa += downtown_margin
        n = max(1, int((xb - xa) / LOT_W))
        w = (xb - xa) / n
        special = skip if (side == "n") else "res"
        mid = n // 2
        for k in range(n):
            lx = xa + (k + 0.5) * w
            if special == "church" and k in (mid - 1, mid):
                if k == mid:
                    church(L, np.array([lx - w, front]), inward, w * 2, side)
                continue
            if special == "tower" and k == mid:
                water_tower(L, np.array([lx, front + inward[1] * 24]))
                continue
            house_lot(L, np.array([lx, front]), inward, w, LOT_D if (z1 - z0) > 2 * LOT_D + 2 else (z1 - z0) / 2 - 1)


def role_is_downtown_neighbour(town, i, j):
    return i in (-1, 0) and j in (-1, 0, 1)


def house_lot(L, front_c, inward, w, depth):
    """One lot: a house 8-11 m back, a driveway down one side to a garage or the back yard, trees, and a vacant lot
    now and then."""
    rng = L.rng
    rt = np.array([inward[1], -inward[0]])
    u = rng.random()
    curb = front_c - inward * ROW          # the street's edge
    if u < 0.08:
        # a vacant lot: a tree or two
        for _ in range(rng.integers(1, 3)):
            L.tree(rng.choice([0, 1, 1, 2]), front_c + inward * rng.uniform(5, depth - 5) + rt * rng.uniform(-w * 0.4, w * 0.4), rng.uniform(0.7, 1.1))
        return
    hw = min(rng.uniform(8.5, 11.5), w - 4.0)
    hl = rng.uniform(9.0, 14.0)
    side_gable = rng.random() < 0.55
    setback = rng.uniform(8.0, 10.5)
    along_street = hl if side_gable else hw      # the house's extent across the lot
    into_lot = hw if side_gable else hl          # its depth
    if along_street > w - 3.5:
        side_gable = False
        along_street, into_lot = hw, hl
    hc = front_c + inward * (setback + into_lot / 2)
    yaw = yaw_of(inward) + (90 if side_gable else 0)
    bw, bl = (hl, hw) if side_gable else (hw, hl)
    wall = HOUSE_WALLS[rng.integers(0, len(HOUSE_WALLS))]
    roofc = HOUSE_ROOFS[rng.integers(0, len(HOUSE_ROOFS))]
    storeys = 2 if rng.random() < 0.12 else 1
    L.add("house", hc, yaw, bw, bl, 2.9 * storeys + 0.3, rng.uniform(0.35, 0.55), wall, roofc)
    # the driveway on one side, from the street to the back yard, and a garage at its end on most lots
    dside = rng.choice([-1, 1])
    doff = min(along_street / 2 + 2.4, w / 2 - 2.0)
    da = curb + rt * dside * doff
    glen = setback + into_lot + rng.uniform(4, 9)
    db = front_c + inward * glen + rt * dside * doff
    L.strip(da, db, 3.4, "concrete")
    if rng.random() < 0.6:
        L.add("garage", db + inward * 3.8, yaw_of(inward), 6.2, 6.8, 2.7, 0.3, wall, roofc)
    # the walk to the front door
    L.strip(front_c - inward * (ROW - 1.0), front_c + inward * (setback - 0.5), 1.1, "concrete")
    # trees: front yard, back yard, the parkway
    if rng.random() < 0.55:
        L.tree(rng.choice([1, 1, 0, 2]), front_c + inward * rng.uniform(2.5, setback - 2) + rt * -dside * rng.uniform(1, w * 0.4), rng.uniform(0.6, 1.0))
    for _ in range(rng.integers(0, 3)):
        L.tree(rng.choice([1, 1, 1, 0, 2, 3]), front_c + inward * rng.uniform(glen - 2, depth - 3) + rt * rng.uniform(-w * 0.45, w * 0.45), rng.uniform(0.6, 1.15))
    if rng.random() < 0.5:
        L.tree(rng.choice([1, 1, 0]), curb + inward * 1.5 + rt * rng.uniform(-w * 0.3, w * 0.3), rng.uniform(0.7, 1.05))


def church(L, corner, inward, w, side):
    """A brick church on two lots: the nave end-on to the street, a white steeple at its front, a parking pad beside."""
    rng = L.rng
    rt = np.array([inward[1], -inward[0]])
    front = corner + rt * (w * 0.33)
    yaw = yaw_of(inward)
    brick = BRICK[rng.integers(0, 2)]
    L.add("house", front + inward * (6 + 15), yaw, 14.0, 30.0, 6.0, 0.45, brick, (0.30, 0.22, 0.20), tag="church")
    L.add("steeple", front + inward * 7.5, yaw, 3.2, 3.2, 16.0, 0.0, (0.96, 0.96, 0.96), (0.96, 0.96, 0.96))
    L.add("house", front + inward * 30 + rt * 12, yaw + 90, 10.0, 24.0, 3.4, 0.3, brick, (0.30, 0.22, 0.20))
    lot = rect(front + inward * 18 + rt * (w * 0.33 + 1), inward, rt, 16, w * 0.2)
    L.pad(lot, "plain_asphalt")
    L.strip(front - inward * (ROW - 1.0), front + inward * 5.5, 2.4, "concrete")
    for _ in range(3):
        L.tree(1, front + inward * rng.uniform(2, 5) + rt * rng.uniform(-w * 0.45, -w * 0.1), rng.uniform(0.8, 1.1))


def water_tower(L, p):
    L.add("watertower", p, 0.0, 11.0, 7.0, 36.0, 0.0, (0.70, 0.82, 0.92), (0.70, 0.82, 0.92), tag=L.town["name"])
    L.pad(rect(p, np.array([0, 1.0]), np.array([1.0, 0]), 9, 9), "plain_gravel")


# -------------------------------------------------------------------------------------------------------- downtown

def downtown(L, i, j):
    """Storefronts shoulder to shoulder on Main (two storeys on the first block, mixed on the others), a wide sidewalk,
    and on the blocks at the crossing a bank or a corner store facing the highway. The back of each block takes houses
    (residential(), which keeps 34 m clear of Main)."""
    town, rng = L.town, L.rng
    x0, z0, x1, z1 = block_rect(town, i, j)
    xs, setback, inward = lot_frontage(town, i, j, "e" if i == -1 else "w")
    lotline = xs + inward[0] * setback
    za, zb = z0, z1
    if j == 0 or j == -1:
        # the corner on the highway: a bank or a drug store facing the highway, on the first two lots' worth
        corner_z = za if j == 0 else zb
        c = np.array([lotline + inward[0] * 13, corner_z + (1 if j == 0 else -1) * 13])
        yaw = 180.0 if j == 0 else 0.0     # faces the highway (south for the block north of it)
        L.add("store", c, yaw, 24.0, 26.0, 5.0 if rng.random() < 0.6 else 7.5, 0.0, BRICK[2 if rng.random() < 0.5 else 4], (0.35, 0.3, 0.28), tag="bank")
        if j == 0:
            za += 26
        else:
            zb -= 26
    n = max(1, int((zb - za) / 11.0))
    w = (zb - za) / n
    for k in range(n):
        zc = za + (k + 0.5) * w
        depth = rng.uniform(18, 30)
        two = j == 0 or rng.random() < 0.45
        h = (7.4 if two else 4.6) + rng.uniform(-0.4, 0.6)
        L.add("store", np.array([lotline + inward[0] * depth / 2, zc]), yaw_of(inward), w - 0.1, depth, h, 0.0,
              BRICK[rng.integers(0, len(BRICK))], (0.35, 0.30, 0.28))
    # the sidewalk: from the road edge to the lot line, the whole block
    L.strip(np.array([xs + inward[0] * (setback - ROW / 2), z0 - ROW]), np.array([xs + inward[0] * (setback - ROW / 2), z1 + ROW]), ROW, "concrete")
    # parking behind the stores, off the side street
    back = lotline + inward[0] * 33
    L.pad(np.array([[back, z0 + 2], [back + inward[0] * 12, z0 + 2], [back + inward[0] * 12, z1 - 2], [back, z1 - 2]]), "plain_asphalt")
    residential(L, i, j)


# -------------------------------------------------------------------------------------------------- highway strip

def strip_frame(L, i, j):
    """A highway-strip block: the frontage on the highway (east-west, j = 0 north of it, -1 south), inward unit vector,
    the block rectangle and the direction along the highway."""
    town = L.town
    x0, z0, x1, z1 = block_rect(town, i, j)
    side = "s" if j == 0 else "n"
    zs, setback, inward = lot_frontage(town, i, j, side)
    front = zs + inward[1] * setback
    along = np.array([1.0, 0.0])
    return front, inward, along, (x0, z0, x1, z1)


def truck_stop(L, i, j):
    """The truck stop after the Love's references: a long canopy over three pump islands along the highway, a store
    behind it, diesel lanes with their own canopy at one end, the tall price sign at the road, the lot in asphalt all
    round, and the marked overnight parking spot at the side."""
    town, rng = L.town, L.rng
    front, inward, along, (x0, z0, x1, z1) = strip_frame(L, i, j)
    cx = (x0 + x1) / 2
    gas_at_east = town["gas"][0] == "e"
    lot = np.array([[x0 + 2, front], [x1 - 2, front], [x1 - 2, front + inward[1] * 78], [x0 + 2, front + inward[1] * 78]])
    L.pad(lot, "plain_asphalt")
    yaw = yaw_of(inward)
    canopy_c = np.array([cx - 6, front + inward[1] * 22])
    L.add("canopy", canopy_c, 90.0, 16.0, 40.0, 5.6, 0.0, (0.95, 0.75, 0.15), (0.80, 0.15, 0.10), tag="gas")
    pumps = []
    for k in range(3):
        p = canopy_c + along * (-12 + 12 * k)
        L.add("pump", p, 90.0, 1.0, 2.6, 1.9, 0.0, (0.92, 0.92, 0.92), (0.80, 0.15, 0.10))
        pumps.append(p)
    store_c = np.array([cx - 6, front + inward[1] * 52])
    L.add("store", store_c, yaw, 36.0, 16.0, 5.4, 0.0, (0.86, 0.80, 0.68), (0.80, 0.15, 0.10), tag="gas_store")
    # the diesel lanes at the far end: a higher canopy over two islands, trucks pull through
    diesel_c = np.array([x1 - 20 if gas_at_east else x0 + 20, front + inward[1] * 30])
    L.add("canopy", diesel_c, 0.0, 14.0, 30.0, 6.4, 0.0, (0.95, 0.75, 0.15), (0.80, 0.15, 0.10), tag="diesel")
    diesel = []
    for k in range(2):
        p = diesel_c + along * (-4 + 8 * k)
        L.add("pump", p, 0.0, 1.0, 3.0, 2.2, 0.0, (0.92, 0.92, 0.92), (0.25, 0.60, 0.25))
        diesel.append(p)
    sign_p = np.array([x0 + 10 if gas_at_east else x1 - 10, front + inward[1] * 3])
    L.add("sign", sign_p, 180.0 if inward[1] < 0 else 0.0, 5.0, 7.0, 15.0, 0.0, (0.95, 0.75, 0.15), (0.80, 0.15, 0.10), tag=town["gas_name"])
    # the overnight spot: a marked space at the side of the lot, nose to the fence, away from the pumps
    spot = np.array([x0 + 8 if gas_at_east else x1 - 8, front + inward[1] * 60])
    spot_yaw = yaw_of(inward)
    L.add("box", np.array([x0 + 4 if gas_at_east else x1 - 4, front + inward[1] * 70]), 0.0, 2.0, 3.0, 1.6, 0.0, (0.2, 0.4, 0.25), (0.2, 0.4, 0.25), tag="dumpster")
    for _ in range(4):
        L.tree(2, np.array([rng.uniform(x0 + 3, x1 - 3), front + inward[1] * rng.uniform(80, 88)]), rng.uniform(0.7, 1.0))
    L.places["gas"] = dict(name=town["gas_name"], x=float(store_c[0]), z=float(store_c[1]), yaw=float(yaw),
                           canopy=dict(x=float(canopy_c[0]), z=float(canopy_c[1])),
                           pumps=[dict(x=float(p[0]), z=float(p[1]), yaw=90.0) for p in pumps],
                           diesel=[dict(x=float(p[0]), z=float(p[1]), yaw=0.0) for p in diesel],
                           sleep=dict(x=float(spot[0]), z=float(spot[1]), yaw=float(spot_yaw)))


def motel(L, i, j):
    """A two-storey motel after the Best Western reference: an L of rooms with hip roofs, the office under a
    porte-cochere at the corner, parking in front, the sign at the road and the marked guest spot."""
    town, rng = L.town, L.rng
    front, inward, along, (x0, z0, x1, z1) = strip_frame(L, i, j)
    cx = (x0 + x1) / 2
    yaw = yaw_of(inward)
    L.pad(np.array([[x0 + 2, front], [x1 - 2, front], [x1 - 2, front + inward[1] * 48], [x0 + 2, front + inward[1] * 48]]), "plain_asphalt")
    wall, roofc = (0.84, 0.74, 0.58), (0.36, 0.26, 0.20)
    main_c = np.array([cx + 8, front + inward[1] * 56])
    L.add("motel", main_c, yaw + 90, 14.0, 60.0, 7.2, 0.3, wall, roofc, tag="motel")
    wing_c = np.array([cx - 28, front + inward[1] * 40])
    L.add("motel", wing_c, yaw, 14.0, 44.0, 7.2, 0.3, wall, roofc, tag="motel")
    office = np.array([cx - 14, front + inward[1] * 46])
    L.add("canopy", office, yaw, 12.0, 10.0, 4.6, 0.0, roofc, roofc, tag="porte")
    sign_p = np.array([x0 + 10, front + inward[1] * 3])
    L.add("sign", sign_p, 180.0 if inward[1] < 0 else 0.0, 3.6, 4.0, 9.0, 0.0, (0.15, 0.30, 0.65), (0.95, 0.95, 0.95), tag=town["motel_name"])
    spot = np.array([x1 - 10, front + inward[1] * 42])
    for _ in range(5):
        L.tree(rng.choice([1, 2]), np.array([rng.uniform(x0 + 4, x1 - 4), front + inward[1] * rng.uniform(70, 86)]), rng.uniform(0.7, 1.0))
    for k in range(3):
        L.tree(1, np.array([x0 + 6 + k * 18, front + inward[1] * 8]), 0.8)
    L.places["motel"] = dict(name=town["motel_name"], x=float(office[0]), z=float(office[1]), yaw=float(yaw),
                             sleep=dict(x=float(spot[0]), z=float(spot[1]), yaw=float(yaw)))


def mechanic(L, i, j):
    """The mechanic's after the reference: a blue steel building and a larger tan shop behind it on a gravel lot, cars
    waiting outside, the service bay door toward the lot."""
    town, rng = L.town, L.rng
    front, inward, along, (x0, z0, x1, z1) = strip_frame(L, i, j)
    cx = (x0 + x1) / 2
    yaw = yaw_of(inward)
    L.pad(np.array([[x0 + 2, front], [x1 - 2, front], [x1 - 2, front + inward[1] * 60], [x0 + 2, front + inward[1] * 60]]), "plain_gravel")
    shop_c = np.array([cx - 18, front + inward[1] * 34])
    L.add("shed", shop_c, yaw, 14.0, 26.0, 5.0, 0.18, (0.25, 0.42, 0.72), (0.86, 0.86, 0.84), tag="mechanic")
    L.add("shed", np.array([cx + 16, front + inward[1] * 40]), yaw, 22.0, 28.0, 6.5, 0.2, (0.72, 0.66, 0.52), (0.80, 0.80, 0.78))
    L.add("sign", np.array([x0 + 8, front + inward[1] * 3]), 180.0 if inward[1] < 0 else 0.0, 3.0, 1.6, 5.0, 0.0, (0.95, 0.95, 0.95), (0.25, 0.42, 0.72), tag=town["mech_name"])
    for k in range(rng.integers(5, 9)):
        p = np.array([x0 + 6 + k * 6.5, front + inward[1] * rng.uniform(8, 16)])
        L.add("car", p, rng.uniform(-20, 20) + (180 if inward[1] < 0 else 0), 1.85, 4.7, 1.45, 0.0, CAR_PAINT[rng.integers(0, len(CAR_PAINT))], (0.1, 0.1, 0.1))
    spot = shop_c - inward * 18
    L.places["mechanic"] = dict(name=town["mech_name"], x=float(shop_c[0]), z=float(shop_c[1]), yaw=float(yaw),
                                spot=dict(x=float(spot[0]), z=float(spot[1]), yaw=float(yaw)))
    for _ in range(3):
        L.tree(rng.choice([0, 1]), np.array([rng.uniform(x0 + 3, x1 - 3), front + inward[1] * rng.uniform(70, 88)]), rng.uniform(0.8, 1.2))


# ----------------------------------------------------------------------------------------------- civic and the edge

def elevator(L, i, j):
    """The grain elevator at the end of Main: a row of concrete silos with the headhouse over them, steel bins, a scale
    house and an office, on a gravel pad along Main."""
    town = L.town
    x0, z0, x1, z1 = block_rect(town, i, j)
    xs, setback, inward = lot_frontage(town, i, j, "w")
    # on the east side of Main (block 0 is east of it)
    base = np.array([xs + inward[0] * (setback + 14), (z0 + z1) / 2])
    L.pad(np.array([[x0, z0], [x1, z0], [x1, z1], [x0, z1]]), "plain_gravel")
    n = 7
    for k in range(n):
        L.add("silo", base + np.array([0, (k - (n - 1) / 2) * 7.4]), 0.0, 7.2, 7.2, 34.0, 0.0, (0.80, 0.79, 0.76), (0.80, 0.79, 0.76))
    L.add("box", base, 0.0, 8.0, 9.0, 46.0, 0.0, (0.78, 0.77, 0.74), (0.5, 0.5, 0.5), tag="headhouse")
    L.add("box", base + np.array([9.5, 0]), 0.0, 5.0, 40.0, 10.0, 0.0, (0.72, 0.71, 0.68), (0.5, 0.5, 0.5), tag="annex")
    for k in range(4):
        dia = 12.0 + 3 * (k % 2)
        L.add("bin", base + np.array([30 + (k % 2) * 16, (k // 2) * 18 - 9]), 0.0, dia, dia, dia * 1.1, 0.35, (0.70, 0.72, 0.73), (0.62, 0.64, 0.66))
    L.add("shed", base + np.array([2, -(z1 - z0) / 2 + 10]), 0.0, 8.0, 10.0, 3.2, 0.2, (0.86, 0.84, 0.78), (0.35, 0.36, 0.38), tag="office")
    L.add("box", base + np.array([-6, (z1 - z0) / 2 - 14]), 0.0, 4.0, 18.0, 1.2, 0.0, (0.5, 0.5, 0.5), (0.5, 0.5, 0.5), tag="scale")


def school(L, i, j):
    """The school: a wide flat building with its gym, parking in front, the field behind."""
    town = L.town
    x0, z0, x1, z1 = block_rect(town, i, j)
    cx, cz = (x0 + x1) / 2, (z0 + z1) / 2
    fwd = np.array([0.0, 1.0 if j >= 0 else -1.0])
    L.add("box", np.array([cx, cz - fwd[1] * 12]), 0.0, 62.0, 22.0, 5.0, 0.0, (0.84, 0.78, 0.66), (0.4, 0.4, 0.4), tag="school")
    L.add("box", np.array([cx + 24, cz + fwd[1] * 10]), 0.0, 24.0, 26.0, 9.0, 0.0, (0.84, 0.78, 0.66), (0.4, 0.4, 0.4), tag="gym")
    L.pad(np.array([[x0 + 4, cz - fwd[1] * 26], [x1 - 4, cz - fwd[1] * 26], [x1 - 4, cz - fwd[1] * 44], [x0 + 4, cz - fwd[1] * 44]]), "plain_asphalt")
    for k in range(6):
        L.tree(1, np.array([x0 + 8 + k * 20, cz - fwd[1] * 46]), 0.9)
    L.places.setdefault("school", dict(x=float(cx), z=float(cz)))


def park(L, i, j):
    town, rng = L.town, L.rng
    x0, z0, x1, z1 = block_rect(town, i, j)
    for _ in range(int((x1 - x0) * (z1 - z0) / 600)):
        L.tree(rng.choice([0, 1, 1, 1, 2]), np.array([rng.uniform(x0 + 3, x1 - 3), rng.uniform(z0 + 3, z1 - 3)]), rng.uniform(0.9, 1.3))
    L.add("shed", np.array([(x0 + x1) / 2, (z0 + z1) / 2]), 0.0, 8.0, 10.0, 3.0, 0.25, (0.55, 0.42, 0.30), (0.35, 0.36, 0.38), tag="shelter")


def sidewalks(L, role):
    """A sidewalk along every residential street frontage of every block, both sides of the street."""
    town = L.town
    for (i, j), r in role.items():
        if r in ("elev", "gas", "motel", "mech"):
            continue
        x0, z0, x1, z1 = block_rect(town, i, j)
        for side in ("n", "s"):
            zs, setback, inward = lot_frontage(town, i, j, side)
            z = zs + inward[1] * (setback - 1.6)
            L.strip(np.array([x0 - ROW, z]), np.array([x1 + ROW, z]), 1.2, "concrete")
        for side in ("w", "e"):
            xs, setback, inward = lot_frontage(town, i, j, side)
            if r == "downtown" and ((side == "e" and i == -1) or (side == "w" and i == 0)):
                continue   # Main's wide downtown sidewalk is laid by downtown()
            x = xs + inward[0] * (setback - 1.6)
            L.strip(np.array([x, z0 - ROW]), np.array([x, z1 + ROW]), 1.2, "concrete")


def summary(town, L):
    x0, z0, x1, z1 = box(town)
    return (f"{town['name']} ({town['key']}): {len(blocks(town))} blocks, {(x1 - x0) / MILE:.2f} x {(z1 - z0) / MILE:.2f} mi "
            f"({(x1 - x0) * (z1 - z0) / MILE ** 2:.2f} sq mi), {sum(1 for b in L.buildings if b['kind'] == 'house')} houses, "
            f"{sum(1 for b in L.buildings if b['kind'] == 'store')} stores, {len(L.trees)} trees, {len(L.pads)} pads, "
            f"places {sorted(L.places)}")


if __name__ == "__main__":
    for t in TOWNS:
        print(summary(t, layout(t)))

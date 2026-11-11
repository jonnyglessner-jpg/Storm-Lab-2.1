# Storm structure from the environment: the reference behind StormStructure

Distilled on 2026-11-07 from the library in `../meteorology` (METR 4403 notes and slides, Stull's *Practical Meteorology*
ch. 6, 7, 14 and 15, the CM1, SHARPpy, MetPy and VORTEX2 notes). Numbers marked **[GK]** are general knowledge rather
than the library's, and are starting values to tune.

## Separating the updraft from the downdrafts

- Deep-layer shear carries negatively buoyant air and hydrometeors away from the updraft (METR 4403 §09).
- Strong storm-relative (SR) winds aloft ventilate the fallout; low-level SR inflow keeps the gust front from undercutting
  the updraft (§14).
- Fallout is sorted by size (Stull ch. 14). Hail and graupel fall nearest the updraft, medium hydrometeors further
  downwind, and the smallest ice stays aloft as the anvil.
- Thresholds:
  - 0-6 km bulk shear over 30 kt gives supercells.
  - SR inflow over 40 kt gives very large supercells; under 25 kt, mini-supercells.
  - SR outflow over 40 kt ventilates strongly; under 20 kt leaves the downdrafts near the updraft.
- Anvil-level SR wind (Rasmussen and Straka 1998): under 18 m/s HP, 18-28 classic, over 28 LP.
- **[GK]** Weak mid-level SR flow (under about 8-10 m/s) lets precipitation wrap into the low-level mesocyclone (Brooks
  et al. 1994).
- HP storms: the rain falls near the updraft and rotation sweeps it round into the hook and the "bear's cage".
- LP storms: high bases and strongly tilted updrafts carry the precipitation away before it grows.
- **[GK]** Distances:

  | | LP | Classic | HP |
  |---|---|---|---|
  | Updraft to the forward-flank core | 10-20+ km | 5-12 km | 0-5 km (wrapping) |
  | Hook length | | 3-8 km | broad, rain-filled |

## CAPE and the updraft

- w_max = sqrt(2 CAPE); the likely value is about half of that (Stull 14.8).
- Normalised CAPE, CAPE / (z_EL - z_LFC): 0.1 or less is tall and thin, 0.3 or more short and fat (Stull).
- The mesocyclone is 3-10 km across (Stull ch. 14). A Cb stem is about as wide as it is deep.
- Stronger deep shear and stronger SR inflow both give wider updrafts (METR 4403 §15, §32).
- The anvil top sits at the EL. Overshooting tops reach up to 5 km above the tropopause.

## Moisture and the LCL

- z_LCL ≈ 125 m per °C of the dew-point spread (Stull).
- The LCL term in STP is 1 below 1000 m and 0 above 2000 m. A low LCL means a humid boundary layer and a warmer RFD.
- Cloud between the LCL and the LFC is smooth and laminar; above the LFC it turns to cauliflower turrets (Stull ch. 14).
- LP skies are clear and high-based; HP skies are full of low and mid cloud.

## Wall cloud forms

From the user's five reference photographs (2026-11-10) and Stull ch. 14. A wall cloud is an abrupt lowering of the
rain-free base under the main updraft, where rain-cooled, moistened air from the forward flank is drawn in and condenses
below the base. Its shape varies from storm to storm and from cycle to cycle:

- A shallow, ragged bowl that curves up to the base at its rim (weak or young lowerings, high bases).
- A block: a flat floor with crisp, near-vertical sides, two to four times as wide as it is deep (the photographs'
  commonest; a strongly rotating lowering reads as a cylinder).
- A stepped lowering: a terrace round a lower inner floor, often with the tail cloud feeding the upper step.
- A broad cone tapering down toward the tornado (large tornadoes under low bases).
- Many slope: lower on the side the tail cloud feeds, or on the far side.
- A low LCL (humid air) hangs bigger, blockier and lower wall clouds; high bases keep small shallow ones.

The game's odds and sizes for these are its own numbers (`StormStructure.WallForm`, `WallSize`), not measured ones.

## Lifecycle

- The ordinary cell lasts H/w0 + H/v_t: about 50 min.
- Storm-scale lift takes about 5 min to reach the LFC.
- Occlusion maps onto the shrinking, rope stage of a tornado (Huang and Xue 2026, METR 4403 §23-24). The mid-level
  mesocyclone separates from the low-level one and the occlusion downdraft joins the axial downdraft.
- **[GK]** An occluding tornado slows and turns left of the storm's motion: the classic leftward hook at the end of a
  damage track. It is a turn of tens of degrees over several minutes, never a jump.
- **[GK]** Cycles: 20-60 min, about 40 typical. The new low-level mesocyclone forms 3-10 km east-south-east along the
  RFD gust front.
- **[GK]** The anvil appears 10-20 min after the tower reaches the EL and spreads at about the anvil-level SR wind.

## Anvils and glaciation

- Neighbouring anvils merge into one shield (Stull ch. 14).
- Supercooled water survives to -40 °C (homogeneous freezing, Stull ch. 7). Cb calvus (no fibrous ice) becomes capillatus
  (a fibrous, glaciated anvil).
- The updraft has cauliflower turrets, and striations where it rotates. The anvil has fibrous edges and mammatus under it.

## How StormStructure uses it

| Environment | Geometry |
|---|---|
| SR inflow 0-1 km, SRH 0-3 km, precipitation mode | mesocyclone radius 1.6-4.8 km |
| Normalised CAPE, deep shear | tower radius (0.9-1.15x; at least the mesocyclone's) |
| Anvil-level SR wind, mode | forward-flank core: tower + 2 km + 0.25 km per m/s (HP less, LP more), never closer than the web build |
| Mid-level SR wind | hail core just outside the tower, further in stronger flow |
| Tornado | the web build's surge (the RFD is not separated), at least 1.9x the funnel's top + 0.7 km |
| CAPE, EL, base | tower reaches the EL at 0.45 sqrt(2 CAPE): 9-20 min |
| LCL, SRH 0-1 km, seed and cycle | the wall cloud's form: bowl, block, tiered or cone, tilted or level, 0.5-1.1x as wide and 0.85-1.55x as deep as the design's bowl (`-- --wallforms`) |
| Tornado rope-out | the occlusion drifts the wall cloud back and left at up to 0.6 and 0.5 of the storm's speed, at most 2.6 km: the tornado slows and hooks left |
| Day draw | cloud base (the parcel's LCL) at most 2 km |
| Base to EL | glaciation from 55 % of the way up to 1.5 km under the EL |

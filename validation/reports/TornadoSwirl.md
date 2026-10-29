# Tornado funnels from the vortex's swirl ratio (2026-10-29)

Branch `feature/tornado-improvements`. The funnel's form now comes from the vortex it hangs in instead of the hand-set
radius curve (`lerp(ground, top, pow(h, profile))`): a swirl ratio per tornado (`StormTornadoes.SwirlOf`, within its
shape's range by its width), the vortex breakdown's height from it (`BreakdownOf`, S_tor7.w), and the condensation
surface of a Rankine vortex with a 2.5x wider core above the breakdown (`Form` in C#, `tornadoForm` / `tornadoRadius`
in StormMarch.compute, the same sums), bent as a power through the foot's radius (the shape's, within the user's
limits) and the top's (a plain funnel's the form's own; a wedge's envelope keeps the user's 1.5-2.1 flare). Sources and
the reasoning: the comment above `SwirlRange` in `StormTornadoes.cs` (Ward 1972; Church, Snow, Baker & Agee 1979;
Rotunno 1979; Lewellen 1993; Davies-Jones 2015).

| tornado | swirl | breakdown at | foot / top before | foot / top after |
|---|---|---|---|---|
| plains seed 1 cycle 0, EF2 stovepipe (250 yd) | 0.45 | 0.79 of the funnel | 114 / 149 m | 114 / 425 m |
| plains seed 3 cycle 1, EF2 cone (212 yd) | 0.61 | 0.55 | 97 / 367 m | 97 / 361 m |
| plains seed 2 cycle 1, EF3 wedge (1177 yd) | 1.17 | 0 (two-celled) | 538 / 895 m | 538 / 895 m |

`tornado_swirl_before_after.png`: left the previous build (the trumpet collar lowered to half way up for stovepipes),
right the vortex form; top the stovepipe at 0.3 of its life, bottom the cone at 0.45. The stovepipe is one straight
column up to its breakdown near the cloud, then curves out into the lowering (the breakdown and the condensation
surface's flare, not a collar). The cone's change is subtle (mean pixel difference 0.04/255): its breakdown half way
up gives a slight bulge there, but at this size and distance it reads as the taper it was. `tornado_swirl_wedge.png`:
the wedge at 0.45 of its life, a two-celled block with slightly convex sides, the user's flare kept.

Checks: `validation/parity/csharp -- --shapes` PASS (every tornado of 800 free storms and 300 games per setup within
the width and EF limits), `-- --life` PASS; sheets `StormLabTests -slTornadoLife plains 1 0 / 3 1 / 2 1`
(`tests/StormLabTest/Screenshots/storm_life_plains_*.png`). The compute shader compiled (ComputeShaderImporter in the
batch log). Not checked in Play on the test map; not pushed.

Not done, and the natural next steps if the look is wanted: a breakdown bubble drawn as a region of broadened, ragged
funnel (the form widens the funnel there, the skin does not change); secondary vortices placed by swirl for the
multiple-vortex shape as well as wedges; the dust whirl's height from the corner flow (`tornadoWhirlTop` still uses
the profile); advecting the skin and debris through the analytic vortex flow for fluid-looking motion.

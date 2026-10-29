# Storm Lab 2.1

Built from [StormLab](https://github.com/jonnyglessner-jpg/StormLab), the web build, at commit f5bd687 ("Add varied
tornado shapes and reproducible storm cycles"): its designed supercell as a volumetric storm for Unity 6 HDRP, with
Storm Lab 2.0's lighting (physical sun, sky, ground bounce and night), its lightning and thunder, and its tornadoes. It
follows Supercell Lab 2.0 (`../supercell-lab-2`): the storm is the website's, not 2.0's brain-driven skin.

## Layout

| Path | What |
| --- | --- |
| `src/com.stormlab.unity/` | the Unity package (namespace `StormLab`, MIT) |
| `Runtime/Physics` | the web build's environment physics and presets (C# port, from dodge-city-drive) |
| `Runtime/Design` | `StormDesign` (design.ts) and `StormCycles` (cycles.ts, mulberry32): plan and per-moment parameter block; `StormTornadoes` (2.0's tornado character); `StormScenario` (2.0's game layout) |
| `Runtime/Rendering` | `StormRenderer` (the component), `StormVolume` (GPU volume), `StormCustomPass` (HDRP), shaders in `Resources/` and `Shaders/` |
| `Runtime/Lightning` | 2.0's lightning core (own assembly `StormLab.Lightning`, unchanged but for its namespace) |
| `Runtime/Lightning/Integration` | `StormLightning` (add to the storm), `LightningCloudBridge`, `StormLightningActivity` (flash rates from this storm's cycles) |
| `Runtime/Audio` | the sound (assembly `StormLab.Audio`): `StormAudio` (tornado wind, rain and hail, thunder: recordings in `Resources/StormLab/Thunder`), `CountryAmbience` (breeze, birds), `ProceduralAudio`, `MasterLimiter` (a peak limiter on the final mix); the synths in `Synth/` are plain C# |
| `tests/StormLabTest/` | Unity 6000.6.6f1 HDRP test project; the plains test map (`Assets/TestMap`) and the render checks (`Assets/Editor`) |
| `tools/testmap/` | the test map's Python pipeline (from 2.0; README there) |
| `validation/` | parity check, website captures and comparisons, reports |

## Use it

Add `StormRenderer` to a GameObject in an HDRP scene. It installs its own custom pass. Set `preset` (the game's setups,
`StormPreset.All`: plains, lp, dixie, ohio; lp is the website's "Big hail day" environment renamed, and it draws a classic
supercell. The website's own seven presets stay in `StormPreset.Website` for the parity check and comparison, picked
with `web:<key>`, e.g. `web:lp`), `varied` and `seed` (on: 2-4 irregular cycles with varied tornado shapes, as the
website's "New storm"; off: the original teaching sequence), and place it with `origin` or `PlaceUpdraftAt(ground,
minutes)`. `minutesPerSecond` is the storm clock: 1/60 is real time (the default), 1 is the website's 1x (an hour of storm in a
minute). For lightning, add `StormLightning` next to it: it adds the flashes, the bridge that lights the cloud, and storm-driven
flash rates. For a floating origin call `ShiftOrigin(offset)` with the offset the game applied.

On the test map, every new storm (Play, **N**, or a new setup or seed in the panel) starts at storm time 0, as towering
cumulus, off the map to the west, south-west or south (`TestMapWorld.startBearings`, 165-285 deg), on a track picked by
its seed. Its updraft crosses onto the map 2 minutes (`enterBeforeMature`) before it is fully mature (a supercell's
first wall cloud), then across it with its environment's motion. A storm whose motion cannot bring it in from that
side starts off the map on the side its motion comes from (none of the game's setups now does).
**StormLab > Test Map > Storm Spawn Check** checks every setup over 12 seeds (`validation/reports/StormSpawnCheck.md`,
tracks drawn in `storm_tracks.png`).

**A game** (`StormScenario`, 2.0's scenario on the web build's storms; `StormScenarioRunner` draws it): two supercells
from one game seed, the second starting 2-12 game minutes after the first, in lanes about 26 km apart across the storm
motion (never under 15 miles: closer draws are pushed apart, and the check measures how close the storms come), each its own `StormRenderer` (and, through `StormScenarioRunner.StormCreated`, its own lightning). Each starts as
towering cumulus off the map's upwind edge and comes onto the map 2 storm minutes before its first wall cloud. A game is
60 minutes; each storm's life cycle runs 2.5x the game clock (`pace`) while it crosses the ground at its real speed
(`StormRenderer.motionScale = 1/pace`), so a 30 min build-up to the first wall cloud takes 12 game minutes, and the
lightning follows the faster life cycle. 2.0's rules: the game's top rating is drawn once (60 % no tornado above EF2,
20 % EF3, 20 % EF4 or, rarely, EF5); the strongest tornado goes to one storm, most likely the northern one; every other
tornado is capped at that rating (`StormTornadoes.Rate` resizes the funnel to suit); the northern storm most likely goes
HP, the southern one classic (or every storm classic or HP); every storm puts down a tornado during the game (the next
storm seed is tried if not). `dotnet run --project validation/parity/csharp -- --scenario` gives the statistics over
400 games. On the test map **G** (or the panel's game-mode toggle) swaps the test storm for a game; T, P, [ ] and N
then work on the game clock and the game seed, the HUD lists both storms, the camera's storm view turns to the nearer
one, and the truck feels both storms' tornado wind. **StormLab > Test Map > Game Check** checks a game in Play
(`validation/reports/StormGameCheck.md`, `-slGameSeed`, `-slGamePreset`).

**Storms die as they leave the map** (`StormLife`, a game addition beside the port; the web build's supercell never
dies). A storm begins to die when its updraft comes within 3 km of the map's edge (all round) on its way out, or after
its last cycle if that comes first. No tornado outlasts that moment: a cycle whose tornado would still be on the ground
is non-tornadic. Then the collapse (22 storm min): the wall cloud, inflow tail, hail and flanking line go first, the
tower sinks and narrows, rain, rotation and lightning fade; the anvil fades last (14 min). `StormRenderer.DieLeaving
(mapHalfM)` sets it from where the storm is placed; the test map and game mode call it. `dotnet run --project
validation/parity/csharp -- --life` checks that no tornado is ever off the map (300 seeds per setup, test-map storms and
games).

**The game's plan** (`StormGame.Adapt`, a game addition beside the port, user 2026-10-26): every varied storm the game
draws (the test map, game mode, the checks that mirror them) goes through it before `StormLife` trims it; "web:" presets
and parity keep the web build's plan. No multiple-vortex tornado: such a cycle becomes a wedge on a low base or a
stovepipe on a high one (another shape when a neighbouring cycle already has it), at that shape's size; wedges keep
their multiple-vortex start. A quicker build-up: the clear slot opening and the updraft wrapping round it take about
1.5 storm min each (from about 4.5), the mature stage of a cycle without a tornado half as long, and everything after
comes that much sooner (a mean plains storm runs 148 storm min instead of 170). `-- --runup` lists both per stage. The teaching sequence (varied off) is the
web build's and keeps its second tornado, which starts as multiple vortices and grows into a mile-wide wedge. No
anticyclonic side (user, 2026-10-27, every game storm, the teaching sequence too): no anti-mesocyclone lowering at the
surge's southern curl and no anticyclonic tornado; `-- --anti` checks that game plans have none.

**Rotation** (`StormSpin`, a game addition beside the port): every turning cloud feature turns by an angle
added up frame by frame, where the web build turns them by rate x wall clock. With a clock that has run for many minutes
any change of rate swamped the rate itself: the mesocyclone looked fast while it spun up and slowed sharply once the
wall cloud formed, a widening wedge turned backwards, and suction vortices whirled at touchdown. With `spinRates = Real`
(the default; user, 2026-10-27: everything at real-world speeds) each feature turns at a real wind over its own radius, so
its edge moves at that wind in real time: the mesocyclone 14-37 m/s at the updraft's edge (a turn in about 20 min; the
web build's rates had made it a 20-60x time-lapse), the wall cloud 12-26 m/s at its rim (4-16 min), the RFD lip 12 m/s,
the anti-mesocyclone 10 m/s ("web:" presets only), the tornado's skin its own wind at the funnel's edge, its core solid-body inside the radius
of maximum wind (Rankine), suction vortices 30 m/s about their axes orbiting at half the parent's wind, the dust whirl the
1/r wind outside the core, satellite and anticyclonic ("web:" only) tornadoes 35 and 30 m/s. Constants and sources are in
`StormSpin.cs`; `Website` gives the web build's rates (the website comparison uses it).
`-- --spin [preset]` lists, stage by stage, the speeds the web build's way showed and the real ones, then every feature's
time per turn.

On the test map (**StormLab > Test Map > Open Test Map**), **Tab** opens the storm and lighting panel
(`TestMapStormPanel`): setup, seed, storm time and speed, jumps to the next wall cloud or tornado, bringing the storm to
the truck; a 24 hour clock that drives the sun, the moon, HDRP's sky and the storm (with date, latitude and moon phase,
and a running clock); the lighting model and night settings; auto exposure that adapts as 2.0's night model does, or a
manual EV; and the lightning (on/off, thunder, rate). **StormLab > Test Map > Day and Night Check** renders the chaser's
view through the panel at eight times of day and night (`validation/reports/StormDayCheck.md`).

Useful properties for a game: `Frame.stage` / `Frame.caption`, `TornadoAmount`, `TornadoGroundWorld`,
`TornadoWindAt(world)` (a Rankine-vortex presentation wind), `UpdraftWorld(minutes)`.

Lighting (`lighting`):
- **Unified** (default; user, 2026-10-28: every part of the storm responds to light the same way, darker undersides
  and rain, storms darken the sky, hail glows green): the physical model as one light field (`stormLight` in
  StormMarch.compute). The light reaching a point is worked out once, the same way for cloud, wall cloud, funnel, rain,
  hail, dust and the air under the storm; the media differ only in their phase function, albedo and how many times light
  scatters in them (cloud: the four octaves; rain and dust, thin media, once). Light diffusing through thick cloud falls
  off as 1 / (1 + `diffusion` tau) (two-stream diffuse transmission) instead of the physical model's floor of a fifth of
  the sky everywhere, so under a deep storm the base is lit by the horizon under its edge and the ground below. Rain is
  `rainExtinction` (2.5) times the drawn rain's extinction, for the eye and for the light alike (heavy rain is about 4-7
  per km; at 1 the low sun lit rain cores right through and they were drawn white). Light that has diffused through the
  hail core is tinted by ice's absorption of red (`hailGlow`; Bohren and Fraser 1993, Gallagher et al. 1996): turquoise,
  and green under a reddened evening sun. The haze between the eye and the storm is lit as the air at both ends is.
  The panel has sliders for the four. References and reasoning: the comment above `stormLight`. The sky-visibility grid
  (cells about 2.6 km) cannot see the beaver tail, a band a few hundred metres deep, hide its own sky, so the game's
  beaver tail takes 60 % of the grid's sky, blended by how much of the near field there is beaver tail (a probe of the
  volume's coarse copy round each point did it first, and left dark blotches on the base where the tail joins it) (user, 2026-10-28: the
  beaver tail was twice as bright in sky light as the cloud it joins; reproduce with `StormGameCheck -slGameSeed 131261
  -slGameSpot "25.4,1.9,22.0,17,283,283,1,0.11" -slGameSpotPitch 16 -slGameProbe`, which adds the light's parts alone).

The game's beaver tail (user, 2026-10-28: "too big and too long", "blend into the main updraft more smoothly", "flowing
into the storm"; `_WallP.w`, on with the game's smooth wall-cloud base, so the website comparison keeps the web build's):
the volume's inflow ramp (4 km tall, 10 km long, the band that read as too big) keeps only its first few kilometres,
where it rises into the updraft's side; beyond it the render pass's beaver tail is three quarters as long, a little
narrower, widens and rises over its last 3 km into the ramp and the updraft, and tapers to a tip that breaks up. Its
texture streams in toward the updraft at a real 15 m/s inflow (`StormSpin.InflowWind`, added up on the wall clock in
`S_spin3.w` like the rotations; 0 under the web build's rates), with fine laminar streaks about 1 km by 250 m so the
motion shows. `StormLightCheck -slLightTailFlow` renders it side-on, 8 frames 3 s apart (`Screenshots/light_tailflow_*`);
`StormGameCheck -slGameSpotFrames count,seconds` adds frames of a spot view. The beaver tail, the inflow ramp and the tail cloud's
aim follow one line (`tailDir`, `tailRoot` in StormCommon.hlsl). The web build's runs 35 degrees right of the storm's
motion, out into the open inflow, its tip 8 km from the rain; the game's runs along the forward flank's inflow-side edge,
10 degrees left of the motion, as a beaver tail lies "along the inflow/FFD boundary" (Stull ch. 14, in
../meteorology/Practical_Meteorology_Stull/NOTES.md; user, 2026-10-28). HP storms (`tailHP`, by their rain wrap) keep the
web build's full-size beaver tail and a longer ramp (Stull: beaver tails are most common in HP storms).
`StormLightCheck -slLightTailFlow [-slLightPreset key]` also renders where it meets the updraft (`light_<preset>_tailroot_*`)
and the tail cloud side-on (`light_<preset>_tailcloud_*`). HP storms' beaver tail and ramp also have the full amount (the
web build gives them 0.85 of a classic storm's; `tailAmt`). The game's tail cloud (user, 2026-10-28, against Stull ch.
14) points at the forward-flank rain nearest the wall cloud, a quarter of the way from the hail core to the forward
flank's core (`tailCloudAim`; along the beaver tail it pointed 29 degrees right of it); its texture streams in toward the
wall cloud at 1.6 x the inflow, and torn scud forms beside and below its outer half and past its tip and races in along
it ("drawn quickly into the tail and the wall cloud by strong winds").
- **Physical**: 2.0's multiple-scattering sunlight with a Mie phase function, sky light by how much of the sky each point
  sees (19-cone sky visibility), light bounced from the ground, the sun's colour from its height, and the night model
  (moon, twilight, adaptation, blue shift, town light), each part of the storm with its own formula. Unified keeps its
  sun, sky and night. Sun from a Light, from time of day, or fixed.
- **Website**: the web build's lighting and fixed colours, for comparison with it.

The storms' darkening of the scene's ambient light (`StormSceneAmbient`, `darkenSceneAmbient`, on in Play with Unified
or Physical): HDRP's indirect diffuse and reflection light, and the fog's sky colour (by `fogFollows`), are scaled by how
much of the sky the camera sees (the storm's sky-visibility grid), the hidden part replaced by the storm's underside at a
tenth of the sky (`underside`). About 0.9 25 km off, 0.3 under the base's edge, 0.2 in the rain.
By place (`local`, the default; user, 2026-10-28): the indirect diffuse light is instead set where the ground is. A map
80 km round the camera of how much of the sky the ground sees (StormShadow.compute `Ambient`, storms multiplied) is the
ambient occlusion of a decal projected straight down (HDRP's AO darkens a surface's sky light, not the sun), so the
ground under a storm is dark wherever the camera is and open country stays lit while the camera is under the base
(plains check at 4 pm: 0.16-0.3 under the storm's cores, 0.97 30 km out). It needs the HDRP asset's decals with "Metal
and Ambient Occlusion properties" (the test project's has them since 2026-10-28); without, it falls back to the camera.
Reflections and fog still follow the camera. Under the base at 4 pm the ambient is about 15 % of the ground's light (the
sun through the storm is most of it), so the change shows most in shade and at low sun.
`StormLightCheck -slLightScene` checks it, the scene's shadow and town light (`validation/reports/StormLightCheck_scene.md`).

Night (user, 2026-10-26): a moonlit night is shown at 1.2 % of day (`nightBrightness`), a moonless one no brighter than
0.4 % (`nightFloor`); the panel's exposure opens to that. At 8 % and 4.5 % the storm stayed light grey all night, a new
moon brightest of all, while the moonlit ground was black. `StormDayCheck` measures both nights with and without the
storm's town light and night fill.
Towns (user, 2026-10-28: "town lights glowing on the base"): a game can list its towns (`StormRenderer.Towns`: where,
how far the lights spread, how bright; glow 1 for about 15,000 people). The base over a town then takes 4x the even town
light (`lightPollution`), falling off as a lit disc seen from the base, and open country, the horizon's glow and the haze
take about a seventh of it (`TownFloor`); with no towns listed every base takes the even light as before. The test maps
list theirs (`TestMapTowns`, by radar site: Dodge City and Garden City by the plains map, Natchez and Vidalia, Effingham,
Mattoon and Charleston). Over a town of 15,000 on a moonless night the base is about 30 % brighter and warmer than with
the even light (the night fill's sky light is most of a base's light then; the night blue shift mutes the orange). Lightning at night (`LightningScale`): where the game's exposure does the night
adaptation (`exposureAdaptsAtNight`, the test map's panel), lightning is dimmed by the same gain to `lightningAtNight`
(0.3) of its daytime look on screen, cloud light, ground light (`LightningSystem.lightScale`) and bolt alike; before, a
night flash came out about a thousand times brighter than by day and turned the whole storm white. The storm's
composite is capped below the half-float limit. `StormDayCheck` runs flashes at night with and without it, reading
frames back in half float to count broken (infinite or NaN) pixels; `-slFlashAtNight a,b` tries other strengths.

The storm's shadow on the scene (`StormSceneShadow`, `castSceneShadow`, on in Play): HDRP's sun gets a cookie of how
much sunlight gets through every storm (StormShadow.compute: from the ground up toward the sun through the volume, with
the light grid's extinction and the storm's transmittance curve), 120 km round the camera at about 120 m, refreshed
every 0.25 s. Under the unified lighting it takes the storm's rain extinction (`rainExtinction`) and its sun reach (the
octaves and the light diffused through the cloud, StormCommon.hlsl `sunReach`), as the storm's ground light does: the
ground under the forward-flank core got 32 % of the sun at 18.8 h with the old curve and unscaled rain, 6.5 % now. The terrain, roads and vehicles fall into the shadow of the anvil, updraft and rain; the storm's own
lighting is unchanged. `StormDayCheck` renders it from the air and straight down (`Screenshots/shadow_*.png`). The moon
casts no storm shadow.

Tornado (`tornado`):
- **Detailed** (default): StormLab 2.0's tornadoes on the website's cycles (`StormTornadoes`). Each cycle keeps the
  website's timing and size; a seeded step (2.0's generator and rules, on its own streams) gives it a rating and wind
  from its width, 2.0's extra shapes (drillbit, elephant trunk, dust whirl, sidewinder), proportions and lean, shape
  changes over its life, skin (ragged, laminar, ghost), brightness, soil by region, suction vortices for big ones, a
  collar and a satellite. Drawn with 2.0's photo-matched funnel, debris, own sun test and horizon light; its flare is
  handed to the website's wall cloud where they meet.
  Wedges are half a mile to two miles wide at the ground (`StormTornadoes.WedgeMinM/MaxM`; a bigger web-build cycle is
  a wider wedge, and their wind and rating follow the width), with a smooth outline, and a wedge's (or multiple-vortex)
  envelope takes the light of the wall cloud it hangs from, so it is that cloud's colour in any light (`-- --wedges`
  checks the widths). Its sides slant evenly out to a top 1.5-2.1 times as wide as its foot (user, 2026-10-26). As it
  ropes out it first narrows to 60 % of its width over 2 storm min, then its envelope breaks up into a scuddy collar from the ground up, whose ragged bottom lifts and rises into the
  base over the rope-out while the central vortex inside narrows to a rope.
  Stovepipes are one laminar vortex, a quarter thinner than the website's. Cones and stovepipes come down as a thin
  tube and widen once on the ground, each its own way (how fast, top or foot first, how thin, how much it snakes).
  `StormLabTests -slTornadoLife <preset> <seed> <cycle> [tag]` renders one tornado through its life (a tag with
  "early" shows its formation, "rope" its rope-out, "sharp" one unblended full-resolution frame from closer).
  Cost (user, 2026-10-27: frames dropped near tornadoes, more the bigger they were): the march steps through it at a
  share of its narrowest part's radius (`tornadoStep`, 6-50 m; 2.0 took 6 m whatever its size), strides through the
  clear air inside its reach by its distance from the bent axis and the dust whirl, lights it only where it draws, and
  bounds a wedge by its own width at each height, not its top's. On the 7900 XT at 1080p a 1-2 km wedge from 1.5 km
  went from ~50 to ~15-18 ms a march, a close rope from 18 to 10 ms, images unchanged (mean difference under 1/255).
  `StormLabTests -slTornadoCost` (or StormLab > Tornado Cost Check) times rope, cone and wedges from three distances
  against 2.0's march, with step-count heat maps (`validation/reports/TornadoCost.md`, `Screenshots/tornado_cost_sheet.png`,
  `tornado_steps_sheet.png`). Rays ending on the terrain take only the part of their last step short of it: the whole step
  had drawn layers in a tornado's dust and arcs on the ground (2.0's march too). `StormGameCheck -slGameSpot ... ,1,km
  -slGameSpotPitch -9` renders a game view looking down from that height; `-slGameSpot20` draws it with 2.0's march.
- **Website**: the website's tornado, unchanged (the website comparison uses it).

Wall-cloud base (`wallBase`, also on the panel):
- **Smooth** (default): a soft underside with scud hugging the base under its middle, more of it under a bigger, lower wall
  cloud (`dotnet run --project validation/parity/csharp -- --wall` lists the wall clouds' sizes). Its tail cloud (user,
  2026-10-27) is a band about a kilometre across, tapering away, with a rounded underside that grows out of the wall
  cloud's at about its level and eases up into the base, joined to it by a soft union; the render pass draws it at full
  detail along its whole length (`inTailCloud`), where the web build's fine edge stopped at a ring round the wall cloud.
  `StormLabTests.RunFromCommandLine -slTail` renders the web build's tail, the game's and none from four views
  (`Screenshots/tail_sheet.png`, `validation/reports/TailChecks.md`).
- **Website**: the web build's ragged fringe (its streaks point at the wall cloud's centre) and its tail cloud (a flat slab
  over 2 km wide hanging below the wall cloud's rim); the website comparison uses it.

Cloud detail (`cloudDetail`, also on the panel; user, 2026-10-27):
- **Enhanced** (default): finer billows on water cloud near the camera. `DetailNoise` is a 64-texel tiling noise of
  inverted Worley cells, read twice by the march (tiles of about 400 m and 140 m, cells 9-100 m). Each read takes the mip
  level of the pixel's footprint and fades out by about 6 km at 1080p, so far cloud does not sparkle. A second
  self-shadow probe 60 m toward the sun shades the side of each small lobe that faces away from it. Only the march
  changes: the volume, light grid, radar, sound, lightning and scene shadow read nothing new. Cost at 1080p (half-
  resolution march, 7900 XT): +0.2 to +0.7 ms in close views of cauliflower, about nothing under the base or in rain.
- **Website**: the web build's detail (`CloudNoise`'s octaves only). It draws the same image as before the switch existed
  (0.00-0.01 % mean difference, against 0.05-0.3 % between two identical runs); the website comparison uses it.

**Sound** (`Runtime/Audio`). `StormAudio` (one per scene) hears every storm at the `player`: the tornado wind
(`TornadoWindAt`), silent below 3 m/s, then a gusty rush, moaning and whistling round the truck, the low roar, debris
strikes and buffeting as it rises to full intensity at 80 m/s (`windRange`); rain and hail where the player is, read back
from the storm volume's precipitation (so it follows the shafts and curtains you see), from scattered drops ticking on the
truck to a downpour's hiss and roar, with big hailstones thudding into the ground round the truck in the hail core; and
thunder for every flash, through `LightningSystem.ThunderOverride`. `thunder` switches it on and off (on).
By default (`thunderSound = Recordings`) it is real thunder: 30 clips cut from 18 CC0 Freesound field
recordings (listed in `audio-sources/thunder/CREDITS.md`) into `Resources/StormLab/Thunder`, sorted close (a crack or
clap, within ~2 km), medium (~2-6 km) and far (low rolling); each flash plays one from its distance's tier (none repeats
until the tier has been through), starting when the sound of the channel's nearest point arrives, at full level within
700 m (`thunderFullLevelM`) and falling as distance^-0.9 beyond (-8 dB at 2 km, -19 dB at 8 km), low-passed as air takes
the highs (11 kHz at 2 km, 2.4 kHz at 16 km), panned toward the flash. `Synthesized` instead makes each flash's thunder
from its channel (`ThunderSynth`: every segment's pulse at its own arrival time, 1/distance, air absorption, a seeded
character). Up to 16 thunder voices; a nearer flash displaces a farther one still on its way. `MasterLimiter` on the
listener keeps everything heard together under -1 dBFS. `CountryAmbience` (procedural): a soft breeze, birds (western
meadowlark, red-winged blackbird, sparrows, mourning dove) calling from 20-140 m and often repeating; the birds stop as
soon as the tornado wind is heard and come back 2 minutes after it has gone (`birdsReturnAfter`); `birdVolume` sets their
level. On the test map `TestMapAudio` adds both, and `TestMapCarAudio` gives the truck a V8 on a six-speed automatic,
tyre squeal when the tyres slide past their grip, road roar and suspension thumps; the wind and rain are taken at the
truck (at the camera in free flight); **M** mutes. `dotnet run --project validation/audio` renders every synth to WAV in
`validation/audio/out/` (gitignored) and checks their levels; `python -I validation/audio/thunder/check.py` checks the
thunder clips (`cut.py` remakes them from the raw recordings, `analyse.py` finds the events in them).

**The tilted updraft** (`StormTilt`, `precip = Tilted`, a game adaptation beside the port; user, 2026-10-27). The web
build leans the updraft at most 0.14 km a km and drops its rain at fixed places round the base. Here the updraft leans
by its storm-relative wind over its speed (half the 0-6 km shear over the plan's updraft: 0.27 plains, 0.43 Dixie), and
the rain lands where the lean and its fall through the storm-relative wind carry it: rain (5 m/s, about 15 minutes'
fall) 4-5 km further off, hail (23 m/s) about 2 km, less drift in an HP storm. The base is rain-free on its inflow
side (1.4-2.2 km), and a tornado has clear air round it, 0.35 km + 0.15 a rating step, 1.6x in a classic storm and
1x in an HP one, whose rain wraps outside it. The volume's build applies it after the port's rain (`precipClear`), so
the 3-D storm, its rain sound and the radar agree; `-- --tilt` in `validation/parity/csharp` lists it per setup, and
the panel's "rain layout" switches it off. Parity and the website comparison use the web build's layout.

The rain shield (user, 2026-10-28: "trim the rain shield", same layout): the web build's forward-flank rain has no cloud
of its own, and its sweep downwind (up to 7.5 km past the core) fell 9 km out of the anvil's underside with nothing below
6 km, where an afternoon sun slipped under the anvil and lit it white from 20 km off. Once the volume is built,
`RainUnderCloud` (StormVolume.compute) keeps that sweep's rain only where cloud within 6 km of the base lies within about
5 km; the forward-flank and hail cores keep theirs (cutting them too left classic storms with no forward-flank curtain).
The rain sound follows it; the radar (analytic) still shows the whole sweep. The forward-flank rain's own cloud (user,
2026-10-28: "add the cloud deck", same layout, with the anvil): a deep mass over the rain's footprint, its ragged base 1-2.2
km over the updraft's and rising downwind, its top 4-5.5 km over the base and rising into the anvil near the core; its
outline wanders, it thins toward it like a lens, and ragged tags hang from its base into heavy rain (user, same day: the
first deck was a box, "straight edged"). The curtain under it leans downwind with height and its edge frays, so side-on
it is no longer a vertical line from cloud to ground (`ffd_25km`, `ffd_shade_20km` and their `_norain` frames). The rain
falls from cloud and is shaded by (inside the forward-flank rain 0.082 -> 0.027 of the frame's light in `StormLightCheck`). A face of it the sun lights is
white, as a sunlit face of the updraft is (`ffd_12km`). A repaint spread over frames applies the last repaint's
map of the rain kept as it writes each slab (trimming in place at the end flashed the whole sweep back for 3 frames at
every repaint; `StormLightCheck -slLightFlicker` measures it: 11 % before, 0.02 % after). `StormGameCheck -slGameProbe` lists cloud,
rain and the light grids along the 25-minute view and renders the unified light's parts there.

**Map and radar** (user, 2026-10-27). On the test map a map in the bottom-right corner (`TestMapMinimap`) follows the
truck, heading up, with the roads, the radar and the next turn; click it to open it full screen (north up: drag to pan,
scroll to zoom, click a road to drive there, Esc closes). The GPS (`TestMapRoads`) routes over the whole road network
(2,757 junctions and corners, every piece named: K-19, Rd 12, Ave 30), the fastest way by road class (state 65 mph,
paved 55, gravel 45), with turn-by-turn steps, and plans again when the truck is 60 m off the route for 2.5 s. Its data
is made by `tools/testmap/navmap.py` (the network from roads.py and a base map; gitignored with the rest of Generated/).
The radar (`StormRadar`, `Runtime/Radar`) is a WSR-88D at KDDC's real site (Dodge City, just inside the map's south-west
corner): base reflectivity at 0.5 deg with the beam's true height (4/3 Earth) and width, or composite; 250 m gates and
0.5 deg radials; the antenna turns every 12 s and repaints the wedge it swept, with gate-to-gate noise new every scan.
The echo is a supercell's laid out on each storm's own features, from the notes in `../meteorology`: Z = 300 RR^1.4
(Stull) with each region's drops (Kumjian et al. 2015: melted ice in the forward flank, the largest drops along its inflow
edge, warm small drops in the hook), hail lifting the rear of the core to 45 + 25 x its amount dBZ and tapering evenly
into the rain round it (no separate disc), a debris ball at the tornado, |K|^2 for
ice above the freezing level; laid out as the user's reference scans: a forward-flank shield with a sharp inflow edge,
the inflow notch under the updraft, the hook wrapping cyclonically round the mesocyclone to a ball at its tip with a
clear eye, first echo aloft, light rain round it and the anvil's snow aloft. Colours: smooth (RadarScope-like, default)
or the NWS steps.

Output: by default the storm's light is in HDRP's physical units under the game's exposure and tonemapping.
`UseStandaloneLook()` gives the website's backdrop and display curve (HDRP tonemapping off).

## Checks

- **Design parity**: `bash validation/parity/parity.sh` runs the website's own `design.ts` in Node and the C# port, and
  compares every parameter of every frame (1.7 min steps) of all 7 presets x (teaching sequence, seeds 42, 7, 2026).
  Last run: 190,164 numbers, identical (worst difference 0), stages and cycle shapes identical.
- **Website comparison**: `node validation/website/capture.mjs` captures the live site through its test hook (headless
  Chrome, WebGPU) with each image's exact camera; `StormLabTests -slCompare validation/website/cases.json` renders the
  same cameras in Unity; `python -I validation/website/compare_images.py` writes side-by-sides to
  `validation/website/side/`. Last run (7 views, website lighting): mean absolute difference 0.6-1.6 % on six views,
  2.4 % on one where the backdrop's field pattern differs (see Known gaps).
- **Render checks**: `-executeMethod StormLabTests.RunFromCommandLine` (`-slQuick` for one time): both lighting models
  through the plains storm's life, LP, Dixie and Ohio storms, dusk, a moonlit night, and a ground flash at night.
  Report: `validation/reports/StormChecks.md`, images in `tests/StormLabTest/Screenshots/`.
- **Tornadoes**: `StormLabTests -slTornadoes`: the nine shapes forced at 2.0's widths and ratings and framed as 2.0's
  sheet (`storm_tornado_sheet.png`, beside 2.0's in `tornado_sheet_vs_2.0.jpg`), six close with the sun to the side,
  unforced variety storms, and the website's tornado beside the detailed one. Report: `validation/reports/TornadoChecks.md`.
  `dotnet run --project validation/parity/csharp -- --tornadoes` lists the characters a few storms get.
- **Test map**: `-executeMethod TestMapBuilder.BuildFromCommandLine` (needs `TestMapData/` and the generated textures,
  both gitignored), then `-executeMethod TestMapTests.RunFromCommandLine`: the drive test (all 9 roads pass) and views,
  including the storm from a chaser's position in the game's HDRP look. Report: `validation/reports/TestMapReport.md`.

- **Play mode on the test map**: `-executeMethod StormPlayCheck.RunFromCommandLine` (about 3 min). Real time and 10x
  (storm clock, lightning clock, flashes, strikes on the terrain, thunder), a floating-origin shift of 36 km, and the frame
  cost at the game's half-resolution march. Report: `validation/reports/StormPlayCheck.md`. Last run: PASS (below).
- **Cloud detail**: **StormLab > Test Map > Cloud Detail Check** (`CloudDetailCheck.RunFromCommandLine`, about 2 min): six
  fixed views of the plains storm (young cumulonimbus at 4 and 6 km, the tower from 14 km, the base from 8 km, the wall
  cloud from 3 km, the storm from 25 km), each drawn with the website's and the enhanced detail: the march's GPU time,
  each image's fine-detail energy above the horizon, and sheets with both frames and 1:1 crops
  (`Screenshots/detail_<view>_sheet.png`). Report: `validation/reports/CloudDetailCheck.md`. Last run: PASS.
- **Lighting**: **StormLab > Test Map > Lighting Check** (`StormLightCheck.RunFromCommandLine [-slLightPreset key]
  [-slLightHour h] [-slLightDebug]`, about 2 min): seven fixed views of one storm (the whole storm from 25 km, the base from
  10 km, under the base, inside the forward-flank rain, against the sun, the forward flank side-on, the hail core), each drawn with the physical and
  the unified lighting: the brightness of the sky and storm in each, the scene's ambient scale, the march's GPU time, and
  sheets (`Screenshots/light_<preset>_<view>_sheet.png`). `-slLightDebug` adds the unified light's parts alone and the
  light grids' values through the updraft, rain and hail cores. Report: `validation/reports/StormLightCheck[_preset].md`.
- **Sound**: `dotnet run --project validation/audio` (the synths offline: wind and rain grow with their drivers, thunder
  falls off with distance, nothing clips; WAVs to listen to); `python -I validation/audio/thunder/check.py` (the thunder
  clips: tiers, levels, import settings, credits; a preview by distance). `-executeMethod StormAudioCheck.RunFromCommandLine` (about
  3 min, `-slAudioWiring` for the first part only) in Play on the test map: the truck's gears and squeal, dry upwind and
  pouring under the core, the wind sound walking in to a tornado and the birds falling silent, thunder heard and nearer
  thunder louder. Report: `validation/reports/StormAudioCheck.md`. Last run: PASS.
- **Map and radar**: **StormLab > Test Map > Map and Radar Check** (`TestMapMinimapCheck.RunFromCommandLine`): the road
  network in one piece, 300 random trips routed (continuous, ending where asked, not much longer than by the section
  grid), then in Play the radar through a plains storm's life and a Dixie storm (the inflow notch, the hook, the debris
  ball) and the map drawn over the chaser's view, corner and full screen (`Screenshots/minimap_*.png`; the map's text is
  IMGUI and is not in them). Report: `validation/reports/TestMapMinimapCheck.md`.

Headless Unity: `"C:\Program Files\Unity\Hub\Editor\6000.6.6f1\Editor\Unity.exe" -batchmode -projectPath
tests/StormLabTest -executeMethod <method> -logFile <log>`. Never with `-nographics`.

## Known gaps (first version, 2026-10-24)

- The website backdrop's farm-field pattern hashes `sin()` of large numbers, which GPUs evaluate differently, so its
  fields differ from the website's. Only the standalone backdrop has it; a game draws its own ground.
- Physical lighting is calibrated at a 15 degree sun (2.0's reference); the default sun is the website's, 21 degrees up.
- The lightning light field spans the whole 180 km volume (96 x 96 x 32, about 1.9 km cells); 2.0's spanned 60 km at
  about 0.9 km. A ground flash lights the base and rain shaft strongly; an in-cloud flash seen from 24 km is a modest
  glow. The bolt itself has not yet been checked in an open-air view (the test flash struck inside the rain curtain).
- Not carried over from 2.0: the brain, wind advection of the cloud, the 30 m near-field volume and StormGenome's
  climatology (SPC ratings and path lengths): the detailed tornado's rating comes from the website's funnel width.
- The detailed tornado partway through a central-vortex takeover (big stovepipes and wedges, early in their lives) has
  a ragged, dark crown: 2.0 draws its envelope half-formed then. 2.0 did the same; its sheet shot wedges later.
- Game mode differs from 2.0's scenario in two ways: tornadoes keep the website's cycle timing (2.0 cut a tornado's
  life short so it ended within the game; here a late tornado can still be on the ground at 60 min), and ratings come
  from the funnel width, so a game built for EF2 may top out at EF0-1 (often in the LP and Ohio setups). Two storms
  cost about twice the march of one.
- Two-storm views (user, 2026-10-28: "vertical seams"): the seams were the forward-flank rain of the storm nearer the
  camera, which rose to the anvil through clear air above the deck as a sunlit white wall with a vertical edge
  (StormGameCheck -slGameAlone 15,35 shows each storm alone, the composite with each storm in false colour, and the
  rain and cloud along the rays). Above the base, rain is now kept only inside cloud (game layout), and each storm's
  distance for the composite's ordering blends in where it draws next to nothing instead of switching at a threshold.
  The nearer storm's deck still overlaps the farther storm with a cloud's own crisp outline.
- Seen from under a storm's anvil well downstream (inside its forward-flank rain, e.g. 36 km down its motion), the
  rain region shows straight-edged openings onto the sky beyond. One storm alone does it, so it is the storm's own
  shape from there, not the game's two storms; it has not been compared with the website from that spot.
- The dust whirl needs a high cloud base to look like one; on the Plains setup its condensation gap is small.
- Floating origin: the storm, its lightning and the view follow a shift exactly (Play check: 0 m error, 0.28 % image
  difference in the storm), but the test map's roads are `BatchingStatic`, and Unity combines static-batched meshes when
  Play starts, so they stay behind. A game with a floating origin must not static-batch what it shifts.
- At 10x the lightning runs on the wall clock (Auto), so flashes stay watchable and thunder plays at its real delay; on
  the storm clock it would be silent, as in 2.0. At real time the 16 thunder voices were all in use at once (2.0's cap).
- Frame cost, RX 7900 XT, game look, half-resolution march, a stovepipe in view from 8 km (Play check, GPU time, with
  the detailed tornado): march and resolve 3.56 ms at 1080p (2.08 checkerboard), 5.20 ms at 1440p (3.19); 4.23 ms at
  1080p while a flash lights the cloud. With the website's tornado the same view was 1.85 ms at 1080p. Volume: 0.85 ms on
  each of the 4 frames of a repaint plus 2.44 ms relight on the last (at most every 0.2 s while storm time moves), 5.9 ms
  for a full repaint after a seek. Worst frame about 6.9 ms at 1080p. An RTX 3060 was not measured (2.0's rough estimate is
  2.5x slower, which would put relight frames over an 8 ms budget).
- Standalone checks at 1600 x 1000 and full march resolution: 21-26 ms a frame.
- The website checkout has an uncommitted change to `shaders.ts` (wedge debris wisps); this port follows the committed
  f5bd687, as the live site does.

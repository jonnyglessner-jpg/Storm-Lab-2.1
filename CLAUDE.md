# CLAUDE.md

Storm Lab 2.1 (github.com/jonnyglessner-jpg/Storm-Lab-2.1; the game it carries is **Never Stop Chasing**, user 2026-11-02): built from the StormLab web build's designed supercell
(github.com/jonnyglessner-jpg/StormLab at f5bd687) in Unity 6 HDRP, with Storm Lab 2.0's lighting, lightning and
tornadoes. It follows Supercell Lab 2.0 (`../supercell-lab-2`). The folder and package (`com.stormlab.unity`) keep
their first names.
README.md has the layout, use, checks and known gaps.

## Rules

- **The website is the reference.** `Runtime/Design` and the shaders' BUILD / LIGHT / RENDER code are line-for-line
  ports of `../StormLabWebsite/src/storm3d/{design,cycles,shaders}.ts` (f5bd687). Keep them diffable: same names, same
  order, same comments. Run `bash validation/parity/parity.sh` after touching the design (it must stay at difference 0).
- Game adaptations go beside the port, not into it (as `lighting = Physical`, `tornado = Detailed`, `cloudDetail =
  Enhanced`, scene depth, the lightning hook, `TornadoWindAt`). `CloudNoise` is the web build's noise and the volume's
  build reads it: never change it for looks; the enhanced detail has its own `DetailNoise`, read only by the march. `StormTornadoes` (2.0's tornado character) post-processes the frame and owns slots
  23-28; `StormDesign` fills 0-22 exactly as the website. `StormSpin` owns 29-33: the angles every turning cloud feature has turned
  (mesocyclone, wall cloud with its inner ring and its junction with the funnel, RFD lip, anti-mesocyclone, the detailed tornado's parts, satellite and anticyclonic tornadoes),
  added up per frame. Rates are real-world winds over each feature's radius (`spinRates = Real`, user 2026-10-27: everything
  at real speeds); `Website` gives the web build's rates for the website comparison. Never turn a feature by `rate x clock` when its rate can change (the
  speed seen is then rate + clock x d(rate)/dt: stalls, lurches, spin reversing); integrate it in `StormSpin` instead
  (`validation/parity/csharp -- --spin` compares the two). The same goes for a texture that rises or flows in units of a
  size that changes: `(z - v x clock) / r` slides the whole texture by clock x d(1 / r) (a funnel widening out of its rope
  stage sent the skin and dust plunging at ~1 km/s, user 2026-11-01); sample `z / r - rise` with the rise added up in
  `StormSpin` (S_spin5; `-- --rise` checks the speeds). New random draws get their own `StormRng` stream.
  `StormScenario` (2.0's game layout) uses streams 500 (tiers, lanes, starts), 510 (the day's motion turn and speed, the
  lanes' gap and shift), 600+i (storm seeds), 700+i (precip), 800+i (each storm's own motion and initiation point);
  its storms reach the website plan only through `PlanOverrides` (precip, forced tornadic; its turnDeg / speedScale are
  applied after the design by `StormGame.Deviate`, which turns fwd / lft / tilt / adir with the motion), which parity must not see.
  The storms never come within 25 miles of each other (user, 2026-11-02; `MinSeparationM`, the lanes pushed apart until
  `ClosestApproachM` says so): keep it when touching lanes, motions or starts, and run `-- --scenario` to see the spread.
  Game changes to the plan itself (cycle shapes, timing, no anticyclonic side) go in `StormGame.Adapt`, which every game plan passes through
  (`StormGame.Plan` in the renderer, `StormScenario`, the game-facing `validation/parity/csharp` checks) and parity does not.
  Re-run the website comparison (README) after shader changes; it uses the website's lighting and tornado.
- Setups: the game offers `StormPreset.All` (plains, lp = the website's hail environment renamed, dixie, ohio; the
  website's lp, capped and pulse removed at the user's request, 2026-10-25). `StormPreset.Website` keeps the website's
  seven for parity and the comparison (`web:<key>`); never edit it to change a game setup.
- Structure from the environment (`structure = Environmental`, the game's default; user, 2026-11-07: updraft and
  downdrafts too close, tiny hooks, slots too small for the tornado, the wall cloud shoved aside as the slot opened, toy-like
  parts and anvils, anvils not mixing with three storms): `StormStructure.Apply` re-lays the frame after `StormTilt` from
  the plan's `StormEnv` (kept by `StormGame.Design`). It sizes the mesocyclone, tower and cores; the RFD keeps the web build's surge size and wall cloud place
  (user, 2026-11-08: separation is for the forward flank only), with the surge never under the tornado's need. It fixes the wall
  cloud under the updraft's rear flank; the occlusion moves it back only as the tornado ropes out, as a drift velocity
  (`StormStructure.Occlusion`: back and left at a share of the storm's speed, integrated over time), never a fixed offset
  put in over the rope-out, which made the tornado run backwards. It eases the
  build-up's amounts over about 3 storm min and runs the tower's rise on the storm's own CAPE.
  - It marks the frame `S_struct` (slot 32.w). That flag gates every shader part of the overhaul: `GameNoise` detail and fibrous ice, the step jitter, glaciation with height,
    soft joins, the anvil's outline, no clear cylinder round a tornado, a ragged rain-free base edge, and the rain stored
    as 0.8 x intensity^1.6 (user, 2026-11-08: a box cut from the RFD rain, rain too thick; `-slRain`).
  - Light through rain takes `StormStructure.RainTransport` (0.3) of the eye's extinction on every light path: the light
    grid (`StormVolume.rainLightShare`), the scene cookie, and the tornado's tests (`_RainP` in the march). Keep a new
    light path through rain on the same share, or a funnel and its wall cloud fall out of step again
    (`StormLightCheck -slLightTornado -slLightPreset dixie`). The test map's day exposure adapts to the storm's shade
    (`TestMapStormPanel.stormAdaptation`, user, 2026-11-08: "unnaturally dark"): the share of open daylight that
    reaches the camera, the sun's (`StormSceneShadow.SunAtCamera`) and the sky's (`StormSceneAmbient.Ambient`)
    together (`StormShade`; 2026-11-09: by the sky seen alone the ground under the anvil was near black at noon).
    Check it on the roads round a game's storms with `StormExposureCheck`.
  - `S_game` (slot 34) holds each storm's noise offset; `S_wform` (slot 35, the block is now 36) the wall cloud's form.
  - Wall clouds (user, 2026-11-10, with five photographs: "sharper, more vertical edges ... variety"; "all the cloud
    features ... blended together ... with smooth transitions"): each cycle of each storm draws a form
    (`StormStructure.WallForm`, stream 540 + cycle: bowl, block, tiered, cone, and a tilt) and its own width and depth
    (`WallSize`, 560 + cycle), drawn by `nearField` from `S_wform` (all zero is the web build's bowl, so the website's
    plans are untouched). In the march the game's wall cloud takes the game noise's lobes on its surface
    (`STORM_WALL_LOBES`: `nearField` adds them for the march alone) and their shade (`g_wallShade`: one probe of the
    lobes, toward the sun where it reaches and toward the open sky otherwise, feeding `selfSh` and the sky they hide:
    inputs of `stormLight`, never a light of its own). Near-ground features join by soft union (`a + b - a b`) under
    `S_struct`, not the larger of the two. Check the spread with `-- --wallforms` and the look with
    `StormGameCheck -slGameSpot ... -slGameWallForms "block,cone,tilt,tiers;...;own"`.
  - Two to four storms are composited in one pass (`StormRenderer.RecordCompositeAll`, soft order, `lerp(1, T, w)`).
  - Game days draw their own environment (`StormPreset.Day`, stream 520, key `plains~<seed>`). Keep the reference in
    `docs/meteorology/storm-structure.md`.
  - Check with `-- --structure` (the wall cloud is never pushed while the slot grows; the surge is at least 1.8x the
    tornado's top), `-- --track` (the tornado's ground speed at most 1.3x the storm's, at least 0.15x along the motion,
    turning at most 30 degrees per minute), `StormLabTests -slStructure [presets]` (web vs game: views and radar) and `-slAnvils` (three storms).
- Storms die leaving the map (`StormLife`, after `StormDesign.At` and `StormTornadoes.Apply`): no tornado may be off the
  map; check with `validation/parity/csharp -- --life` after changing tracks, placement or cycles.
- Look changes: one named problem at a time, checked in the test map; say when a request is taste, not a bug.
- Lighting (`lighting = Unified`, the default since 2026-10-28; user: "everything on the storm needs to respond the same
  way"): every medium takes its light from `stormLight` in StormMarch.compute and differs only in its phase function,
  albedo and scattering octaves. Never give a part of the storm its own light formula or multiplier; fix the shared one.
  What the storm shades besides itself (the scene's sun cookie, its ground light) takes the sun the same way
  (`sunReach` in StormCommon.hlsl, and the rain extinction); check with `StormLightCheck -slLightScene`. So does the sky
  visibility (`SkyVis` in StormVolume.compute, through rain and cloud by `sunReach` under unified lighting; user,
  2026-10-31: the Dixie wall cloud lit grey over a dark funnel when it saw rain at a tenth of the sun's extinction).
  Check a tornado against its wall cloud with `StormLightCheck -slLightTornado -slLightPreset <key> -slLightSeed <n>`.
  Check with `StormLightCheck` (physical beside unified, at 17 h and 18.8 h).
- The second lighting overhaul (user, 2026-11-10: "another full lighting overhaul ... things just still aren't looking
  right"; their target: match storm photographs, where the base is a dark slate, lowered cloud is as dark as the base or
  darker, the horizon under it is bright and there is colour; everything in the light in scope, structure untouched):
  - Under a storm the light comes in from the horizon beneath the base, and only where the anvil ends. The sky
    visibility's rays run on to the volume's top or edge (`skyTrace`: no step rises over 1.2 km); `SkyFill` then carries
    the horizon's light at the last open level up into the cloud above each column, at `UnderShare` (0.03: an underside
    catches it at a grazing angle; more of it flattened the bases). Lowered cloud and the base's own fringe take the sky visibility just inside the base
    (`_WallL2.z`), so wall cloud, tail cloud and funnel junction are lit as the base is. The ground map's sky is what the
    ground sees, and the bounce takes the scene's darker ground. Calibrate these against the scene: a base about as
    bright as the ground under it, both far under the sky; never brighten a base to make it readable.
  - A tornado is lowered cloud too (user, 2026-11-10: "the tornadoes got left out of the lighting overhaul", a white
    funnel and bright orange dust under a slate base at noon, out of the sun). The funnel, its join with the wall cloud
    and its dust take the base's sky visibility (`_WallL2.z`) and, on top, `SideShare` (0.09, StormMarch.compute) of
    the open air's under the base: a flank turned to the horizon, about three times what the underside catches. Anything
    new that hangs under a base takes its sky the same way, or it comes out some 30 times the base in sky light. Read
    that sky where it is used: held across the funnel's tests it cost the close cone 1.6 ms. Rain below the base's
    level takes its sky the same way (`RainShare`, 0.09; user, 2026-11-10: "Yes, rain"): under a base it is the base's
    slate, not a pale shaft, and where nothing is overhead at that level it keeps the open air's sky.
  - Readability is the exposure's job. The test map's exposure follows the picture (`StormViewMeter`, a custom pass at
    BeforePostProcess; `TestMapStormPanel.viewAdaptation` / `viewTarget` / `ViewTrim`) on top of the storm's shade, as a
    camera's meter does: real cameras open three stops and more under a storm and let the horizon go white. Its grade is
    HDRP's Neutral curve with contrast +25 and saturation +20 (`grade`): ACES crushed the ground in a storm's shade to
    black. A headless check must draw frames while it waits (`StormLabTestKit.Pump`), or the meter has no picture.
  - The sky after sunset (user, 2026-11-10: "the dusk lighting does not look right at all. The sky goes black way too
    soon"): HDRP's sky is lit by the sun Light, which the test map's panel fades out by 1 degree down, and is far under
    the storm model's sky from about 8 degrees up, so the storm stood lit against a black sky all through twilight.
    `StormTwilightSky` (drawn by `StormCustomPass` on the sky pixels, before the storms) shows the model's own sky
    (the march's `skyRad`, for the sun alone: the moon's sky is HDRP's) for the share lost, the same share the sky
    fill gives the ground, at `Scale` 0.4 (the model's dome is two to three times HDRP's; at 1 it came out white). As
    shown, its pale horizon turns blue as the sky's level falls and is darker away from the sun; that is the picture
    only, the storm's light is untouched. Check the run of it with `StormLightCheck -slLightSpot` at hours 18.5 to 20.1.
    When the user's editor has the project open, headless checks run in `tests/StormLabTwin` (the same Assets by
    junction, its own Library and Screenshots).
  - Judge it on the roads with `StormExposureCheck -slExpQuick` (both storms, three hours, sheets) and a spot with
    `StormGameCheck -slGameSpot ... -slGameWallForms "L:<stops>:<aces|neutral|grade>[:con=..:sat=..]"`. A frame that
    comes out pale once in a repeated render is lightning.
- Funnels are smooth (no vertical grooves), as in 2.0.
- Everything on a tornado turns by the angles in `S_spin` / `S_spin2` / `S_spin3`, each wrapped at 2 pi on its own: never
  blend two of them into one angle (a lerp between the core's and the whirl's wound the dust up and snapped it every turn,
  user 2026-10-30); rotate by one and blend what is sampled. Run `TornadoFlowCheck` after touching how anything turns.
- Tornado sizes and ratings by shape (user, 2026-10-29) live in `StormTornadoes` (`MinWidthM`, `MaxWidthM`, `MinEf`,
  `MaxEf`; wedges 750-2000 yd (2026-10-31) and EF3+, EF5 only wedges from 1000 yd or drillbits, ropes/trunks EF2 at most, cones and
  stovepipes EF4). Free storms are held to them in `Constrain`, games in `Rate` (which changes the shape when it must).
  Run `validation/parity/csharp -- --shapes` after touching tornado sizes, ratings or the game's tiers.
- Wedges (user, 2026-10-31): only from the storm's second cycle on (`StormGame.Adapt` reshapes a first-cycle wedge, `Rate`
  gives a first cycle a drillbit instead), 30-45 storm min from touchdown to gone (`StormGame.WedgeLifeMin/Max`, the
  cycle's milestones restretched in `Adapt`), and a life of their own in `StormTornadoes.Apply`: multiple vortices, a
  drillbit central vortex that takes over and widens slowly into the wedge, then the collar breakdown and rope-out
  (`wedgeMultiEnd` / `wedgeDrillEnd` / `wedgeGrown`, `DrawFormation`). Each cycle's clear slot, horseshoe and wall
  cloud are sized to its tornado (`StormCycle.bigness`, `StormGame.Bigness`, set by `Adapt` and refined by
  `StormTornadoes.Build` / `Rate`; `StormDesign.At` reads it in place of the web build's second-cycle enlargement, NaN
  keeping the web build's). Check with `-- --shapes` and `-- --slots`, and a wedge's look with
  `StormLabTests -slTornadoLife <preset> <seed> <cycle> [early|rope]`.
- `Runtime/Lightning` (core) is 2.0's code but for its namespace, the `ThunderOverride` hook (2026-10-26, for
  `StormAudio`'s thunder), the 2026-10-30/31 bolt geometry in `LightningModel` (reaching ground bolts with a few calm
  branches and second contacts; crawlers that crawl: a trunk that forks into secondary trunks, every channel setting out
  when the leader passes its root, `LightningChannel.startAge` / `growSeconds`, `CrawlerSpeedMps`; copy it back to 2.0
  with its namespace) and the
  channels' own custom pass (`ChannelPass` at BeforePostProcess, shader pass `StormLightning`: with Refraction off HDRP
  draws transparents before the storm's BeforeTransparent pass, so the storm's transmittance went over the bolts on top
  of their own occlusion; no hardware depth test, since the storm writes its cloud's depth, but the fragment tests
  against the opaque depth copy `_CameraDepthTexture`, which holds no storm depth, so the ground hides a bolt's foot and
  cloud never cuts one; cloud and rain hide a bolt by its
  optical depth, through the bolt's own storm and every other storm drawn: `slOpticalDepthWorld`, the others bound from
  `StormRenderer.Enabled` by `LightningCloudBridge`; user 2026-10-31: the other storm's crawlers showed through the rain
  and cloud overhead. The nearest other's low features are analytic with its own block: `StormLightningMedium.hlsl`
  compiles StormCommon.hlsl a second time with every top-level name suffixed `_o`; a new top-level symbol in
  StormCommon.hlsl goes into that rename list, or the second copy redefines it), the channel emission in storm units (600; the night scale goes into the
  material, never the 8-bit vertex colours); fixes there should go back to 2.0 too. The flash rate
  and brightness are right (user, 2026-10-30: "already good"): change what the flashes look like and where they go,
  not how many or how bright. Check bolts with `LightningLookCheck`.
- Sound (`Runtime/Audio`): thunder plays CC0 recordings (`Resources/StormLab/Thunder`, cut by
  `validation/audio/thunder/cut.py` from `audio-sources/thunder`, credited in its CREDITS.md; new recordings must be
  CC0 or CC-BY and listed there; run `check.py` after a re-cut). The rest is procedural; the synths in `Synth/` stay plain
  C# (no UnityEngine) so `validation/audio` can render and check them. Run it after changing a synth, and `StormAudioCheck` after changing what drives them.
- Performance (user, 2026-10-30: "spec is to my GPU", RX 7900 XT at 3440 x 1440; README Performance): the game runs the
  `High` tier. Run `StormPerfCheck` (images against its baseline, GPU time per view) after touching StormMarch.compute
  or the volume; a change meant to keep the look must stay at 0.00 / 0 against a baseline made just before it. Keep the
  tornado's code out of the `STORM_NO_TORNADO` path, and anything new that changes where a ray may meet a tornado in
  `reachesTornado` too (Classify), or tiles will be marched without it. Register pressure decides the march's speed:
  a value kept live across the step loop can cost more than the work it saves (a shared light struct made the close
  cone 50 -> 57 ms). FXC's temp count does not predict it; time it. Two ways a few lines cost 1.5 ms each in the close
  tornado views with the same steps a pixel (the wall cloud's lobes, 2026-11-10): arithmetic on uniforms alone inside
  the step (the compiler lifts it out of the loop and holds each result in a register all through it: work such
  values out on the CPU and pass them, as `StormRenderer.WallLobeParams` does), and several values carried from
  `nearField` to where the cloud is lit, across the beaver tail's evaluation (carry one). A change of that kind costs
  the same whether its code ever runs, so a variant that never executes it is no control: compile it out. And check
  that a cost experiment changes the image where it should (`-slPerfOnly`, a baseline made just before).
- C: is nearly full (about 14 GB free on 2026-10-24); the test map's generated data is about 2 GB.

## Running

- Unity 6000.6.6f1, project `tests/StormLabTest`, headless with `-batchmode` (never `-nographics`).
  A headless check opens an empty scene, and the editor reopens on the last scene it had: after running one, the editor
  comes up on an empty scene and the game looks gone (user, 2026-10-30). File > Open Scene > `Assets/Scenes/TestMap.unity`
  brings it back, or set `Library/LastSceneManagerSetup.txt` to that scene before the user opens the editor.
- `StormLabTests.RunFromCommandLine [-slQuick] [-slCompare <cases.json> -slOut <dir>]`, `TestMapBuilder.BuildFromCommandLine`,
  `TestMapTests.RunFromCommandLine [-testmapShotsOnly] [-testmapMap dixie|midwest]`; the real-data maps build with
  `BuildDixieFromCommandLine` / `BuildMidwestFromCommandLine` after `tools/testmap/real` (README there).
- Play-mode checks on the test map: `StormPlayCheck`, `StormDayCheck`, `StormGameCheck [-slGameSeed n] [-slGamePreset key]`,
  `StormAudioCheck [-slAudioWiring]`, `LightningLookCheck [-slBoltHour h]` (all `.RunFromCommandLine`); `StormSpawnCheck`
  runs without Play.
- Sound synths offline: `dotnet run --project validation/audio` (WAVs to `validation/audio/out/`).
- Pure C# checks with Unity's bundled dotnet: `validation/parity/csharp`.

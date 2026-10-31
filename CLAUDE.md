# CLAUDE.md

Storm Lab 2.1 (github.com/jonnyglessner-jpg/Storm-Lab-2.1): built from the StormLab web build's designed supercell
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
  23-28; `StormDesign` fills 0-22 exactly as the website. `StormSpin` owns 29-31: the angles every turning cloud feature has turned
  (mesocyclone, wall cloud, RFD lip, anti-mesocyclone, the detailed tornado's parts, satellite and anticyclonic tornadoes),
  added up per frame. Rates are real-world winds over each feature's radius (`spinRates = Real`, user 2026-10-27: everything
  at real speeds); `Website` gives the web build's rates for the website comparison. Never turn a feature by `rate x clock` when its rate can change (the
  speed seen is then rate + clock x d(rate)/dt: stalls, lurches, spin reversing); integrate it in `StormSpin` instead
  (`validation/parity/csharp -- --spin` compares the two). New random draws get their own `StormRng` stream.
  `StormScenario` (2.0's game layout) uses streams 500 (tiers, lanes, starts), 600+i (storm seeds), 700+i (precip);
  its storms reach the website plan only through `PlanOverrides` (precip, forced tornadic), which parity must not see.
  Game changes to the plan itself (cycle shapes, timing, no anticyclonic side) go in `StormGame.Adapt`, which every game plan passes through
  (`StormGame.Plan` in the renderer, `StormScenario`, the game-facing `validation/parity/csharp` checks) and parity does not.
  Re-run the website comparison (README) after shader changes; it uses the website's lighting and tornado.
- Setups: the game offers `StormPreset.All` (plains, lp = the website's hail environment renamed, dixie, ohio; the
  website's lp, capped and pulse removed at the user's request, 2026-10-25). `StormPreset.Website` keeps the website's
  seven for parity and the comparison (`web:<key>`); never edit it to change a game setup.
- Storms die leaving the map (`StormLife`, after `StormDesign.At` and `StormTornadoes.Apply`): no tornado may be off the
  map; check with `validation/parity/csharp -- --life` after changing tracks, placement or cycles.
- Look changes: one named problem at a time, checked in the test map; say when a request is taste, not a bug.
- Lighting (`lighting = Unified`, the default since 2026-10-28; user: "everything on the storm needs to respond the same
  way"): every medium takes its light from `stormLight` in StormMarch.compute and differs only in its phase function,
  albedo and scattering octaves. Never give a part of the storm its own light formula or multiplier; fix the shared one.
  What the storm shades besides itself (the scene's sun cookie, its ground light) takes the sun the same way
  (`sunReach` in StormCommon.hlsl, and the rain extinction); check with `StormLightCheck -slLightScene`.
  Check with `StormLightCheck` (physical beside unified, at 17 h and 18.8 h).
- Funnels are smooth (no vertical grooves), as in 2.0.
- Everything on a tornado turns by the angles in `S_spin` / `S_spin2` / `S_spin3`, each wrapped at 2 pi on its own: never
  blend two of them into one angle (a lerp between the core's and the whirl's wound the dust up and snapped it every turn,
  user 2026-10-30); rotate by one and blend what is sampled. Run `TornadoFlowCheck` after touching how anything turns.
- Tornado sizes and ratings by shape (user, 2026-10-29) live in `StormTornadoes` (`MinWidthM`, `MaxWidthM`, `MinEf`,
  `MaxEf`; wedges 750 yd-2 mi and EF3+, EF5 only wedges from 1000 yd or drillbits, ropes/trunks EF2 at most, cones and
  stovepipes EF4). Free storms are held to them in `Constrain`, games in `Rate` (which changes the shape when it must).
  Run `validation/parity/csharp -- --shapes` after touching tornado sizes, ratings or the game's tiers.
- `Runtime/Lightning` (core) is 2.0's code but for its namespace, the `ThunderOverride` hook (2026-10-26, for
  `StormAudio`'s thunder), the 2026-10-30 bolt geometry in `LightningModel` (reaching ground bolts with forked branches
  and second contacts, crawlers forking under and through the anvil; copy it back to 2.0 with its namespace) and the
  channels' own custom pass (`ChannelPass` at BeforePostProcess, shader pass `StormLightning`: with Refraction off HDRP
  draws transparents before the storm's BeforeTransparent pass, so the storm's transmittance went over the bolts on top
  of their own occlusion; no depth test, since the storm writes its cloud's depth; cloud and rain hide a bolt by its
  optical depth), the channel emission in storm units (600; the night scale goes into the
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
  cone 50 -> 57 ms). FXC's temp count does not predict it; time it.
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

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
- Game adaptations go beside the port, not into it (as `lighting = Physical`, `tornado = Detailed`, scene depth, the
  lightning hook, `TornadoWindAt`). `StormTornadoes` (2.0's tornado character) post-processes the frame and owns slots
  23-28; `StormDesign` fills 0-22 exactly as the website. `StormSpin` owns 29-30: the angles the mesocyclone and the
  detailed tornado have turned, added up per frame. Never turn a feature by `rate x clock` when its rate can change (the
  speed seen is then rate + clock x d(rate)/dt: stalls, lurches, spin reversing); integrate it in `StormSpin` instead
  (`validation/parity/csharp -- --spin` compares the two). New random draws get their own `StormRng` stream.
  `StormScenario` (2.0's game layout) uses streams 500 (tiers, lanes, starts), 600+i (storm seeds), 700+i (precip);
  its storms reach the website plan only through `PlanOverrides` (precip, forced tornadic), which parity must not see.
  Game changes to the plan itself (cycle shapes, timing) go in `StormGame.Adapt`, which every game plan passes through
  (`StormGame.Plan` in the renderer, `StormScenario`, the game-facing `validation/parity/csharp` checks) and parity does not.
  Re-run the website comparison (README) after shader changes; it uses the website's lighting and tornado.
- Setups: the game offers `StormPreset.All` (plains, lp = the website's hail environment renamed, dixie, ohio; the
  website's lp, capped and pulse removed at the user's request, 2026-10-25). `StormPreset.Website` keeps the website's
  seven for parity and the comparison (`web:<key>`); never edit it to change a game setup.
- Storms die leaving the map (`StormLife`, after `StormDesign.At` and `StormTornadoes.Apply`): no tornado may be off the
  map; check with `validation/parity/csharp -- --life` after changing tracks, placement or cycles.
- Look changes: one named problem at a time, checked in the test map; say when a request is taste, not a bug.
- Funnels are smooth (no vertical grooves), as in 2.0.
- `Runtime/Lightning` (core) is 2.0's unchanged code but for its namespace and the `ThunderOverride` hook (2026-10-26,
  for `StormAudio`'s thunder); fixes there should go back to 2.0 too.
- Sound (`Runtime/Audio`) is procedural; the synths in `Synth/` stay plain C# (no UnityEngine) so `validation/audio`
  can render and check them. Run it after changing a synth, and `StormAudioCheck` after changing what drives them.
- C: is nearly full (about 14 GB free on 2026-10-24); the test map's generated data is about 2 GB.

## Running

- Unity 6000.6.6f1, project `tests/StormLabTest`, headless with `-batchmode` (never `-nographics`).
- `StormLabTests.RunFromCommandLine [-slQuick] [-slCompare <cases.json> -slOut <dir>]`, `TestMapBuilder.BuildFromCommandLine`,
  `TestMapTests.RunFromCommandLine [-testmapShotsOnly]`.
- Play-mode checks on the test map: `StormPlayCheck`, `StormDayCheck`, `StormGameCheck [-slGameSeed n] [-slGamePreset key]`,
  `StormAudioCheck [-slAudioWiring]` (all `.RunFromCommandLine`); `StormSpawnCheck` runs without Play.
- Sound synths offline: `dotnet run --project validation/audio` (WAVs to `validation/audio/out/`).
- Pure C# checks with Unity's bundled dotnet: `validation/parity/csharp`.

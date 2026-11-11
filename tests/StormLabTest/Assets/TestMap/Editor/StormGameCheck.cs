// Play-mode check of game mode (StormScenario through StormScenarioRunner) on the test map: switches the map to a game,
// then checks each storm against the game's rules at its own times (and the pair at least 25 miles apart): towering
// cumulus off the map at its start, its updraft on the map's edge when it comes on, over the map when mature, lightning bound and active once mature, and the
// tornado wind summed over the storms. Renders a chaser's view of both storms at several game times.
// Screenshots/game_*.png, a contact sheet game_sheet.jpg, validation/reports/StormGameCheck.md, and the storms' tracks
// validation/reports/game_tracks.csv.
//   StormLab > Test Map > Game Check
// Batch: -executeMethod StormGameCheck.RunFromCommandLine [-slGameSeed <n>] [-slGamePreset <key>] (never with -nographics)
// -slGameProbe: with the 25 min view of each storm alone, the unified light's parts (game_25_storm<k>_dbg1..4.png) and the
// cloud, rain and light grids along the view (in the report)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using StormLab;
using StormLab.Lightning;
using StormLab.TestMap;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

[InitializeOnLoad]
public static class StormGameCheck
{
    const string Key = "StormGameCheck.Active", BatchKey = "StormGameCheck.Batch", CodeKey = "StormGameCheck.Code";
    const string SeedKey = "StormGameCheck.Seed", PresetKey = "StormGameCheck.Preset", SpotKey = "StormGameCheck.Spot";
    static string ShotDir => Path.Combine(StormLabTestKit.ProjectDir, "Screenshots");
    static string ReportDir => Path.Combine(StormLabTestKit.RepoRoot, "validation", "reports");

    static StormGameCheck() { if (SessionState.GetBool(Key, false)) EditorApplication.playModeStateChanged += OnState; }

    [MenuItem("StormLab/Test Map/Game Check")]
    public static void RunMenu() => Begin(false);
    public static void RunFromCommandLine() => Begin(true);

    static void Begin(bool batch)
    {
        var args = System.Environment.GetCommandLineArgs();
        string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        SessionState.SetInt(SeedKey, int.TryParse(Arg("-slGameSeed"), out var sd) ? sd : 1);
        SessionState.SetString(PresetKey, Arg("-slGamePreset") ?? "plains");
        // -slGameSpot "eastMi,northMi,gameMin,hour[,fromDeg,toDeg,live,km...]" (live 1: the game runs at real time into gameMin and
        // the views, from each of the heights in km that follow, are taken as Play draws them, without forced rebuilds or history resets): views all round from that spot on the map's grid (as the HUD gives it),
        // with both storms and each alone, instead of the checks
        SessionState.SetString(SpotKey, Arg("-slGameSpot") ?? "");
        EditorSceneManager.OpenScene(TestMapBuilder.ScenePath, OpenSceneMode.Single);
        SessionState.SetBool(Key, true); SessionState.SetBool(BatchKey, batch);
        EditorApplication.playModeStateChanged -= OnState;
        EditorApplication.playModeStateChanged += OnState;
        EditorApplication.EnterPlaymode();
    }

    static IEnumerator<object> routine; static readonly StringBuilder rep = new StringBuilder(); static bool ok; static int errors;
    static readonly List<string> errorLines = new List<string>();

    static void OnState(PlayModeStateChange s)
    {
        if (s == PlayModeStateChange.EnteredPlayMode)
        {
            rep.Clear(); ok = true; errors = 0; errorLines.Clear();
            Application.logMessageReceived += OnLog;
            routine = Run().GetEnumerator();
            EditorApplication.update += Tick;
        }
        else if (s == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key, false))
        {
            SessionState.SetBool(Key, false);
            EditorApplication.playModeStateChanged -= OnState;
            if (SessionState.GetBool(BatchKey, false)) EditorApplication.Exit(SessionState.GetInt(CodeKey, 1));
        }
    }

    static void OnLog(string msg, string stack, LogType t)
    {
        if (t != LogType.Error && t != LogType.Exception && t != LogType.Assert) return;
        if (stack != null && stack.Contains("UnityEditor.Search")) return;   // Unity's search indexer, not StormLab's
        errors++; if (errorLines.Count < 10) errorLines.Add($"{t}: {msg.Split('\n')[0]}");
    }

    static void Tick()
    {
        if (!EditorApplication.isPlaying) return;
        bool more;
        try { more = routine.MoveNext(); }
        catch (Exception e) { Debug.LogException(e); ok = false; more = false; }
        if (more) return;
        EditorApplication.update -= Tick;
        Application.logMessageReceived -= OnLog;
        rep.AppendLine().AppendLine($"Errors logged: {errors}");
        foreach (var l in errorLines) rep.AppendLine($"- {l}");
        ok &= errors == 0;
        rep.Insert(0, $"# Game check: {(ok ? "PASS" : "FAIL")}\n\nGenerated by `StormGameCheck` on {DateTime.Now:yyyy-MM-dd HH:mm}, {SystemInfo.graphicsDeviceName}.\n\n");
        Directory.CreateDirectory(ReportDir);
        File.WriteAllText(Path.Combine(ReportDir, "StormGameCheck.md"), rep.ToString());
        Debug.Log($"[StormGame] {(ok ? "PASS" : "FAIL")}");
        SessionState.SetInt(CodeKey, ok ? 0 : 1);
        EditorApplication.ExitPlaymode();
    }

    static IEnumerable<object> Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

    static void Check(bool pass, string what) { ok &= pass; rep.AppendLine($"- {(pass ? "PASS" : "FAIL")}: {what}"); }

    static IEnumerable<object> Run()
    {
        foreach (var x in Frames(30)) yield return x;
        var world = Object.FindAnyObjectByType<TestMapWorld>();
        var panel = world ? world.GetComponent<TestMapStormPanel>() : null;
        var cam = Camera.main;
        if (!world || !panel || !cam) { ok = false; rep.AppendLine($"FAIL: world {world}, panel {panel}, camera {cam}"); yield break; }
        var rig = cam.GetComponent<TestMapCamera>(); if (rig) rig.enabled = false;
        panel.hour = 17f; panel.clockRuns = false; panel.Apply();

        world.storm.preset = SessionState.GetString(PresetKey, "plains");
        world.SetGameMode(true);
        var game = world.game;
        game.preset = world.storm.preset; game.seed = SessionState.GetInt(SeedKey, 1); game.playing = false;
        game.Spawn();
        foreach (var x in Frames(3)) yield return x;
        var sc = game.Scenario;
        float half = world.sizeM / 2;
        var spot = SessionState.GetString(SpotKey, "");
        if (spot.Length > 0)
        {
            foreach (var x in Spot(world, panel, game, cam, spot)) yield return x;
            yield break;
        }
        float Out(Vector3 p) => Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.z)) - half;   // > 0 off the map
        rep.AppendLine($"Game: {sc.preset.key}, game seed {sc.seed}: {sc}. Map {world.sizeM / 1609.344f:0} miles square; life cycles {game.pace:0.#}x the game clock; afternoon light (17:00 solar).").AppendLine();
        rep.AppendLine("| Storm | Seed | Mode, lane | Starts | Start bearing | Off the map at start | On the map | Mature | Inside at maturity | Stage at start | Stage when mature | Tornadoes in the game |");
        rep.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        var inv = CultureInfo.InvariantCulture;
        var csv = new StringBuilder("storm,gameMin,lifeMin,x,z,stage\n");
        bool tracksOk = true, stagesOk = true, boundsOk = true;
        for (int i = 0; i < sc.storms.Count; i++)
        {
            var st = sc.storms[i]; var r = game.Storms[i];
            double LifeAt(double g) => (g - st.startMin) * game.pace;
            Vector3 S = r.UpdraftWorld(0), E = r.UpdraftWorld(LifeAt(st.entryMin)), M = r.UpdraftWorld(LifeAt(st.matureMin));
            string stage0 = StormDesign.At(r.Plan, 0, 0).stage, stageM = StormDesign.At(r.Plan, LifeAt(st.matureMin), 0).stage;
            bool startOff = Out(S) > 100f, onEdge = Mathf.Abs(Out(E)) < 200f, inside = Out(M) < 0f, startOn = Out(S) < -100f;
            tracksOk &= (st.onMap ? startOn : startOff && onEdge) && inside;
            stagesOk &= stage0 == "Towering cumulus";
            boundsOk &= Math.Abs(S.x - st.startX) < 50 && Math.Abs(S.z - st.startZ) < 50;
            var tors = st.InGame(game.gameLength, game.pace).Select(t => $"EF{t.ef} {StormTornadoes.ShapeName[(int)t.shape].ToLowerInvariant()} at {st.startMin + t.formMin / game.pace:0} min").ToList();
            rep.AppendLine($"| {i + 1} | {st.seed} | {st.precip}, {(st.northness > 0.5 ? "north" : "south")} | {st.startMin:0.0} min | {st.bearing:0} deg{(st.onMap ? " (over the map)" : "")}, toward {(Math.Atan2(r.Plan.motion.x, r.Plan.motion.y) * 180 / Math.PI + 360) % 360:0} deg | {Out(S) / 1000:0.0} km | {st.entryMin:0.0} min | {st.matureMin:0.0} min | {-Out(M) / 1000:0.0} km | {stage0} | {stageM} | {string.Join("; ", tors)} |");
            for (double g = st.startMin; g <= game.gameLength + 1e-6; g += 1)
            {
                double life = LifeAt(g); if (life > r.Plan.duration) break;
                var p = r.UpdraftWorld(life);
                csv.AppendLine(string.Join(",", (i + 1).ToString(inv), g.ToString("0", inv), life.ToString("0.0", inv), p.x.ToString("0", inv), p.z.ToString("0", inv), StormDesign.At(r.Plan, life, 0).stage));
            }
        }
        File.WriteAllText(Path.Combine(ReportDir, "game_tracks.csv"), csv.ToString());
        rep.AppendLine();
        Check(sc.storms.Count == 2 && game.Storms.Count == 2, "two storms, each its own StormRenderer");
        Check(stagesOk, "every storm starts as towering cumulus");
        Check(tracksOk, "every storm starts off the map with its updraft onto the map's edge at its entry time, or initiates well over the map; every one is over the map when mature");
        Check(boundsOk, "the drawn storms start where the scenario placed them");
        // how close the drawn storms' updrafts come while both are alive, every 6 s of game time
        double closest = double.PositiveInfinity;
        for (double g = 0; g <= game.gameLength + 1e-6; g += 0.1)
        {
            double l0 = (g - sc.storms[0].startMin) * game.pace, l1 = (g - sc.storms[1].startMin) * game.pace;
            if (l0 < 0 || l1 < 0 || l0 > game.Storms[0].Plan.duration || l1 > game.Storms[1].Plan.duration) continue;
            var d = game.Storms[0].UpdraftWorld(l0) - game.Storms[1].UpdraftWorld(l1); d.y = 0;
            closest = Math.Min(closest, d.magnitude);
        }
        Check(closest >= StormScenario.MinSeparationM - 1, $"the storms are never closer than {StormScenario.MinSeparationM / 1609.344:0} miles (closest {closest / 1609.344:0.0} mi; lanes {Math.Abs(sc.storms[0].laneM - sc.storms[1].laneM) / 1609.344:0.0} mi apart)");
        Check(sc.EveryStormTornadic, "every storm puts down a tornado during the game");
        Check(sc.MaxEf <= sc.topEf, $"no tornado above the game's top rating (strongest EF{sc.MaxEf}, built for EF{sc.topEf})");

        // the clock: before the second storm starts it is switched off; both live mid-game
        game.gameMinutes = (float)(sc.storms[1].startMin * 0.5); game.Apply();
        bool secondOff = sc.storms[1].startMin < 0.5 || !game.Storms[1].gameObject.activeSelf;
        game.gameMinutes = 30f; game.Apply();
        Check(secondOff && game.Storms.All(r => r.gameObject.activeSelf), "a storm is switched off before it starts; both are live at 30 game min");
        Check(game.Storms.All(r => Math.Abs(r.motionScale - 1.0 / game.pace) < 1e-9), $"each storm crosses the ground at its real speed (motion scale 1/{game.pace:0.#})");

        // lightning: each storm got its own, bound to its plan; flashing once mature, with the game running at 10x
        var lights = game.Storms.Select(r => r.GetComponent<StormLightning>()).ToList();
        Check(lights.All(l => l && l.Flashes && l.Bridge), "each storm has its own lightning (StormLightning, its flashes and its cloud light)");
        game.gameMinutes = (float)sc.storms.Max(s => s.matureMin) + 4f; game.minutesPerSecond = 10f / 60f; game.playing = true;
        foreach (var x in Frames(90)) yield return x;
        game.playing = false;
        var acts = lights.Select(l => l && l.Flashes.CurrentActivity.HasValue ? l.Flashes.CurrentActivity.Value.ToString() : "none").ToList();
        for (int i = 0; i < lights.Count; i++) rep.AppendLine($"  - storm {i + 1} lightning ({(lights[i] ? lights[i].BoundClock.ToString() : "-")} clock, at {game.StormMinutes(i):0.0} storm min): {acts[i]}");
        Check(lights.All(l => l && l.Storm == l.GetComponent<StormRenderer>() && l.Flashes.CurrentActivity.HasValue), "each storm's lightning runs on its own storm once mature");
        // one storm's channels seen through the other's rain and cloud (user, 2026-10-31: the other storm's anvil crawlers
        // showed through the rain and cloud overhead; each storm's lightning saw only its own volume): the transmittance
        // the channel shader gives from the ground under each storm (its forward-flank core, its hail core, and 3 km down
        // its motion from its updraft, under the main core) to where a crawler's legs hang under the other storm's anvil
        // (four tower radii downwind, just below the anvil's bottom), through the far storm alone, through the near storm
        // alone (its own lightning's occlusion) and through both as the far storm's channels are now hidden: the last must
        // be the product of the first two (the near storm's wall cloud and tail taken from the volume's coarse copy instead
        // of the march's analytic field, hence the tolerance), and from one of the three spots the near storm must hide
        // the far storm's crawler
        // the volumes as the storms stand now (the views below force the same before trusting a volume)
        foreach (var r in game.Storms) r.StepTo(r.StormTime, r.Clock, true);
        foreach (var x in Frames(4)) yield return x;
        bool hideOk = true;
        for (int i = 0; i < 2; i++)
        {
            var near = game.Storms[i]; var far = game.Storms[1 - i];
            var N = near.Frame.param; var F = far.Frame.param;
            float z = F[20] - 0.3f, R = Mathf.Max(F[9], 0.5f);   // S_anvil2.x the anvil's bottom, S_tower.y the tower's radius
            var ad = new Vector2(F[16], F[17]); ad = ad.sqrMagnitude > 0.01f ? ad.normalized : Vector2.right;   // S_anvil xy
            var c = new Vector2(F[64] + F[12] * (z - F[4]), F[65] + F[13] * (z - F[4])) + ad * (4f * R);   // S_up tilted up to z, out along the anvil
            Vector3 crawler = far.StormToWorld(new Vector3(c.x, c.y, z));
            var farBridge = lights[1 - i] ? lights[1 - i].Bridge : null; var nearBridge = lights[i] ? lights[i].Bridge : null;
            var up = new Vector2(N[64], N[65]);   // S_up
            var eyes = new (string name, Vector2 at)[] { ("forward-flank core", new Vector2(N[36], N[37])), ("hail core", new Vector2(N[40], N[41])), ("main core", up + new Vector2(N[0], N[1]) * 3f) };
            float best = 1f;
            foreach (var (name, at) in eyes)
            {
                Vector3 eye = near.StormToWorld(new Vector3(at.x, at.y, 0.002f));   // 2 m up
                float farOnly = farBridge ? farBridge.ProbeTransmittance(eye, crawler, false) : float.NaN;
                float nearOnly = nearBridge ? nearBridge.ProbeTransmittance(eye, crawler, false) : float.NaN;
                float withBoth = farBridge ? farBridge.ProbeTransmittance(eye, crawler, true) : float.NaN;
                float overhead = nearBridge ? nearBridge.ProbeTransmittance(eye, eye + Vector3.up * 12000f, false) : float.NaN;
                bool product = Mathf.Abs(withBoth - farOnly * nearOnly) < 0.05f;
                hideOk &= product; best = Mathf.Min(best, nearOnly);
                rep.AppendLine($"  - storm {2 - i}'s crawler ({Vector3.Distance(eye, crawler) / 1000f:0.0} km off, {z:0.0} km up under its anvil) from under storm {i + 1}'s {name} ({Vector2.Distance(at, up):0.0} km from its updraft, transmittance straight up {overhead:0.000}): transmittance {farOnly:0.000} through storm {2 - i} alone, {nearOnly:0.000} through storm {i + 1} alone, {withBoth:0.000} through both as drawn{(product ? "" : " (NOT their product)")}");
            }
            hideOk &= best < 0.2f;
        }
        Check(hideOk, "a storm's channels are hidden by the other storm's rain and cloud as by their own (transmittance through both storms = the product of each alone, within 0.05, from three spots under the storm overhead; from one of them it hides the other storm's crawler, under 0.2)");

        // tornado wind, summed over the storms: next to the first tornado on the ground
        bool windOk = false; string windNote = "no tornado found on the ground";
        for (int i = 0; i < sc.storms.Count && !windOk; i++)
            foreach (var t in sc.storms[i].InGame(game.gameLength, game.pace))
            {
                double life = (t.formMin + t.goneMin) / 2;
                game.gameMinutes = (float)(sc.storms[i].startMin + life / game.pace); game.Apply();
                var r = game.Storms[i];
                r.StepTo(r.StormTime, r.Clock, false);
                if (r.TornadoAmount < 0.15f) continue;
                var at = r.TornadoGroundWorld + Vector3.right * (float)Math.Max(t.widthM * 0.6, 60);
                var w = game.TornadoWindAt(at); var own = r.TornadoWindAt(at);
                windOk = w.magnitude > 10f && (w - own).magnitude < 0.5f + 0.05f * w.magnitude;
                windNote = $"storm {i + 1}'s EF{t.ef} at {game.gameMinutes:0.0} game min: {w.magnitude * 2.237f:0} mph summed, {own.magnitude * 2.237f:0} mph from its own storm, {t.widthM * 0.6:0} m off its centre";
                break;
            }
        Check(windOk, $"the tornado wind is summed over the storms ({windNote})");
        rep.AppendLine();

        // renders: a chaser on the inflow side, 35 km from the pair, behind and to the right of their motion (square to it,
        // the lanes would hide one another; down the motion, the camera sits in the forward-flank rain), kept on the map
        var mv3 = new Vector3((float)game.Storms[0].Plan.motion.x, 0, (float)game.Storms[0].Plan.motion.y).normalized;
        var viewFrom = (new Vector3(mv3.z, 0, -mv3.x) - mv3).normalized;
        var times = new[] { 6f, 15f, 25f, 35f, 45f, 55f };
        // -slGameAlone 15,35: the views at those minutes with each storm alone too (25 always), to tell which storm draws what
        var argvA = System.Environment.GetCommandLineArgs(); int ai = Array.IndexOf(argvA, "-slGameAlone");
        var aloneArg = ai >= 0 && ai + 1 < argvA.Length ? argvA[ai + 1] : null;
        var alone = new List<float> { 25f };
        if (!string.IsNullOrEmpty(aloneArg)) foreach (var a in aloneArg.Split(',')) if (float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var am) && !alone.Contains(am)) alone.Add(am);
        var poses = new Dictionary<float, (Vector3, Quaternion)>();
        foreach (var g in times)
        {
            game.gameMinutes = g; game.Apply();
            var live = game.Storms.Where((r, i) => game.Alive(i)).ToList();
            foreach (var r in live) r.StepTo(r.StormTime, r.Clock, true);
            var mid = live.Count > 0 ? live.Select(r => r.UpdraftWorld(r.StormTime)).Aggregate(Vector3.zero, (a, b) => a + b) / live.Count : Vector3.zero;
            mid.y = 0;
            var from = mid + viewFrom * 35000f;
            from.x = Mathf.Clamp(from.x, -half + 500f, half - 500f); from.z = Mathf.Clamp(from.z, -half + 500f, half - 500f);
            from.y = Physics.Raycast(from + Vector3.up * 3000f, Vector3.down, out var hit, 6000f) ? hit.point.y + 2f : 2f;
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(new Vector3(mid.x, from.y + 3200f, mid.z) - from, Vector3.up));
            if (alone.Contains(g)) poses[g] = (from, cam.transform.rotation);
            foreach (var r in live) r.ResetHistory(cam);
            foreach (var x in Frames(3)) yield return x;
            StormLabTestKit.Render(cam, Path.Combine(ShotDir, $"game_{g:00}.png"), 1600, 900, 6);
            if (alone.Contains(g))
            {
                var amb = StormSceneAmbient.Current;
                rep.AppendLine($"- game_{g:00}.png scene ambient: camera-wide {(amb ? amb.Ambient : 1f):0.00}, local {(amb && amb.LocalActive ? "on" : "off")}");
                // which storm draws what in the composite: each storm in turn drawn as its light grid's depths (lightDebug 1)
                for (int k = 0; k < live.Count; k++)
                {
                    live[k].lightDebug = 1; foreach (var r in live) r.ResetHistory(cam);
                    foreach (var x in Frames(2)) yield return x;
                    StormLabTestKit.Render(cam, Path.Combine(ShotDir, $"game_{g:00}_which{Enumerable.Range(0, game.Storms.Count).First(i => game.Storms[i] == live[k]) + 1}.png"), 800, 450, 6);
                    live[k].lightDebug = 0;
                }
                foreach (var r in live) r.ResetHistory(cam);
            }
            rep.AppendLine($"- game_{g:00}.png: game {g:0} min; " + string.Join("; ", Enumerable.Range(0, game.Storms.Count).Select(i =>
                game.Alive(i) ? $"storm {i + 1} {game.Storms[i].Frame.stage} ({game.StormMinutes(i):0} storm min), {Out(game.UpdraftWorld(i)) / -1000:0.0} km inside the map" : $"storm {i + 1} not on (life {game.StormMinutes(i):0} min)")));
        }
        // the mid-game view (and -slGameAlone's) with each storm alone, to tell which storm draws what
        foreach (var ga in alone)
        {
            if (!poses.TryGetValue(ga, out var pose)) continue;
            game.gameMinutes = ga; game.Apply();
            cam.transform.SetPositionAndRotation(pose.Item1, pose.Item2);
            for (int k = 0; k < game.Storms.Count; k++)
            {
                if (!game.Alive(k)) continue;
                // (the runner's Update shows every live storm each frame: paused while one is shown alone)
                game.enabled = false;
                for (int j = 0; j < game.Storms.Count; j++) game.Storms[j].gameObject.SetActive(j == k);
                game.Storms[k].StepTo(game.Storms[k].StormTime, game.Storms[k].Clock, true);
                game.Storms[k].ResetHistory(cam);
                foreach (var x in Frames(3)) yield return x;
                StormLabTestKit.Render(cam, Path.Combine(ShotDir, $"game_{ga:00}_storm{k + 1}.png"), 1600, 900, 6);
                if (ga != 25f)
                {
                    // where the storm's volume box stands in the frame: its vertical edges (corners) at 1 km up, screen x of 1600
                    var rk = game.Storms[k]; float Lm = StormVolume.L;
                    var xs = new List<string>();
                    foreach (var (cx, cy) in new[] { (-Lm, -Lm), (Lm, -Lm), (Lm, Lm), (-Lm, Lm) })
                    {
                        var w = rk.StormToWorld(new Vector3(cx, cy, 1f));
                        var sp = cam.WorldToViewportPoint(w);
                        xs.Add(sp.z > 0f ? $"({cx:+0;-0}, {cy:+0;-0}) x {sp.x * 1600f:0}" : $"({cx:+0;-0}, {cy:+0;-0}) behind");
                    }
                    var cS = rk.WorldToStorm(cam.transform.position);
                    rep.AppendLine($"- game_{ga:00}_storm{k + 1}.png: camera at ({cS.x:0.0}, {cS.y:0.0}) km in its frame; box corners: {string.Join(", ", xs)}");
                    // what the view crosses, column by column: rain and cloud optical depth along the rays at the frame's
                    // middle and three quarters up (the volume holds sqrt(density)), and a plan of the storm's rain
                    {
                        var vol = rk.Volume; int NV = vol.NV, NZ = vol.NZ;
                        var rq = UnityEngine.Rendering.AsyncGPUReadback.Request(vol.Dens, 0, 0, NV, 0, NV, 0, NZ, TextureFormat.RGBA32); rq.WaitForCompletion();
                        var lay = new byte[NZ][]; for (int z = 0; z < NZ; z++) lay[z] = rq.GetData<byte>(z).ToArray();
                        float D(int ch, Vector3 q)
                        {
                            int ix = Mathf.FloorToInt((q.x + Lm) / (2f * Lm) * NV), iy = Mathf.FloorToInt((q.y + Lm) / (2f * Lm) * NV), iz = Mathf.FloorToInt(q.z / StormVolume.ZT * NZ);
                            if (ix < 0 || iy < 0 || iz < 0 || ix >= NV || iy >= NV || iz >= NZ) return 0f;
                            float v = lay[iz][(iy * NV + ix) * 4 + ch] / 255f; return v * v;
                        }
                        float rMax = StormVolume.RainMax * vol.rainScale, zb = rk.Frame.param[4];
                        foreach (var vy in new[] { 0.5f, 0.75f })
                        {
                            var line = new StringBuilder($"  - rays {vy * 100:0}% up, screen x: rain tau / cloud tau / rain tau above base+2 km / opacity / mean distance km:");
                            for (int sx = 0; sx <= 1600; sx += 50)
                            {
                                var ray = cam.ViewportPointToRay(new Vector3(sx / 1600f, vy, 0f));
                                var o = rk.WorldToStorm(ray.origin); var d = StormRenderer.WorldToStormDir(ray.direction);
                                float tr = 0f, tc = 0f, tu = 0f, T = 1f, ds = 0f, ws = 0f;
                                for (float t = 0.1f; t < 120f; t += 0.2f)
                                {
                                    var q = o + d * t; if (q.z > StormVolume.ZT || q.z < 0f) break;
                                    float r = D(1, q) * rMax * 0.2f, c = D(0, q) * StormVolume.CloudMax * 0.2f; tr += r; tc += c; if (q.z > zb + 2f) tu += r;
                                    float a = 1f - Mathf.Exp(-(r + c)); ds += T * a * t; ws += T * a; T *= 1f - a;
                                }
                                line.Append($" {sx}: {tr:0.0}/{tc:0}/{tu:0.0}/{1f - T:0.00}/{(ws > 1e-3f ? ds / ws : -1f):0};");
                            }
                            rep.AppendLine(line.ToString());
                        }
                        // the plan: red the cloud below base + 6 km, green the rain at 1 km, blue the rain above base + 2 km;
                        // white the camera and its rays at screen x 0, 400, 800, 1200 and 1600
                        var img = new Texture2D(NV, NV, TextureFormat.RGB24, false); var px = new Color32[NV * NV];
                        for (int iy = 0; iy < NV; iy++) for (int ix = 0; ix < NV; ix++)
                        {
                            float cl = 0f, rl = 0f, ru = 0f;
                            for (int z = 0; z < NZ; z++)
                            {
                                float zz = (z + 0.5f) * StormVolume.ZT / NZ; int i = (iy * NV + ix) * 4;
                                float c = lay[z][i] / 255f, rr = lay[z][i + 1] / 255f;
                                if (zz < zb + 6f) cl += c * c * StormVolume.CloudMax * StormVolume.ZT / NZ;
                                if (Mathf.Abs(zz - 1f) < StormVolume.ZT / NZ) rl = Mathf.Max(rl, rr * rr);
                                if (zz > zb + 2f) ru = Mathf.Max(ru, rr * rr);
                            }
                            px[iy * NV + ix] = new Color(Mathf.Clamp01(cl / 30f), Mathf.Clamp01(rl * 1.5f), Mathf.Clamp01(ru * 1.5f));
                        }
                        foreach (var sx in new[] { 0, 400, 800, 1200, 1600 })
                        {
                            var ray = cam.ViewportPointToRay(new Vector3(sx / 1600f, 0.5f, 0f));
                            var o = rk.WorldToStorm(ray.origin); var d = StormRenderer.WorldToStormDir(ray.direction); d.z = 0f; d.Normalize();
                            for (float t = 0f; t < 120f; t += 0.1f)
                            {
                                var q = o + d * t; int ix = Mathf.FloorToInt((q.x + Lm) / (2f * Lm) * NV), iy = Mathf.FloorToInt((q.y + Lm) / (2f * Lm) * NV);
                                if (ix < 0 || iy < 0 || ix >= NV || iy >= NV) continue;
                                px[iy * NV + ix] = sx == 800 ? new Color32(255, 255, 0, 255) : new Color32(255, 255, 255, 255);
                            }
                        }
                        img.SetPixels32(px); img.Apply();
                        File.WriteAllBytes(Path.Combine(ShotDir, $"game_{ga:00}_storm{k + 1}_plan.png"), img.EncodeToPNG());
                        Object.DestroyImmediate(img);
                    }
                    rk.hideRain = true; rk.StepTo(rk.StormTime, rk.Clock, true); rk.ResetHistory(cam);
                    foreach (var x in Frames(3)) yield return x;
                    StormLabTestKit.Render(cam, Path.Combine(ShotDir, $"game_{ga:00}_storm{k + 1}_norain.png"), 1600, 900, 6);
                    rk.hideRain = false; rk.StepTo(rk.StormTime, rk.Clock, true);
                }
                // -slGameProbe: what the view crosses (StormLightCheck.Probe): cloud and rain, the sun's optical depth and
                // the depth overhead, 1 km up along the view
                if (ga == 25f && System.Environment.GetCommandLineArgs().Contains("-slGameProbe"))
                {
                    for (int dbg = 1; dbg <= 4; dbg++)
                    {
                        game.Storms[k].lightDebug = dbg; game.Storms[k].ResetHistory(cam);
                        StormLabTestKit.Render(cam, Path.Combine(ShotDir, $"game_25_storm{k + 1}_dbg{dbg}.png"), 800, 450, 6);
                    }
                    game.Storms[k].lightDebug = 0;
                    var r = game.Storms[k]; var vol = r.Volume;
                    var f = cam.transform.forward; f.y = 0f; f.Normalize();
                    var F = r.Frame.param; var u0 = r.WorldToStorm(r.UpdraftWorld(r.StormTime));
                    rep.AppendLine().AppendLine($"Storm {k + 1}: forward-flank core size {F[38]:0.00} amount {F[39]:0.00}; motion/forward ({F[0]:0.00}, {F[1]:0.00})");
                    rep.AppendLine().AppendLine($"Storm {k + 1} along the 25 min view (1 km up): km, cloud, rain, sun depth, depth overhead");
                    foreach (var km in new[] { 0f, 5f, 10f, 15f, 20f, 25f, 30f, 35f })
                    {
                        var p = r.WorldToStorm(cam.transform.position + f * km * 1000f); p.z = 1f;
                        var d = StormLightCheck.Probe(vol.Dens, vol.NV, vol.NZ, p.x, p.y, p.z, TextureFormat.RGBA32);
                        var l = StormLightCheck.Probe(vol.LightTex, vol.LN, vol.LZ, p.x, p.y, p.z);
                        // the cloud's optical depth in the whole column, and below 5 km over the base (the volume holds sqrt(cloud))
                        float L = StormVolume.L, dz = StormVolume.ZT / vol.NZ, zb = r.Frame.param[4];
                        int ix = Mathf.Clamp(Mathf.FloorToInt((p.x + L) / (2f * L) * vol.NV), 0, vol.NV - 1), iy = Mathf.Clamp(Mathf.FloorToInt((p.y + L) / (2f * L) * vol.NV), 0, vol.NV - 1);
                        var col = UnityEngine.Rendering.AsyncGPUReadback.Request(vol.Dens, 0, ix, 1, iy, 1, 0, vol.NZ, TextureFormat.RGBA32); col.WaitForCompletion();
                        float tAll = 0f, tLow = 0f, zLow = -1f;
                        for (int z = 0; z < vol.NZ; z++)
                        {
                            float c = col.GetData<byte>(z)[0] / 255f, t = c * c * StormVolume.CloudMax * dz;
                            tAll += t; if ((z + 0.5f) * dz < zb + 5f) tLow += t;
                            if (zLow < 0f && c * c > 0.1f) zLow = (z + 0.5f) * dz;
                        }
                        rep.AppendLine($"- {km:0} km: cloud {d.x:0.00}, rain {d.y:0.00}, sun depth {l.x:0.0}, overhead {-10f * Mathf.Log(Mathf.Max(l.y, 1e-12f)):0.0}; column cloud depth {tAll:0.0}, below base + 5 km {tLow:0.0}, lowest cloud {zLow:0.0} km (base {zb:0.0}); from the updraft {Vector2.Distance(new Vector2(p.x, p.y), new Vector2(u0.x, u0.y)):0.0} km, forward-flank core {Vector2.Distance(new Vector2(p.x, p.y), new Vector2(F[36], F[37])):0.0} km, hail core {Vector2.Distance(new Vector2(p.x, p.y), new Vector2(F[40], F[41])):0.0} km, surge {Vector2.Distance(new Vector2(p.x, p.y), new Vector2(F[72], F[73])):0.0} km (radius {F[74]:0.0})");
                    }
                }
            }
            game.enabled = true;
            game.Apply();
        }
        Sheet(times.Select(g => $"game_{g:00}.png").ToArray(), "game_sheet.jpg");
        rep.AppendLine().AppendLine("Images: `Screenshots/game_*.png`, contact sheet `Screenshots/game_sheet.jpg`; tracks `validation/reports/game_tracks.csv`.");
    }

    static IEnumerable<object> Spot(TestMapWorld world, TestMapStormPanel panel, StormScenarioRunner game, Camera cam, string spot)
    {
        var v = spot.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        float half = world.sizeM / 2, mile = 1609.344f;
        var at = new Vector3(v[0] * mile - half, 0, v[1] * mile - half);
        at.y = Physics.Raycast(at + Vector3.up * 3000f, Vector3.down, out var hit, 6000f) ? hit.point.y + 2f : 2f;
        panel.hour = v[3]; panel.Apply();
        game.gameMinutes = v[2]; game.Apply();
        rep.AppendLine($"Spot {v[0]:0.0} mi E, {v[1]:0.0} mi N (world {at.x / 1000:0.0}, {at.z / 1000:0.0} km), game {v[2]:0.0} min, {v[3]:0.0} h solar.").AppendLine();
        for (int i = 0; i < game.Storms.Count; i++)
        {
            var r = game.Storms[i]; if (!game.Alive(i)) continue;
            var u = r.UpdraftWorld(r.StormTime); var o = r.origin;
            var local = r.WorldToStorm(at);
            bool inBox = Mathf.Abs(local.x) < StormVolume.L && Mathf.Abs(local.y) < StormVolume.L;
            rep.AppendLine($"- storm {i + 1}: updraft ({u.x / 1000:0.0}, {u.z / 1000:0.0}) km, {Vector3.Distance(new Vector3(u.x, 0, u.z), new Vector3(at.x, 0, at.z)) / mile:0.0} mi off; the spot in its frame ({local.x:0.0}, {local.y:0.0}) km, {(inBox ? "inside" : "OUTSIDE")} its 180 km volume");
        }
        var names = new List<string>();
        if (v.Length > 6 && v[6] > 0.5)
        {
            // as a player sees it: the game running at real time, the volumes repainted in slabs, storm after storm
            game.gameMinutes = v[2] - 0.5f; game.minutesPerSecond = 1f / 60f; game.playing = true;
            foreach (var x in Frames(10)) yield return x;
            cam.transform.SetPositionAndRotation(at, Quaternion.Euler(-11f, v[4], 0f));
            float t0 = Time.realtimeSinceStartup;
            while (game.gameMinutes < v[2] && Time.realtimeSinceStartup - t0 < 120f) yield return null;
            var heights = v.Length > 7 ? v.Skip(7).ToArray() : new[] { 0f };
            // -slGameSpotPitch deg: looking up that far (negative: down); 5 up by default
            var argv = System.Environment.GetCommandLineArgs(); int pi = Array.IndexOf(argv, "-slGameSpotPitch");
            float pitchUp = pi >= 0 && pi + 1 < argv.Length ? float.Parse(argv[pi + 1], CultureInfo.InvariantCulture) : 5f;
            // -slGameSpot20: 2.0's tornado march (6 m steps, its lighting and reach), to compare with the game's
            if (argv.Contains("-slGameSpot20")) foreach (var r in game.Storms) { r.tornadoStep = 0f; r.tornadoLightAll = true; }
            // -slGameWallForms "block,cone,tilt,tiers;...": the same views with every wall cloud in each of these forms
            // in turn (StormStructure.WallFormOverride; "own": each storm's own), named ..._form<k>.png
            int wfi = Array.IndexOf(argv, "-slGameWallForms");
            var forms = wfi >= 0 && wfi + 1 < argv.Length ? argv[wfi + 1].Split(';') : new[] { "" };
            for (int fk = 0; fk < forms.Length; fk++)
            {
            if (forms[fk].StartsWith("L:"))
            {
                // a look instead of a form, "L:<stops brighter>:<aces|neutral|none>[:<toe strength>:<toe length>:<shoulder strength>]"
                // (the panel's exposure compensation and the scene's tonemapping; a custom curve when the three numbers follow)
                var lk = forms[fk].Split(':');
                panel.compensation = float.Parse(lk[1], CultureInfo.InvariantCulture);
                panel.grade = lk.Length > 2 && lk[2] == "grade"; if (panel.grade) panel.Apply();
                foreach (var vv in Object.FindObjectsByType<UnityEngine.Rendering.Volume>(FindObjectsSortMode.None))
                    if (vv.isGlobal && vv.sharedProfile && vv.sharedProfile.Has<UnityEngine.Rendering.HighDefinition.Tonemapping>() && vv.profile.TryGet(out UnityEngine.Rendering.HighDefinition.Tonemapping tm))
                    {
                        var mode = lk.Length > 2 ? lk[2] : "aces";
                        if (mode == "grade") break;
                        if (lk.Length > 5 && !lk[3].Contains("="))
                        {
                            tm.mode.Override(UnityEngine.Rendering.HighDefinition.TonemappingMode.Custom);
                            tm.toeStrength.Override(float.Parse(lk[3], CultureInfo.InvariantCulture)); tm.toeLength.Override(float.Parse(lk[4], CultureInfo.InvariantCulture));
                            tm.shoulderStrength.Override(float.Parse(lk[5], CultureInfo.InvariantCulture));
                        }
                        else tm.mode.Override(mode == "neutral" ? UnityEngine.Rendering.HighDefinition.TonemappingMode.Neutral : mode == "none" ? UnityEngine.Rendering.HighDefinition.TonemappingMode.None : UnityEngine.Rendering.HighDefinition.TonemappingMode.ACES);
                    }
                // (the camera in place first: the view meter reads the picture, and the exposure follows it over a few seconds)
                cam.transform.SetPositionAndRotation(at + Vector3.up * heights[0] * 1000f, Quaternion.Euler(-pitchUp, v[4], 0f));
                // further "key=value" fields: con (contrast, -100..100), sat (saturation), sh (the shadows' gain, 1 = none),
                // wb (white balance temperature, -100..100)
                foreach (var vv in Object.FindObjectsByType<UnityEngine.Rendering.Volume>(FindObjectsSortMode.None))
                    if (!panel.grade && vv.isGlobal && vv.sharedProfile && vv.sharedProfile.Has<UnityEngine.Rendering.HighDefinition.Tonemapping>())
                    {
                        T Get<T>() where T : UnityEngine.Rendering.VolumeComponent => vv.profile.TryGet(out T c) ? c : vv.profile.Add<T>(true);
                        float F(string key, float dflt) { foreach (var x in lk) if (x.StartsWith(key + "=")) return float.Parse(x.Substring(key.Length + 1), CultureInfo.InvariantCulture); return dflt; }
                        var ca = Get<UnityEngine.Rendering.HighDefinition.ColorAdjustments>();
                        ca.contrast.Override(F("con", 0f)); ca.saturation.Override(F("sat", 0f));
                        var smh = Get<UnityEngine.Rendering.HighDefinition.ShadowsMidtonesHighlights>();
                        float shg = F("sh", 1f); smh.shadows.Override(new Vector4(1f, 1f, 1f, shg - 1f)); smh.shadowsStart.Override(0f); smh.shadowsEnd.Override(F("she", 0.3f));
                        var wb = Get<UnityEngine.Rendering.HighDefinition.WhiteBalance>(); wb.temperature.Override(F("wb", 0f));
                    }
                float tl = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - tl < 6f) { StormLabTestKit.Pump(cam); yield return null; }
                rep.AppendLine($"- form {fk + 1} ({forms[fk]}): EV100 {panel.ExposureEV:0.00}, storm lift {panel.StormLift:0.00}, shade {panel.StormShade:0.000}, view trim {panel.ViewTrim:0.00} (picture log-average {StormViewMeter.LogAverage:0.000}, bright {StormViewMeter.Bright:0.00})");
            }
            else if (forms[fk].Length > 0)
            {
                if (forms[fk] == "own") StormStructure.WallFormOverride = null;
                else { var w = forms[fk].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray(); StormStructure.WallFormOverride = (w[0], w[1], w[2], w[3]); }
                // (the game runs on: the storms take the form with their next frame, and their volumes with the next repaint)
                float tf = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - tf < 3f) yield return null;
                foreach (var r in game.Storms) if (r && r.gameObject.activeInHierarchy)
                    rep.AppendLine($"- form {fk + 1} ({forms[fk]}): storm wall cloud form in the frame {r.Frame.param[StormStructure.FormSlot * 4]:0.00}, {r.Frame.param[StormStructure.FormSlot * 4 + 1]:0.00}, {r.Frame.param[StormStructure.FormSlot * 4 + 2]:0.00}, {r.Frame.param[StormStructure.FormSlot * 4 + 3]:0.00}; radius {r.Frame.param[6 * 4 + 2]:0.00} km, lowering {r.Frame.param[7 * 4]:0.00} km");
            }
            string formTag = forms[fk].Length > 0 ? $"_form{fk + 1}" : "";
            foreach (var up in heights)
            for (int h = (int)v[4]; h <= (int)v[5]; h += 30)
            {
                cam.transform.SetPositionAndRotation(at + Vector3.up * up * 1000f, Quaternion.Euler(-pitchUp, h, 0f));
                foreach (var x in Frames(20)) yield return x;
                var name = $"spot_live_{up:00}km_{h:000}{formTag}.png";
                var rt = new RenderTexture(1600, 900, 24);
                var keepT = cam.targetTexture; cam.targetTexture = rt;
                cam.Render(); cam.Render();
                RenderTexture.active = rt; var tex = new Texture2D(1600, 900, TextureFormat.RGB24, false); tex.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); tex.Apply();
                RenderTexture.active = null; cam.targetTexture = keepT;
                File.WriteAllBytes(Path.Combine(ShotDir, name), tex.EncodeToPNG());
                Object.DestroyImmediate(tex); rt.Release(); Object.DestroyImmediate(rt);
                names.Add(name);
                // -slGameProbe: the unified light's parts alone in the same view (StormRenderer.lightDebug)
                if (argv.Contains("-slGameProbe"))
                    for (int dbg = 1; dbg <= 4; dbg++)
                    {
                        foreach (var r in game.Storms) { r.lightDebug = dbg; r.ResetHistory(cam); }
                        StormLabTestKit.Render(cam, Path.Combine(ShotDir, name.Replace(".png", $"_dbg{dbg}.png")), 1600, 900, 8);
                    }
                foreach (var r in game.Storms) r.lightDebug = 0;
                // -slGameSpotFrames count,seconds: more frames of the same view as Play draws them, that many seconds apart
                // (the game running at real time), for motion: Screenshots/<name>_f<k>.png
                int fi = Array.IndexOf(argv, "-slGameSpotFrames");
                if (fi >= 0 && fi + 1 < argv.Length)
                {
                    var fs = argv[fi + 1].Split(',');
                    int count = int.Parse(fs[0]); float gap = float.Parse(fs[1], CultureInfo.InvariantCulture);
                    for (int k = 1; k <= count; k++)
                    {
                        float tw = Time.realtimeSinceStartup;
                        while (Time.realtimeSinceStartup - tw < gap) yield return null;
                        var rtk = new RenderTexture(1600, 900, 24);
                        cam.targetTexture = rtk; cam.Render(); cam.Render();
                        RenderTexture.active = rtk; var tk = new Texture2D(1600, 900, TextureFormat.RGB24, false); tk.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); tk.Apply();
                        RenderTexture.active = null; cam.targetTexture = keepT;
                        File.WriteAllBytes(Path.Combine(ShotDir, name.Replace(".png", $"_f{k}.png")), tk.EncodeToPNG());
                        rep.AppendLine($"- frame {k}: inflow flow " + string.Join(", ", game.Storms.Select(r => $"{r.Frame.param[StormSpin.Slot0 * 4 + 11]:0.000} km (clock {r.Clock:0.0} s, rates {r.spinRates})")));
                        Object.DestroyImmediate(tk); rtk.Release(); Object.DestroyImmediate(rtk);
                    }
                }
            }
            }
            StormStructure.WallFormOverride = null;
            game.playing = false;
            rep.AppendLine().AppendLine($"Live views at game {game.gameMinutes:0.0} min: `Screenshots/spot_live_*.png`.");
            yield break;
        }
        game.enabled = false;   // its Update would switch every live storm back on
        for (int k = -1; k < game.Storms.Count; k++)
        {
            for (int j = 0; j < game.Storms.Count; j++) game.Storms[j].gameObject.SetActive(game.Alive(j) && (k < 0 || j == k));
            foreach (var r in game.Storms.Where(r => r.gameObject.activeSelf)) r.StepTo(r.StormTime, r.Clock, true);
            for (int h = (int)(v.Length > 4 ? v[4] : 150); h <= (int)(v.Length > 5 ? v[5] : 300); h += 30)
            for (int pitch = 5; pitch <= 25; pitch += 20)
            {
                cam.transform.SetPositionAndRotation(at, Quaternion.Euler(-pitch, h, 0f));
                foreach (var r in game.Storms.Where(r => r.gameObject.activeSelf)) r.ResetHistory(cam);
                foreach (var x in Frames(2)) yield return x;
                string name = $"spot_{(k < 0 ? "both" : $"storm{k + 1}")}_{h:000}_{pitch:00}.png";
                StormLabTestKit.Render(cam, Path.Combine(ShotDir, name), 960, 540, 6);
                names.Add(name);
            }
        }
        game.enabled = true; game.Apply();
        rep.AppendLine().AppendLine("Images: `Screenshots/spot_*.png` (both storms, then each alone; headings 150-300 deg, 5 and 25 deg up).");
    }

    static void Sheet(string[] names, string outName)
    {
        int cw = 800, ch = 450, cols = 2, rows = (names.Length + 1) / 2;
        var sheet = new Texture2D(cw * cols, ch * rows, TextureFormat.RGB24, false);
        for (int i = 0; i < names.Length; i++)
        {
            var tex = new Texture2D(2, 2); tex.LoadImage(File.ReadAllBytes(Path.Combine(ShotDir, names[i])));
            var o = new Color[cw * ch];
            for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++) o[y * cw + x] = tex.GetPixelBilinear((x + 0.5f) / cw, (y + 0.5f) / ch);
            sheet.SetPixels(i % cols * cw, (rows - 1 - i / cols) * ch, cw, ch, o);
            Object.DestroyImmediate(tex);
        }
        sheet.Apply();
        File.WriteAllBytes(Path.Combine(ShotDir, outName), sheet.EncodeToJPG(88));
        Object.DestroyImmediate(sheet);
    }
}

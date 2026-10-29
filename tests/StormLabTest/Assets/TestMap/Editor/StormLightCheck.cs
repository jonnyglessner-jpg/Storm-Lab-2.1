// Play-mode check of the storm's lighting models (StormRenderer.lighting) on the plains test map: fixed views of one storm,
// paused, each drawn with the physical lighting and with the unified lighting, in the game's look. The views are the ones
// the unified lighting was made for (user, 2026-10-28): the whole storm, the base and its rise into the updraft, under the
// base, inside the forward-flank rain, the storm against the sun, and its hail core. For each view: the mean luminance of
// the frame's upper half (the sky and the storm) in each model, and a sheet with both frames above and a 1:1 crop of the
// centre of each below; and the scale StormSceneAmbient puts on the scene's ambient light there (both models).
// Screenshots/light_<preset>_*.png, validation/reports/StormLightCheck[_<preset>].md.
//   StormLab > Test Map > Lighting Check
// Batch: -executeMethod StormLightCheck.RunFromCommandLine [-slLightPreset <key>] [-slLightHour <solar hour>] [-slLightDebug]
// (never with -nographics). -slLightDebug adds the unified light's parts alone for each view (light_*_dbg1..4.png)
// -slLightScene: what the storm does to the scene round it (SceneLight): its shadow on the ground, the scene's ambient
// light by place, and town light on the base at night.
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
public static class StormLightCheck
{
    const string Key = "StormLightCheck.Active", BatchKey = "StormLightCheck.Batch", CodeKey = "StormLightCheck.Code";
    const string PresetKey = "StormLightCheck.Preset", HourKey = "StormLightCheck.Hour", DebugKey = "StormLightCheck.Debug", FlickerKey = "StormLightCheck.Flicker", TailKey = "StormLightCheck.Tail", SceneKey = "StormLightCheck.Scene";
    static string ShotDir => Path.Combine(StormLabTestKit.ProjectDir, "Screenshots");
    static string ReportDir => Path.Combine(StormLabTestKit.RepoRoot, "validation", "reports");

    static StormLightCheck() { if (SessionState.GetBool(Key, false)) EditorApplication.playModeStateChanged += OnState; }

    [MenuItem("StormLab/Test Map/Lighting Check")]
    public static void RunMenu() => Begin(false);
    public static void RunFromCommandLine() => Begin(true);

    static void Begin(bool batch)
    {
        var args = System.Environment.GetCommandLineArgs();
        string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        SessionState.SetString(PresetKey, Arg("-slLightPreset") ?? "");
        SessionState.SetFloat(HourKey, float.TryParse(Arg("-slLightHour"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : -1f);
        SessionState.SetBool(DebugKey, args.Contains("-slLightDebug"));
        SessionState.SetBool(FlickerKey, args.Contains("-slLightFlicker"));
        SessionState.SetBool(TailKey, args.Contains("-slLightTailFlow"));
        SessionState.SetBool(SceneKey, args.Contains("-slLightScene"));
        EditorSceneManager.OpenScene(TestMapBuilder.ScenePath, OpenSceneMode.Single);
        SessionState.SetBool(Key, true); SessionState.SetBool(BatchKey, batch);
        EditorApplication.playModeStateChanged -= OnState;
        EditorApplication.playModeStateChanged += OnState;
        EditorApplication.EnterPlaymode();
    }

    static void OnState(PlayModeStateChange s)
    {
        if (s == PlayModeStateChange.EnteredPlayMode)
        {
            errors = 0; errorLines.Clear(); rep.Clear(); ok = true;
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

    static int errors; static readonly List<string> errorLines = new List<string>();
    static void OnLog(string msg, string stack, LogType t)
    {
        if (t != LogType.Error && t != LogType.Exception && t != LogType.Assert) return;
        if (stack != null && stack.Contains("UnityEditor.Search")) return;   // Unity's search indexer at start-up, not StormLab's
        errors++; if (errorLines.Count < 12) errorLines.Add($"{t}: {msg.Split('\n')[0]}");
    }

    static readonly StringBuilder rep = new StringBuilder();
    static bool ok; static IEnumerator<object> routine; static string reportName = "StormLightCheck";

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
        rep.Insert(0, $"# Lighting check: {(ok ? "PASS" : "FAIL")}\n\nGenerated by `StormLightCheck` on {DateTime.Now:yyyy-MM-dd HH:mm}, {SystemInfo.graphicsDeviceName}.\n\n");
        Directory.CreateDirectory(ReportDir);
        File.WriteAllText(Path.Combine(ReportDir, reportName + ".md"), rep.ToString());
        Debug.Log($"[StormLight] {(ok ? "PASS" : "FAIL")}");
        SessionState.SetInt(CodeKey, ok ? 0 : 1);
        EditorApplication.ExitPlaymode();
    }

    static void Check(bool pass, string what) { ok &= pass; rep.AppendLine($"- {(pass ? "PASS" : "FAIL")}: {what}"); }
    static IEnumerable<object> Wait(float seconds) { float t0 = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - t0 < seconds) yield return null; }

    // where the camera stands: from the updraft (or the forward-flank core, or the hail core), km away, at a bearing from
    // the sun's (0: the sun behind the camera, 180: looking into it); aimKm: the height looked at over the target
    enum Target { Updraft, Rain, Hail }
    struct View { public string name, what; public Target target; public float km, sideDeg, aimKm, fov; }

    static IEnumerable<object> Run()
    {
        foreach (var x in Wait(2f)) yield return x;
        var world = Object.FindAnyObjectByType<TestMapWorld>();
        var storm = world ? world.storm : null;
        var panel = Object.FindAnyObjectByType<TestMapStormPanel>();
        var sl = storm ? storm.GetComponent<StormLightning>() : null;
        var cam = Camera.main;
        if (!world || !storm || !cam) { Check(false, $"scene objects: world {world}, storm {storm}, camera {cam}"); yield break; }
        var rig = cam.GetComponent<TestMapCamera>(); if (rig) rig.enabled = false;
        if (sl) sl.Flashes.enabled = false;
        storm.playing = false; storm.renderScale = 0.5f; storm.checkerboard = false;
        string preset = SessionState.GetString(PresetKey, "");
        if (preset.Length > 0 && preset != storm.preset) { storm.preset = preset; storm.Replan(); world.PlaceStorm(); }
        if (preset.Length > 0) reportName += "_" + preset;
        bool debug = SessionState.GetBool(DebugKey, false);
        float hour = SessionState.GetFloat(HourKey, -1f);
        if (hour >= 0f && panel) { panel.hour = hour; panel.Apply(); }

        var modes = new[] { StormLighting.Physical, StormLighting.Unified };
        var P = storm.Plan;
        float form = P.cycles != null ? (float)P.cycles[0].times[2] : 48f;
        float at = form + 4f;
        var views = new[]
        {
            new View { name = "far_25km", what = "the whole storm from 25 km, lit from the side", target = Target.Updraft, km = 25f, sideDeg = 70f, aimKm = 5f, fov = 70f },
            new View { name = "base_10km", what = "the base and its rise into the updraft from 10 km", target = Target.Updraft, km = 10f, sideDeg = 60f, aimKm = 1.5f, fov = 70f },
            new View { name = "under_3km", what = "under the base's edge, 3 km from the updraft", target = Target.Updraft, km = 3f, sideDeg = 90f, aimKm = 1.6f, fov = 75f },
            new View { name = "in_rain", what = "inside the forward-flank rain, looking toward the updraft", target = Target.Rain, km = 0f, sideDeg = 0f, aimKm = 0.6f, fov = 75f },
            new View { name = "backlit_15km", what = "the storm against the sun from 15 km", target = Target.Updraft, km = 15f, sideDeg = 165f, aimKm = 2f, fov = 70f },
            new View { name = "ffd_12km", what = "the forward flank side-on from 12 km, on the sun's side", target = Target.Rain, km = 12f, sideDeg = 0f, aimKm = 2.5f, fov = 75f },
            new View { name = "ffd_25km", what = "the forward flank side-on from 25 km, on the sun's side", target = Target.Rain, km = 25f, sideDeg = 0f, aimKm = 3f, fov = 60f },
            new View { name = "ffd_shade_20km", what = "the forward flank side-on from 20 km, away from the sun", target = Target.Rain, km = 20f, sideDeg = -1f, aimKm = 3f, fov = 60f },
            new View { name = "hail_8km", what = "the hail core from 8 km, from the side away from the forward-flank rain", target = Target.Hail, km = 8f, sideDeg = 0f, aimKm = 1.5f, fov = 60f },
        };
        storm.stormMinutes = at; storm.StepTo(at, storm.Clock, true);
        foreach (var x in Wait(0.5f)) yield return x;
        var F = storm.Frame.param;
        if (SessionState.GetBool(TailKey, false)) { reportName += "_tail"; foreach (var x in TailFlow(storm, cam)) yield return x; yield break; }
        if (SessionState.GetBool(FlickerKey, false)) { reportName += "_flicker"; foreach (var x in Flicker(storm)) yield return x; yield break; }
        if (SessionState.GetBool(SceneKey, false)) { reportName += "_scene"; foreach (var x in SceneLight(storm, cam, panel)) yield return x; yield break; }
        rep.AppendLine($"Storm: {storm.preset}, seed {storm.seed}, {P.kind}, at {at:0.0} min ({storm.Frame.stage}); sun at {storm.solarHour:0.0} h solar. Hail core amount {F[10 * 4 + 3]:0.00}, radius {F[10 * 4 + 2]:0.0} km. March at half resolution.").AppendLine();
        rep.AppendLine($"Unified settings: diffusion {storm.diffusion:0.###}, diffused sun {storm.diffuseSun:0.##}, hail glow {storm.hailGlow:0.##}.").AppendLine();
        if (debug)
        {
            // the light grid (r the sun's optical depth, g the sky's transmittance overhead, b the overhead openness, a the
            // ice shade) and the sky visibility (cap, ring, horizon, horizon toward the sun) at points through the storm
            rep.AppendLine("| point | km up | sun depth | depth overhead | sky cap | ring | horizon | horizon sunward |").AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            var u0 = storm.WorldToStorm(storm.UpdraftWorld(storm.StormTime));
            var pts = new (string, float, float)[] { ("updraft", u0.x, u0.y), ("forward-flank core", F[36], F[37]), ("hail core", F[40], F[41]) };
            foreach (var (n, px, py) in pts)
                foreach (var z in new[] { 0.3f, 1.0f, 2.0f, 4.0f, 8.0f })
                {
                    var l = Probe(storm.Volume.LightTex, storm.Volume.LN, storm.Volume.LZ, px, py, z);
                    var sv = Probe(storm.Volume.SkyVisTex, storm.Volume.SN, storm.Volume.SZ, px, py, z);
                    rep.AppendLine($"| {n} | {z:0.0} | {l.x:0.0} | {-10f * Mathf.Log(Mathf.Max(l.y, 1e-12f)):0.0} | {sv.x:0.00} | {sv.y:0.00} | {sv.z:0.00} | {sv.w:0.00} |");
                }
            rep.AppendLine();
        }
        rep.AppendLine("| view | what | upper-half luminance, physical | unified | unified / physical | scene ambient | march 1080p, physical | unified |").AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");

        double SyncMs() { var sw = System.Diagnostics.Stopwatch.StartNew(); StormLabTestKit.Sync(); return sw.Elapsed.TotalMilliseconds; }
        // the march's GPU time at 1080p (the game's half-resolution march), as CloudDetailCheck times it
        double Timed(int reps)
        {
            using (var warm = new UnityEngine.Rendering.CommandBuffer()) { storm.RecordMarch(warm, cam, null, 1920, 1080, false, out _); Graphics.ExecuteCommandBuffer(warm); }
            StormLabTestKit.Sync();
            double idle = Enumerable.Range(0, 5).Select(_ => SyncMs()).Min();
            using (var cmd = new UnityEngine.Rendering.CommandBuffer { name = "StormLightCheck timing" })
            {
                for (int i = 0; i < reps; i++) storm.RecordMarch(cmd, cam, null, 1920, 1080, false, out _);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Graphics.ExecuteCommandBuffer(cmd); StormLabTestKit.Sync();
                return Math.Max(0, sw.Elapsed.TotalMilliseconds - idle) / reps;
            }
        }
        var ms = new Dictionary<string, double>();
        var lum = new Dictionary<string, float>();
        var ambient = new Dictionary<string, float>();
        foreach (var v in views)
        {
            var u = storm.UpdraftWorld(storm.StormTime);
            Vector3 tgt = v.target == Target.Rain ? storm.StormToWorld(new Vector3(F[9 * 4], F[9 * 4 + 1], 0f))
                        : v.target == Target.Hail ? storm.StormToWorld(new Vector3(F[10 * 4], F[10 * 4 + 1], 0f)) : u;
            tgt.y = 0f;
            var k = storm.Lighting.keyDir;
            var toSun = new Vector3(k.x, 0f, k.y).normalized;
            Vector3 from;
            if (v.target == Target.Rain && v.km > 0f)
            {
                // the forward flank side-on: square to the line from the updraft to its core, on the sun's side (sideDeg
                // under 0: the side away from the sun)
                var along = tgt - new Vector3(u.x, 0f, u.z); along.y = 0f;
                var side = Vector3.Cross(Vector3.up, along.normalized);
                if ((Vector3.Dot(side, toSun) < 0f) != (v.sideDeg < 0f)) side = -side;
                from = tgt + side * v.km * 1000f;
            }
            else if (v.target == Target.Rain) from = tgt;
            else if (v.target == Target.Hail)
            {
                // from the side of the core away from the forward-flank rain
                var ffd = storm.StormToWorld(new Vector3(F[9 * 4], F[9 * 4 + 1], 0f)); ffd.y = 0f;
                var away = tgt - ffd; away.y = 0f;
                from = tgt + (away.sqrMagnitude > 1f ? away.normalized : -toSun) * v.km * 1000f;
            }
            else
            {
                // on whichever side of the sun's bearing is further from the forward-flank rain (the side a chaser stands)
                var ffd = storm.StormToWorld(new Vector3(F[9 * 4], F[9 * 4 + 1], 0f)); ffd.y = 0f;
                var fa = tgt + Quaternion.Euler(0, v.sideDeg, 0) * toSun * v.km * 1000f;
                var fb = tgt + Quaternion.Euler(0, -v.sideDeg, 0) * toSun * v.km * 1000f;
                from = (fa - ffd).sqrMagnitude >= (fb - ffd).sqrMagnitude ? fa : fb;
            }
            from.y = Physics.Raycast(from + Vector3.up * 3000f, Vector3.down, out var hit, 6000f) ? hit.point.y + 2f : 2f;
            // inside the rain: look at the updraft from the core
            var aimAt = v.target == Target.Rain && v.km <= 0f ? new Vector3(u.x, from.y + v.aimKm * 1000f, u.z) : new Vector3(tgt.x, from.y + v.aimKm * 1000f, tgt.z);
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(aimAt - from, Vector3.up));
            cam.fieldOfView = v.fov;
            // a first render at a new spot streams in the map's trees and textures; draw one and throw it away
            StormLabTestKit.Render(cam, Path.Combine(ShotDir, "light_warmup.png"), 1920, 1080, 12);
            // the scene's ambient light settles to what the storm hides of the sky here (StormSceneAmbient)
            foreach (var x in Wait(4f)) yield return x;
            var amb = StormSceneAmbient.Current;
            ambient[v.name] = amb ? amb.Ambient : 1f;
            var paths = new List<string>();
            foreach (var m in modes)
            {
                storm.lighting = m;
                storm.StepTo(storm.StormTime, storm.Clock);   // (a model's own light grids: unified rain shades more)
                storm.ResetHistory(cam);
                string path = Path.Combine(ShotDir, $"light_{storm.preset}_{v.name}_{m}.png");
                StormLabTestKit.Render(cam, path, 1920, 1080, 8);
                paths.Add(path);
                lum[v.name + m] = UpperLuminance(path);
                ms[v.name + m] = Timed(20);
                yield return null;
            }
            // -slLightDebug: the unified light's parts alone (StormRenderer.lightDebug)
            if (debug)
                for (int d = 1; d <= 4; d++)
                {
                    storm.lightDebug = d; storm.ResetHistory(cam);
                    StormLabTestKit.Render(cam, Path.Combine(ShotDir, $"light_{storm.preset}_{v.name}_dbg{d}.png"), 960, 540, 8);
                    storm.lightDebug = 0;
                    yield return null;
                }
            // the forward flank's views again without rain: the cloud deck alone (StormRenderer.hideRain)
            if (v.name.StartsWith("ffd_") && v.km >= 20f)
            {
                storm.hideRain = true; storm.StepTo(storm.StormTime, storm.Clock, true); storm.ResetHistory(cam);
                StormLabTestKit.Render(cam, Path.Combine(ShotDir, $"light_{storm.preset}_{v.name}_norain.png"), 1920, 1080, 8);
                storm.hideRain = false; storm.StepTo(storm.StormTime, storm.Clock, true);
                yield return null;
            }
            float a = lum[v.name + modes[0]], b = lum[v.name + modes[1]];
            rep.AppendLine($"| {v.name} | {v.what} | {a:0.000} | {b:0.000} | {b / Mathf.Max(a, 1e-4f):0.00} | {ambient[v.name]:0.00} | {ms[v.name + modes[0]]:0.00} ms | {ms[v.name + modes[1]]:0.00} ms |");
            Sheet(paths[0], paths[1], Path.Combine(ShotDir, $"light_{storm.preset}_{v.name}_sheet.png"));
        }
        storm.lighting = StormLighting.Unified;
        rep.AppendLine().AppendLine($"Sheets: `Screenshots/light_{storm.preset}_<view>_sheet.png` (physical left, unified right; full frames above, 1:1 centre crops below).");
        // (not the views of a face the sun lights: the unified cloud takes its full sun there, where the physical near field
        // took 1.4 / 1.9 of it, and a sunlit face is bright in any model)
        Check(views.Where(v => v.name != "backlit_15km" && v.name != "ffd_12km" && v.name != "ffd_25km").All(v => lum[v.name + "Unified"] <= lum[v.name + "Physical"] * 1.02f),
            "the unified lighting is no brighter than the physical in any view of the storm's shaded side (storms darken the sky)");
        double worst = views.Max(v => ms[v.name + "Unified"] - ms[v.name + "Physical"]);
        Check(worst < 0.5, $"the unified lighting costs under 0.5 ms more march at 1080p in every view (worst {worst:+0.00;-0.00} ms)");
    }

    /// <summary>
    /// -slLightScene (user, 2026-10-28: fix the ground shadow, ambient by place and town light). Under the storm: the sun's
    /// share reaching the ground (StormSceneShadow's cookie) under the updraft, the forward-flank core and the hail core,
    /// physical against unified (the unified shadow takes the storm's rain extinction and sun reach); the ambient map
    /// (StormSceneAmbient, local) under the camera, under those cores and on open ground, with the camera under the base;
    /// the storm from open ground 15 km off, ambient from the camera and by place; and on a moonless night the base from
    /// 4 km with no towns given, the map's towns, and a town of 15,000 put under the base.
    /// Screenshots/light_scene_*.png
    /// </summary>
    static IEnumerable<object> SceneLight(StormRenderer storm, Camera cam, TestMapStormPanel panel)
    {
        var F = storm.Frame.param;
        var u0 = storm.WorldToStorm(storm.UpdraftWorld(storm.StormTime));
        var k = storm.Lighting.keyDir; var toSun = new Vector3(k.x, 0f, k.y).normalized;
        var pts = new (string, Vector2)[] { ("updraft", new Vector2(u0.x, u0.y)), ("forward-flank core", new Vector2(F[36], F[37])), ("hail core", new Vector2(F[40], F[41])),
                                             ("open ground 30 km sunward", new Vector2(u0.x, u0.y) + new Vector2(toSun.x, toSun.z) * 30f) };
        Vector3 Ground(Vector2 p) { var w = storm.StormToWorld(new Vector3(p.x, p.y, 0f)); w.y = Physics.Raycast(w + Vector3.up * 3000f, Vector3.down, out var h, 6000f) ? h.point.y : 0f; return w; }
        float Read(RenderTexture t, float fx, float fy, int ch)
        {
            if (!t || fx < 0f || fx > 1f || fy < 0f || fy > 1f) return float.NaN;
            int ix = Mathf.Clamp((int)(fx * t.width), 0, t.width - 1), iy = Mathf.Clamp((int)(fy * t.height), 0, t.height - 1);
            var req = UnityEngine.Rendering.AsyncGPUReadback.Request(t, 0, ix, 1, iy, 1, 0, 1, TextureFormat.RGBAHalf); req.WaitForCompletion();
            return req.hasError ? float.NaN : Mathf.HalfToFloat(req.GetData<ushort>()[ch]);
        }
        var sun = storm.sun;
        var shadow = sun ? sun.GetComponent<StormSceneShadow>() : null;
        float Cookie(Vector3 g)
        {
            if (!shadow || !shadow.Cookie) return float.NaN;
            var c = shadow.transform.position; float size = shadow.sizeKm * 1000f; var rel = g - c;
            return Read(shadow.Cookie, Vector3.Dot(rel, shadow.transform.right) / size + 0.5f, Vector3.Dot(rel, shadow.transform.up) / size + 0.5f, 0);
        }
        var amb = StormSceneAmbient.Current;
        float AmbientAt(Vector3 g)
        {
            if (!amb || !amb.LocalActive || !amb.LocalMap) return float.NaN;
            float size = amb.sizeKm * 1000f, texel = size / amb.LocalMap.width; var c = cam.transform.position;
            var centre = new Vector3(Mathf.Round(c.x / texel) * texel, 0f, Mathf.Round(c.z / texel) * texel);
            return Read(amb.LocalMap, (g.x - centre.x) / size + 0.5f, (g.z - centre.z) / size + 0.5f, 1);
        }

        // under the base's edge, 3 km from the updraft on the side away from the forward flank, looking out
        var uW = Ground(new Vector2(u0.x, u0.y));
        var ffdW = Ground(new Vector2(F[36], F[37]));
        var away = uW - ffdW; away.y = 0f; away = away.sqrMagnitude > 1f ? away.normalized : -toSun;
        var from = uW + away * 3000f; var fS = storm.WorldToStorm(from); from.y = Ground(new Vector2(fS.x, fS.y)).y + 2f;
        cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(away + Vector3.up * 0.12f, Vector3.up));
        cam.fieldOfView = 75f;
        StormLabTestKit.Render(cam, Path.Combine(ShotDir, "light_warmup.png"), 1920, 1080, 12);

        rep.AppendLine($"Storm: {storm.preset}, seed {storm.seed}, at {storm.StormTime:0.0} min ({storm.Frame.stage}); sun at {storm.solarHour:0.0} h solar. Camera under the base 3 km from the updraft.").AppendLine();
        rep.AppendLine("Ground shadow (the share of sunlight reaching the ground; StormSceneShadow) and the ambient map (StormSceneAmbient's ambient occlusion):").AppendLine();
        rep.AppendLine("| ground under | shadow, physical | shadow, unified | ambient map, unified |").AppendLine("| --- | --- | --- | --- |");
        var sh = new Dictionary<string, float>();
        foreach (var m in new[] { StormLighting.Physical, StormLighting.Unified })
        {
            storm.lighting = m; storm.StepTo(storm.StormTime, storm.Clock, true);
            foreach (var x in Wait(2.5f)) yield return x;
            foreach (var (n, p) in pts) { sh[n + m] = Cookie(Ground(p)); if (m == StormLighting.Unified) sh[n + "amb"] = AmbientAt(Ground(p)); }
        }
        float ambCam = AmbientAt(cam.transform.position);
        foreach (var (n, p) in pts) rep.AppendLine($"| {n} | {sh[n + StormLighting.Physical]:0.000} | {sh[n + StormLighting.Unified]:0.000} | {sh[n + "amb"]:0.00} |");
        rep.AppendLine($"| (the camera) | | | {ambCam:0.00} |").AppendLine();
        rep.AppendLine($"Camera-wide ambient there (what the camera sees of the sky): {(amb ? amb.Ambient : 1f):0.00}; local ambient {(amb && amb.LocalActive ? "on" : "off")}.").AppendLine();
        float ffdU = sh["forward-flank core" + StormLighting.Unified], ffdP = sh["forward-flank core" + StormLighting.Physical];
        Check(!float.IsNaN(ffdU) && ffdU <= ffdP * 1.05f + 0.01f,
            $"under the unified lighting the ground under the forward-flank rain gets no more sun than under the physical ({ffdU:0.000} against {ffdP:0.000}): its rain shades as the storm's own light does");
        Check(amb && amb.LocalActive, "the scene's ambient light is set by place (the local ambient decal is on)");
        float open = sh["open ground 30 km sunward" + "amb"];
        Check(!float.IsNaN(ambCam) && ambCam < 0.6f && open > 0.9f,
            $"with the camera under the base, the ground there is shaded ({ambCam:0.00}) and the open ground 30 km sunward is not ({open:0.00})");

        // the storm from open ground 15 km off on the sun's side, looking a little down so the ground under it shows: the
        // ambient from the camera (in the open: the whole scene lit as open sky), then by place
        // (of eight bearings, the one on the map furthest from the forward-flank core)
        var world = Object.FindAnyObjectByType<TestMapWorld>(); float half = world ? world.sizeM / 2f - 2000f : 38000f;
        Vector3 oFrom = uW; float best = -1f;
        for (int b = 0; b < 8; b++)
        {
            var c = uW + Quaternion.Euler(0f, b * 45f, 0f) * Vector3.forward * 15000f;
            if (Mathf.Abs(c.x) > half || Mathf.Abs(c.z) > half) continue;
            float dd = (c - ffdW).sqrMagnitude; if (dd > best) { best = dd; oFrom = c; }
        }
        var oS = storm.WorldToStorm(oFrom); oFrom.y = Ground(new Vector2(oS.x, oS.y)).y + 30f;
        cam.transform.SetPositionAndRotation(oFrom, Quaternion.LookRotation(new Vector3(uW.x, oFrom.y + 300f, uW.z) - oFrom, Vector3.up));
        cam.fieldOfView = 60f;
        StormLabTestKit.Render(cam, Path.Combine(ShotDir, "light_warmup.png"), 1920, 1080, 12);
        var paths = new List<string>();
        foreach (var local in new[] { false, true })
        {
            if (amb) amb.local = local;
            foreach (var x in Wait(3f)) yield return x;
            storm.ResetHistory(cam);
            string path = Path.Combine(ShotDir, $"light_scene_open_{(local ? "local" : "camera")}.png");
            StormLabTestKit.Render(cam, path, 1920, 1080, 8);
            paths.Add(path);
            rep.AppendLine($"- {Path.GetFileName(path)}: from open ground 15 km off, ambient {(local ? "by place" : "from the camera")} (camera-wide {(amb ? amb.Ambient : 1f):0.00}); ground just below the horizon (the ground under the storm) luminance {BandLuminance(path, 0.40f, 0.48f):0.0000}, near ground {BandLuminance(path, 0.0f, 0.2f):0.0000}");
        }
        if (amb) amb.local = true;
        Sheet(paths[0], paths[1], Path.Combine(ShotDir, "light_scene_open_sheet.png"));
        // from 600 m up over that open ground, looking down at the ground under the base 6 km off (in the storm's
        // shadow, lit by the sky alone): the camera sees open sky, so the camera-wide ambient lit that ground as open
        // country; by place it is as dark as the storm makes it
        var dFrom = uW + (oFrom - uW).normalized * 7000f; var dS = storm.WorldToStorm(dFrom); dFrom.y = Ground(new Vector2(dS.x, dS.y)).y + 600f;
        var dAt = uW + (oFrom - uW).normalized * 1000f; var dAS = storm.WorldToStorm(dAt); dAt.y = Ground(new Vector2(dAS.x, dAS.y)).y;
        cam.transform.SetPositionAndRotation(dFrom, Quaternion.LookRotation(dAt - dFrom, Vector3.up));
        cam.fieldOfView = 50f;
        StormLabTestKit.Render(cam, Path.Combine(ShotDir, "light_warmup.png"), 1920, 1080, 12);
        var dPaths = new List<string>(); var dl = new float[2];
        for (int li = 0; li < 2; li++)
        {
            bool local = li == 1;
            if (amb) amb.local = local;
            foreach (var x in Wait(3f)) yield return x;
            storm.ResetHistory(cam);
            string path = Path.Combine(ShotDir, $"light_scene_down_{(local ? "local" : "camera")}.png");
            StormLabTestKit.Render(cam, path, 1920, 1080, 8);
            dPaths.Add(path);
            dl[li] = PatchLuminanceAndWarmth(path, 0.5f, 0.5f, 0.08f).Item1;
            rep.AppendLine($"- {Path.GetFileName(path)}: from 600 m up, the ground under the base 1 km from the updraft (the frame's centre; ambient map there {AmbientAt(dAt):0.00}), ambient {(local ? "by place" : "from the camera")} (camera-wide {(amb ? amb.Ambient : 1f):0.00}): luminance {dl[li]:0.0000}");
        }
        // how much of that ground's light is the scene's ambient, and whether the decal reaches it: the camera-wide scale
        // forced to 1 and 0, and the decal's occlusion forced to 0
        if (amb)
        {
            var forced = new StringBuilder("- the same view forced (centre luminance):");
            var fv = new List<float>();
            foreach (var (local, fa, fl, name) in new[] { (false, 1f, -1f, "ambient 1"), (false, 0f, -1f, "ambient 0"), (true, -1f, 0f, "decal occlusion 0"), (true, -1f, 1f, "decal occlusion 1") })
            {
                amb.local = local; amb.forceAmbient = fa; amb.forceLocal = fl;
                foreach (var x in Wait(2f)) yield return x;
                amb.Ambient.ToString();
                string path = Path.Combine(ShotDir, "light_scene_down_forced.png");
                StormLabTestKit.Render(cam, path, 960, 540, 8);
                fv.Add(PatchLuminanceAndWarmth(path, 0.5f, 0.5f, 0.08f).Item1);
                forced.Append($" {name} {fv[fv.Count - 1]:0.0000};");
            }
            amb.forceAmbient = -1f; amb.forceLocal = -1f;
            rep.AppendLine(forced.ToString());
            float byAmb = fv[0] - fv[1], byDecal = fv[3] - fv[2];
            // (the camera-wide scale also dims the fog's sky colour, which the decal does not: it takes a little more)
            Check(byAmb > 0f && byDecal > 0.5f * byAmb && byDecal <= byAmb * 1.05f,
                $"the decal reaches the scene's ambient light: its occlusion from 1 to 0 takes {byDecal:0.0000} off the ground there, the camera-wide ambient from 1 to 0 {byAmb:0.0000} (with the fog's; the ambient is {byAmb / Mathf.Max(fv[0], 1e-6f) * 100f:0}% of that ground's light)");
        }
        if (amb) amb.local = true;
        Sheet(dPaths[0], dPaths[1], Path.Combine(ShotDir, "light_scene_down_sheet.png"));
        // the storm's shadow on that ground alone: the physical lighting's (the old cookie) and the unified's, the ambient
        // from the camera in both
        if (amb) amb.local = false;
        var shPaths = new List<string>();
        foreach (var m in new[] { StormLighting.Physical, StormLighting.Unified })
        {
            storm.lighting = m; storm.StepTo(storm.StormTime, storm.Clock, true);
            foreach (var x in Wait(3f)) yield return x;
            storm.ResetHistory(cam);
            string path = Path.Combine(ShotDir, $"light_scene_shadow_{m}.png");
            StormLabTestKit.Render(cam, path, 1920, 1080, 8);
            shPaths.Add(path);
            rep.AppendLine($"- {Path.GetFileName(path)}: the same view, {m} lighting's shadow; centre luminance {PatchLuminanceAndWarmth(path, 0.5f, 0.5f, 0.08f).Item1:0.0000}");
        }
        storm.lighting = StormLighting.Unified; storm.StepTo(storm.StormTime, storm.Clock, true);
        if (amb) amb.local = true;
        Sheet(shPaths[0], shPaths[1], Path.Combine(ShotDir, "light_scene_shadow_sheet.png"));
        rep.AppendLine();

        // night, moonless: the base from 10 km, no towns, the map's towns, a town under the base
        // (4 stops brighter than the game shows it, so the frames are not lost in the 8-bit image's darkest levels)
        float comp0 = panel ? panel.compensation : 0f;
        if (panel) { panel.hour = 21.8f; panel.compensation = 4f; panel.Apply(); }
        float moon0 = storm.moonPhase; storm.moonPhase = 0f;
        storm.StepTo(storm.StormTime, storm.Clock, true);
        // (the town is put under the base 4 km from the updraft away from the forward flank; the camera 4 km from it)
        var mapTowns = new List<StormRenderer.TownGlow>(StormRenderer.Towns);
        var under = Ground(new Vector2(u0.x, u0.y) + new Vector2(away.x, away.z) * 4f);
        var side = Vector3.Cross(Vector3.up, away);
        var nFrom = under + (away * 0.5f + side * 0.85f).normalized * 4000f; var nS = storm.WorldToStorm(nFrom); nFrom.y = Ground(new Vector2(nS.x, nS.y)).y + 2f;
        var overTown = new Vector3(under.x, under.y + F[4] * 1000f, under.z);
        cam.transform.SetPositionAndRotation(nFrom, Quaternion.LookRotation(overTown - nFrom, Vector3.up));
        cam.fieldOfView = 70f;
        var cases = new (string, List<StormRenderer.TownGlow>)[]
        {
            ("none", new List<StormRenderer.TownGlow>()),
            ("map", mapTowns),
            ("under", new List<StormRenderer.TownGlow> { new StormRenderer.TownGlow { position = under, radiusM = 2500f, glow = 1f } }),
        };
        rep.AppendLine($"Night ({storm.solarHour:0.0} h solar, key light the {(storm.Lighting.keyIsMoon ? "moon" : "sun")} at {storm.Lighting.keyC.w:0} deg), the base over the town from 4 km. Map towns, km in the storm's frame (updraft {u0.x:0}, {u0.y:0}): {string.Join("; ", mapTowns.Select(t => $"{storm.WorldToStorm(t.position).x:0}, {storm.WorldToStorm(t.position).y:0} (glow {t.glow:0.00})"))}").AppendLine();
        var nightPaths = new List<string>();
        foreach (var (n, list) in cases)
        {
            StormRenderer.Towns.Clear(); StormRenderer.Towns.AddRange(list);
            storm.StepTo(storm.StormTime, storm.Clock, true); storm.ResetHistory(cam);
            foreach (var x in Wait(0.3f)) yield return x;
            string path = Path.Combine(ShotDir, $"light_scene_night_{n}.png");
            StormLabTestKit.Render(cam, path, 1920, 1080, 8);
            nightPaths.Add(path);
            var (l, rb) = UpperLuminanceAndWarmth(path);
            var (lt, rbt) = PatchLuminanceAndWarmth(path, 0.5f, 0.5f, 0.06f);
            {
                // the ground light's town channel (b) under the town and under the updraft
                var gt = storm.Volume.GroundTex; float Lk = StormVolume.L;
                float G(Vector3 w) { var q = storm.WorldToStorm(w); int ix = Mathf.Clamp(Mathf.FloorToInt((q.x + Lk) / (2f * Lk) * gt.width), 0, gt.width - 1), iy = Mathf.Clamp(Mathf.FloorToInt((q.y + Lk) / (2f * Lk) * gt.height), 0, gt.height - 1);
                    var rq = UnityEngine.Rendering.AsyncGPUReadback.Request(gt, 0, ix, 1, iy, 1, 0, 1, TextureFormat.RGBAHalf); rq.WaitForCompletion(); return Mathf.HalfToFloat(rq.GetData<ushort>()[2]); }
                rep.AppendLine($"  - town light on the base (x the even town light): over the town {G(under):0.00}, over the updraft {G(uW):0.00}");
            }
            rep.AppendLine($"- {Path.GetFileName(path)}: towns {n}; upper-half luminance {l:0.0000}, red / blue {rb:0.00}; the base over the town (the frame's centre) {lt:0.00000}, red / blue {rbt:0.00}");
            if (n != "map")
            {
                var parts = new StringBuilder("  - the base over the town, each light alone (sun, sky, ground):");
                for (int d = 2; d <= 4; d++)
                {
                    storm.lightDebug = d; storm.ResetHistory(cam);
                    string dp = Path.Combine(ShotDir, $"light_scene_night_{n}_dbg{d}.png");
                    StormLabTestKit.Render(cam, dp, 960, 540, 8);
                    var (ld, rbd) = PatchLuminanceAndWarmth(dp, 0.5f, 0.5f, 0.06f);
                    parts.Append($" {ld:0.00000} (red / blue {rbd:0.00});");
                }
                storm.lightDebug = 0;
                rep.AppendLine(parts.ToString());
            }
        }
        StormRenderer.Towns.Clear(); StormRenderer.Towns.AddRange(mapTowns);
        storm.moonPhase = moon0;
        if (panel) { panel.compensation = comp0; panel.Apply(); }
        Sheet(nightPaths[0], nightPaths[2], Path.Combine(ShotDir, "light_scene_night_sheet.png"));
        rep.AppendLine().AppendLine("Sheets: `Screenshots/light_scene_open_sheet.png` (ambient from the camera left, by place right), `light_scene_night_sheet.png` (no towns left, a town under the base right).");
    }

    /// <summary>Mean linear luminance (0..1) of the rows from y0 to y1 (shares of the height, from the bottom).</summary>
    static float BandLuminance(string path, float y0, float y1)
    {
        var t = new Texture2D(2, 2); t.LoadImage(File.ReadAllBytes(path));
        var p = t.GetPixels(); int w = t.width, h = t.height;
        double s = 0; long n = 0;
        for (int y = (int)(y0 * h); y < (int)(y1 * h); y++) for (int x = 0; x < w; x++) { var c = p[y * w + x].linear; s += 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; n++; }
        Object.DestroyImmediate(t);
        return (float)(s / Math.Max(n, 1));
    }

    /// <summary>A square patch's mean linear luminance and red / blue: centre (x, y) and half-size as shares of the height.</summary>
    static (float, float) PatchLuminanceAndWarmth(string path, float cx, float cy, float r)
    {
        var t = new Texture2D(2, 2); t.LoadImage(File.ReadAllBytes(path));
        var p = t.GetPixels(); int w = t.width, h = t.height;
        double s = 0, rr = 0, b = 0; long n = 0;
        int x0 = (int)(cx * w - r * h), x1 = (int)(cx * w + r * h), y0 = (int)((cy - r) * h), y1 = (int)((cy + r) * h);
        for (int y = Math.Max(y0, 0); y < Math.Min(y1, h); y++) for (int x = Math.Max(x0, 0); x < Math.Min(x1, w); x++) { var c = p[y * w + x].linear; s += 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; rr += c.r; b += c.b; n++; }
        Object.DestroyImmediate(t);
        return ((float)(s / Math.Max(n, 1)), (float)(rr / Math.Max(b, 1e-9)));
    }

    /// <summary>The upper half's mean linear luminance and its red / blue.</summary>
    static (float, float) UpperLuminanceAndWarmth(string path)
    {
        var t = new Texture2D(2, 2); t.LoadImage(File.ReadAllBytes(path));
        var p = t.GetPixels(); int w = t.width, h = t.height;
        double s = 0, r = 0, b = 0; long n = 0;
        for (int y = h / 2; y < h; y++) for (int x = 0; x < w; x++) { var c = p[y * w + x].linear; s += 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; r += c.r; b += c.b; n++; }
        Object.DestroyImmediate(t);
        return ((float)(s / n), (float)(r / Math.Max(b, 1e-9)));
    }

    /// <summary>
    /// -slLightTailFlow (user, 2026-10-28: the beaver tail "needs to look as if it is flowing into the storm"): the beaver
    /// tail side-on from 5 km, square to it at 4 km out from its root, 8 frames 3 s apart (the storm paused; the flow runs on
    /// the wall clock, StormSpin), and how far the tail's fine detail moves along the screen between them.
    /// Screenshots/light_tailflow_<k>.png and light_tailflow.gif
    /// </summary>
    static IEnumerable<object> TailFlow(StormRenderer storm, Camera cam)
    {
        var F = storm.Frame.param;
        var up = storm.WorldToStorm(storm.UpdraftWorld(storm.StormTime));
        // the game's line (StormCommon.hlsl tailDir, tailRoot: along the forward flank's inflow edge)
        Vector2 fw = new Vector2(F[0], F[1]), lf = new Vector2(F[2], F[3]), td = (fw + 0.176f * lf).normalized;
        var t0 = new Vector2(F[64], F[65]) + fw * 2.5f - lf * 0.5f;   // S_up.xy
        // (from its right: the inflow side, where a chaser stands; its left is the forward flank's rain)
        var mid = t0 + td * 4f; var side = new Vector2(td.y, -td.x);
        float zb = F[4];
        var mW = storm.StormToWorld(new Vector3(mid.x, mid.y, 0f));
        var fromS = mid + side * 5f;
        var from = storm.StormToWorld(new Vector3(fromS.x, fromS.y, 0f));
        from.y = (Physics.Raycast(from + Vector3.up * 3000f, Vector3.down, out var hit, 6000f) ? hit.point.y : 0f) + 300f;
        cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(new Vector3(mW.x, from.y - 300f + zb * 1000f, mW.z) - from, Vector3.up));
        cam.fieldOfView = 60f;
        StormLabTestKit.Render(cam, Path.Combine(ShotDir, "light_warmup.png"), 1600, 900, 12);
        float flow0 = F[StormSpin.Slot0 * 4 + 11];
        var paths = new List<string>(); var flows = new List<float>();
        for (int k = 0; k < 8; k++)
        {
            if (k > 0) foreach (var x in Wait(3f)) yield return x;
            string path = Path.Combine(ShotDir, $"light_{storm.preset}_tailflow_{k}.png");
            StormLabTestKit.Render(cam, path, 1600, 900, 4);
            paths.Add(path); flows.Add(storm.Frame.param[StormSpin.Slot0 * 4 + 11]);
        }
        // where it meets the updraft: side-on 4 km off its root, and from 9 km out along it looking back at the updraft
        // (1 km to its right, so the band runs across the view toward the updraft)
        string pre = $"light_{storm.preset}_tailroot";
        var views = new[] { (t0 + td * 0.5f + side * 4f, t0, "side"), (t0 + td * 9f + side * 1f, t0, "along"), (mid + side * 15f, mid, "wide"), (t0 + td * 1.5f + side * 2.5f, t0 - td * 1f, "join") };
        foreach (var (fromV, atV, nm) in views)
        {
            var f3 = storm.StormToWorld(new Vector3(fromV.x, fromV.y, 0f)); var a3 = storm.StormToWorld(new Vector3(atV.x, atV.y, 0f));
            f3.y = (Physics.Raycast(f3 + Vector3.up * 3000f, Vector3.down, out var h2, 6000f) ? h2.point.y : 0f) + 150f;
            cam.transform.SetPositionAndRotation(f3, Quaternion.LookRotation(new Vector3(a3.x, f3.y - 150f + (zb + 0.6f) * 1000f, a3.z) - f3, Vector3.up));
            cam.fieldOfView = 70f;
            StormLabTestKit.Render(cam, Path.Combine(ShotDir, "light_warmup.png"), 1600, 900, 12);
            StormLabTestKit.Render(cam, Path.Combine(ShotDir, $"{pre}_{nm}.png"), 1600, 900, 8);
            // -slLightDebug: the same without the near field's own sky occlusion (StormRenderer.lightDebug 5)
            if (SessionState.GetBool(DebugKey, false))
            {
                storm.lightDebug = 5; storm.ResetHistory(cam);
                StormLabTestKit.Render(cam, Path.Combine(ShotDir, $"{pre}_{nm}_noocc.png"), 1600, 900, 8);
                storm.lightDebug = 0;
            }
            yield return null;
        }
        // the tail cloud side-on from 4 km on the inflow side (its right), 6 frames 2 s apart: its scud racing in
        // (StormCommon.hlsl tailCloudAim: at the forward-flank rain, a quarter of the way from the hail core to its core)
        {
            var wc = new Vector2(F[24], F[25]); float Rw = F[26];
            var aimT = Vector2.Lerp(new Vector2(F[40], F[41]), new Vector2(F[36], F[37]), 0.25f);
            var ax = (aimT - wc).normalized; var midT = wc + ax * (Rw + 2.2f); var sideT = new Vector2(ax.y, -ax.x);
            var fT = storm.StormToWorld(new Vector3(midT.x + sideT.x * 4f, midT.y + sideT.y * 4f, 0f));
            var mT = storm.StormToWorld(new Vector3(midT.x, midT.y, 0f));
            fT.y = (Physics.Raycast(fT + Vector3.up * 3000f, Vector3.down, out var h3, 6000f) ? h3.point.y : 0f) + 60f;
            cam.transform.SetPositionAndRotation(fT, Quaternion.LookRotation(new Vector3(mT.x, fT.y - 60f + (zb - 0.15f) * 1000f, mT.z) - fT, Vector3.up));
            cam.fieldOfView = 60f;
            StormLabTestKit.Render(cam, Path.Combine(ShotDir, "light_warmup.png"), 1600, 900, 12);
            for (int k = 0; k < 6; k++)
            {
                if (k > 0) foreach (var x in Wait(2f)) yield return x;
                StormLabTestKit.Render(cam, Path.Combine(ShotDir, $"light_{storm.preset}_tailcloud_{k}.png"), 1600, 900, 4);
            }
            rep.AppendLine($"The tail cloud side-on from 4 km, 6 frames 2 s apart: `Screenshots/light_{storm.preset}_tailcloud_<k>.png`.").AppendLine();
        }
        rep.AppendLine($"Where it meets the updraft: `Screenshots/{pre}_side.png` (4 km off its root), `{pre}_along.png` (from 9 km out along it).").AppendLine();
        rep.AppendLine($"Storm: {storm.preset}, seed {storm.seed}, plan wrap {storm.Plan.wrap:0.00}; rain wrap (HP) {F[47]:0.00}, so the HP size share {Mathf.Clamp01(F[47] / 0.75f):0.00}.").AppendLine();
        rep.AppendLine($"Beaver tail side-on from 5 km (its axis ({td.x:0.00}, {td.y:0.00}), {F[34]:0} km long in the web build's design), 8 frames 3 s apart. Flow carried: {flows.Last() - flows.First():0.000} km.").AppendLine();
        rep.AppendLine($"Frames: `Screenshots/light_{storm.preset}_tailflow_<k>.png`.");
        Check(flows.Last() - flows.First() > 0.2, "the inflow carries the beaver tail's air in while the storm is paused");
    }

    /// <summary>
    /// -slLightFlicker (user, 2026-10-28: flashing round the forward-flank rain, "as if the old trimmed boundaries keep popping
    /// up"): the storm plays (0.5 storm min a second, so the volume is repainted about every 0.2 s, spread over Slabs
    /// frames, as in Play) and every frame the rain in the volume's level at 0.5 km is read back and summed. Rain that
    /// pops in and out shows as jumps from one frame to the next; the storm's own change is a fraction of a per cent a frame.
    /// </summary>
    static IEnumerable<object> Flicker(StormRenderer storm)
    {
        var vol = storm.Volume;
        int iz = Mathf.Clamp(Mathf.FloorToInt(0.5f / StormVolume.ZT * vol.NZ), 0, vol.NZ - 1);
        storm.minutesPerSecond = 0.5f; storm.playing = true;
        var sums = new List<double>(); var rebuild = new List<bool>();
        float t0 = Time.realtimeSinceStartup;
        while (sums.Count < 300 && Time.realtimeSinceStartup - t0 < 150f)
        {
            yield return null;
            var req = UnityEngine.Rendering.AsyncGPUReadback.Request(vol.Dens, 0, 0, vol.NV, 0, vol.NV, iz, 1, TextureFormat.RGBA32);
            req.WaitForCompletion();
            if (req.hasError) continue;
            var d = req.GetData<byte>(); double sum = 0;
            for (int i = 1; i < d.Length; i += 4) { double g = d[i] / 255.0; sum += g * g; }   // (the volume holds sqrt(rain))
            sums.Add(sum); rebuild.Add(vol.Rebuilding);
        }
        storm.playing = false;
        int repaints = 0; for (int i = 1; i < rebuild.Count; i++) if (rebuild[i - 1] && !rebuild[i]) repaints++;
        double mean = sums.Average();
        var jumps = Enumerable.Range(1, sums.Count - 1).Select(i => Math.Abs(sums[i] - sums[i - 1]) / Math.Max(mean, 1e-9)).OrderBy(j => j).ToList();
        double worst = jumps.Last(), p95 = jumps[(int)(0.95 * (jumps.Count - 1))];
        int big = jumps.Count(j => j > 0.02);
        rep.AppendLine($"Flicker: {storm.preset} storm from {storm.StormTime:0.0} storm min, {sums.Count} frames, {repaints} repaints finished; rain summed over the level at 0.5 km.").AppendLine();
        rep.AppendLine($"- frame-to-frame change: worst {worst * 100:0.00} %, 95th percentile {p95 * 100:0.00} %, frames jumping over 2 %: {big}");
        rep.AppendLine("- series (rain sum / mean, R = repainting): " + string.Join(" ", sums.Take(80).Select((v, i) => $"{v / mean:0.000}{(rebuild[i] ? "R" : "")}")));
        Check(worst < 0.02, $"no rain pops in or out between frames (worst change {worst * 100:0.00} % of the level's rain)");
    }

    /// <summary>One texel of a storm grid (n x n x nz over the volume, half-float RGBA) at storm km (x, y, z).</summary>
    internal static Vector4 Probe(RenderTexture t, int n, int nz, float x, float y, float z, TextureFormat f = TextureFormat.RGBAHalf)
    {
        float L = StormVolume.L;
        int ix = Mathf.Clamp(Mathf.FloorToInt((x + L) / (2f * L) * n), 0, n - 1), iy = Mathf.Clamp(Mathf.FloorToInt((y + L) / (2f * L) * n), 0, n - 1);
        int iz = Mathf.Clamp(Mathf.FloorToInt(z / StormVolume.ZT * nz), 0, nz - 1);
        var req = UnityEngine.Rendering.AsyncGPUReadback.Request(t, 0, ix, 1, iy, 1, iz, 1, f);
        req.WaitForCompletion();
        if (req.hasError) return Vector4.zero;
        if (f == TextureFormat.RGBA32) { var b = req.GetData<byte>(); return new Vector4(b[0], b[1], b[2], b[3]) / 255f; }
        var d = req.GetData<ushort>();
        return new Vector4(Mathf.HalfToFloat(d[0]), Mathf.HalfToFloat(d[1]), Mathf.HalfToFloat(d[2]), Mathf.HalfToFloat(d[3]));
    }

    /// <summary>Mean linear luminance (0..1) of the upper half of the frame: the sky and the storm in these views.</summary>
    static float UpperLuminance(string path)
    {
        var t = new Texture2D(2, 2); t.LoadImage(File.ReadAllBytes(path));
        var p = t.GetPixels(); int w = t.width, h = t.height;
        double s = 0; long n = 0;
        for (int y = h / 2; y < h; y++) for (int x = 0; x < w; x++)
        {
            var c = p[y * w + x].linear;
            s += 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; n++;
        }
        Object.DestroyImmediate(t);
        return (float)(s / n);
    }

    /// <summary>1920 x 1080: the two frames at half size above, the centre 960 x 540 of each at 1:1 below.</summary>
    static void Sheet(string a, string b, string path)
    {
        var ta = new Texture2D(2, 2); ta.LoadImage(File.ReadAllBytes(a));
        var tb = new Texture2D(2, 2); tb.LoadImage(File.ReadAllBytes(b));
        int W = ta.width, H = ta.height;
        var o = new Texture2D(W, H, TextureFormat.RGB24, false);
        var px = new Color32[W * H];
        var pa = ta.GetPixels32(); var pb = tb.GetPixels32();
        int hw = W / 2, hh = H / 2;
        for (int y = 0; y < hh; y++) for (int x = 0; x < hw; x++)
        {
            // rows run bottom-up: the upper half holds the half-size frames, the lower half the crops
            px[(y + hh) * W + x] = pa[(2 * y) * W + 2 * x];
            px[(y + hh) * W + x + hw] = pb[(2 * y) * W + 2 * x];
            px[y * W + x] = pa[(y + hh / 2) * W + x + hw / 2];
            px[y * W + x + hw] = pb[(y + hh / 2) * W + x + hw / 2];
        }
        for (int y = 0; y < H; y++) { px[y * W + hw] = new Color32(255, 255, 255, 255); }
        o.SetPixels32(px); o.Apply();
        File.WriteAllBytes(path, o.EncodeToPNG());
        Object.DestroyImmediate(ta); Object.DestroyImmediate(tb); Object.DestroyImmediate(o);
    }
}

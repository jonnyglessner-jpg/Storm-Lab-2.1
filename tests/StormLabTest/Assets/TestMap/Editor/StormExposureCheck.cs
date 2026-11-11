// Play-mode check of the test map's auto exposure round a game's storms (user, 2026-11-09: "assess whether it is too dark
// or too bright in various positions on the roads relative to the storm"). A game is laid out and stopped at a game
// minute; for each live storm the camera is put at driver's eye height on the road nearest a set of spots round its
// updraft (by distance and by bearing from the storm's motion: the inflow side, behind it, in the core, ahead under the
// anvil), the scene's ambient and the panel's exposure are left to settle as in Play, and two frames are drawn: toward the
// updraft and away from it. For each frame: the exposure in use, the stops the storm's shade opened it, and how bright
// the frame came out on screen (log-average, median, the darkest and brightest twentieths, the share of pixels near
// black and near white), for the sky and storm above the horizon and for the ground below it.
// Also (the same day: "why is the wall cloud lighter than the surrounding clouds"): the light and sky-visibility grids
// up through each storm's wall cloud, its main base and the base beside the wall cloud, with the sky light each takes.
// Screenshots/exp_*.png, sheets exp_sheet_*.jpg, validation/reports/StormExposureCheck.md and .csv.
// Batch: -executeMethod StormExposureCheck.RunFromCommandLine [-slExpSeed n] [-slExpPreset key] [-slExpMin gameMin]
//        [-slExpHours "12,16,18.5"] [-slExpTag name] (never with -nographics)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using StormLab;
using StormLab.TestMap;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

[InitializeOnLoad]
public static class StormExposureCheck
{
    const string Key = "StormExposureCheck.Active", BatchKey = "StormExposureCheck.Batch", CodeKey = "StormExposureCheck.Code";
    const string SeedKey = "StormExposureCheck.Seed", PresetKey = "StormExposureCheck.Preset", MinKey = "StormExposureCheck.Min";
    const string HoursKey = "StormExposureCheck.Hours", TagKey = "StormExposureCheck.Tag";
    static string ShotDir => Path.Combine(StormLabTestKit.ProjectDir, "Screenshots");
    static string ReportDir => Path.Combine(StormLabTestKit.RepoRoot, "validation", "reports");

    static StormExposureCheck() { if (SessionState.GetBool(Key, false)) EditorApplication.playModeStateChanged += OnState; }

    [MenuItem("StormLab/Test Map/Exposure Check")]
    public static void RunMenu() => Begin(false);
    public static void RunFromCommandLine() => Begin(true);

    static void Begin(bool batch)
    {
        var args = System.Environment.GetCommandLineArgs();
        string Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        SessionState.SetInt(SeedKey, int.TryParse(Arg("-slExpSeed"), out var sd) ? sd : 816557);
        SessionState.SetString(PresetKey, Arg("-slExpPreset") ?? "plains");
        SessionState.SetFloat(MinKey, float.TryParse(Arg("-slExpMin"), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 38.9f);
        SessionState.SetString(HoursKey, Arg("-slExpHours") ?? "12,16,18.5");
        SessionState.SetString(TagKey, Arg("-slExpTag") ?? "");
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
        string tag = SessionState.GetString(TagKey, "");
        rep.Insert(0, $"# Exposure check: {(ok ? "DONE" : "FAIL")}\n\nGenerated by `StormExposureCheck` on {DateTime.Now:yyyy-MM-dd HH:mm}, {SystemInfo.graphicsDeviceName}.\n\n");
        Directory.CreateDirectory(ReportDir);
        File.WriteAllText(Path.Combine(ReportDir, $"StormExposureCheck{(tag.Length > 0 ? "_" + tag : "")}.md"), rep.ToString());
        Debug.Log($"[StormExposure] {(ok ? "DONE" : "FAIL")}");
        SessionState.SetInt(CodeKey, ok ? 0 : 1);
        EditorApplication.ExitPlaymode();
    }

    static IEnumerable<object> Frames(int n) { for (int i = 0; i < n; i++) yield return null; }
    static IEnumerable<object> Wait(float seconds) { float t0 = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - t0 < seconds) yield return null; }

    // a spot round the updraft: km out, on a bearing clockwise from the storm's motion (45: the inflow side, right-front;
    // 180: behind; -30: the forward-flank core, left-front; 0: ahead under the anvil)
    struct Place { public string name; public float rel, km; }
    static readonly Place[] Places =
    {
        new Place { name = "under the base", rel = 0, km = 0 },
        new Place { name = "inflow 4 km", rel = 45, km = 4 },
        new Place { name = "inflow 10 km", rel = 45, km = 10 },
        new Place { name = "inflow 20 km", rel = 45, km = 20 },
        new Place { name = "inflow 40 km", rel = 45, km = 40 },
        new Place { name = "right 8 km", rel = 90, km = 8 },
        new Place { name = "right-rear 6 km", rel = 135, km = 6 },
        new Place { name = "behind 12 km", rel = 180, km = 12 },
        new Place { name = "left 12 km", rel = -90, km = 12 },
        new Place { name = "core 8 km", rel = -30, km = 8 },
        new Place { name = "core 16 km", rel = -30, km = 16 },
        new Place { name = "ahead 15 km", rel = 0, km = 15 },
        new Place { name = "ahead 35 km", rel = 0, km = 35 },
    };

    /// <summary>How a frame came out on screen: over the pixels of rows [y0, y1) (0 the bottom row).</summary>
    struct Stat { public float logAvg, mean, median, p5, p95, black, white; }
    static readonly float[] Lin = Enumerable.Range(0, 256).Select(i => Mathf.GammaToLinearSpace(i / 255f)).ToArray();

    static Stat Measure(Color32[] px, int w, int y0, int y1)
    {
        var hist = new int[256]; double sumLog = 0, sum = 0; int n = 0;
        for (int y = Mathf.Max(y0, 0); y < y1; y++)
            for (int x = 0; x < w; x++)
            {
                var c = px[y * w + x];
                float Y = 0.2126f * Lin[c.r] + 0.7152f * Lin[c.g] + 0.0722f * Lin[c.b];
                sum += Y; sumLog += Math.Log(Math.Max(Y, 1e-4)); n++;
                hist[Mathf.Clamp(Mathf.RoundToInt(Mathf.LinearToGammaSpace(Y) * 255f), 0, 255)]++;
            }
        var s = new Stat();
        if (n == 0) { s.logAvg = s.mean = float.NaN; return s; }
        s.logAvg = (float)Math.Exp(sumLog / n); s.mean = (float)(sum / n);
        float Pct(float p) { int acc = 0, need = Mathf.CeilToInt(p * n); for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= need) return i; } return 255; }
        s.median = Pct(0.5f); s.p5 = Pct(0.05f); s.p95 = Pct(0.95f);
        int b = 0, wh = 0; for (int i = 0; i < 20; i++) b += hist[i]; for (int i = 245; i < 256; i++) wh += hist[i];
        s.black = (float)b / n; s.white = (float)wh / n;
        return s;
    }

    static IEnumerable<object> Run()
    {
        foreach (var x in Frames(30)) yield return x;
        var inv = CultureInfo.InvariantCulture;
        var world = Object.FindAnyObjectByType<TestMapWorld>();
        var panel = world ? world.GetComponent<TestMapStormPanel>() : null;
        var cam = Camera.main;
        if (!world || !panel || !cam) { ok = false; rep.AppendLine($"FAIL: world {world}, panel {panel}, camera {cam}"); yield break; }
        var rig = cam.GetComponent<TestMapCamera>(); if (rig) rig.enabled = false;
        var roads = world.GetComponent<TestMapMinimap>()?.Roads;
        string tag = SessionState.GetString(TagKey, ""); string pre = tag.Length > 0 ? $"exp_{tag}" : "exp";
        var hours = SessionState.GetString(HoursKey, "12").Split(',').Select(h => float.Parse(h, inv)).ToArray();
        float gameMin = SessionState.GetFloat(MinKey, 38.9f);

        panel.clockRuns = false; panel.hour = hours[0]; panel.dayOfYear = 145; panel.latitude = 38.1f; panel.Apply();
        world.storm.preset = SessionState.GetString(PresetKey, "plains");
        world.SetGameMode(true);
        var game = world.game;
        game.preset = world.storm.preset; game.seed = SessionState.GetInt(SeedKey, 816557); game.playing = false;
        game.Spawn();
        foreach (var x in Frames(3)) yield return x;
        game.gameMinutes = gameMin; game.Apply();
        foreach (var x in Frames(3)) yield return x;
        var sc = game.Scenario;
        float half = world.sizeM / 2, mile = 1609.344f;
        rep.AppendLine($"Game: {sc.preset.key}, game seed {sc.seed}, at {gameMin:0.0} game min; 25 May at 38.1 N. Auto exposure {(panel.autoExposure ? "on" : "OFF")}: storm adaptation {panel.stormAdaptation:0.00} stop per halving of the shade, at most {panel.stormAdaptationMax:0.0}; compensation {panel.compensation:0.0}. Camera {cam.fieldOfView:0} deg, 1.7 m over the road.").AppendLine();
        for (int i = 0; i < game.Storms.Count; i++)
        {
            var r = game.Storms[i]; if (!game.Alive(i)) { rep.AppendLine($"- storm {i + 1}: not alive"); continue; }
            var u = r.UpdraftWorld(r.StormTime);
            rep.AppendLine($"- storm {i + 1}: {sc.storms[i].precip}, {r.Frame.stage}, {r.StormTime:0.0} min into its life; updraft {(u.x + half) / mile:0.0} mi E, {(u.z + half) / mile:0.0} mi N; base {r.Frame.param[4]:0.00} km");
        }
        rep.AppendLine();

        var csv = new StringBuilder("hour,storm,place,km,compassDeg,road,offRoadM,view,EV,stormLift,ambient,sun,all_logAvg,all_median,sky_logAvg,sky_median,sky_p5,sky_p95,sky_black,sky_white,gnd_logAvg,gnd_median,gnd_p5,gnd_p95,gnd_black,gnd_white,shade,sunMetered,sunWeight\n");
        const int W = 1200, H = 500;
        foreach (float hour in hours)
        {
            panel.hour = hour; panel.Apply();
            foreach (var r in game.Storms.Where(r => r && r.gameObject.activeInHierarchy)) r.StepTo(r.StormTime, r.Clock, true);
            foreach (var x in Wait(1f)) yield return x;
            var sunAzEl = panel.SunAzEl;
            rep.AppendLine($"## {hour:0.0} h solar (sun {sunAzEl.x:0} deg, {sunAzEl.y:0.0} deg up)").AppendLine();
            for (int i = 0; i < game.Storms.Count; i++)
            {
                var r = game.Storms[i]; if (!game.Alive(i)) continue;
                if (hour == hours[0]) { Grids(r, i); Overhead(r, i); }
                if (System.Environment.GetCommandLineArgs().Contains("-slExpGridsOnly")) continue;
                Vector3 u = r.UpdraftWorld(r.StormTime), u1 = r.UpdraftWorld(r.StormTime + 2), u0 = r.UpdraftWorld(r.StormTime - 2);
                float motion = Mathf.Atan2(u1.x - u0.x, u1.z - u0.z) * Mathf.Rad2Deg;
                rep.AppendLine($"### Storm {i + 1} ({sc.storms[i].precip}, {r.Frame.stage}), moving toward {(motion + 360f) % 360f:0} deg").AppendLine();
                rep.AppendLine("| place | from the updraft | road | view | EV100 | storm lift | shade | sky seen | sun metered | sun | sky+storm: log-avg, median, 5-95 %, black, white | ground: log-avg, median, 5-95 %, black, white |");
                rep.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
                var shots = new List<string>();
                // -slExpQuick: six places, toward the storm only (a look at a lighting change in a few minutes)
                bool quick = System.Environment.GetCommandLineArgs().Contains("-slExpQuick");
                foreach (var pl in Places)
                {
                    if (quick && !(pl.name == "under the base" || pl.name == "inflow 4 km" || pl.name == "inflow 10 km" || pl.name == "right 8 km" || pl.name == "right-rear 6 km" || pl.name == "behind 12 km")) continue;
                    float b = (motion + pl.rel) * Mathf.Deg2Rad;
                    var at = new Vector3(u.x + Mathf.Sin(b) * pl.km * 1000f, 0f, u.z + Mathf.Cos(b) * pl.km * 1000f);
                    if (Mathf.Abs(at.x) > half - 800f || Mathf.Abs(at.z) > half - 800f) { rep.AppendLine($"| {pl.name} | - | off the map | | | | | | | | |"); continue; }
                    string road = "-"; float off = 0f;
                    if (roads != null)
                    {
                        var s = roads.Nearest(new Vector2(at.x, at.z));
                        if (s.edge >= 0) { off = s.off; at = new Vector3(s.point.x, 0f, s.point.y); road = roads.edges[s.edge].name; }
                    }
                    at.y = (Physics.Raycast(at + Vector3.up * 3000f, Vector3.down, out var hit, 6000f) ? hit.point.y : 0f) + 1.7f;
                    var toU = new Vector3(u.x - at.x, 0f, u.z - at.z);
                    float dKm = toU.magnitude / 1000f;
                    // (under the base there is no "toward": it looks to the rear-right, where the wall cloud is)
                    float yawTo = dKm > 1.5f ? Mathf.Atan2(toU.x, toU.z) * Mathf.Rad2Deg : motion + 225f;
                    float compass = dKm > 1.5f ? (Mathf.Atan2(-toU.x, -toU.z) * Mathf.Rad2Deg + 360f) % 360f : 0f;
                    // let the scene's ambient and the exposure settle here, as they do in Play
                    cam.transform.SetPositionAndRotation(at, Quaternion.Euler(-10f, yawTo, 0f));
                    var amb = StormSceneAmbient.Current;
                    // (frames drawn all the while: the exposure follows the picture too, StormViewMeter)
                    IEnumerable<object> WaitDrawing(float seconds) { float tw = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - tw < seconds) { StormLabTestKit.Pump(cam); yield return null; } }
                    foreach (var x in WaitDrawing(1.2f)) yield return x;
                    float t0 = Time.realtimeSinceStartup, lastA = panel.StormShade, lastV = panel.ViewTrim;
                    while (Time.realtimeSinceStartup - t0 < 12f)
                    {
                        foreach (var x in WaitDrawing(0.6f)) yield return x;
                        float a = panel.StormShade, vt = panel.ViewTrim;
                        bool still = Mathf.Abs(a - lastA) < 0.004f * Mathf.Max(a, 0.02f) && Mathf.Abs(vt - lastV) < 0.02f; lastA = a; lastV = vt;
                        if (still) break;
                    }
                    foreach (var (view, yaw, pitch) in new[] { ("toward", yawTo, dKm < 6f ? 18f : 10f), ("away", yawTo + 180f, 3f) })
                    {
                        if (quick && view == "away") continue;
                        cam.transform.SetPositionAndRotation(at, Quaternion.Euler(-pitch, yaw, 0f));
                        foreach (var g in game.Storms) if (g && g.gameObject.activeInHierarchy) g.ResetHistory(cam);
                        foreach (var x in Frames(2)) yield return x;
                        string name = $"{pre}_{hour:00.0}_s{i + 1}_{pl.name.Replace(' ', '_')}_{view}.png";
                        string path = Path.Combine(ShotDir, name);
                        StormLabTestKit.Render(cam, path, W, H, 8);
                        var tex = new Texture2D(2, 2); tex.LoadImage(File.ReadAllBytes(path));
                        var px = tex.GetPixels32(); Object.DestroyImmediate(tex);
                        // the horizon's row: level ground far ahead (rows from the bottom)
                        var fwd = cam.transform.forward; fwd.y = 0f; fwd.Normalize();
                        int hz = Mathf.Clamp(Mathf.RoundToInt(cam.WorldToViewportPoint(cam.transform.position + fwd * 2e5f).y * H), 0, H);
                        Stat all = Measure(px, W, 0, H), sky = Measure(px, W, hz, H), gnd = Measure(px, W, 0, hz);
                        string S(Stat s) => float.IsNaN(s.logAvg) ? "-" : $"{s.logAvg:0.000}, {s.median:0}, {s.p5:0}-{s.p95:0}, {s.black * 100f:0} %, {s.white * 100f:0} %";
                        rep.AppendLine($"| {pl.name} | {dKm:0.0} km, {compass:0} deg | {road}{(off > 60f ? $" ({off:0} m off)" : "")} | {view} | {panel.ExposureEV:0.00} | {panel.StormLift:0.00} | {panel.StormShade:0.000} | {(amb ? amb.Ambient : 1f):0.000} | {panel.SunShare:0.000} | {SunAt(game, at):0.000} | {S(sky)} | {S(gnd)} |");
                        string C(Stat s) => string.Join(",", new[] { s.logAvg, s.median, s.p5, s.p95, s.black, s.white }.Select(v => v.ToString("0.####", inv)));
                        csv.AppendLine(string.Join(",", hour.ToString("0.0", inv), (i + 1).ToString(inv), pl.name, dKm.ToString("0.0", inv), compass.ToString("0", inv), road.Replace(',', ' '), off.ToString("0", inv), view,
                            panel.ExposureEV.ToString("0.00", inv), panel.StormLift.ToString("0.00", inv), (amb ? amb.Ambient : 1f).ToString("0.000", inv), SunAt(game, at).ToString("0.000", inv), all.logAvg.ToString("0.####", inv), all.median.ToString("0", inv), C(sky), C(gnd), panel.StormShade.ToString("0.000", inv), panel.SunShare.ToString("0.000", inv), panel.SunWeight.ToString("0.000", inv)));
                        shots.Add(name);
                    }
                }
                Sheet(shots.ToArray(), $"{pre}_sheet_{hour:00.0}_s{i + 1}.jpg");
                rep.AppendLine().AppendLine($"Frames in this order, two a row (toward, away): `Screenshots/{pre}_sheet_{hour:00.0}_s{i + 1}.jpg`.").AppendLine();
            }
        }
        File.WriteAllText(Path.Combine(ReportDir, $"StormExposureCheck{(tag.Length > 0 ? "_" + tag : "")}.csv"), csv.ToString());
        rep.AppendLine("Log-average: of the frame's luminance on screen (0-1, linear; 0.18 is mid grey). Median and 5-95 %: screen levels 0-255. Black: the share under level 20; white: over 244. Shade: the share of open daylight the exposure meters at the camera (TestMapStormPanel.StormShade), from the sky seen (StormSceneAmbient.Ambient) and the sun metered (StormSceneShadow.SunAtCamera). Sun: the share of the sun's light reaching the ground at the spot by the light grid, for comparison.");
    }

    /// <summary>The share of the sun's light that reaches the ground at a spot through every live storm (the unified
    /// lighting's sun reach of the light grid's depth there, as the scene's shadow takes it).</summary>
    static float SunAt(StormScenarioRunner game, Vector3 world)
    {
        float s = 1f;
        for (int i = 0; i < game.Storms.Count; i++)
        {
            var r = game.Storms[i]; if (!game.Alive(i)) continue;
            var p = r.WorldToStorm(world);
            if (Mathf.Abs(p.x) > StormVolume.L || Mathf.Abs(p.y) > StormVolume.L) continue;
            float tau = StormLightCheck.Probe(r.Volume.LightTex, r.Volume.LN, r.Volume.LZ, p.x, p.y, 0.05f).x;
            var m = r.SunReach; float a = 0f, a0 = 0f, b = 1f, k = 0.38f;
            for (int o = 0; o < (int)m.x && o < 4; o++) { a += b * Mathf.Exp(-k * tau); a0 += b; b *= 0.5f; k *= m.y; }
            s *= m.x > 0.5f ? (a + m.w * (1f - Mathf.Exp(-0.1f * tau)) / (1f + m.z * Mathf.Max(tau, 0f))) / Mathf.Max(a0, 1e-4f) : Mathf.Exp(-0.38f * tau);
        }
        return s;
    }

    /// <summary>The light grid and the sky visibility up through a storm's wall cloud, its main base (under the updraft)
    /// and the base beside the wall cloud, with the sky light each height takes (stormLight's sky term, luminance in the
    /// storm's units): what it sees directly, and what diffuses down through the cloud overhead.</summary>
    static void Grids(StormRenderer r, int i)
    {
        var F = r.Frame.param; var vol = r.Volume;
        SkyLighting.Bands(r.Lighting, out var zen, out var mid, out var hor, out var horK);
        float Lum(Vector4 c) => 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;
        float lz = Lum(zen), lm = Lum(mid), lh = Lum(hor), lk = Lum(horK), mean = 0.18f * lz + 0.477f * lm + 0.342f * lh;
        var wc = new Vector2(F[24], F[25]); var up = new Vector2(F[64], F[65]); float Rw = F[26];
        var d = (up - wc).sqrMagnitude > 1e-4f ? (up - wc).normalized : Vector2.right; var side = new Vector2(-d.y, d.x);
        var beside = wc + side * (Rw + 1.5f);
        rep.AppendLine($"### Storm {i + 1}: light through the wall cloud").AppendLine();
        rep.AppendLine($"Base {F[4]:0.00} km, lowering {F[28]:0.00} km (the wall cloud's foot at {F[4] - F[28]:0.00} km), wall cloud radius {Rw:0.00} km, amount {F[27]:0.00}; {Vector2.Distance(up, wc):0.0} km from the updraft's centre. " +
            $"Grids: cloud {2f * StormVolume.L / vol.NV:0.00} x {StormVolume.ZT / vol.NZ:0.000} km, light {2f * StormVolume.L / vol.LN:0.00} x {StormVolume.ZT / vol.LZ:0.000} km, sky visibility {2f * StormVolume.L / vol.SN:0.00} x {StormVolume.ZT / vol.SZ:0.000} km (across x up). " +
            $"Sky bands (luminance): cap {lz:0.000}, ring {lm:0.000}, horizon {lh:0.000}, sunward horizon {lk:0.000}; sky diffuse {r.skyDiffuse:0.00}, diffusion {r.diffusion:0.00}, rain's share for light {r.RainLightShare:0.00}.").AppendLine();
        rep.AppendLine("| column | km up | cloud | rain | near field | sun depth | depth overhead | sky cap | ring | horizon | sunward | sky seen | sky diffused | sky light |");
        rep.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var (n, p) in new[] { ("wall cloud", wc), ("base beside it", beside), ("main base", up) })
            foreach (var z in new[] { 0.2f, 0.4f, 0.6f, 0.8f, 1.0f, 1.2f, 1.4f, 1.6f, 1.8f, 2.0f, 2.4f, 2.8f })
            {
                var l = StormLightCheck.Probe(vol.LightTex, vol.LN, vol.LZ, p.x, p.y, z);
                var sv = StormLightCheck.Probe(vol.SkyVisTex, vol.SN, vol.SZ, p.x, p.y, z);
                var dn = StormLightCheck.Probe(vol.Dens, vol.NV, vol.NZ, p.x, p.y, z, TextureFormat.RGBA32);
                float upDepth = -10f * Mathf.Log(Mathf.Max(l.y, 1e-12f));
                float seen = 0.5f * (0.18f * lz * sv.x + 0.477f * lm * sv.y + 0.342f * (lh * sv.z + 0.25f * sv.w * (lk - lh))) * r.skyScale;
                float diff = 0.5f * mean * r.skyDiffuse / (1f + r.diffusion * Mathf.Max(upDepth, 0f)) * r.skyScale;
                rep.AppendLine($"| {n} | {z:0.0} | {dn.x:0.00} | {dn.y:0.00} | {dn.z:0.00} | {l.x:0.0} | {upDepth:0.0} | {sv.x:0.000} | {sv.y:0.000} | {sv.z:0.000} | {sv.w:0.000} | {seen:0.0000} | {diff:0.0000} | {seen + diff:0.0000} |");
            }
        rep.AppendLine();
    }

    /// <summary>What is overhead out on the inflow side (the anvil): the cloud up through the column 10, 20 and 40 km
    /// from the updraft, the sun's depth and share at the ground there, and the sky the ground sees (the scene's ambient
    /// and the exposure follow the last; the scene's sun the first).</summary>
    static void Overhead(StormRenderer r, int i)
    {
        var vol = r.Volume;
        Vector3 u = r.UpdraftWorld(r.StormTime), u1 = r.UpdraftWorld(r.StormTime + 2), u0 = r.UpdraftWorld(r.StormTime - 2);
        float b = (Mathf.Atan2(u1.x - u0.x, u1.z - u0.z) * Mathf.Rad2Deg + 45f) * Mathf.Deg2Rad;
        rep.AppendLine($"### Storm {i + 1}: overhead on the inflow side").AppendLine();
        rep.AppendLine("| km out | cloud at 4, 6, 8, 9, 10, 11, 12, 13, 14, 16 km up | sun depth at the ground | sky cap | ring | horizon | sky seen (ambient) |");
        rep.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (float km in new[] { 10f, 20f, 40f })
        {
            var p = r.WorldToStorm(new Vector3(u.x + Mathf.Sin(b) * km * 1000f, u.y, u.z + Mathf.Cos(b) * km * 1000f));
            var col = string.Join(", ", new[] { 4f, 6f, 8f, 9f, 10f, 11f, 12f, 13f, 14f, 16f }.Select(z => StormLightCheck.Probe(vol.Dens, vol.NV, vol.NZ, p.x, p.y, z, TextureFormat.RGBA32).x.ToString("0.00")));
            var l = StormLightCheck.Probe(vol.LightTex, vol.LN, vol.LZ, p.x, p.y, 0.05f);
            var sv = StormLightCheck.Probe(vol.SkyVisTex, vol.SN, vol.SZ, p.x, p.y, 0.05f);
            rep.AppendLine($"| {km:0} | {col} | {l.x:0.0} | {sv.x:0.000} | {sv.y:0.000} | {sv.z:0.000} | {Mathf.Clamp01(0.18f * sv.x + 0.477f * sv.y + 0.342f * sv.z):0.000} |");
        }
        rep.AppendLine();
    }

    static void Sheet(string[] names, string outName)
    {
        if (names.Length == 0) return;
        int cw = 600, ch = 250, cols = 2, rows = (names.Length + 1) / 2;
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

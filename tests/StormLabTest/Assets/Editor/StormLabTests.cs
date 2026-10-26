// Headless render checks of the storm (never run with -nographics: the passes need a GPU).
//   StormLab > Render Storm Checks
// Batch: -executeMethod StormLabTests.RunFromCommandLine [-slQuick] [-slCompare <cases.json>] [-slOut <dir>]
//
// Default: the plains storm (seed 42) from the web build's opening view at five moments, in both lighting models; a
// dusk, a moonlit night and a night with a lightning flash (physical lighting); and an LP and an HP storm. Writes
// Screenshots/storm_*.png and validation/reports/StormChecks.md (frame times, what the lightning did).
// -slCompare renders the cases a website capture wrote (validation/website/capture.js: preset, seed, storm time and
// the exact camera) with the web build's lighting, as storm_cmp_<name>.png beside the website's web_<name>.png.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using StormLab;
using StormLab.Lightning;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

public static class StormLabTests
{
    static string ShotDir => Path.Combine(StormLabTestKit.ProjectDir, "Screenshots");
    static string ReportPath => Path.Combine(StormLabTestKit.RepoRoot, "validation", "reports", "StormChecks.md");

    /// <summary>The web build's Camera: azimuth round the target (0 = east of it), distance and height km, the height it
    /// looks at, the target km in the storm frame.</summary>
    [Serializable] public class WebCam { public float az, dist, h, tz, tx, ty; }
    [Serializable] public class Case
    {
        public string name, preset = "plains"; public int seed = 42; public bool varied = true; public float t;
        public WebCam cam; public int w = 1600, h = 1000;
    }
    [Serializable] class CaseList { public Case[] cases; }

    [MenuItem("StormLab/Render Storm Checks")]
    public static void RunMenu() => Run(false, null, null);

    public static void RunFromCommandLine()
    {
        var a = System.Environment.GetCommandLineArgs();
        string Arg(string k) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        bool ok;
        try { ok = Run(a.Contains("-slQuick"), Arg("-slCompare"), Arg("-slOut")); }
        catch (Exception e) { Debug.LogException(e); ok = false; }
        EditorApplication.Exit(ok ? 0 : 1);
    }

    static StormRenderer storm; static Camera cam;

    static void Setup()
    {
        StormLabTestKit.EnsurePipeline();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        ShaderUtil.allowAsyncCompilation = false;
        var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
        cam = camGo.AddComponent<Camera>();
        cam.nearClipPlane = 1f; cam.farClipPlane = 400000f; cam.fieldOfView = 64f;   // the web build's 32 degree half angle
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
        StormLabTestKit.SetupCamera(camGo);
        camGo.AddComponent<AudioListener>();
        var go = new GameObject("Storm");
        storm = go.AddComponent<StormRenderer>();
        storm.UseStandaloneLook();
        storm.playing = false; storm.renderScale = 1f; storm.temporalBlend = 1f; storm.checkerboard = false;
        storm.sunSource = SunSource.Fixed; storm.sunAzimuth = 272f; storm.sunElevation = 21f;
    }

    /// <summary>Place the camera as the web build's cameraBasis does (storm frame km, looking at the target).</summary>
    static void Aim(WebCam c, float tMin)
    {
        float az = c.az * Mathf.Deg2Rad;
        var cp = new Vector3(c.tx + c.dist * Mathf.Cos(az), c.ty + c.dist * Mathf.Sin(az), c.h);
        var tgt = new Vector3(c.tx, c.ty, c.tz);
        var wp = storm.StormToWorld(cp, tMin); var wt = storm.StormToWorld(tgt, tMin);
        cam.transform.SetPositionAndRotation(wp, Quaternion.LookRotation(wt - wp, Vector3.up));
    }

    static void Show(Case k)
    {
        if (storm.preset != k.preset || storm.seed != k.seed || storm.varied != k.varied || storm.Plan == null)
        { storm.preset = k.preset; storm.seed = k.seed; storm.varied = k.varied; storm.Replan(); }
        storm.StepTo(k.t, 100f, true);
        StormLabTestKit.Sync();
        if (k.cam != null) Aim(k.cam, k.t);
        storm.ResetHistory(cam);
    }

    static double Shot(string file, Case k, int frames = 3)
    {
        var sw = Stopwatch.StartNew();
        StormLabTestKit.Render(cam, Path.Combine(ShotDir, file), k.w, k.h, frames);
        StormLabTestKit.Sync();
        return sw.Elapsed.TotalMilliseconds / frames;
    }

    /// <summary>The wall cloud's underside, the web build's and the game's smooth one, side by side: a tornado seen from a
    /// few km off at a low evening sun (as a chaser sees it) and from right under the base. Screenshots/wall_*.png and
    /// wall_sheet.png (rows: views; left the web build's base, right the smooth one).</summary>
    static void WallBases(StringBuilder rep)
    {
        storm.lighting = StormLighting.Physical; storm.tornado = TornadoModel.Detailed;
        storm.sunAzimuth = 294f; storm.sunElevation = 4f;
        rep.AppendLine("## Wall-cloud underside: web build's (left) and smooth (right)").AppendLine();
        var tiles = new List<string>();
        foreach (var (seed, tMin) in new[] { (116865, 47.67f), (42, -1f) })
        {
            storm.preset = "plains"; storm.varied = true; storm.seed = seed; storm.Replan();
            float t = tMin >= 0 ? tMin : (float)storm.Plan.cycles[0].times[3];
            foreach (var (view, dist, h, look) in new[] { ("chaser", 5.5f, 0.002f, 1.1f), ("under", 2.2f, 0.002f, 1.6f) })
                foreach (var wb in new[] { WallBase.Website, WallBase.Smooth })
                {
                    storm.wallBase = wb;
                    var k = new Case { seed = seed, t = t }; Show(k);
                    var tor = new Vector2(storm.Frame.param[52], storm.Frame.param[53]);
                    // looking 58 deg (north-east), as the screenshot that asked for this
                    var dir = new Vector2(Mathf.Sin(58f * Mathf.Deg2Rad), Mathf.Cos(58f * Mathf.Deg2Rad));
                    var c = tor - dir * dist;
                    k.cam = new WebCam { az = Mathf.Atan2(c.y - tor.y, c.x - tor.x) * Mathf.Rad2Deg, dist = dist, h = h, tz = look, tx = tor.x, ty = tor.y };
                    Aim(k.cam, t); storm.ResetHistory(cam);
                    string f = $"wall_{seed}_{view}_{wb.ToString().ToLowerInvariant()}.png";
                    Shot(f, new Case { w = 1200, h = 675 }, 4);
                    tiles.Add(f);
                }
            rep.AppendLine($"- plains seed {seed} at {t:0.0} min: {storm.Frame.stage}");
        }
        Sheet(tiles, 2, Path.Combine(ShotDir, "wall_sheet.png"));
        rep.AppendLine().AppendLine("Sheet: `Screenshots/wall_sheet.png`.");
    }

    /// <summary>The opening view of the web build (CameraRig az -70, 24 km, 0.1 km up, looking 2.9 km up) at the updraft.</summary>
    static WebCam Opening()
    {
        var u = storm.Plan.Updraft;
        return new WebCam { az = -70, dist = 24, h = 0.1f, tz = 2.9f, tx = (float)u.x, ty = (float)u.y };
    }

    public static bool Run(bool quick, string compare, string outDir)
    {
        Setup();
        var rep = new StringBuilder("# Storm checks\n\n");
        rep.AppendLine($"Generated by `StormLabTests` on {DateTime.Now:yyyy-MM-dd HH:mm}, {SystemInfo.graphicsDeviceName}, {SystemInfo.graphicsDeviceType}.").AppendLine();
        bool ok = true;
        if (outDir != null) Directory.CreateDirectory(outDir);
        string Out(string f) => outDir != null ? Path.Combine(outDir, f) : f;

        if (System.Environment.GetCommandLineArgs().Contains("-slTornadoes"))
        {
            ok &= TornadoSheets(rep);
            File.WriteAllText(Path.Combine(StormLabTestKit.RepoRoot, "validation", "reports", "TornadoChecks.md"), rep.ToString());
            return ok;
        }
        var argv = System.Environment.GetCommandLineArgs();
        int li = Array.IndexOf(argv, "-slTornadoLife");
        if (li >= 0 && li + 3 < argv.Length)
            return TornadoLife(argv[li + 1], int.Parse(argv[li + 2]), int.Parse(argv[li + 3]), li + 4 < argv.Length && !argv[li + 4].StartsWith("-") ? argv[li + 4] : "");
        if (System.Environment.GetCommandLineArgs().Contains("-slWall"))
        {
            WallBases(rep);
            File.WriteAllText(Path.Combine(StormLabTestKit.RepoRoot, "validation", "reports", "WallBaseChecks.md"), rep.ToString());
            return ok;
        }
        if (compare != null)
        {
            var list = JsonUtility.FromJson<CaseList>(File.ReadAllText(compare));
            storm.lighting = StormLighting.Website; storm.tornado = TornadoModel.Website; storm.wallBase = WallBase.Website; storm.mesoSpinScale = 1;
            rep.AppendLine("## Website comparison").AppendLine();
            foreach (var k in list.cases)
            {
                // the capture's keys are the web build's presets (its "lp" is not the game's)
                if (!k.preset.StartsWith(StormPreset.WebPrefix)) k.preset = StormPreset.WebPrefix + k.preset;
                Show(k);
                double ms = Shot(Out($"storm_cmp_{k.name}.png"), k);
                rep.AppendLine($"- {k.name}: {k.preset} seed {k.seed} at {k.t:0.#} min ({storm.Frame.stage}), {ms:0} ms a frame");
            }
            File.WriteAllText(ReportPath, rep.ToString());
            return ok;
        }

        storm.preset = "plains"; storm.seed = 42; storm.varied = true; storm.Replan();
        var P = storm.Plan;
        var times = quick ? new[] { 45f } : new[] { 12f, 22f, 36f, (float)P.cycles[0].times[3], (float)P.cycles[1].times[3] };
        rep.AppendLine($"## Plains, seed 42 ({string.Join(", ", P.cycles.Select(c => c.shape))}; {P.duration:0} min)").AppendLine();
        rep.AppendLine("| storm min | stage | lighting | ms/frame (1600 x 1000, full res) |").AppendLine("| --- | --- | --- | --- |");
        foreach (var t in times)
            foreach (var lm in new[] { StormLighting.Website, StormLighting.Physical })
            {
                storm.lighting = lm;
                var k = new Case { t = t };
                Show(k); k.cam = Opening(); Aim(k.cam, t);
                double ms = Shot($"storm_plains_{t:000}_{lm.ToString().ToLowerInvariant()}.png", k);
                rep.AppendLine($"| {t:0.0} | {storm.Frame.stage} | {lm} | {ms:0} |");
            }
        rep.AppendLine();

        // closer, under the base at the first tornado
        {
            storm.lighting = StormLighting.Physical;
            float t = (float)P.cycles[0].times[3];
            var k = new Case { t = t }; Show(k);
            var tor = new Vector2(storm.Frame.param[52], storm.Frame.param[53]);
            k.cam = new WebCam { az = -100, dist = 6, h = 0.05f, tz = 0.7f, tx = tor.x, ty = tor.y }; Aim(k.cam, t);
            Shot("storm_plains_tornado_close_physical.png", k);
            storm.lighting = StormLighting.Website; Show(k); Aim(k.cam, t);
            Shot("storm_plains_tornado_close_website.png", k);
        }

        if (!quick)
        {
            // other setups at their mature stage
            storm.lighting = StormLighting.Physical;
            foreach (var pre in new[] { "lp", "dixie", "ohio" })
            {
                storm.preset = pre; storm.seed = 42; storm.varied = true; storm.Replan();
                float t = storm.Plan.cycles != null ? (float)storm.Plan.cycles[0].times[3] : 50f;
                var k = new Case { preset = pre, t = t }; Show(k); k.cam = Opening(); Aim(k.cam, t);
                Shot($"storm_{pre}_physical.png", k);
                rep.AppendLine($"- {pre}: {storm.Plan.kind}, at {t:0.0} min: {storm.Frame.stage}");
            }
            rep.AppendLine();
        }

        // dusk and night (physical lighting with the night model), and a lightning flash at night
        storm.preset = "plains"; storm.seed = 42; storm.varied = true; storm.Replan();
        storm.lighting = StormLighting.Physical;
        {
            float t = (float)P.cycles[0].times[2] - 3f;   // the lightning jump before the first tornado
            var k = new Case { t = t };
            foreach (var (name, el) in new[] { ("dusk", -2f), ("night", -20f) })
            {
                storm.sunElevation = el; storm.moonAzimuth = 120f; storm.moonElevation = 35f; storm.moonPhase = 0.5f;
                Show(k); k.cam = Opening(); Aim(k.cam, t);
                Shot($"storm_plains_{name}.png", k);
            }
            ok &= LightningShot(k, rep);
            storm.sunElevation = 21f;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, rep.ToString());
        Debug.Log($"[StormLab] storm checks {(ok ? "PASS" : "FAIL")}; report {ReportPath}");
        return ok;
    }

    // ------------------------------------------------------------------ tornadoes (-slTornadoes)
    /// <summary>A compass bearing (degrees) as a horizontal world direction.</summary>
    static Vector3 Bearing(float deg) => new Vector3(Mathf.Sin(deg * Mathf.Deg2Rad), 0f, Mathf.Cos(deg * Mathf.Deg2Rad));

    static Texture2D Load(string path) { var t = new Texture2D(2, 2); t.LoadImage(File.ReadAllBytes(path)); return t; }

    /// <summary>Tiles (all the same size) into a sheet of `cols` columns, first tile top left.</summary>
    static void Sheet(List<string> tiles, int cols, string path)
    {
        var first = Load(tiles[0]); int w = first.width, h = first.height; Object.DestroyImmediate(first);
        int rows = (tiles.Count + cols - 1) / cols;
        var sheet = new Texture2D(w * cols, h * rows, TextureFormat.RGB24, false);
        for (int i = 0; i < tiles.Count; i++)
        {
            var t = Load(tiles[i]);
            sheet.SetPixels(i % cols * w, (rows - 1 - i / cols) * h, w, h, t.GetPixels());
            Object.DestroyImmediate(t);
        }
        sheet.Apply();
        File.WriteAllBytes(path, sheet.EncodeToPNG());
        Object.DestroyImmediate(sheet);
    }

    /// <summary>
    /// One tornado through its life (-slTornadoLife preset seed cycle [tag]): the detailed tornado of that cycle at six
    /// points of its life, from a chaser 140 degrees round from it (as the sheets), the game's half-resolution blended march
    /// and a 15 degree evening sun. Sheet: Screenshots/storm_life_{preset}_{seed}_{cycle}{_tag}.png; the stage and funnel size
    /// of each shot go to the log. A tag containing "early" shows the first 30 % of its life instead, "rope" the last 40 %.
    /// </summary>
    static bool TornadoLife(string preset, int seed, int k, string tag)
    {
        storm.lighting = StormLighting.Physical; storm.tornado = TornadoModel.Detailed;
        storm.renderScale = 0.5f; storm.temporalBlend = 0.25f; storm.sunAzimuth = 272f; storm.sunElevation = 15f;
        // "sharp": one full-resolution frame, not blended, from half as far (to see what the march draws)
        bool sharp = tag.Contains("sharp");
        if (sharp) { storm.renderScale = 1f; storm.temporalBlend = 1f; }
        storm.preset = preset; storm.varied = true; storm.seed = seed; storm.Replan();
        var tg = storm.Tornadoes != null && k < storm.Tornadoes.Length ? storm.Tornadoes[k] : null;
        if (tg == null) { Debug.LogError($"[StormLife] {preset} seed {seed} cycle {k}: no tornado"); return false; }
        Debug.Log($"[StormLife] {preset} seed {seed} cycle {k}: EF{tg.ef} {tg.shape}, ground {tg.groundKm * 1000:0} m, top {tg.topKm * 1000:0} m, {tg.skin}, roughness {tg.roughness:0.00}, sub-vortices {tg.subVortices}, phases {string.Join(" > ", tg.phases.Select(p => p.shape))}");
        var tiles = new List<string>();
        // a tag with "early" in it shows its formation instead (the first 30 % of its life)
        // ("rope": a wedge narrowing before its rope-out, then the rope-out)
        float r0 = (float)StormTornadoes.RopeStart(tg), rs = (float)StormTornadoes.ShrinkSpan(tg), rf = (float)tg.ropeFrac;
        var fracs = tag.Contains("early") ? new[] { 0.005f, 0.03f, 0.07f, 0.12f, 0.19f, 0.3f }
            : tag.Contains("rope") ? new[] { r0 - rs - 0.01f, r0 - 0.5f * rs, r0, r0 + 0.25f * rf, r0 + 0.5f * rf, r0 + 0.8f * rf }
            : new[] { 0.05f, 0.15f, 0.3f, 0.45f, 0.6f, 0.8f };
        foreach (float f in fracs)
        {
            float T = (float)(tg.formMin + f * tg.LifeMin);
            storm.StepTo(T, 100f, true); StormLabTestKit.Sync();
            var g = storm.TornadoGroundWorld; g.y = storm.origin.y;
            float zb = (float)(storm.Plan.zb - StormDesign.CLOUD_BASE_LIFT_KM);
            float D = Mathf.Clamp((float)(tg.groundKm * 9000), 3000f, 6000f) * (sharp ? 0.5f : 1f);
            var from = g + Bearing(140f) * D + Vector3.up * 2f;
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(g + Vector3.up * zb * 450f - from, Vector3.up));
            cam.fieldOfView = 2f * Mathf.Atan(zb * 1000f * 1.25f / D) * Mathf.Rad2Deg;
            storm.ResetHistory(cam);
            string path = Path.Combine(ShotDir, $"storm_life_{tiles.Count + 1}.png");
            StormLabTestKit.Render(cam, path, 960, 540, sharp ? 1 : 12);
            tiles.Add(path);
            Debug.Log($"[StormLife] {f:0.000} of its life, {T:0.0} min: {storm.Frame.stage}; funnel ground {storm.Frame.param[14 * 4] * 1000:0} m, top {storm.Frame.param[14 * 4 + 1] * 1000:0} m, envelope mode {storm.Frame.param[21 * 4 + 3]:0.00}");
        }
        string sheet = Path.Combine(ShotDir, $"storm_life_{preset}_{seed}_{k}{(tag != "" ? "_" + tag : "")}.png");
        Sheet(tiles, 3, sheet);
        Debug.Log($"[StormLife] sheet {sheet}");
        return true;
    }

    /// <summary>
    /// 2.0-style tornado renders, for the detailed tornado (StormLab 2.0's, on the web build's cycles):
    ///   storm_tornado_sheet.png    all nine shapes forced at 2.0's widths and ratings, framed as 2.0's designed_tornado_sheet
    ///   storm_tornado_close.png    six of them close, the sun 60 degrees to the viewer's side
    ///   storm_tornado_variety.png  unforced tornadoes of several setups and seeds, 0.45 of the way through their lives
    ///   storm_tornado_ab_*.png     the web build's tornado beside the detailed one at the same moments
    /// </summary>
    static bool TornadoSheets(StringBuilder rep)
    {
        bool ok = true;
        storm.lighting = StormLighting.Physical; storm.tornado = TornadoModel.Detailed;
        // as the game draws it (and as 2.0's sheets were made): a half-resolution march blended over frames, which smooths the
        // funnel's fine skin; one unblended full-resolution frame shows its finest octave as a saw-toothed edge
        storm.renderScale = 0.5f; storm.temporalBlend = 0.25f;
        // 2.0's sheet: its evening sun, 15 degrees up in the west
        storm.sunAzimuth = 272f; storm.sunElevation = 15f;
        rep.AppendLine("## Tornado shapes (2.0's sheet: forced shapes at 2.0's widths and ratings)").AppendLine();
        rep.AppendLine("| Shape | EF | Width | Funnel ground / top now | Skin | Shot |").AppendLine("| --- | --- | --- | --- | --- | --- |");
        var widths = new Dictionary<FunnelShape, (double w, int ef)>
        {
            { FunnelShape.Rope, (90, 1) }, { FunnelShape.Cone, (350, 2) }, { FunnelShape.Stovepipe, (450, 3) }, { FunnelShape.Wedge, (1400, 4) },
            { FunnelShape.MultiVortex, (900, 3) }, { FunnelShape.Drillbit, (140, 1) }, { FunnelShape.ElephantTrunk, (220, 2) }, { FunnelShape.DustWhirl, (180, 1) },
            { FunnelShape.Sidewinder, (110, 1) },
        };
        var closeShapes = new[] { FunnelShape.Rope, FunnelShape.Cone, FunnelShape.Stovepipe, FunnelShape.Wedge, FunnelShape.MultiVortex, FunnelShape.ElephantTrunk };
        var tiles = new List<string>(); var close = new List<string>();
        // the teaching sequence: circulation 0 draws a plain funnel, circulation 1 the web build's multiple-vortex takeover
        storm.preset = "plains"; storm.varied = false; storm.Replan();
        var P = storm.Plan;
        foreach (FunnelShape shape in Enum.GetValues(typeof(FunnelShape)))
        {
            var (w, ef) = widths[shape];
            int k = shape == FunnelShape.MultiVortex ? 1 : 0;
            var forced = StormTornadoes.Forced(P, ClimoRegion.Plains, k, shape, w, ef);
            if (forced == null) { ok = false; rep.AppendLine($"FAIL: no circulation {k} tornado to reshape for {shape}"); continue; }
            storm.Replan();
            var chars = storm.Tornadoes; chars[k] = forced; storm.Tornadoes = chars;
            float frac = shape == FunnelShape.MultiVortex ? 0.25f : shape == FunnelShape.Wedge ? 0.55f : 0.4f;
            float T = (float)(forced.formMin + frac * forced.LifeMin);
            storm.StepTo(T, 100f, true); StormLabTestKit.Sync();
            var g = storm.TornadoGroundWorld; g.y = storm.origin.y;
            float zb = (float)(P.zb - StormDesign.CLOUD_BASE_LIFT_KM);
            float groundNow = storm.Frame.param[14 * 4], topNow = storm.Frame.param[14 * 4 + 1];
            // a chaser in the inflow, south-east of the tornado, 3-6 km off by its size (2.0's framing)
            float D = Mathf.Clamp((float)(forced.groundKm * 9000), 3000f, 6000f);
            var from = g + Bearing(140f) * D + Vector3.up * 2f;
            cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(g + Vector3.up * zb * 450f - from, Vector3.up));
            cam.fieldOfView = 2f * Mathf.Atan(zb * 1000f * 1.25f / D) * Mathf.Rad2Deg;
            storm.ResetHistory(cam);
            string path = Path.Combine(ShotDir, $"storm_tornado_{shape}.png");
            StormLabTestKit.Render(cam, path, 960, 540, 12);
            tiles.Add(path);
            if (storm.TornadoAmount <= 0.15f) { ok = false; rep.AppendLine($"FAIL: {shape}: no tornado on the ground at {T:0.0} min"); }
            rep.AppendLine($"| {shape} | EF{forced.ef} | {w:0} m | {groundNow * 1000:0} / {topNow * 1000:0} m | {forced.skin}, albedo {forced.albedo:0.00} | `storm_tornado_{shape}.png` |");
            if (closeShapes.Contains(shape))
            {
                // close, with the sun 60 degrees to the viewer's side, so the lit side and the shading across it show
                float Dc = Mathf.Clamp(topNow * 5000f, 900f, 3500f);
                var fc = g + Bearing(storm.sunAzimuth + 60f) * Dc + Vector3.up * 2f;
                cam.transform.SetPositionAndRotation(fc, Quaternion.LookRotation(g + Vector3.up * zb * 420f - fc, Vector3.up));
                cam.fieldOfView = 2f * Mathf.Atan(zb * 1000f * 1.1f / Dc) * Mathf.Rad2Deg;
                storm.ResetHistory(cam);
                string pc = Path.Combine(ShotDir, $"storm_tornado_close_{shape}.png");
                StormLabTestKit.Render(cam, pc, 960, 540, 12);
                close.Add(pc);
            }
        }
        Sheet(tiles, 3, Path.Combine(ShotDir, "storm_tornado_sheet.png"));
        Sheet(close, 3, Path.Combine(ShotDir, "storm_tornado_close.png"));
        rep.AppendLine().AppendLine("Sheets: `storm_tornado_sheet.png` (Rope, Cone, Stovepipe / Wedge, MultiVortex, Drillbit / ElephantTrunk, DustWhirl, Sidewinder, as 2.0's `designed_tornado_sheet.png`) and `storm_tornado_close.png` (Rope, Cone, Stovepipe / Wedge, MultiVortex, ElephantTrunk). The dust whirl is on the Plains setup's low base here (2.0 used a High Plains dryline day).").AppendLine();

        // unforced tornadoes: whatever the character draws on the web build's cycles
        rep.AppendLine("## Variety (unforced)").AppendLine().AppendLine("| Tile | Setup | Seed | Cycle | Tornado | Look | Life |").AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        var variety = new List<string>();
        foreach (var (pre, seeds) in new[] { ("plains", new[] { 1, 2, 3, 4 }), ("dixie", new[] { 5 }), ("ohio", new[] { 6 }) })
            foreach (int sd in seeds)
            {
                storm.preset = pre; storm.varied = true; storm.seed = sd; storm.Replan();
                var ch = storm.Tornadoes; if (ch == null) continue;
                var tg = ch.FirstOrDefault(x => x != null);
                if (tg == null) continue;
                float T = (float)(tg.formMin + 0.45 * tg.LifeMin);
                storm.StepTo(T, 100f, true); StormLabTestKit.Sync();
                var g = storm.TornadoGroundWorld; g.y = storm.origin.y;
                float zb = (float)(storm.Plan.zb - StormDesign.CLOUD_BASE_LIFT_KM);
                float D = Mathf.Clamp((float)(tg.groundKm * 9000), 3000f, 6000f);
                var from = g + Bearing(140f) * D + Vector3.up * 2f;
                cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(g + Vector3.up * zb * 450f - from, Vector3.up));
                cam.fieldOfView = 2f * Mathf.Atan(zb * 1000f * 1.25f / D) * Mathf.Rad2Deg;
                storm.ResetHistory(cam);
                string path = Path.Combine(ShotDir, $"storm_tornado_variety_{variety.Count + 1}.png");
                StormLabTestKit.Render(cam, path, 960, 540, 12);
                variety.Add(path);
                string phases = string.Join(" > ", tg.phases.Select(p => p.shape));
                rep.AppendLine($"| {variety.Count} | {pre} | {sd} | {tg.cycle} | EF{tg.ef} {tg.shape} (web: {tg.webShape}), {tg.widthM:0} m | {tg.skin}, albedo {tg.albedo:0.00}, {tg.soilName} soil{(tg.subVortices > 0 ? $", {tg.subVortices} suction vortices" : "")}{(tg.satellite ? ", satellite" : "")} | {tg.LifeMin:0.0} min: {phases} |");
            }
        if (variety.Count > 0) Sheet(variety, 3, Path.Combine(ShotDir, "storm_tornado_variety.png"));
        rep.AppendLine().AppendLine("Sheet: `storm_tornado_variety.png`, left to right, top to bottom.").AppendLine();

        // the web build's tornado beside the detailed one: plains, seed 42, at both of its first tornadoes, from 6 km
        rep.AppendLine("## The web build's tornado and the detailed one").AppendLine();
        storm.preset = "plains"; storm.varied = true; storm.seed = 42; storm.Replan();
        storm.sunElevation = 21f;
        var ab = new List<string>();
        foreach (int k in new[] { 0, 1 })
        {
            var tg = storm.Tornadoes[k]; if (tg == null) continue;
            float T = (float)(tg.formMin + 0.3 * tg.LifeMin);
            foreach (var model in new[] { TornadoModel.Website, TornadoModel.Detailed })
            {
                storm.tornado = model;
                storm.StepTo(T, 100f, true); StormLabTestKit.Sync();
                var g = storm.TornadoGroundWorld; g.y = storm.origin.y;
                float zb = (float)(storm.Plan.zb - StormDesign.CLOUD_BASE_LIFT_KM);
                var from = g + Bearing(140f) * 6000f + Vector3.up * 2f;
                cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(g + Vector3.up * zb * 450f - from, Vector3.up));
                cam.fieldOfView = 2f * Mathf.Atan(zb * 1000f * 1.25f / 6000f) * Mathf.Rad2Deg;
                storm.ResetHistory(cam);
                string path = Path.Combine(ShotDir, $"storm_tornado_ab_{k}_{model.ToString().ToLowerInvariant()}.png");
                StormLabTestKit.Render(cam, path, 960, 540, 12);
                ab.Add(path);
                if (model == TornadoModel.Detailed) rep.AppendLine($"- circulation {k} at {T:0.0} min ({storm.Frame.stage}): web build's {tg.webShape}; detailed EF{tg.ef} {tg.shape}, {tg.skin}, {tg.soilName} soil");
            }
        }
        if (ab.Count > 0) Sheet(ab, 2, Path.Combine(ShotDir, "storm_tornado_ab.png"));
        rep.AppendLine().AppendLine("Sheet: `storm_tornado_ab.png`, each row one moment: the web build's tornado left, the detailed one right.").AppendLine();
        storm.tornado = TornadoModel.Detailed; storm.renderScale = 1f; storm.temporalBlend = 1f;
        return ok;
    }

    /// <summary>Find a flash at night on the storm's own lightning clock and render it at its brightest.</summary>
    static bool LightningShot(Case k, StringBuilder rep)
    {
        rep.AppendLine("## Lightning").AppendLine();
        var sl = storm.gameObject.AddComponent<StormLightning>();
        sl.clock = LightningClock.Storm;
        if (!sl.Bind()) { rep.AppendLine("FAIL: no plan to bind").AppendLine(); return false; }
        var fl = sl.Flashes;
        fl.thunder = false;
        var act = fl.ActivityAtTime(k.t * 60.0);
        rep.AppendLine($"Activity at {k.t:0.0} min: {act}.").AppendLine();
        int flashes = 0; double best = 0, bestT = -1;
        fl.FlashStarted += _ => flashes++;
        // walk the lightning clock in 20 ms steps (no seeks) until a flash lights the cloud
        double t0 = k.t * 60.0, last = t0;
        // a ground flash, so the channel shows as well as the light it puts into the cloud
        for (double s = t0; s < t0 + 300.0; s += 0.02)
        {
            fl.SampleAt(s); last = s;
            float lit = 0f;
            foreach (var x in fl.Illumination) if (x.kind == FlashKind.NegativeGround || x.kind == FlashKind.PositiveGround) lit = Mathf.Max(lit, x.intensity);
            if (lit > best) { best = lit; bestT = s; }
            // each illumination sample is a quarter of its flash's intensity: 0.2 is a return stroke near its peak
            if (lit >= 0.2f) break;
        }
        // (a seek back would show the flash between strokes: only re-sample if the walk ran past the brightest moment)
        if (best > 0 && last != bestT) fl.SampleAt(bestT);
        rep.AppendLine($"Flashes started in the search: {flashes}; brightest ground flash {best:0.00} at {bestT / 60:0.00} min.").AppendLine();
        if (best <= 0) { rep.AppendLine("FAIL: no ground flash within five minutes of storm time").AppendLine(); Object.DestroyImmediate(sl); return false; }
        // from 12 km, looking at the flash's ground point
        var g = fl.Illumination.Where(x => x.kind != FlashKind.InCloud).OrderBy(x => x.position.y).First().position;
        var gs = storm.WorldToStorm(g);
        k.cam = new WebCam { az = -100, dist = 12, h = 0.05f, tz = 3f, tx = gs.x, ty = gs.y }; Aim(k.cam, (float)(bestT / 60.0));
        storm.ResetHistory(cam);
        storm.stormMinutes = (float)(bestT / 60.0);
        sl.Bridge.RefreshChannelOcclusion();
        Shot("storm_plains_night_lightning.png", k, 1);
        // what the cloud's lightning light was made of
        rep.AppendLine($"Adapter on the storm: {(storm.TransientLighting != null ? storm.TransientLighting.GetType().Name : "none")}; " +
            $"light field {(sl.Bridge.LightField ? $"{sl.Bridge.LightField.width}x{sl.Bridge.LightField.height}x{sl.Bridge.LightField.volumeDepth}" : "not made")}.");
        foreach (var x in fl.Illumination.Take(8)) rep.AppendLine($"- source {x.kind} {x.intensity:0.000} at storm frame {storm.WorldToStorm(x.position)} km");
        if (sl.Bridge.LightField)
        {
            var lf = sl.Bridge.LightField; float mx = 0;
            var req = UnityEngine.Rendering.AsyncGPUReadback.Request(lf, 0);
            req.WaitForCompletion();
            if (!req.hasError)
                for (int z = 0; z < lf.volumeDepth; z++)
                {
                    var d = req.GetData<ushort>(z);
                    for (int i = 0; i < d.Length; i++) if ((i & 3) != 3) mx = Mathf.Max(mx, Mathf.HalfToFloat(d[i]));
                }
            rep.AppendLine($"Light field peak radiance {mx:0.000}{(req.hasError ? " (readback failed)" : "")}.").AppendLine();
        }
        // and with the lightning off, the same moment, for comparison
        fl.enabled = false; sl.Bridge.enabled = false;
        Shot("storm_plains_night_nolightning.png", k, 1);
        Object.DestroyImmediate(sl.Bridge); Object.DestroyImmediate(fl); Object.DestroyImmediate(sl);
        rep.AppendLine("- `Screenshots/storm_plains_night_lightning.png`, `storm_plains_night_nolightning.png`").AppendLine();
        return true;
    }
}

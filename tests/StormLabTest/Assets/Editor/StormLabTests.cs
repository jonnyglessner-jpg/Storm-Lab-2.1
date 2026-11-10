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

    [MenuItem("StormLab/Tornado Cost Check")]
    public static void TornadoCostMenu() { Setup(); TornadoCost(); }

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
        storm.performance = StormPerformance.Custom;   // the quality fields below as set (the checks set their own)
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

    /// <summary>The wall cloud's tail cloud (the low inflow band feeding it), the web build's (left) and the game's (right,
    /// StormRenderer.wallBase Smooth): from beside the tail, down the tail into the wall cloud, from the south-east as a
    /// chaser sees it, and from its other side. Before touchdown and with a tornado. Screenshots/tail_*.png and tail_sheet.png.</summary>
    static void Tails(StringBuilder rep)
    {
        storm.lighting = StormLighting.Physical; storm.tornado = TornadoModel.Detailed; storm.wallBase = WallBase.Smooth;
        storm.sunAzimuth = 250f; storm.sunElevation = 25f;
        rep.AppendLine("## Tail cloud: web build's (left), the game's (middle) and none (right)").AppendLine();
        var tiles = new List<string>();
        storm.preset = "plains"; storm.varied = true; storm.seed = 42; storm.Replan();
        var cy = storm.Plan.cycles[0];
        foreach (var (when, t) in new[] { ("pre", (float)cy.times[2] - 0.5f), ("tor", (float)cy.times[3]) })
        {
            var k0 = new Case { seed = 42, t = t }; Show(k0);
            var a = storm.Frame.param;
            float zb = a[4];
            var wc = new Vector2(a[24], a[25]); float Rw = a[26];
            // the tail's direction, as StormCommon.hlsl nearField aims it
            var f = new Vector2(a[0], a[1]); var l = new Vector2(a[2], a[3]); var tail = new Vector2(a[32], a[33]);
            var root = new Vector2(a[64], a[65]) + f * 2.5f + l * 0.5f;
            float bend = 0.035f * (a[34] - 2f) * (a[34] - 2f) * 0.12f - 0.6f * Mathf.Sin(0.7f);
            var td = (root + tail * 2f + new Vector2(-tail.y, tail.x) * bend - wc).normalized;
            var nrm = new Vector2(-td.y, td.x);
            var join = wc + td * (Rw + 0.5f);
            var views = new (string name, Vector2 at, Vector2 from, float h, float tz)[]
            {
                ("side", join, join + nrm * 4.5f, 0.002f, zb - 0.1f),
                ("along", wc, wc + td * (Rw + 6f) + nrm * 1.0f, 0.002f, zb - 0.2f),
                ("chaser", wc + td * 1.0f, wc + td * 3f - nrm * 5f, 0.002f, zb - 0.1f),
                ("far side", join, join - nrm * 4.5f, 0.002f, zb - 0.1f),
            };
            foreach (var v in views)
                foreach (int mode in new[] { 0, 1, 2 })
                {
                    bool web = mode == 0; storm.webTail = web; storm.hideTail = mode == 2;
                    var k = new Case { seed = 42, t = t }; Show(k);
                    var d = v.from - v.at;
                    k.cam = new WebCam { az = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, dist = d.magnitude, h = v.h, tz = v.tz, tx = v.at.x, ty = v.at.y };
                    Aim(k.cam, t); storm.ResetHistory(cam);
                    string file = $"tail_{when}_{v.name.Replace(" ", "")}_{(mode == 0 ? "web" : mode == 1 ? "game" : "none")}.png";
                    Shot(file, new Case { w = 1200, h = 675 }, 4);
                    tiles.Add(Path.Combine(ShotDir, file));
                }
            rep.AppendLine($"- plains seed 42 at {t:0.0} min ({when}): {storm.Frame.stage}; wall cloud radius {Rw:0.00} km, lowering {a[28]:0.00} km, tail {a[30]:0.00}, base {zb:0.00} km");
        }
        storm.webTail = false; storm.hideTail = false;
        Sheet(tiles, 3, Path.Combine(ShotDir, "tail_sheet.png"));
        rep.AppendLine().AppendLine("Sheet: `Screenshots/tail_sheet.png`.");
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
        if (System.Environment.GetCommandLineArgs().Contains("-slTornadoCost")) return TornadoCost();
        int ri = Array.IndexOf(argv, "-slRain");
        if (ri >= 0)
        {
            ok &= RainSheets(rep, ri + 1 < argv.Length && !argv[ri + 1].StartsWith("-") ? argv[ri + 1] : "now");
            File.WriteAllText(Path.Combine(StormLabTestKit.RepoRoot, "validation", "reports", "RainChecks.md"), rep.ToString());
            return ok;
        }
        int ai = Array.IndexOf(argv, "-slAnvils");
        if (ai >= 0)
        {
            ok &= AnvilSheets(rep, ai + 1 < argv.Length && !argv[ai + 1].StartsWith("-") ? argv[ai + 1] : "plains", 24f);
            File.WriteAllText(Path.Combine(StormLabTestKit.RepoRoot, "validation", "reports", "AnvilChecks.md"), rep.ToString());
            return ok;
        }
        int si = Array.IndexOf(argv, "-slStructure");
        if (si >= 0)
        {
            ok &= StructureSheets(rep, si + 1 < argv.Length && !argv[si + 1].StartsWith("-") ? argv[si + 1] : "plains,lp,dixie,ohio");
            File.WriteAllText(Path.Combine(StormLabTestKit.RepoRoot, "validation", "reports", "StructureChecks.md"), rep.ToString());
            return ok;
        }
        if (System.Environment.GetCommandLineArgs().Contains("-slTail"))
        {
            Tails(rep);
            File.WriteAllText(Path.Combine(StormLabTestKit.RepoRoot, "validation", "reports", "TailChecks.md"), rep.ToString());
            return ok;
        }
        if (System.Environment.GetCommandLineArgs().Contains("-slWall"))
        {
            WallBases(rep);
            File.WriteAllText(Path.Combine(StormLabTestKit.RepoRoot, "validation", "reports", "WallBaseChecks.md"), rep.ToString());
            return ok;
        }
        if (compare != null)
        {
            var list = JsonUtility.FromJson<CaseList>(File.ReadAllText(compare));
            storm.lighting = StormLighting.Website; storm.tornado = TornadoModel.Website; storm.wallBase = WallBase.Website; storm.precip = PrecipLayout.Website; storm.spinRates = SpinRates.Website; storm.cloudDetail = CloudDetail.Website;
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

    // ------------------------------------------------------------------ rain (-slRain <tag>)
    /// <summary>The rear-flank rain round a tornado and the storm's light at noon and in the afternoon (user, 2026-11-08: a
    /// box missing from the RFD rain round the tornado; the rain too thick, the storm too dark at midday). Per setup at its
    /// first tornado: from the rear flank 7 km off looking at the tornado, from the inflow side 14 km off; suns 60 and 30
    /// degrees up. Screenshots/rain_&lt;tag&gt;.png: rows the setups, columns rear noon, rear afternoon, inflow noon, inflow
    /// afternoon. Also each view's mean brightness, in the report.</summary>
    static bool RainSheets(StringBuilder rep, string tag)
    {
        rep.AppendLine($"## Rain and midday light ({tag})").AppendLine();
        storm.tornado = TornadoModel.Detailed; storm.lighting = StormLighting.Unified; storm.structure = StructureModel.Environmental;
        storm.drawBackdrop = true;
        // (a probe of the unified lighting's diffused sun: -slDiffuseSun x)
        var argv = System.Environment.GetCommandLineArgs(); int di = Array.IndexOf(argv, "-slDiffuseSun");
        if (di >= 0 && di + 1 < argv.Length && float.TryParse(argv[di + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dsun))
        { storm.diffuseSun = dsun; rep.AppendLine($"diffuseSun {dsun}").AppendLine(); }
        int dfi = Array.IndexOf(argv, "-slDiffusion");
        if (dfi >= 0 && dfi + 1 < argv.Length && float.TryParse(argv[dfi + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dif))
        { storm.diffusion = dif; rep.AppendLine($"diffusion {dif}").AppendLine(); }
        const int W = 800, H = 500;
        var tiles = new List<string>();
        foreach (var pre in new[] { "plains", "dixie", "ohio" })
        {
            storm.preset = pre; storm.seed = 42; storm.varied = true; storm.Replan();
            var c0 = storm.Plan.cycles[0];
            float t = (float)(c0.times[3] + 1.5);
            var line = new StringBuilder($"- {pre} at {t:0.0} min: ");
            foreach (var (view, el) in new[] { ("rear", 60f), ("rear", 30f), ("inflow", 60f), ("inflow", 30f) })
            {
                storm.sunElevation = el; storm.sunAzimuth = 240f;
                var k = new Case { preset = pre, t = t, w = W, h = H };
                Show(k);
                var f = new Vector2(storm.Frame.param[0], storm.Frame.param[1]);
                float azF = Mathf.Atan2(f.y, f.x) * Mathf.Rad2Deg;
                var tor = new Vector2(storm.Frame.param[52], storm.Frame.param[53]);
                k.cam = view == "rear" ? new WebCam { az = azF + 200, dist = 7, h = 0.05f, tz = 1.2f, tx = tor.x, ty = tor.y }
                                       : new WebCam { az = azF - 70, dist = 14, h = 0.05f, tz = 1.6f, tx = tor.x, ty = tor.y };
                Aim(k.cam, t);
                string file = Path.Combine(ShotDir, $"rain_{tag}_{pre}_{view}_{el:0}.png");
                Shot(Path.GetFileName(file), k);
                tiles.Add(file);
                var img = Load(file); var px = img.GetPixels(); double sum = 0;
                for (int i = 0; i < px.Length; i += 7) sum += px[i].grayscale;
                Object.DestroyImmediate(img);
                line.Append($"{view} {el:0} deg mean {sum / (px.Length / 7.0):0.000}; ");
            }
            rep.AppendLine(line.ToString());
        }
        Sheet(tiles, 4, Path.Combine(ShotDir, $"rain_{tag}.png"));
        return true;
    }

    // ------------------------------------------------------------------ several storms' anvils (-slAnvils)
    /// <summary>Three storms side by side across their motion, their anvils overlapping, with the web build's layout and the
    /// old pairwise composite (structure Website) and with the game's (Environmental: its textures and the one-pass
    /// composite of every storm): from the ground 45 km off, from under the anvils, and from the air. Screenshots/
    /// anvils_sheet.png: rows the views, left the old, right the new.</summary>
    static bool AnvilSheets(StringBuilder rep, string pre, float spacingKm)
    {
        rep.AppendLine($"## Three storms' anvils ({pre}, {spacingKm:0} km apart)").AppendLine();
        storm.tornado = TornadoModel.Detailed; storm.lighting = StormLighting.Unified;
        storm.sunAzimuth = 250f; storm.sunElevation = 30f;
        storm.drawBackdrop = false;
        cam.backgroundColor = new Color(0.36f, 0.52f, 0.78f);
        var hd = cam.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
        if (hd) hd.backgroundColorHDR = new Color(0.36f, 0.52f, 0.78f);
        const float t = 75f;
        var storms = new List<StormRenderer> { storm };
        for (int i = 1; i < 3; i++)
        {
            var go = new GameObject($"Storm {i + 1}");
            var r = go.AddComponent<StormRenderer>();
            r.UseStandaloneLook(); r.drawBackdrop = false;
            r.performance = StormPerformance.Custom; r.playing = false; r.renderScale = 1f; r.temporalBlend = 1f; r.checkerboard = false;
            r.sunSource = SunSource.Fixed; r.sunAzimuth = storm.sunAzimuth; r.sunElevation = storm.sunElevation;
            r.tornado = storm.tornado; r.lighting = storm.lighting;
            storms.Add(r);
        }
        for (int i = 0; i < 3; i++)
        {
            var r = storms[i];
            r.drawAlongside = true; r.preset = pre; r.seed = 42 + 17 * i; r.varied = true; r.Replan();
        }
        var P = storm.Plan;
        // across the motion (its left), in world metres: x east, z north
        var lw = new Vector3((float)P.lft.x, 0, (float)P.lft.y).normalized;
        var fw = new Vector3((float)P.fwd.x, 0, (float)P.fwd.y).normalized;
        var tiles = new List<string>();
        var views = new (string name, Vector3 offKm, float hKm, Vector3 lookKm)[]
        {
            // from the inflow side, 45 km right of the motion: the three storms in a row, their anvils overlapping
            ("ground", -lw * 45 - fw * 5, 0.1f, fw * 10 + new Vector3(0, 7, 0)),
            // under the anvils downstream of the middle storm, looking up and across them
            ("under", fw * 28, 0.1f, fw * 32 + lw * 14 + new Vector3(0, 11, 0)),
            // from the air, upstream and above
            ("aerial", -fw * 70 - lw * 20, 20f, fw * 25),
        };
        foreach (var mode in new[] { StructureModel.Website, StructureModel.Environmental })
        {
            for (int i = 0; i < 3; i++)
            {
                storms[i].structure = mode;
                storms[i].PlaceUpdraftAt(lw * (spacingKm * 1000f * (i - 1)), t);
                storms[i].StepTo(t, 100f, true);
            }
            StormLabTestKit.Sync();
            foreach (var (name, offKm, hKm, lookKm) in views)
            {
                var basePos = storm.UpdraftWorld(t);
                var cp = basePos + offKm * 1000f + Vector3.up * (hKm * 1000f);
                var tgt = basePos + new Vector3(lookKm.x * 1000f, lookKm.y * 1000f, lookKm.z * 1000f);
                cam.transform.SetPositionAndRotation(cp, Quaternion.LookRotation(tgt - cp, Vector3.up));
                foreach (var r in storms) r.ResetHistory(cam);
                string f = Path.Combine(ShotDir, $"anvils_{name}_{(mode == StructureModel.Website ? "old" : "new")}.png");
                StormLabTestKit.Render(cam, f, 960, 600, 3);
                tiles.Add(f);
            }
            rep.AppendLine($"- {mode}: {StormCustomPass.Composites} composites so far");
        }
        // rows the views, columns old | new
        var ordered = new List<string>();
        for (int v = 0; v < views.Length; v++) { ordered.Add(tiles[v]); ordered.Add(tiles[views.Length + v]); }
        Sheet(ordered, 2, Path.Combine(ShotDir, "anvils_sheet.png"));
        for (int i = 1; i < 3; i++) Object.DestroyImmediate(storms[i].gameObject);
        storm.drawAlongside = false;
        return true;
    }

    // ------------------------------------------------------------------ structure (-slStructure [presets])
    /// <summary>The game's structure from the environment (StormStructure) beside the web build's layout: per setup (a
    /// comma list; "plains~1234" is a day's environment) at four stages of the first cycle (organising, slot open, mature
    /// tornado, rope-out), a chaser's view from the inflow side, a closer view under the base, and the radar's 0.5 deg
    /// scan. Screenshots/structure_(preset).png: rows the stages; columns web view, game view, web close, game close, web
    /// radar, game radar.</summary>
    static bool StructureSheets(StringBuilder rep, string presets)
    {
        rep.AppendLine("## Structure from the environment (StormStructure) beside the web build's layout").AppendLine();
        storm.tornado = TornadoModel.Detailed; storm.lighting = StormLighting.Unified;
        storm.sunAzimuth = 250f; storm.sunElevation = 24f;
        var radarGo = new GameObject("Radar");
        var radar = radarGo.AddComponent<StormRadar>();
        radar.resolution = 600; radar.areaHalfM = 45000f; radar.gateNoiseDb = 2.5f;
        var late = typeof(StormRadar).GetMethod("LateUpdate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        const int W = 800, H = 500;
        foreach (var pre in presets.Split(','))
        {
            storm.preset = pre; storm.seed = 42; storm.varied = true; storm.structure = StructureModel.Environmental; storm.Replan();
            var P = storm.Plan;
            if (P.cycles == null) { rep.AppendLine($"- {pre}: {P.kind}, no cycles"); continue; }
            var c0 = P.cycles[0];
            var stages = new (string name, float t)[]
            {
                ("organising", (float)(c0.start - 4)), ("slot open", (float)(c0.times[1] + 0.6 * (c0.times[2] - c0.times[1]))),
                ("tornado", (float)(c0.times[3] + 1)), ("rope-out", (float)(c0.times[4] + 0.6 * (c0.times[5] - c0.times[4]))),
            };
            rep.AppendLine($"### {pre}: {P.env}").AppendLine();
            var sc = StormStructure.Scales(P);
            rep.AppendLine($"meso radius {sc.meso:0.0} km, tower {sc.tower:0.0} km, forward-flank core {sc.ffd:0.0} km and hail {sc.hail:0.0} km from the updraft; tower at the EL after {StormStructure.RiseMin(P):0.0} min; HP-ness {P.hpness:0.00}").AppendLine();
            var tiles = new List<string>();
            foreach (var (name, t) in stages)
            {
                foreach (var mode in new[] { StructureModel.Website, StructureModel.Environmental })
                {
                    storm.structure = mode;
                    var k = new Case { preset = pre, t = t, w = W, h = H };
                    Show(k);
                    var u = new Vector2(storm.Frame.param[64], storm.Frame.param[65]);
                    var f = new Vector2(storm.Frame.param[0], storm.Frame.param[1]);
                    float azF = Mathf.Atan2(f.y, f.x) * Mathf.Rad2Deg;
                    // from the inflow side: south-east of the updraft for a north-east mover (right of the motion)
                    k.cam = new WebCam { az = azF - 80, dist = 26, h = 0.1f, tz = 3.2f, tx = u.x, ty = u.y }; Aim(k.cam, t);
                    string tag = $"{pre.Replace('~', '_')}_{name.Replace(' ', '_')}_{(mode == StructureModel.Website ? "web" : "game")}";
                    Shot($"structure_{tag}_view.png", k); tiles.Add(Path.Combine(ShotDir, $"structure_{tag}_view.png"));
                    var wc = new Vector2(storm.Frame.param[24], storm.Frame.param[25]);
                    k.cam = new WebCam { az = azF - 75, dist = 10, h = 0.05f, tz = 1.0f, tx = wc.x, ty = wc.y }; Aim(k.cam, t);
                    Shot($"structure_{tag}_close.png", k); tiles.Add(Path.Combine(ShotDir, $"structure_{tag}_close.png"));
                    // the radar 70 km south-east of the updraft
                    var uw = storm.UpdraftWorld(t);
                    var dirSE = new Vector3(1, 0, -1).normalized;
                    radar.site = uw + dirSE * 70000f; radar.site.y = storm.origin.y;
                    radar.areaCentre = new Vector2(uw.x, uw.z);
                    radar.ScanAll();
                    late.Invoke(radar, null); StormLabTestKit.Sync();
                    string rp = Path.Combine(ShotDir, $"structure_{tag}_radar.png");
                    SaveRadar(radar, rp, W, H); tiles.Add(rp);
                    var a = storm.Frame.param;
                    rep.AppendLine($"- {name} ({t:0.0} min, {storm.Frame.stage}), {mode}: tower R {a[9]:0.0} km, wall cloud {Vector2.Distance(wc, u):0.0} km from the updraft (radius {a[26]:0.0}), surge radius {a[74]:0.0} km at {Vector2.Distance(new Vector2(a[72], a[73]), u):0.0} km, FFD core {Vector2.Distance(new Vector2(a[36], a[37]), u):0.0} km, tornado top {a[57]:0.00} km");
                }
            }
            // per stage the columns web view, game view, web close, game close, web radar, game radar
            var ordered = new List<string>();
            for (int s = 0; s < stages.Length; s++)
            {
                int b = s * 6;   // web: view, close, radar; then the game's
                ordered.AddRange(new[] { tiles[b], tiles[b + 3], tiles[b + 1], tiles[b + 4], tiles[b + 2], tiles[b + 5] });
            }
            Sheet(ordered, 6, Path.Combine(ShotDir, $"structure_{pre.Replace('~', '_')}.png"));
            rep.AppendLine();
        }
        Object.DestroyImmediate(radarGo);
        return true;
    }

    /// <summary>The radar's image (linear palette colours, alpha) over a dark map, cropped to w x h round its centre.</summary>
    static void SaveRadar(StormRadar radar, string path, int w, int h)
    {
        var img = radar.Image; if (!img) return;
        var prev = RenderTexture.active; RenderTexture.active = img;
        var t = new Texture2D(img.width, img.height, TextureFormat.RGBA32, false, true);
        t.ReadPixels(new Rect(0, 0, img.width, img.height), 0, 0); t.Apply();
        RenderTexture.active = prev;
        var o = new Texture2D(w, h, TextureFormat.RGB24, false);
        int x0 = (img.width - w) / 2, y0 = (img.height - h) / 2;
        var bg = new Color(0.07f, 0.08f, 0.09f);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sx = Mathf.Clamp(x + x0, 0, img.width - 1), sy = Mathf.Clamp(y + y0, 0, img.height - 1);
                var c = t.GetPixel(sx, sy);
                o.SetPixel(x, y, Color.Lerp(bg, new Color(c.r, c.g, c.b).gamma, c.a));
            }
        o.Apply();
        File.WriteAllBytes(path, o.EncodeToPNG());
        Object.DestroyImmediate(t); Object.DestroyImmediate(o);
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
    /// points of its life (a tag with "spin": six frames 3 s apart at 0.45 of its life instead, to see it turn), from a chaser 140 degrees round from it (as the sheets), the game's half-resolution blended march
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
        // ("spin": six frames 3 s of storm time apart at 0.45 of its life, to see it turn)
        bool spin = tag.Contains("spin");
        var fracs = tag.Contains("early") ? new[] { 0.005f, 0.03f, 0.07f, 0.12f, 0.19f, 0.3f }
            : tag.Contains("rope") ? new[] { r0 - rs - 0.01f, r0 - 0.5f * rs, r0, r0 + 0.25f * rf, r0 + 0.5f * rf, r0 + 0.8f * rf }
            : spin ? new[] { 0.45f, 0.45f, 0.45f, 0.45f, 0.45f, 0.45f }
            : new[] { 0.05f, 0.15f, 0.3f, 0.45f, 0.6f, 0.8f };
        foreach (float f in fracs)
        {
            float T = (float)(tg.formMin + f * tg.LifeMin) + (spin ? tiles.Count * 3f / 60f : 0f);
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
    /// What a tornado costs to march, by its width and how close the camera is (-slTornadoCost, or StormLab > Tornado Cost
    /// Check): forced rope, cone and wedges 0.9-3 km wide, seen from 0.4, 1.5 and 4 km beyond the funnel's foot (and from
    /// 250 m up in its dust), the game's
    /// half-resolution march at 1920 x 1080. Each view is timed three ways: 2.0's march (fixed 6 m steps, the tornado lit
    /// wherever its fields touch), lit only where it draws, and the game's (tornadoStep). The first and last are rendered
    /// and compared. validation/reports/TornadoCost.md, Screenshots/tornado_cost_sheet.png (left 2.0's, right the game's).
    /// </summary>
    static bool TornadoCost()
    {
        storm.lighting = StormLighting.Physical; storm.tornado = TornadoModel.Detailed;
        storm.renderScale = 0.5f; storm.temporalBlend = 0.25f; storm.checkerboard = false;
        storm.sunAzimuth = 272f; storm.sunElevation = 15f;
        float step = storm.tornadoStep > 0f ? storm.tornadoStep : 0.12f;
        var rep = new StringBuilder("# Tornado march cost\n\n");
        rep.AppendLine($"Generated by `StormLabTests -slTornadoCost` on {DateTime.Now:yyyy-MM-dd HH:mm}, {SystemInfo.graphicsDeviceName}. " +
                       "March and resolve GPU time at 1920 x 1080 (half-resolution march, no checkerboard), synchronised command buffers of 10 marches. " +
                       $"Image difference: mean and 99th percentile per-pixel difference (0-255) between 2.0's march and the game's (tornadoStep {step:0.00}), 12 blended frames each.").AppendLine();
        rep.AppendLine("| Tornado | Width at the ground now | Camera past its foot | 2.0's march | lit where drawn | game's | speed-up | no tornado yet (2 min before) | game's without its sun / horizon tests | game's fine steps only | game's steps a pixel: mean / in the tornado / 95th percentile | image difference |").AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        double SyncMs() { var sw = Stopwatch.StartNew(); StormLabTestKit.Sync(); return sw.Elapsed.TotalMilliseconds; }
        double Timed(int reps)
        {
            using (var warm = new UnityEngine.Rendering.CommandBuffer()) { storm.RecordMarch(warm, cam, null, 1920, 1080, false, out _); Graphics.ExecuteCommandBuffer(warm); }
            StormLabTestKit.Sync();
            double idle = Enumerable.Range(0, 5).Select(_ => SyncMs()).Min();
            using (var cmd = new UnityEngine.Rendering.CommandBuffer { name = "TornadoCost timing" })
            {
                for (int i = 0; i < reps; i++) storm.RecordMarch(cmd, cam, null, 1920, 1080, false, out _);
                var sw = Stopwatch.StartNew();
                Graphics.ExecuteCommandBuffer(cmd); StormLabTestKit.Sync();
                return Math.Max(0, sw.Elapsed.TotalMilliseconds - idle) / reps;
            }
        }
        storm.preset = "plains"; storm.varied = false; storm.Replan();
        var P = storm.Plan;
        var tiles = new List<string>(); var heat = new List<string>();
        (double mean, double tor, double p95) StepCounts(string png)
        {
            RenderTexture rt;
            using (var cmd = new UnityEngine.Rendering.CommandBuffer()) { rt = storm.RecordMarch(cmd, cam, null, 1920, 1080, false, out _); Graphics.ExecuteCommandBuffer(cmd); }
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels(); var steps = new float[px.Length]; double s = 0, st = 0;
            for (int i = 0; i < px.Length; i++)
            {
                steps[i] = px[i].r * 1000f; s += steps[i]; st += px[i].g * 1000f;
                px[i] = new Color(px[i].r / 0.7f, px[i].g / 0.7f, 0f, 1f);
            }
            tex.SetPixels(px); tex.Apply();
            File.WriteAllBytes(png, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            Array.Sort(steps);
            return (s / px.Length, st / px.Length, steps[(int)(steps.Length * 0.95)]);
        }
        bool ok = true;
        foreach (var (shape, w, ef) in new[] { (FunnelShape.Rope, 90.0, 1), (FunnelShape.Cone, 350.0, 2), (FunnelShape.Wedge, 900.0, 3), (FunnelShape.Wedge, 1800.0, 4), (FunnelShape.Wedge, 3000.0, 5) })
        {
            var forced = StormTornadoes.Forced(P, ClimoRegion.Plains, 0, shape, w, ef);
            if (forced == null) { ok = false; rep.AppendLine($"FAIL: no tornado to reshape for {shape} {w} m"); continue; }
            if (shape == FunnelShape.Wedge)
            {
                // Forced sizes a wedge from the web build's cycle (SizeWedge), whatever width is asked: scale it to w here
                double k = w / 2000.0 / forced.groundKm;
                forced.groundKm *= k; forced.topKm *= k; forced.rcKm = Math.Min(forced.rcKm * k, 0.6); forced.widthM = w;
                for (int i = 0; i < forced.phases.Length; i++) { var p = forced.phases[i]; p.groundKm *= k; p.topKm *= k; forced.phases[i] = p; }
            }
            storm.Replan();
            var chars = storm.Tornadoes; chars[0] = forced; storm.Tornadoes = chars;
            float T = (float)(forced.formMin + (shape == FunnelShape.Wedge ? 0.55 : 0.4) * forced.LifeMin);
            storm.StepTo(T, 100f, true); StormLabTestKit.Sync();
            var g = storm.TornadoGroundWorld; g.y = storm.origin.y;
            float zb = (float)(P.zb - StormDesign.CLOUD_BASE_LIFT_KM);
            float foot = storm.Frame.param[14 * 4] * 1000f;
            // on the ground from three distances, and in free flight 250 m up in the dust, looking down at its foot (user,
            // 2026-10-27: the first stride through clear air drew the whirl in layers seen from there)
            foreach (var (km, up) in new[] { (0.4f, 2f), (1.5f, 2f), (4f, 2f), (0.4f, 250f) })
            {
                float D = foot + km * 1000f;
                var from = g + Bearing(140f) * D + Vector3.up * up;
                var at = up > 10f ? g + Vector3.up * 30f : g + Vector3.up * zb * 400f;
                cam.transform.SetPositionAndRotation(from, Quaternion.LookRotation(at - from, Vector3.up));
                cam.fieldOfView = 60f;
                var ms = new double[3]; var shots = new string[2];
                for (int m = 0; m < 3; m++)
                {
                    storm.tornadoStep = m == 2 ? step : 0f; storm.tornadoLightAll = m == 0;
                    storm.ResetHistory(cam);
                    ms[m] = Timed(10);
                    if (m == 1) continue;
                    string path = Path.Combine(ShotDir, $"tornado_cost_{tiles.Count + 1}.png");
                    storm.ResetHistory(cam);
                    StormLabTestKit.Render(cam, path, 960, 540, 12);
                    shots[m / 2] = path; tiles.Add(path);
                }
                var (mean, p99) = Diff(shots[0], shots[1]);
                // where the game's march spends it: without the tornado's sun and horizon tests, and without its fields
                // (then nothing stops the rays in it, so that is all the fine steps through its reach, an upper bound)
                var probe = new double[3];
                for (int pr = 1; pr <= 2; pr++) { storm.tornadoCostProbe = pr; storm.ResetHistory(cam); probe[pr] = Timed(10); }
                // and how many steps each pixel takes (a heat map: red all steps, green those evaluating the tornado, of 700)
                storm.tornadoCostProbe = 3; storm.ResetHistory(cam);
                var (meanSteps, meanTor, p95Steps) = StepCounts(Path.Combine(ShotDir, $"tornado_steps_{heat.Count + 1}.png"));
                heat.Add(Path.Combine(ShotDir, $"tornado_steps_{heat.Count + 1}.png"));
                storm.tornadoCostProbe = 0;
                // the same view 2 storm minutes before it touches down: what the wall cloud, rain and base cost there
                storm.StepTo(forced.formMin - 2f, 100f, true); StormLabTestKit.Sync(); storm.ResetHistory(cam);
                double before = Timed(10);
                storm.StepTo(T, 100f, true); StormLabTestKit.Sync();
                rep.AppendLine($"| {shape} EF{ef} | {2 * foot:0} m | {km:0.0} km{(up > 10f ? $", {up:0} m up" : "")} | {ms[0]:0.00} ms | {ms[1]:0.00} ms | {ms[2]:0.00} ms | {ms[0] / Math.Max(ms[2], 1e-3):0.0}x | {before:0.00} ms | {probe[1]:0.00} ms | {probe[2]:0.00} ms | {meanSteps:0} / {meanTor:0} / {p95Steps:0} | {mean:0.00} / {p99:0} |");
            }
        }
        storm.tornadoStep = step; storm.tornadoLightAll = false;
        if (tiles.Count > 0) Sheet(tiles, 2, Path.Combine(ShotDir, "tornado_cost_sheet.png"));
        if (heat.Count > 0) Sheet(heat, 3, Path.Combine(ShotDir, "tornado_steps_sheet.png"));
        rep.AppendLine().AppendLine("Sheet: `Screenshots/tornado_cost_sheet.png`, one row per view in the table's order, 2.0's march left, the game's right.");
        string report = Path.Combine(StormLabTestKit.RepoRoot, "validation", "reports", "TornadoCost.md");
        File.WriteAllText(report, rep.ToString());
        Debug.Log($"[TornadoCost] {(ok ? "done" : "FAIL")}; report {report}");
        return ok;
    }

    /// <summary>Mean and 99th percentile of the per-pixel difference (largest of r, g, b; 0-255) of two images.</summary>
    static (double mean, int p99) Diff(string a, string b)
    {
        var ta = Load(a); var tb = Load(b);
        var pa = ta.GetPixels32(); var pb = tb.GetPixels32();
        var hist = new int[256]; double s = 0;
        for (int i = 0; i < pa.Length; i++)
        {
            int d = Math.Max(Math.Abs(pa[i].r - pb[i].r), Math.Max(Math.Abs(pa[i].g - pb[i].g), Math.Abs(pa[i].b - pb[i].b)));
            hist[d]++; s += d;
        }
        Object.DestroyImmediate(ta); Object.DestroyImmediate(tb);
        int p = 0, acc = 0; while (p < 255 && (acc += hist[p]) < pa.Length * 0.99) p++;
        return (s / pa.Length, p);
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

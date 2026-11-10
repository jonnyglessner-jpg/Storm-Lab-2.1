// Play-mode check of the towns and the career (user, 2026-11-02) on the plains map: the four towns' places load (truck
// stop, motel, mechanic, traffic light each) and the town rig builds the signals and marked spots and cycles the lights;
// pictures of a town (Main Street at the light, the truck stop, the motel, a residential street, from the air) go to
// Screenshots/town_*.png; a career starts on the morning forecast at 8 am with no storms out and the truck parked at a
// station, the forecast reads the day's event type and its storms, confirming it spawns them at their initiation hour with the truck at
// the chosen truck stop; the day's clock follows the game clock and runs on after the storms; the tank burns under
// load, an empty tank kills the engine, pumping at an island fills it and charges the station's price for the vehicle's
// grade; a worn truck loses power and limps, the mechanic repairs it for the bill; the sanity bar's phases slow the
// chaser and the truck and raise the pay, drain faster by a tornado, and empty it schedules a pass-out; a motel night
// fades out, shows a quote, fades in at 8 am next day with the bar full, the storms gone, the career saved and the
// forecast up; car camping costs a third and rests to 75 %; passing out waits for the truck to stop and the drone to
// land, says "you passed out." and rests to half; the career continues from the saved morning.
// Writes validation/reports/ChaseCareerCheck.md. Uses a scratch profile (the user's is saved and put back).
//   StormLab > Test Map > Chase Career Check
// Batch: -executeMethod ChaseCareerCheck.RunFromCommandLine (never with -nographics)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using StormLab;
using StormLab.Game;
using StormLab.TestMap;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

[InitializeOnLoad]
public static class ChaseCareerCheck
{
    const string Key = "ChaseCareerCheck.Active", BatchKey = "ChaseCareerCheck.Batch", CodeKey = "ChaseCareerCheck.Code";
    static string ReportDir => Path.Combine(StormLabTestKit.ProjectDir, "..", "..", "validation", "reports");
    static string ShotDir => Path.Combine(StormLabTestKit.ProjectDir, "Screenshots");

    static ChaseCareerCheck() { if (SessionState.GetBool(Key, false)) EditorApplication.playModeStateChanged += OnState; }

    [MenuItem("StormLab/Test Map/Chase Career Check")]
    public static void RunMenu() => Begin(false);
    public static void RunFromCommandLine() => Begin(true);

    static void Begin(bool batch)
    {
        EditorSceneManager.OpenScene(TestMapBuilder.ScenePath, OpenSceneMode.Single);
        SessionState.SetBool(Key, true); SessionState.SetBool(BatchKey, batch);
        EditorApplication.playModeStateChanged -= OnState;
        EditorApplication.playModeStateChanged += OnState;
        EditorApplication.EnterPlaymode();
    }

    static IEnumerator<object> routine; static readonly StringBuilder rep = new StringBuilder(); static bool ok; static int errors;
    static readonly List<string> errorLines = new List<string>();
    static string savedProfile;

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
        if (stack != null && stack.Contains("UnityEditor.Search")) return;
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
        try
        {
            if (savedProfile != null) File.WriteAllText(ChaseProfile.Path, savedProfile);
            else if (File.Exists(ChaseProfile.Path)) File.Delete(ChaseProfile.Path);
        }
        catch (Exception e) { rep.AppendLine($"- WARN: could not restore the profile: {e.Message}"); }
        rep.AppendLine().AppendLine($"Errors logged: {errors}");
        foreach (var l in errorLines) rep.AppendLine($"- {l}");
        ok &= errors == 0;
        rep.Insert(0, $"# Chase career check: {(ok ? "PASS" : "FAIL")}\n\nGenerated by `ChaseCareerCheck` on {DateTime.Now:yyyy-MM-dd HH:mm}, {SystemInfo.graphicsDeviceName}.\n\n");
        Directory.CreateDirectory(ReportDir);
        File.WriteAllText(Path.Combine(ReportDir, "ChaseCareerCheck.md"), rep.ToString());
        Debug.Log($"[ChaseCareer] {(ok ? "PASS" : "FAIL")}");
        SessionState.SetInt(CodeKey, ok ? 0 : 1);
        EditorApplication.ExitPlaymode();
    }

    static IEnumerable<object> PlayerFrames(int n) { int f0 = Time.frameCount; while (Time.frameCount < f0 + n) yield return null; }
    static IEnumerable<object> Seconds(float s) { float t0 = Time.unscaledTime; while (Time.unscaledTime - t0 < s) yield return null; }
    static void Check(bool pass, string what) { ok &= pass; rep.AppendLine($"- {(pass ? "PASS" : "FAIL")}: {what}"); }
    static void Note(string what) { rep.AppendLine($"- {what}"); }

    /// <summary>A picture from the free camera at a world point looking along a direction, rendered by the camera alone.</summary>
    static IEnumerable<object> Shot(TestMapWorld world, Camera cam, Vector3 at, Vector3 look, string name)
    {
        var rig = world.cameraRig;
        rig.mode = TestMapCamera.Mode.Free; rig.held = true;
        cam.transform.position = at; cam.transform.rotation = Quaternion.LookRotation(look, Vector3.up);
        foreach (var x in Seconds(1.2f)) { cam.transform.position = at; cam.transform.rotation = Quaternion.LookRotation(look, Vector3.up); yield return x; }
        try
        {
            int w = 1600, h = 900;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 }; rt.Create();
            var req = new RenderPipeline.StandardRequest { destination = rt };
            // twice: in batch the camera is only ever rendered by these requests, so the first carries the motion blur
            // from wherever the last picture was taken
            for (int pass = 0; pass < 3; pass++)
            {
                if (RenderPipeline.SupportsRenderRequest(cam, req)) RenderPipeline.SubmitRenderRequest(cam, req);
                else { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
            }
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false); tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
            RenderTexture.active = prev; rt.Release(); Object.Destroy(rt);
            Directory.CreateDirectory(ShotDir);
            File.WriteAllBytes(Path.Combine(ShotDir, name), tex.EncodeToPNG());
            Object.Destroy(tex);
            Note($"picture {name}");
        }
        catch (Exception e) { Note($"WARN: picture {name} failed: {e.Message}"); }
    }

    static IEnumerable<object> Run()
    {
        foreach (var x in PlayerFrames(5)) yield return x;
        savedProfile = File.Exists(ChaseProfile.Path) ? File.ReadAllText(ChaseProfile.Path) : null;
        ChaseProfile.Reset();

        var world = Object.FindAnyObjectByType<TestMapWorld>();
        var panel = world ? world.GetComponent<TestMapStormPanel>() : null;
        var cam = Camera.main;
        var game = world ? world.GetComponent<ChaseGame>() : null;
        var rig = world ? world.GetComponent<TestMapTownRig>() : null;
        if (!world || !panel || !cam || !game || !rig) { Check(false, $"world {world}, panel {panel}, camera {cam}, ChaseGame {game}, town rig {rig}"); yield break; }
        panel.hour = 15f; panel.clockRuns = false; panel.Apply();
        var car = world.car;

        rep.AppendLine("## The towns").AppendLine();
        var towns = TestMapPlaces.Load(world.navResource);
        Check(towns.Length == 4, $"four towns in the places data ({string.Join(", ", towns.Select(t => t.name))})");
        foreach (var t in towns)
        {
            bool full = t.gas != null && t.gas.pumps != null && t.gas.pumps.Length >= 3 && t.gas.diesel != null && t.gas.diesel.Length >= 2 && t.gas.sleep != null
                        && t.motel != null && t.motel.sleep != null && t.mechanic != null && t.mechanic.spot != null && t.light != null;
            Check(full, $"{t.name}: truck stop {t.gas?.name} (regular {t.gas?.regular:0.00}, premium {t.gas?.premium:0.00}, diesel {t.gas?.dieselPrice:0.00}), {t.motel?.name} ({t.motel?.rate} cr), {t.mechanic?.name}, light at {t.light?.main} / {t.light?.cross}");
            float sqmi = (t.box.x1 - t.box.x0) * (t.box.z1 - t.box.z0) / (1609.344f * 1609.344f);
            Check(sqmi > 0.2f && sqmi < 4f, $"{t.name} covers {sqmi:0.00} sq mi (under four)");
            Check(t.gas.regular > 3f && t.gas.regular < 5.5f && t.gas.premium > t.gas.regular && t.gas.dieselPrice > t.gas.regular, $"{t.name}: prices in the real range, premium and diesel over regular");
        }
        {
            var ne = towns.FirstOrDefault(t => t.key == "ne"); var se = towns.FirstOrDefault(t => t.key == "se");
            Check(ne != null && se != null && Mathf.Abs(ne.centre.x - se.centre.x - 3f * 1609.344f) < 50f, $"the north-east town sits three miles east of the K-183 crossing (x {ne?.centre.x:0} vs {se?.centre.x:0})");
        }
        Check(rig.Signals.Count == 4 && rig.Spots.Count == 12, $"the rig built {rig.Signals.Count} signals and {rig.Spots.Count} marked spots");
        {
            // the lights cycle: Main's green first, then the cross street's (the timing shortened for the check)
            rig.mainGreen = 1.5f; rig.crossGreen = 1.5f; rig.amber = 0.4f; rig.allRed = 0.3f;
            var s0 = rig.Signals[0];
            var seen = new HashSet<(int, int)>();
            foreach (var x in Seconds(4.5f)) { seen.Add((s0.NS, s0.EW)); yield return x; }
            Check(seen.Contains((2, 0)) && seen.Contains((0, 2)) && seen.Contains((1, 0)) && !seen.Contains((2, 2)), $"the signal at {s0.town.name} cycles green / amber / red both ways, never green both ({string.Join(" ", seen.Select(p => $"{p.Item1}{p.Item2}"))})");
            Check(rig.StateFor(s0.at + Vector3.forward * 20f, 0f) == s0.NS && rig.StateFor(s0.at, 90f) == s0.EW, "the signal's state reads by heading");
        }
        var hk = towns.FirstOrDefault(t => t.key == "sw") ?? towns[0];
        {
            // no motion blur in the pictures: in batch the camera only renders on request, so its motion vectors are stale
            var vol = Object.FindObjectsByType<UnityEngine.Rendering.Volume>(FindObjectsSortMode.None).FirstOrDefault(q => q.isGlobal && q.sharedProfile);
            UnityEngine.Rendering.HighDefinition.MotionBlur mb = null;
            if (vol) { if (!vol.profile.TryGet(out mb)) mb = vol.profile.Add<UnityEngine.Rendering.HighDefinition.MotionBlur>(true); mb.intensity.Override(0f); mb.active = true; }
            // pictures of Haskett: Main at the light looking north, the truck stop, the motel, a side street, from the air
            var L = hk.light.Position;
            foreach (var x in Shot(world, cam, L + new Vector3(2f, 1.7f, -60f), Vector3.forward, "town_main_street.png")) yield return x;
            var g = hk.gas; var gc = g.canopy.Position;
            foreach (var x in Shot(world, cam, gc + new Vector3(-70f, 2f, -55f), (gc - (gc + new Vector3(-70f, 0f, -55f))).normalized, "town_truck_stop.png")) yield return x;
            var m = hk.motel.Position;
            var mFrom = m + new Vector3(-30f, 14f, -150f);   // over the trees along the highway
            foreach (var x in Shot(world, cam, mFrom, (m + Vector3.up * 4f - mFrom).normalized, "town_motel.png")) yield return x;
            var mc = hk.mechanic.Position;
            foreach (var x in Shot(world, cam, mc + new Vector3(30f, 2f, -70f), (mc - (mc + new Vector3(30f, 0f, -70f))).normalized, "town_mechanic.png")) yield return x;
            // the motel's guest space from the driver's seat of a truck nosed up to its sign (the text must read the right way round)
            var sp = hk.motel.sleep; var fwdS = Quaternion.Euler(0f, sp.yaw, 0f) * Vector3.forward;
            var eye = sp.Position + Vector3.up * 1.45f - fwdS * 1.2f;
            foreach (var x in Shot(world, cam, eye, (sp.Position + fwdS * 4.55f + Vector3.up * 2.4f - eye).normalized, "town_sleep_sign.png")) yield return x;
            var side = L + new Vector3(-275f, 1.7f, 300f);   // on the east-west street three blocks north, two west of Main
            foreach (var x in Shot(world, cam, side, Vector3.right, "town_side_street.png")) yield return x;
            foreach (var x in Shot(world, cam, L + new Vector3(-900f, 420f, -900f), (L - (L + new Vector3(-900f, 420f, -900f))).normalized, "town_air.png")) yield return x;
            foreach (var x in Shot(world, cam, L + new Vector3(0f, 1800f, -1f), Vector3.down, "town_plan.png")) yield return x;
            world.cameraRig.held = false; world.cameraRig.mode = TestMapCamera.Mode.Cab;
            if (mb) mb.active = false;
        }

        rep.AppendLine().AppendLine("## A career's morning").AppendLine();
        ChaseSetup.map = "plains"; ChaseSetup.vehicle = "dually"; ChaseSetup.seed = 7;
        ChaseProfile.Current.StartCareer(7);
        ChaseSetup.BeginChase();
        game.Menu.StartChase();
        foreach (var x in PlayerFrames(3)) yield return x;
        var p = ChaseProfile.Current;
        Check(ChaseSetup.state == ChaseSetup.State.Chase && ChaseSetup.morning && game.Forecast.Showing, "the career opens on the morning forecast");
        Check(ChaseGame.InputBlocked && !car.acceptInput, "the forecast takes the keys and the wheel");
        Check(Mathf.Abs(ChaseSetup.hour - 8f) < 0.01f && Mathf.Abs(panel.hour - 8f) < 0.01f, $"it is 8 am ({panel.hour:0.00})");
        Check(!world.LiveStorms().Any(), "no storm is out yet");
        Check(TestMapPlaces.Flat(car.transform.position, towns[0].gas.sleep.Position) < 3f, $"the truck is parked at {towns[0].gas.name} ({TestMapPlaces.Flat(car.transform.position, towns[0].gas.sleep.Position):0.0} m)");
        var fc = game.Forecast;
        // the day's event type comes first (user, 2026-11-09): the seed's own, read by the forecast
        var ev = fc.DayEvent;
        Check(fc.Preview != null && ev != null && !ev.forced && ev.rolled == StormEvents.Roll(fc.DaySeed) && (ev.type == ev.rolled || ev.fellBack),
            $"the forecast opens on the day's event, the seed's own (seed {fc.DaySeed}: {ev})");
        Check(ev != null && fc.Outlooks.Length == fc.Preview.storms.Count && fc.Outlooks.Length >= 1 && ev.storms >= ev.spec.minStorms && ev.storms <= ev.spec.maxStorms,
            $"it reads the event's storms ({fc.Outlooks.Length}: {string.Join(", ", fc.Outlooks.Select(o => o.lane))}; {ev.spec.name.ToLowerInvariant()} days have {ev.spec.minStorms}-{ev.spec.maxStorms})");
        // the rest of the morning is checked on a day of two tornadic storms: isolated storms or, when this seed's is a
        // lone storm, a local outbreak (the same draw gives one of them two), asked for as the dev panel does
        foreach (var choice in new[] { StormEventChoice.IsolatedStorms, StormEventChoice.LocalOutbreak })
        {
            world.game.eventType = choice; fc.Open();
            if (fc.Outlooks.Length == 2) break;
        }
        Check(fc.DayEvent.forced && fc.Outlooks.Length == 2, $"an event type asked for is the day's: two storms (seed {fc.DaySeed}: {fc.DayEvent}; initiation {ChaseSetup.ClockText(fc.StartHour)})");
        foreach (var o in fc.Outlooks)
        {
            Note($"{o.lane} storm: initiates {ChaseSetup.ClockText(o.initHour)}, {(o.onMap ? "over the map" : "off the map")} from the {o.bearing:0} deg, toward {o.motionDeg:0} deg at {o.motionMph:0} mph, {o.mode}; tornado {o.pTornado} %, EF2+ {o.pEf2} %, EF4+ {o.pEf4} %; {o.shapes} (really: {o.count} tornadoes, strongest EF{o.maxEf})");
            Check(o.pTornado >= 0 && o.pTornado <= 100 && o.pEf2 <= o.pTornado + 5 && o.pEf4 <= o.pEf2 + 5, $"{o.lane}: the probabilities nest");
            Check(o.count == 0 ? o.pTornado < 35 : o.pTornado >= 70, $"{o.lane}: the tornado chance follows the day's real storm");
        }
        Check(fc.Outlooks.Count(o => o.lane == "northern") == 1 && fc.Outlooks.Count(o => o.lane == "southern") == 1, "one northern and one southern storm");
        {
            // the map's orientation: where a town's lawn lands in the rendered map (north up means its row is above the middle for a northern town)
            var mrt = fc.MapTexture;
            var prev = RenderTexture.active; RenderTexture.active = mrt;
            var mtex = new Texture2D(mrt.width, mrt.height, TextureFormat.RGB24, false); mtex.ReadPixels(new Rect(0, 0, mrt.width, mrt.height), 0, 0); mtex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(ShotDir, "forecast_map.png"), mtex.EncodeToPNG());
            // north up: every town's crossing lands on the orange state route in the drawn image's own rows (row 0 south),
            // which is the texture the screen gets (not the render texture, which an IMGUI draw flips on Direct3D)
            int onRoute = 0;
            foreach (var t in towns)
            {
                float mu = 0.5f + (t.centre.x + 4000f) / fc.MapSpan, mv = 0.5f + t.centre.z / fc.MapSpan;   // 2.5 miles east along the highway, clear of the station squares
                Color up = fc.MapImage.GetPixel((int)(mu * mrt.width), (int)(mv * mrt.height));
                bool orange = up.r > 0.85f && up.g > 0.5f && up.g < 0.8f && up.b < 0.6f && up.r - up.b > 0.3f;   // (orange, or orange under a track cone's tint)
                if (orange) onRoute++;
                Note($"{t.name} (world {t.centre.x:0}, {t.centre.z:0}) at the drawn map's north-up pixel: {up} {(orange ? "(the state route)" : "")}");
            }
            Check(onRoute == towns.Length, "the drawn map is north up: the highway runs east from every crossing");
            var north = fc.Outlooks.First(o => o.lane == "northern"); var south = fc.Outlooks.First(o => o.lane == "southern");
            float half = world.sizeM / 2f;
            float zN = north.entry.y + north.dir.y * (-north.entry.x / Mathf.Max(north.dir.x, 1e-3f)), zS = south.entry.y + south.dir.y * (-south.entry.x / Mathf.Max(south.dir.x, 1e-3f));
            Check(zN > zS, $"the northern storm's track crosses the map's middle north of the southern's ({zN / 1609.344f:0.0} vs {zS / 1609.344f:0.0} mi)");
            {
                // the entry square drawn into the map image: the storm's colour at the entry point's north-up pixel, nothing of it at the mirrored one
                var img = fc.MapImage; int W = img.width, H = img.height;
                int px = (int)(W * (0.5f + north.entry.x / fc.MapSpan)), py = (int)(H * (0.5f + north.entry.y / fc.MapSpan));
                px = Mathf.Clamp(px, 2, W - 3); py = Mathf.Clamp(py, 2, H - 3);
                Color at = img.GetPixel(px, py), mirror = img.GetPixel(px, Mathf.Clamp(H - 1 - py, 2, H - 3));
                Note($"northern entry ({north.entry.x:0}, {north.entry.y:0}) -> image ({px}, {py}): {at}; mirrored row: {mirror}");
                Check(at.r > 0.8f && at.g < 0.6f && !(mirror.r > 0.8f && mirror.g < 0.6f), "the northern storm's entry square is drawn where the storm enters, north up");
            }
            Check(Mathf.Abs(Vector2.Angle(north.dir, new Vector2((float)fc.Preview.storms[north.index].plan.motion.x, (float)fc.Preview.storms[north.index].plan.motion.y))) < 0.5f, $"the drawn track follows the storm's motion (toward {north.motionDeg:0} deg)");
            Object.Destroy(mtex);
            foreach (var o in fc.Outlooks) Note($"{o.lane} storm: entry world ({o.entry.x:0}, {o.entry.y:0}), dir ({o.dir.x:0.00}, {o.dir.y:0.00}), toward {o.motionDeg:0} deg");
        }
        Check(fc.Discussion.Contains("Towering cumulus") && fc.Discussion.Length > 200, "the discussion reads");

        rep.AppendLine().AppendLine("## Heading out").AppendLine();
        var lanark = towns.First(t => t.key == "nw");
        fc.Choose("nw");
        fc.Confirm();
        foreach (var x in PlayerFrames(3)) yield return x;
        Check(!fc.Showing && !ChaseSetup.morning && ChaseSetup.dayStarted, "the forecast closed and the day is on");
        Check(world.game && world.game.Scenario != null && world.game.seed == fc.DaySeed && world.LiveStorms().Any(), $"the day's storms are out from the day's seed ({world.game.seed})");
        {
            var sc = world.game.Scenario; var pv = fc.Preview;
            bool same = sc != null && sc.EventType == pv.EventType && sc.storms.Count == pv.storms.Count;
            for (int i = 0; same && i < sc.storms.Count; i++) same &= sc.storms[i].seed == pv.storms[i].seed && sc.storms[i].label == pv.storms[i].label && Math.Abs(sc.storms[i].startX - pv.storms[i].startX) < 1 && Math.Abs(sc.storms[i].startZ - pv.storms[i].startZ) < 1;
            Check(same, $"the storms out are the ones the forecast read ({sc})");
        }
        Check(Mathf.Abs(ChaseSetup.hour - fc.StartHour) < 0.02f && Mathf.Abs(panel.hour - fc.StartHour) < 0.02f, $"the day fast-forwarded to the first towering cumulus ({ChaseSetup.ClockText(ChaseSetup.hour)})");
        Check(world.game.gameMinutes < 0.5f, "the game clock starts at 0 (towering cumulus)");
        Check(TestMapPlaces.Flat(car.transform.position, lanark.gas.sleep.Position) < 3f && car.acceptInput, $"the truck is at {lanark.gas.name}, {lanark.name}, and drives");
        Check(car.GroundUnder(car.transform.position + Vector3.up * 5f, 20f, out var under) && car.transform.position.y - under.point.y < 2f, "the truck is on the ground there");
        Check(p.spawnTown == "nw", "the station is remembered");
        {
            float h0 = ChaseSetup.hour;
            world.game.gameMinutes += 30f; world.game.Apply();
            foreach (var x in PlayerFrames(2)) yield return x;
            Check(Mathf.Abs(Mathf.Repeat(ChaseSetup.hour - h0, 24f) - 0.5f) < 0.02f, $"the day's clock follows the game clock ({ChaseSetup.ClockText(ChaseSetup.hour)} after 30 min)");
            world.game.gameMinutes = world.game.gameLength; world.game.Apply();
            world.SetClockSpeed(3);   // 60x
            foreach (var x in PlayerFrames(2)) yield return x;
            Check(game.StormsDone && !game.Menu.Showing, "the hour up: the storms are done, no summary, the day goes on");
            float h1 = ChaseSetup.hour;
            foreach (var x in Seconds(1.0f)) yield return x;
            float adv = Mathf.Repeat(ChaseSetup.hour - h1, 24f);
            Check(adv > 0.012f && adv < 0.03f, $"after the storms the clock runs on at the game speed ({adv * 60f:0.0} min in a second at 60x)");
            world.SetClockSpeed(0);
        }

        rep.AppendLine().AppendLine("## Dev mode").AppendLine();
        {
            Check(!world.sandboxKeys && !world.allowRewind && panel.locked, "a career has no sandbox keys, no rewind, the panel locked");
            game.SetDevMode(true);
            foreach (var x in PlayerFrames(2)) yield return x;
            Check(ChaseSetup.devMode && world.sandboxKeys && world.allowRewind && !panel.locked, "F3: the sandbox keys, rewinding and the panel are back");
            var mm0 = world.GetComponent<TestMapMinimap>();
            Check(mm0 && !mm0.hidden && game.MapAllowed && !ChaseProfile.Current.Owns("laptop"), "the map shows in dev mode without the laptop");
            float h0 = ChaseSetup.hour;
            panel.hour = Mathf.Repeat(h0 + 2f, 24f);
            foreach (var x in PlayerFrames(2)) yield return x;
            Check(Mathf.Abs(Mathf.Repeat(ChaseSetup.hour - h0 + 24f, 24f) - 2f) < 0.05f, $"the panel's clock slider moves the day's clock ({ChaseSetup.ClockText(h0)} -> {ChaseSetup.ClockText(ChaseSetup.hour)})");
            float h1 = ChaseSetup.hour;
            world.game.gameMinutes = Mathf.Max(0f, world.game.gameMinutes - 5f); world.game.Apply();
            foreach (var x in PlayerFrames(2)) yield return x;
            Check(Mathf.Abs(Mathf.Repeat(h1 - ChaseSetup.hour + 24f, 24f) - 5f / 60f) < 0.05f, $"rewinding the game clock five minutes takes the day back with it ({ChaseSetup.ClockText(ChaseSetup.hour)})");
            game.SetDevMode(false);
            foreach (var x in PlayerFrames(2)) yield return x;
            Check(!ChaseSetup.devMode && !world.sandboxKeys && !world.allowRewind && panel.locked && mm0.hidden, "off again: locked down, the map away");
        }

        rep.AppendLine().AppendLine("## Fuel").AppendLine();
        var fuel = game.Fuel; var v = ChaseSetup.Vehicle;
        Check(v.fuel == FuelGrade.Diesel && fuel.Gallons > v.tankGal - 0.1f, $"the dually starts with a full tank of {v.FuelName} ({fuel.Gallons:0.0} / {v.tankGal:0} gal)");
        {
            car.acceptInput = false; car.testThrottle = 1f;
            float g0 = fuel.Gallons;
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < 1.5f) yield return null;
            Note($"burn under full throttle: {fuel.BurnGph:0.00} gal/h (idle {v.idleGph}, full {v.fullGph}), load {car.EngineLoad:0.00}, {car.SpeedKph:0} km/h");
            Check(fuel.BurnGph > v.idleGph && fuel.Gallons < g0, $"the tank burns under load ({g0 - fuel.Gallons:0.0000} gal in 1.5 s)");
            car.testThrottle = 0f; car.testBrake = 1f;
            foreach (var x in Seconds(1.0f)) yield return x;
            car.testBrake = 0f; car.acceptInput = true;
            foreach (var x in Seconds(1.0f)) yield return x;
            Check(fuel.BurnGph > 0f && fuel.BurnGph <= v.idleGph + 0.01f, $"idling burns the idle rate ({fuel.BurnGph:0.00} gal/h)");
            fuel.TestSetGallons(0f);
            foreach (var x in PlayerFrames(2)) yield return x;
            Check(fuel.Empty && car.powerScale < 0.01f, "an empty tank kills the engine");
            // to the pump
            var pump = lanark.gas.pumps[0];
            // beside the island, across it (the islands run east-west: along them the 5.6 m truck would overlap the pump box,
            // and the physics pushing them apart counts as crashes)
            car.Teleport(pump.Position + Vector3.forward * 3.0f, Quaternion.Euler(0f, pump.yaw, 0f));
            foreach (var x in Seconds(0.6f)) yield return x;
            Check(fuel.AtPump == lanark.gas && fuel.PumpDistance < 7f, $"at {lanark.gas.name}'s pump ({fuel.PumpDistance:0.0} m)");
            p.credits += 500;
            int c0 = p.credits;
            fuel.Pump(3f);
            foreach (var x in PlayerFrames(2)) yield return x;
            float price = lanark.gas.Price(FuelGrade.Diesel);
            Check(Mathf.Abs(fuel.Gallons - 3f * fuel.pumpGps) < 0.05f, $"three seconds at the pump: {fuel.Gallons:0.0} gal");
            Check(c0 - p.credits >= Mathf.FloorToInt(fuel.Gallons * price) - 1 && c0 - p.credits <= Mathf.CeilToInt(fuel.Gallons * price) + 1, $"charged the diesel price ({c0 - p.credits} cr at {price:0.00}/gal)");
            Check(car.powerScale > 0.5f, "the engine runs again");
            p.credits = 5; fuel.TestSetGallons(1f); fuel.Pump(10f);
            foreach (var x in PlayerFrames(2)) yield return x;
            Check(p.credits == 0 && fuel.Gallons < 1f + 5f / price + 0.05f && fuel.Gallons > 1f, $"the pump stops when the credits run out ({fuel.Gallons:0.0} gal on 5 cr)");
            fuel.TestSetGallons(v.tankGal); p.credits += 2000;
        }

        rep.AppendLine().AppendLine("## Wear and repairs").AppendLine();
        var wear = game.Wear;
        Note($"wear so far: road {wear.RoadWear:P3} ({wear.Surface}), wind {wear.WindWear:P3}, impacts {wear.ImpactWear:P3}; odometer {car.OdometerM:0} m; collisions {car.Impacts}: {string.Join(" | ", car.ImpactLog)}; truck at {car.transform.position}, now t {Time.time:0.00}");
        Check(wear.Condition > 0.999f && wear.PowerScale > 0.999f, $"a new truck at 100 % ({wear.Condition:P2} after the metres driven at the pump)");
        wear.TestSetCondition(0.5f);
        foreach (var x in PlayerFrames(2)) yield return x;
        Check(wear.PowerScale > 0.7f && wear.PowerScale < 0.75f && Mathf.Abs(car.powerScale - wear.PowerScale) < 0.01f, $"at 50 % the engine gives {wear.PowerScale:P0}");
        wear.TestSetCondition(0.05f);
        foreach (var x in PlayerFrames(2)) yield return x;
        Check(wear.Limping && Mathf.Approximately(car.powerScale, 0.12f), "at 5 % it limps");
        {
            var bay = lanark.mechanic.spot;
            car.Teleport(bay.Position, Quaternion.Euler(0f, bay.yaw, 0f));
            foreach (var x in Seconds(0.6f)) yield return x;
            Check(wear.AtMechanic == lanark.mechanic, $"in the bay at {lanark.mechanic.name}");
            int cost = wear.RepairCost, c0 = p.credits;
            Check(cost > 100 && cost < 1200, $"the bill for 95 points at {v.repairRate:0} cr a point and the shop's rate {lanark.mechanic.rate:0}: {cost} cr");
            Check(wear.Repair() && Mathf.Approximately(wear.Condition, 1f) && p.credits == c0 - cost && p.repairs == 1, "repaired and paid");
        }

        rep.AppendLine().AppendLine("## Sanity").AppendLine();
        var san = game.Sanity;
        Check(san.Phase == 0 && Mathf.Approximately(san.SpeedScale, 1f) && Mathf.Approximately(san.ProfitMultiplier, 1f), "rested: full speed, no bonus");
        {
            // the curve
            Check(Mathf.Approximately(san.Curve(12f), 0.5f) && Mathf.Approximately(san.Curve(20f), 0.25f) && Mathf.Approximately(san.Curve(32f), 0f) && Mathf.Approximately(san.Curve(6f), 0.75f), "the bar: 50 % at 12 h, 25 % at 20 h, empty at 32 h");
            Check(Mathf.Approximately(san.HoursFor(0.75f), 6f) && Mathf.Approximately(san.HoursFor(0.5f), 12f), "a part refill starts the clock part way (75 %: 6 h in)");
            san.TestSetAwake(12f);
            foreach (var x in PlayerFrames(2)) yield return x;
            Check(san.Phase == 1 && Mathf.Approximately(san.SpeedScale, 0.5f) && Mathf.Approximately(san.ProfitMultiplier, 1.25f) && Mathf.Approximately(car.speedScale, 0.5f) && Mathf.Approximately(game.Player.speedScale, 0.5f), "12 h: half speed on foot and in the truck, pays +25 %");
            int c0 = p.credits; game.Earn(100, "test");
            Check(p.credits == c0 + 125, $"100 cr pays 125 ({p.credits - c0})");
            san.TestSetAwake(20f);
            foreach (var x in PlayerFrames(2)) yield return x;
            Check(san.Phase == 2 && Mathf.Approximately(san.SpeedScale, 0.25f) && Mathf.Approximately(san.ProfitMultiplier, 1.5f), "20 h: a quarter speed, +50 %");
            san.TestSetAwake(32f);
            foreach (var x in PlayerFrames(2)) yield return x;
            Check(san.Phase == 3 && Mathf.Approximately(san.SpeedScale, 0.1f) && Mathf.Approximately(san.ProfitMultiplier, 1.75f), "32 h: a tenth speed, +75 %");
            Check(san.PassOutAt >= 32f && san.PassOutAt <= 32f + san.passOutWindow, $"the pass-out is scheduled within the window ({san.PassOutAt - 32f:0.0} h on)");
            san.TestSetAwake(1f);
            // the drain, and faster by a tornado: the game at its first tornado, the truck 0.3 mi from it
            var sc = world.game.Scenario;
            double tAt = double.MaxValue; int which = -1;
            for (int i = 0; i < sc.storms.Count; i++)
                foreach (var t in sc.storms[i].InGame(world.game.gameLength, world.game.pace))
                {
                    double at = sc.storms[i].startMin + (t.formMin + 3) / world.game.pace;
                    if (at < tAt) { tAt = at; which = i; }
                }
            if (which >= 0)
            {
                world.game.gameMinutes = (float)tAt; world.game.Apply();
                foreach (var x in PlayerFrames(3)) yield return x;
                var tor = world.game.Storms[which];
                var g = tor.TornadoGroundWorld;
                var from = g + new Vector3(0.7071f, 0f, -0.7071f) * (0.2f * 1609.344f);
                car.Teleport(from, Quaternion.LookRotation(Vector3.ProjectOnPlane(g - from, Vector3.up).normalized, Vector3.up));
                world.SetClockSpeed(1);   // 10x: the tornado moves a tenth of a mile in the second measured
                foreach (var x in PlayerFrames(3)) yield return x;
                Check(san.NearTornado && san.NearestTornadoMi < 0.5f, $"within half a mile of the tornado ({san.NearestTornadoMi:0.00} mi)");
                float a0 = san.AwakeHours, clock = 0f, t0 = Time.unscaledTime;
                while (Time.unscaledTime - t0 < 1f) { clock += game.ClockDeltaHours; yield return null; }
                float drained = san.AwakeHours - a0;
                Check(clock > 0.001f && Mathf.Abs(drained / clock - san.tornadoDrain) < 0.1f, $"by the tornado the bar drains {drained / Mathf.Max(clock, 1e-6f):0.00}x the clock (clock {clock * 60f:0.00} min, awake +{drained * 60f:0.00} min)");
                var far = g + new Vector3(0.7071f, 0f, -0.7071f) * (3f * 1609.344f);
                car.Teleport(far, Quaternion.identity);
                foreach (var x in PlayerFrames(3)) yield return x;
                a0 = san.AwakeHours; clock = 0f; t0 = Time.unscaledTime;
                while (Time.unscaledTime - t0 < 1f) { clock += game.ClockDeltaHours; yield return null; }
                drained = san.AwakeHours - a0;
                Check(!san.NearTornado && Mathf.Abs(drained / Mathf.Max(clock, 1e-6f) - 1f) < 0.1f, $"away from it the bar drains with the clock ({drained / Mathf.Max(clock, 1e-6f):0.00}x)");
                world.SetClockSpeed(0);
            }
            else Check(false, "the day has a tornado");
        }

        rep.AppendLine().AppendLine("## A night at the motel").AppendLine();
        var sleep = game.Sleep;
        Check(Mathf.Approximately(sleep.quoteHold, 5f) && sleep.fadeOut > 0.5f && sleep.fadeIn > 0.5f, $"the quote holds {sleep.quoteHold} s between fades");
        sleep.fadeOut = 0.25f; sleep.quoteIn = 0.2f; sleep.quoteHold = 0.5f; sleep.quoteOutS = 0.2f; sleep.fadeIn = 0.25f;   // (shortened for the check)
        {
            var spot = lanark.motel.sleep;
            car.Teleport(spot.Position, Quaternion.Euler(0f, spot.yaw, 0f));
            foreach (var x in Seconds(0.6f)) yield return x;
            Check(sleep.SpotTown == lanark && sleep.SpotKind == "motel", $"on the guest space at {lanark.motel.name}");
            int day0 = p.day, c0 = p.credits, rate = lanark.motel.rate;
            san.TestSetAwake(14f);
            ChaseSetup.chaseCredits = 321;
            Check(sleep.Sleep(), "F: sleeping");
            bool sawBlack = false, sawQuote = false; float t0 = Time.unscaledTime;
            while (sleep.Busy && Time.unscaledTime - t0 < 20f)
            {
                if (sleep.Black > 0.99f) sawBlack = true;
                if (sleep.TextAlpha > 0.99f && sleep.Black > 0.99f) sawQuote = true;
                yield return null;
            }
            Check(!sleep.Busy && sawBlack && sawQuote, $"the screen went black and the quote showed in full ({sleep.LastQuote.Split('\n')[0]})");
            Check(p.day == day0 + 1 && Mathf.Abs(ChaseSetup.hour - 8f) < 0.01f && Mathf.Abs(panel.hour - 8f) < 0.01f, $"it is 8 am on day {p.day}");
            Check(Mathf.Approximately(p.sanity, 1f) && Mathf.Approximately(p.awakeHours, 0f) && san.Phase == 0, "rested to 100 %");
            Check(p.credits == c0 - rate && p.motelNights == 1, $"the night cost {rate} cr");
            Check(world.game.Scenario == null && !world.LiveStorms().Any() && ChaseSetup.morning, "the storms are gone and it is morning");
            Check(fc.Showing && game.LastDayCredits == 321 && game.LastDay == day0, "the forecast is up with yesterday's tally");
            var onDisk = ChaseProfile.Load();
            Check(onDisk.day == p.day && onDisk.motelNights == 1 && onDisk.credits == p.credits, "the career was saved to disk");
        }

        rep.AppendLine().AppendLine("## Car camping").AppendLine();
        {
            fc.Choose("sw"); fc.Confirm();
            foreach (var x in PlayerFrames(3)) yield return x;
            Check(!ChaseSetup.morning && TestMapPlaces.Flat(car.transform.position, hk.gas.sleep.Position) < 3f, $"day {p.day} from {hk.gas.name}");
            foreach (var x in Seconds(0.6f)) yield return x;
            Check(sleep.SpotTown == hk && sleep.SpotKind == "gas", "parked on the overnight space");
            int cost = sleep.CostOf(hk, "gas"), c0 = p.credits, day0 = p.day;
            Check(cost == Mathf.CeilToInt(hk.motel.rate / 3f), $"car camping costs a third of the motel ({cost} vs {hk.motel.rate})");
            san.TestSetAwake(10f);
            Check(sleep.Sleep(), "sleeping in the truck");
            float t0 = Time.unscaledTime;
            while (sleep.Busy && Time.unscaledTime - t0 < 20f) yield return null;
            Check(p.day == day0 + 1 && p.credits == c0 - cost && Mathf.Approximately(p.sanity, 0.75f) && Mathf.Approximately(p.awakeHours, 6f) && p.carCampNights == 1, $"day {p.day}: {cost} cr, rested to 75 % (6 h into the clock)");
        }

        rep.AppendLine().AppendLine("## Passing out").AppendLine();
        {
            fc.Confirm();
            foreach (var x in PlayerFrames(3)) yield return x;
            // the truck rolling: the chaser has to stop first
            int day0 = p.day; var drone0 = game.Drone;
            car.acceptInput = false; car.testThrottle = 1f;
            // rolling first, then the moment comes (while still: it passes out at once, as it should)
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < 1.5f) yield return null;
            san.TestSetAwake(33f); san.TestPassOutNow();
            t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < 1.0f) yield return null;
            Note($"rolling: passingOut {san.PassingOut}, warning '{san.Warning}', passOutAt {san.PassOutAt:0.00}, awake {san.AwakeHours:0.00}, phase {san.Phase}, sleep busy {sleep.Busy} (forced {sleep.LastForced}, sleeps {sleep.Sleeps}), speed {car.SpeedKph:0.0} km/h, morning {ChaseSetup.morning}, on foot {game.Player.OnFoot}, drone {drone0.Flying}");
            Check(san.PassingOut && san.Warning.Contains("STOP") && !sleep.Busy && car.SpeedKph > 3f, $"rolling at {car.SpeedKph:0} km/h: told to stop, still awake");
            car.testThrottle = 0f; car.testBrake = 1f;
            car.Body.linearVelocity = Vector3.zero; car.Body.angularVelocity = Vector3.zero;
            foreach (var x in PlayerFrames(4)) yield return x;
            Check(sleep.Busy && sleep.LastForced && sleep.LastQuote == "you passed out.", "stopped: out cold, \"you passed out.\"");
            t0 = Time.unscaledTime;
            while (sleep.Busy && Time.unscaledTime - t0 < 20f) yield return null;
            Check(p.day == day0 + 1 && Mathf.Approximately(p.sanity, 0.5f) && p.passOuts == 1 && fc.Showing, $"woke on day {p.day} at half, the forecast up");
            car.testBrake = 0f; car.acceptInput = true;
            // the drone must come home first
            fc.Confirm();
            foreach (var x in PlayerFrames(3)) yield return x;
            var drone = game.Drone;
            var dg = ChaseCatalog.GearItem("drone");
            if (!p.CanAfford(dg.price)) p.credits += dg.price;
            Check(p.Buy(dg), "the drone is bought");
            drone.SetOwnedNow();
            foreach (var x in PlayerFrames(2)) yield return x;
            drone.Launch();
            foreach (var x in PlayerFrames(2)) yield return x;
            Check(drone.Flying, "the drone is up");
            drone.TestPlace(car.transform.position + Vector3.forward * 60f + Vector3.up * 30f, car.transform.position);
            day0 = p.day;
            san.TestSetAwake(33f); san.TestPassOutNow();
            foreach (var x in PlayerFrames(3)) yield return x;
            Check(san.PassingOut && drone.Returning && !sleep.Busy, "the drone is called home first");
            t0 = Time.unscaledTime;
            while (drone.Flying && Time.unscaledTime - t0 < 40f) yield return null;
            foreach (var x in PlayerFrames(4)) yield return x;
            Check(!drone.Flying && sleep.Busy && sleep.LastForced, "landed, then out cold");
            t0 = Time.unscaledTime;
            while (sleep.Busy && Time.unscaledTime - t0 < 20f) yield return null;
            Check(p.day == day0 + 1 && p.passOuts == 2, $"day {p.day}");
            // on foot: on the spot
            fc.Confirm();
            foreach (var x in PlayerFrames(3)) yield return x;
            game.Player.GetOut();
            foreach (var x in PlayerFrames(2)) yield return x;
            day0 = p.day;
            san.TestSetAwake(33f); san.TestPassOutNow();
            foreach (var x in PlayerFrames(4)) yield return x;
            Check(sleep.Busy && sleep.LastForced, "on foot: out on the spot");
            t0 = Time.unscaledTime;
            while (sleep.Busy && Time.unscaledTime - t0 < 20f) yield return null;
            Check(p.day == day0 + 1 && p.passOuts == 3, $"day {p.day}");
            if (game.Player.OnFoot) game.Player.GetIn();
        }

        rep.AppendLine().AppendLine("## Continuing").AppendLine();
        {
            int day0 = p.day;
            ChaseSetup.BackToMenu(); game.ApplyState();
            foreach (var x in PlayerFrames(2)) yield return x;
            Check(game.Menu.page == ChaseMenu.Page.Main && !fc.Showing, "back at the main menu");
            ChaseSetup.BeginChase(); game.Menu.StartChase();
            foreach (var x in PlayerFrames(3)) yield return x;
            Check(ChaseSetup.morning && fc.Showing && p.day == day0 && Mathf.Abs(ChaseSetup.hour - 8f) < 0.01f, $"the career continues from the morning of day {p.day}");
            var prev = TestMapPlaces.TownOf(towns, p.spawnTown);
            Check(prev != null && TestMapPlaces.Flat(car.transform.position, prev.gas.sleep.Position) < 3f, $"parked at the last station ({prev?.name})");
            ChaseSetup.BackToMenu(); game.ApplyState();
            foreach (var x in PlayerFrames(2)) yield return x;
        }
    }
}

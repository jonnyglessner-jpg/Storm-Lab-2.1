// The test map's storm and lighting panel (Tab shows or hides it; drag it by its title). Map: the plains, Dixie or
// Midwest map (loads that scene, keeping the storm setup and the clock). Storm: setup, seed, storm clock and speed, jumps to the next wall cloud or tornado, bringing the storm to the truck, lighting model, lightning.
// Lighting: a 24 hour clock that drives the sun and the moon from the local solar time, the day of the year, the
// latitude and the moon's phase, with an optional running clock, and the camera's exposure: Auto adapts to the scene's
// light level as StormLab 2.0's night model does (SkyLighting: unchanged by day, a moonlit night shown at the storm's
// nightBrightness, 1.2 % of day, never darker than its nightFloor), Manual takes an EV100.
//
// The clock turns the scene's sun and moon Lights and dims them as they set (the moon's light is about 0.25 lux at full),
// so the terrain, HDRP's sky and the storm share one sun and moon. The storm's unit of light is held at the full sun's
// (StormRenderer.luminanceScale), so its night is the night the storm's own lighting model computes rather than the
// dimmed sun Light. The panel works on a copy of the scene's volume profile; nothing it changes is saved.
using System;
using System.Linq;
using StormLab.Lightning;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace StormLab.TestMap
{
    [DefaultExecutionOrder(95)]   // before the storm (100) samples its sun
    public class TestMapStormPanel : MonoBehaviour
    {
        public StormRenderer storm;
        public TestMapWorld world;
        [Tooltip("the scene's sun (the storm's sun Light when empty)")]
        public Light sun;
        [Tooltip("a moon Light; one is made at Start when empty")]
        public Light moon;
        public bool show = true;

        [Header("Clock")]
        [Tooltip("local solar time, hours: 12 is solar noon")]
        [Range(0f, 24f)] public float hour = 18.8f;
        [Range(1, 366)] public int dayOfYear = 145;
        [Tooltip("degrees north; the map is at 38.07 N")]
        [Range(-66f, 66f)] public float latitude = 38.07f;
        [Tooltip("0 new, 0.25 first quarter, 0.5 full, 0.75 last quarter")]
        [Range(0f, 1f)] public float moonPhase = 0.5f;
        [Tooltip("run the clock")]
        public bool clockRuns;
        [Tooltip("clock hours per real minute while it runs")]
        [Range(0.05f, 6f)] public float hoursPerMinute = 1f;

        [Header("Exposure")]
        [Tooltip("adapt to the scene's light level as 2.0's night model does (day 13.5 EV; a moonlit night shown at 8 % of day, about 0.5 EV); off: manualEV")]
        public bool autoExposure = true;
        [Range(-4f, 3f)] public float compensation = 0f;
        [Range(-6f, 16f)] public float manualEV = 13.5f;
        [Tooltip("the sun's illuminance with it well up, lux (HDRP's daylight)")]
        public float sunLux = 100000f;
        [Tooltip("a full moon's illuminance, lux")]
        public float fullMoonLux = 0.25f;

        /// <summary>the exposure in use, EV100</summary>
        public float ExposureEV { get; private set; }
        public Vector2 SunAzEl { get; private set; }
        public Vector2 MoonAzEl { get; private set; }

        Exposure exposure;
        Rect win = new Rect(16, 300, 470, 10);
        Vector2 scroll;
        GUIStyle head, small, wrap;
        string seedText, gameSeedText;
        bool switchGame;
        static readonly string[] Presets = StormPreset.All.Select(p => p.key).ToArray();
        static readonly float[] Speeds = { 0f, 1f, 10f, 30f, 60f };
        // the maps (each scene must be in the build settings; TestMapBuilder adds them)
        static readonly (string name, string scene)[] Maps =
            { ("plains", "Assets/Scenes/TestMap.unity"), ("dixie", "Assets/Scenes/DixieMap.unity"), ("midwest", "Assets/Scenes/MidwestMap.unity") };
        // what the next map takes over from this one (set just before its scene loads)
        static string carryPreset; static float carryHour = -1f;
        string loadingMap; int loadingFrame;

        void Start()
        {
            if (!storm && world) storm = world.storm;
            if (!storm) storm = FindAnyObjectByType<StormRenderer>();
            if (!sun && storm) sun = storm.sun;
            if (!moon)
            {
                var go = new GameObject("Moon");
                moon = go.AddComponent<Light>(); moon.type = LightType.Directional;
                go.AddComponent<HDAdditionalLightData>();
                moon.lightUnit = LightUnit.Lux; moon.color = new Color(0.82f, 0.86f, 1f); moon.shadows = LightShadows.None;
            }
            // the scene's exposure, on a copy of its profile (the asset is left alone)
            foreach (var v in FindObjectsByType<Volume>(FindObjectsSortMode.None))
                if (v.isGlobal && v.sharedProfile && v.sharedProfile.Has<Exposure>()) { v.profile.TryGet(out exposure); break; }
            if (storm)
            {
                storm.sunSource = SunSource.TimeOfDay;
                storm.sun = sun; storm.moon = moon;
                // the storm's unit of light stays the full sun's: its own model makes the night
                storm.luminanceScale = sunLux * StormRenderer.SunToLuminance / Mathf.PI;
                storm.nightAdaptation = false;
                if (carryPreset != null && storm.preset != carryPreset) { storm.preset = carryPreset; storm.Replan(); }
                seedText = storm.seed.ToString();
            }
            if (carryHour >= 0f) hour = carryHour;
            carryPreset = null; carryHour = -1f;
            Apply();
        }

        /// <summary>Load another map, with this storm setup and clock.</summary>
        void SwitchMap(string scene)
        {
            carryPreset = storm ? storm.preset : null; carryHour = hour;
            SceneManager.LoadScene(scene, LoadSceneMode.Single);
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.tabKey.wasPressedThisFrame) show = !show;
            if (switchGame && world) { switchGame = false; world.SetGameMode(!world.gameMode); }
            // loaded a frame after the click, so the window can say so first (a map takes some seconds)
            if (loadingMap != null && Time.frameCount > loadingFrame + 2) { var m = loadingMap; loadingMap = null; SwitchMap(m); }
            if (clockRuns) hour = Mathf.Repeat(hour + Time.deltaTime / 60f * hoursPerMinute, 24f);
            Apply();
        }

        /// <summary>Put the sun, moon, storm and exposure at the panel's clock (also called by tests).</summary>
        public void Apply()
        {
            if (!storm) return;
            storm.solarHour = hour; storm.dayOfYear = dayOfYear; storm.latitude = latitude; storm.moonPhase = moonPhase;
            var s = StormRenderer.SolarPosition(hour, latitude, dayOfYear);
            var m = StormRenderer.MoonPosition(hour, latitude, dayOfYear, moonPhase);
            SunAzEl = s; MoonAzEl = m;
            // a light below the horizon lights nothing: fade each out over the last degrees as it sets
            if (sun)
            {
                sun.transform.rotation = Quaternion.LookRotation(-StormRenderer.StormToWorldDir(StormRenderer.SunVol(s.x, s.y)));
                sun.intensity = sunLux * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-1f, 3f, s.y));
                sun.enabled = sun.intensity > 1e-3f;
            }
            if (moon)
            {
                moon.transform.rotation = Quaternion.LookRotation(-StormRenderer.StormToWorldDir(StormRenderer.SunVol(m.x, m.y)));
                float lit = SkyLighting.MoonLit(moonPhase);
                moon.intensity = fullMoonLux * lit * lit * lit * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-1f, 3f, m.y));
                moon.enabled = moon.intensity > 1e-5f && !(sun && sun.enabled);
            }
            // the exposure: 13.5 EV by day, opened by the night model's adaptation gain after sunset (its eye: unchanged above
            // sunset levels, then darker as a power of the real level, a moonlit night at nightBrightness of day)
            var night = storm.Night; night.adapt = true;
            float gain = storm.PhysicalSky ? SkyLighting.Evaluate(storm.SunDirVol, storm.MoonDirVol, night).gain : 1f;
            ExposureEV = autoExposure ? Mathf.Clamp(13.5f - Mathf.Log(Mathf.Max(gain, 1e-6f), 2f), -6f, 16f) - compensation : manualEV;
            if (exposure) { exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(ExposureEV); }
        }

        // ------------------------------------------------------------------ the window
        void OnGUI()
        {
            if (!show || (world && world.hudHidden) || !storm || storm.Plan == null) return;
            if (head == null)
            {
                head = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 14 };
                small = new GUIStyle(GUI.skin.label) { fontSize = 12 };
                wrap = new GUIStyle(small) { wordWrap = true };
            }
            // sized for 1080p; larger screens scale it up
            float k = Mathf.Max(1f, Screen.height / 1080f);
            var keep = GUI.matrix; GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1f));
            win.height = Mathf.Min(Screen.height / k - win.y - 16, 820);
            win = GUILayout.Window(0x570A, win, Draw, "Storm and lighting   (Tab hides)");
            GUI.matrix = keep;
        }

        void Draw(int id)
        {
            scroll = GUILayout.BeginScrollView(scroll);
            var P = storm.Plan;
            // ---- the map
            GUILayout.BeginHorizontal();
            GUILayout.Label("Map", head, GUILayout.Width(44));
            string active = SceneManager.GetActiveScene().path;
            foreach (var (name, scene) in Maps)
                if (GUILayout.Toggle(active == scene, name, "Button") && active != scene && loadingMap == null) { loadingMap = scene; loadingFrame = Time.frameCount; }
            GUILayout.EndHorizontal();
            if (loadingMap != null) GUILayout.Label("loading the map ...", small);
            // ---- storm, or the game
            bool gm = GUILayout.Toggle(world && world.gameMode, "game mode: two storms on an hour-long game clock (G)");
            // switched in Update: the window's layout changes with the mode
            if (world && gm != world.gameMode) switchGame = true;
            if (InGame) DrawGame();
            else
            {
                GUILayout.Label("Storm", head);
                GUILayout.BeginHorizontal();
                foreach (var p in Presets)
                    if (GUILayout.Toggle(storm.preset == p, p, "Button") && storm.preset != p) { storm.preset = p; storm.Replan(); Respawn(); }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                bool varied = GUILayout.Toggle(storm.varied, "varied cycles");
                if (varied != storm.varied) { storm.varied = varied; storm.Replan(); Respawn(); }
                GUILayout.Label("seed", GUILayout.Width(34));
                seedText = GUILayout.TextField(seedText ?? storm.seed.ToString(), GUILayout.Width(70));
                if (GUILayout.Button("set", GUILayout.Width(36)) && int.TryParse(seedText, out var sd)) { storm.seed = sd; storm.Replan(); Respawn(); }
                if (GUILayout.Button("-", GUILayout.Width(24))) { storm.seed--; seedText = storm.seed.ToString(); storm.Replan(); Respawn(); }
                if (GUILayout.Button("+", GUILayout.Width(24))) { storm.seed++; seedText = storm.seed.ToString(); storm.Replan(); Respawn(); }
                if (GUILayout.Button("random", GUILayout.Width(62))) { storm.seed = UnityEngine.Random.Range(1, 1000000); seedText = storm.seed.ToString(); storm.Replan(); Respawn(); }
                GUILayout.EndHorizontal();
                GUILayout.Label($"{P.kind}{(P.cycles != null ? ": " + string.Join(", ", P.cycles.Select(c => c.shape.ToString().ToLowerInvariant() + (c.tornadic ? "" : " (no tornado)"))) : storm.varied ? "" : ": teaching sequence")} · {P.duration:0} min", small);

                float t = (float)storm.StormTime;
                GUILayout.Label($"Storm time {Mathf.FloorToInt(t)}:{Mathf.FloorToInt(t * 60 % 60):00} of {P.duration:0}:00   ·   {storm.Frame.stage}", small);
                float nt = GUILayout.HorizontalSlider(t, 0f, (float)P.duration);
                if (Mathf.Abs(nt - t) > 1e-4f) storm.StormTime = nt;
                GUILayout.BeginHorizontal();
                foreach (var sp in Speeds)
                {
                    bool on = sp == 0f ? !storm.playing : storm.playing && Mathf.Approximately(storm.minutesPerSecond * 60f, sp);
                    if (GUILayout.Toggle(on, sp == 0f ? "pause" : sp == 1f ? "real time" : $"{sp:0}x", "Button") && !on)
                    {
                        if (sp == 0f) storm.playing = false; else { storm.playing = true; storm.minutesPerSecond = sp / 60f; }
                    }
                }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("next wall cloud")) Jump(c => c.wall, 0.2);
                if (GUILayout.Button("next tornado")) Jump(c => c.tornado ? c.form : double.NaN, 0.3);
                if (GUILayout.Button("-5 min")) storm.StormTime = Math.Max(0, storm.StormTime - 5);
                if (GUILayout.Button("+5 min")) storm.StormTime = Math.Min(P.duration, storm.StormTime + 5);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("bring the storm to the truck")) Place();
                if (GUILayout.Button("restart off the map")) Respawn();
                GUILayout.EndHorizontal();
                GUILayout.Label(storm.Frame.caption, wrap);
            }

            // ---- lighting
            GUILayout.Space(6);
            GUILayout.Label("Lighting", head);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Clock {Mathf.FloorToInt(hour):00}:{Mathf.FloorToInt(hour * 60 % 60):00} solar   ·   {Phase(SunAzEl.y)}", small);
            clockRuns = GUILayout.Toggle(clockRuns, "run", GUILayout.Width(50));
            GUILayout.EndHorizontal();
            hour = GUILayout.HorizontalSlider(hour, 0f, 24f);
            GUILayout.BeginHorizontal();
            foreach (var (label, h) in new[] { ("dawn", 5.2f), ("morning", 8f), ("noon", 12f), ("afternoon", 16f), ("sunset", 19.1f), ("dusk", 19.8f), ("night", 23f) })
                if (GUILayout.Button(label)) hour = h;
            GUILayout.EndHorizontal();
            if (clockRuns) { GUILayout.Label($"clock speed {hoursPerMinute:0.00} h per minute", small); hoursPerMinute = GUILayout.HorizontalSlider(hoursPerMinute, 0.05f, 6f); }
            GUILayout.Label($"Sun {SunAzEl.x:0} deg, {SunAzEl.y:0.0} deg up   ·   moon {MoonAzEl.x:0} deg, {MoonAzEl.y:0.0} deg up", small);
            GUILayout.Label($"Day of year {dayOfYear} ({Date(dayOfYear)})", small);
            dayOfYear = Mathf.RoundToInt(GUILayout.HorizontalSlider(dayOfYear, 1, 366));
            GUILayout.Label($"Latitude {latitude:0.0} deg", small);
            latitude = GUILayout.HorizontalSlider(latitude, -66f, 66f);
            GUILayout.Label($"Moon phase {MoonName(moonPhase)} ({SkyLighting.MoonLit(moonPhase) * 100:0} % lit)", small);
            moonPhase = GUILayout.HorizontalSlider(moonPhase, 0f, 1f);

            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(storm.lighting == StormLighting.Unified, "unified", "Button")) storm.lighting = StormLighting.Unified;
            if (GUILayout.Toggle(storm.lighting == StormLighting.Physical, "physical", "Button")) storm.lighting = StormLighting.Physical;
            if (GUILayout.Toggle(storm.lighting == StormLighting.Website, "website", "Button")) storm.lighting = StormLighting.Website;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("wall-cloud base", small, GUILayout.Width(110));
            if (GUILayout.Toggle(storm.wallBase == WallBase.Smooth, "smooth", "Button")) storm.wallBase = WallBase.Smooth;
            if (GUILayout.Toggle(storm.wallBase == WallBase.Website, "website (ragged)", "Button")) storm.wallBase = WallBase.Website;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("rain layout", small, GUILayout.Width(110));
            if (GUILayout.Toggle(storm.precip == PrecipLayout.Tilted, "tilted updraft", "Button")) storm.precip = PrecipLayout.Tilted;
            if (GUILayout.Toggle(storm.precip == PrecipLayout.Website, "website", "Button")) storm.precip = PrecipLayout.Website;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("cloud detail", small, GUILayout.Width(110));
            if (GUILayout.Toggle(storm.cloudDetail == CloudDetail.Enhanced, "enhanced", "Button")) storm.cloudDetail = CloudDetail.Enhanced;
            if (GUILayout.Toggle(storm.cloudDetail == CloudDetail.Website, "website", "Button")) storm.cloudDetail = CloudDetail.Website;
            GUILayout.EndHorizontal();
            if (storm.PhysicalSky)
            {
                var L = storm.Lighting;
                GUILayout.Label($"Key light: {(L.keyIsMoon ? "moon" : "sun")}   ·   scene level {L.level:0.###E+0} of day", small);
                storm.nightBrightness = Slider("moonlit night shown at (of day)", storm.nightBrightness, 0.002f, 0.095f);
                storm.lightPollution = Slider("town light", storm.lightPollution, 0f, 1f);
                storm.nightFill = Slider("night fill under the base", storm.nightFill, 0f, 1f);
                storm.purkinje = Slider("night blue shift", storm.purkinje, 0f, 1f);
                storm.dropletDiameter = Slider("droplet diameter (um)", storm.dropletDiameter, 5f, 50f);
                if (storm.lighting == StormLighting.Unified)
                {
                    storm.diffusion = Slider("diffusion (darker under the storm)", storm.diffusion, 0.05f, 2f);
                    storm.diffuseSun = Slider("sunlight diffused through cloud", storm.diffuseSun, 0f, 1f);
                    storm.rainExtinction = Slider("rain extinction (x drawn)", storm.rainExtinction, 1f, 5f);
                }
            }
            else GUILayout.Label("The web build's lighting: fixed evening colours, no night model.", small);
            GUILayout.BeginHorizontal();
            autoExposure = GUILayout.Toggle(autoExposure, "auto exposure");
            GUILayout.Label($"EV100 {ExposureEV:0.0}", small);
            GUILayout.EndHorizontal();
            if (autoExposure) compensation = Slider("compensation (stops brighter)", compensation, -4f, 3f);
            else manualEV = Slider("EV100", manualEV, -6f, 16f);

            // ---- lightning
            var all = (InGame ? world.game.Storms.Where(r => r) : new[] { storm }).Select(r => r.GetComponent<StormLightning>()).Where(l => l && l.Flashes).ToList();
            var sl = all.FirstOrDefault(l => l.isActiveAndEnabled) ?? all.FirstOrDefault();
            if (sl)
            {
                GUILayout.Space(6);
                GUILayout.Label("Lightning", head);
                GUILayout.BeginHorizontal();
                bool on = GUILayout.Toggle(sl.Flashes.enabled, "flashes");
                if (on != sl.Flashes.enabled) foreach (var l in all) { l.Flashes.enabled = on; l.Bridge.enabled = on; }
                bool th = GUILayout.Toggle(sl.Flashes.thunder, "thunder");
                foreach (var l in all) l.Flashes.thunder = th;
                GUILayout.Label($"clock: {sl.BoundClock}", small);
                GUILayout.EndHorizontal();
                float rate = Slider("rate x", sl.tuning.rateScale, 0f, 4f);
                if (!Mathf.Approximately(rate, sl.tuning.rateScale)) foreach (var l in all) { l.tuning.rateScale = rate; l.Rebind(); }
                foreach (var l in all)
                {
                    var a = l.isActiveAndEnabled ? l.Flashes.CurrentActivity : null;
                    if (a.HasValue) GUILayout.Label((all.Count > 1 ? l.name.Split(' ')[0] + " " + l.name.Split(' ')[1] + ": " : "") + a.Value, wrap);
                }
            }
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        bool InGame => world && world.gameMode && world.game && world.game.Scenario != null;

        /// <summary>The game (StormScenario): environment, game seed, precipitation, the game clock and its speed, and each
        /// storm's track and tornadoes. Any change to the setup lays out a new game from its start.</summary>
        void DrawGame()
        {
            var g = world.game; var sc = g.Scenario;
            GUILayout.Label("Game", head);
            GUILayout.BeginHorizontal();
            foreach (var p in Presets)
                if (GUILayout.Toggle(g.preset == p, p, "Button") && g.preset != p) { g.preset = p; g.Spawn(); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("game seed", GUILayout.Width(66));
            gameSeedText = GUILayout.TextField(gameSeedText ?? g.seed.ToString(), GUILayout.Width(70));
            if (GUILayout.Button("set", GUILayout.Width(36)) && int.TryParse(gameSeedText, out var sd)) { g.seed = sd; g.Spawn(); }
            if (GUILayout.Button("-", GUILayout.Width(24))) { g.seed--; gameSeedText = g.seed.ToString(); g.Spawn(); }
            if (GUILayout.Button("+", GUILayout.Width(24))) { g.seed++; gameSeedText = g.seed.ToString(); g.Spawn(); }
            if (GUILayout.Button("random", GUILayout.Width(62))) { g.seed = UnityEngine.Random.Range(1, 1000000); gameSeedText = g.seed.ToString(); g.Spawn(); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("precipitation", GUILayout.Width(84));
            foreach (ScenarioPrecip m in Enum.GetValues(typeof(ScenarioPrecip)))
                if (GUILayout.Toggle(g.precipitation == m, m == ScenarioPrecip.Auto ? "by lane" : "all " + m, "Button") && g.precipitation != m) { g.precipitation = m; g.Spawn(); }
            GUILayout.EndHorizontal();
            GUILayout.Label($"{sc}{(sc.headline >= 0 ? $", the strongest from storm {sc.headline + 1}" : "")}", wrap);

            float t = g.gameMinutes;
            GUILayout.Label($"Game time {Mathf.FloorToInt(t)}:{Mathf.FloorToInt(t * 60 % 60):00} of {g.gameLength:0}:00   ·   life cycles run {g.pace:0.#}x the game clock", small);
            float nt = GUILayout.HorizontalSlider(t, 0f, g.gameLength);
            if (Mathf.Abs(nt - t) > 1e-4f) g.gameMinutes = nt;
            GUILayout.BeginHorizontal();
            foreach (var sp in Speeds)
            {
                bool on = sp == 0f ? !g.playing : g.playing && Mathf.Approximately(g.minutesPerSecond * 60f, sp);
                if (GUILayout.Toggle(on, sp == 0f ? "pause" : sp == 1f ? "real time" : $"{sp:0}x", "Button") && !on)
                {
                    if (sp == 0f) g.playing = false; else { g.playing = true; g.minutesPerSecond = sp / 60f; }
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("next tornado")) NextGameTornado();
            if (GUILayout.Button("-5 min")) g.gameMinutes = Mathf.Max(0f, g.gameMinutes - 5f);
            if (GUILayout.Button("+5 min")) g.gameMinutes = Mathf.Min(g.gameLength, g.gameMinutes + 5f);
            if (GUILayout.Button("restart the game")) g.Spawn();
            GUILayout.EndHorizontal();
            for (int i = 0; i < sc.storms.Count; i++)
            {
                var st = sc.storms[i];
                double life = g.StormMinutes(i);
                string now = life < 0 ? "not started" : g.Alive(i) ? $"{g.Storms[i].Frame.stage}, {life:0} min into its life" : "gone";
                var tors = st.InGame(g.gameLength, g.pace).Select(x => $"EF{x.ef} {StormTornadoes.ShapeName[(int)x.shape].ToLowerInvariant()} {x.widthM:0} m wide at {st.startMin + x.formMin / g.pace:0} min").ToList();
                GUILayout.Label($"Storm {i + 1}: {st.precip}, {(st.northness > 0.5 ? "north" : "south")} lane, seed {st.seed}. Starts at {st.startMin:0.0} min {st.bearing:0} deg off the map, " +
                                $"onto it at {st.entryMin:0.0} min, mature at {st.matureMin:0.0} min. Now: {now}. Tornadoes in the game: {(tors.Count > 0 ? string.Join("; ", tors) : "none")}", wrap);
            }
        }

        /// <summary>Game time just before the next tornado of the game touches down, from any storm.</summary>
        void NextGameTornado()
        {
            var g = world.game; double best = double.MaxValue;
            foreach (var st in g.Scenario.storms)
                foreach (var x in st.InGame(g.gameLength, g.pace))
                {
                    double at = st.startMin + (x.formMin - 0.3) / g.pace;
                    if (at > g.gameMinutes + 0.02 && at < best) best = at;
                }
            if (best < double.MaxValue) g.gameMinutes = (float)best;
        }

        float Slider(string label, float v, float lo, float hi)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{label} {v:0.00}", small, GUILayout.Width(210));
            v = GUILayout.HorizontalSlider(v, lo, hi);
            GUILayout.EndHorizontal();
            return v;
        }

        /// <summary>Storm time to the next cycle event after now (its wall cloud or its tornado's touchdown), less a lead.</summary>
        void Jump(Func<LightningCycle, double> when, double leadMin)
        {
            var now = storm.StormTime;
            foreach (var c in StormLightningActivity.Cycles(storm.Plan))
            {
                double e = when(c);
                if (!double.IsNaN(e) && e - leadMin > now + 0.05) { storm.StormTime = Math.Max(0, e - leadMin); return; }
            }
        }

        /// <summary>A new storm (setup, seed or cycles changed): from towering cumulus, off the map on its seed's track.</summary>
        void Respawn() { if (world) world.SpawnStorm(); else { storm.StormTime = 0; storm.ResetVolume(); } }

        /// <summary>The storm's updraft 15 km upwind of the truck now, so the storm comes toward it.</summary>
        void Place()
        {
            var car = world ? world.car : null;
            var at = car ? car.transform.position : Vector3.zero;
            var m = new Vector3((float)storm.Plan.motion.x, 0f, (float)storm.Plan.motion.y);
            m = m.sqrMagnitude > 1e-4f ? m.normalized : Vector3.forward;
            var p = at - m * 15000f;
            storm.PlaceUpdraftAt(new Vector3(p.x, storm.origin.y, p.z), storm.StormTime);
            if (world) storm.DieLeaving(world.sizeM / 2);
        }

        static string Phase(float el) => el > 6 ? "day" : el > 0 ? "golden hour" : el > -6 ? "civil twilight" : el > -12 ? "nautical twilight" : el > -18 ? "astronomical twilight" : "night";
        static string MoonName(float p) => p < 0.03f || p > 0.97f ? "new" : p < 0.22f ? "waxing crescent" : p < 0.28f ? "first quarter" : p < 0.47f ? "waxing gibbous"
            : p < 0.53f ? "full" : p < 0.72f ? "waning gibbous" : p < 0.78f ? "last quarter" : "waning crescent";
        static string Date(int day) => new DateTime(2026, 1, 1).AddDays(day - 1).ToString("d MMMM");
    }
}

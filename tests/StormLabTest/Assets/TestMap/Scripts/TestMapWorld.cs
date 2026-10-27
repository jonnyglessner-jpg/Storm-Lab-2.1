// The plains test map at play time: puts the car at a named spot, runs the storm's clock, pushes the car with the
// tornado's wind, and draws the HUD (speed, where you are on the section grid, the storm and its tornado).
//
//   1-9  jump to a spot          T  storm clock speed (1x, 10x, 30x, 60x real time)    P  pause the storm
//   [ ]  storm clock -/+ 5 min   N  next storm (new seed)                               H  help on / off
//   Tab  the storm and lighting panel (TestMapStormPanel: storm controls, a 24 hour clock, exposure, lightning)
//   C  camera (chase: hold the right mouse button to look round, it eases back 0.5 s after letting go; roof)     F  free flight (V puts the truck under the camera)    R  reset the car upright
//   G  game mode: a StormScenario's two storms on one hour-long game clock (StormScenarioRunner) in place of the test
//      storm; T, P, [ ] and N then work on the game clock and the game's seed
//   M  sound on / off (TestMapAudio: tornado wind, rain, thunder, the truck, birds)
//   F1  hide all the HUD (the readouts, help, the Tab panel and the map), for clean views and screenshots
//   the map (TestMapMinimap, bottom right): GPS and the KDDC radar; click it to open it full screen, click a road there
//      to drive to it (turn-by-turn directions), Esc closes
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StormLab.TestMap
{
    [DefaultExecutionOrder(90)]
    public class TestMapWorld : MonoBehaviour
    {
        [System.Serializable]
        public struct Spot { public string name; public Vector3 position; public float heading; }

        [System.Serializable]
        public class Route { public string name; public string cls; public Vector3[] points; }

        public TestMapCar car;
        public TestMapCamera cameraRig;
        public StormRenderer storm;
        [Tooltip("storm minutes before the storm is fully mature (a supercell's first wall cloud, a pulse storm's 13 min) at " +
                 "which its updraft crosses onto the map")]
        public float enterBeforeMature = 2f;
        [Tooltip("compass bearings from the map's centre, degrees, that a storm may start in (off the map): west, south-west, south")]
        public Vector2 startBearings = new Vector2(165f, 285f);
        /// <summary>the last spawn's track: where the updraft starts (storm time 0) and crosses onto the map, world metres; the
        /// storm minutes at which it crosses and is mature; the start's compass bearing from the map's centre</summary>
        public Vector3 TrackStart { get; private set; }
        public Vector3 TrackEntry { get; private set; }
        public float EntryMinutes { get; private set; }
        public float MatureMinutes { get; private set; }
        public float StartBearing { get; private set; }
        /// <summary>false when the storm's own motion cannot bring it in from the start bearings, and it came in over
        /// whichever edge its motion crosses instead</summary>
        public bool FromAllowedSide { get; private set; }
        public Spot[] spots = new Spot[0];
        public Route[] routes = new Route[0];
        [Tooltip("map size, m (square, centred on the world origin)")]
        public float sizeM = 80467.2f;
        [Tooltip("elevation (m NAVD88) of world y = 0")]
        public float datum = 680f;
        [Tooltip("the GPS data in Resources: <name>.json (roads, radar site) and <name>Base.png (tools/testmap/navmap.py)")]
        public string navResource = "TestMapNav";
        [Tooltip("roads on the mile grid (the plains map): the HUD names the section line; off (the Dixie map): it names " +
                 "the nearest road from the GPS data")]
        public bool sectionGrid = true;
        public bool showHelp = true;
        [Tooltip("all the HUD hidden (F1): the readouts, help and the storm panel")]
        public bool hudHidden;
        [Tooltip("push the car with the tornadoes' wind (drag on a car-sized body)")]
        public bool tornadoWind = true;
        public int startSpot;
        [Header("Game mode")]
        [Tooltip("play a game (two storms on an hour-long clock, StormScenario) instead of the single test storm")]
        public bool gameMode;
        [Tooltip("the game's runner; made at Start when empty. Its storms copy the test storm's lighting and quality")]
        public StormScenarioRunner game;

        static readonly float[] Timelapses = { 1f, 10f, 30f, 60f };
        int timelapseIndex = 1, lastSpot;
        GUIStyle big, small;
        const float Mile = 1609.344f;

        void Start()
        {
            if (car && spots.Length > 0) TeleportTo(Mathf.Clamp(startSpot, 0, spots.Length - 1));
            if (storm) { storm.minutesPerSecond = Timelapses[timelapseIndex] / 60f; SpawnStorm(); }
            // the storm and lighting panel, also in scenes built before it existed
            if (storm && !GetComponent<TestMapStormPanel>()) { var panel = gameObject.AddComponent<TestMapStormPanel>(); panel.world = this; panel.storm = storm; }
            // the sound: storms, truck and ambience
            if (!GetComponent<TestMapAudio>()) gameObject.AddComponent<TestMapAudio>().world = this;
            // the GPS and radar map
            if (!GetComponent<TestMapMinimap>()) gameObject.AddComponent<TestMapMinimap>().world = this;
            if (gameMode) SetGameMode(true);
        }

        /// <summary>Game mode on: the test storm is switched off and a new game laid out from the game's seed, its storms
        /// starting off the map. Off: the game's storms are removed and the test storm starts afresh.</summary>
        public void SetGameMode(bool on)
        {
            gameMode = on;
            if (on)
            {
                if (!game)
                {
                    var go = new GameObject("Game");
                    go.transform.SetParent(transform, false);
                    game = go.AddComponent<StormScenarioRunner>();
                    if (storm) { game.preset = storm.preset; game.seed = storm.seed; }
                }
                game.gameObject.SetActive(true);
                game.mapHalfM = sizeM / 2;
                game.lookFrom = storm;
                if (storm) { game.groundY = storm.origin.y; game.tornado = storm.tornado; storm.gameObject.SetActive(false); }
                game.minutesPerSecond = Timelapses[timelapseIndex] / 60f;
                game.Spawn();
            }
            else
            {
                if (game) { game.Clear(); game.gameObject.SetActive(false); }
                if (storm) { storm.gameObject.SetActive(true); SpawnStorm(); }
            }
        }

        /// <summary>The storms on the map now: the game's live storms in game mode, else the test storm.</summary>
        public IEnumerable<StormRenderer> LiveStorms()
        {
            if (gameMode && game && game.Scenario != null)
            {
                for (int i = 0; i < game.Storms.Count; i++) if (game.Alive(i)) yield return game.Storms[i];
            }
            else if (storm && storm.isActiveAndEnabled && storm.Plan != null) yield return storm;
        }

        bool InGame => gameMode && game && game.Scenario != null;

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                for (int i = 0; i < Mathf.Min(9, spots.Length); i++)
                    if (kb[(Key)((int)Key.Digit1 + i)].wasPressedThisFrame) TeleportTo(i);
                if (kb.hKey.wasPressedThisFrame) showHelp = !showHelp;
                if (kb.f1Key.wasPressedThisFrame) hudHidden = !hudHidden;
                if (kb.gKey.wasPressedThisFrame) SetGameMode(!gameMode);
                if (InGame)
                {
                    if (kb.tKey.wasPressedThisFrame) { timelapseIndex = (timelapseIndex + 1) % Timelapses.Length; game.minutesPerSecond = Timelapses[timelapseIndex] / 60f; }
                    if (kb.pKey.wasPressedThisFrame) game.playing = !game.playing;
                    if (kb.rightBracketKey.wasPressedThisFrame) game.gameMinutes = Mathf.Min(game.gameMinutes + 5f, game.gameLength);
                    if (kb.leftBracketKey.wasPressedThisFrame) game.gameMinutes = Mathf.Max(game.gameMinutes - 5f, 0f);
                    if (kb.nKey.wasPressedThisFrame) { game.seed++; game.Spawn(); }
                }
                else if (storm && storm.Plan != null)
                {
                    if (kb.tKey.wasPressedThisFrame) { timelapseIndex = (timelapseIndex + 1) % Timelapses.Length; storm.minutesPerSecond = Timelapses[timelapseIndex] / 60f; }
                    if (kb.pKey.wasPressedThisFrame) storm.playing = !storm.playing;
                    if (kb.rightBracketKey.wasPressedThisFrame) storm.stormMinutes = Mathf.Min(storm.stormMinutes + 5f, (float)storm.Plan.duration);
                    if (kb.leftBracketKey.wasPressedThisFrame) storm.stormMinutes = Mathf.Max(storm.stormMinutes - 5f, 0f);
                    if (kb.nKey.wasPressedThisFrame) { storm.seed++; storm.Replan(); SpawnStorm(); }
                }
            }
            var gp = Gamepad.current;
            if (gp != null && spots.Length > 0)
            {
                if (gp.dpad.right.wasPressedThisFrame) TeleportTo((lastSpot + 1) % spots.Length);
                if (gp.dpad.left.wasPressedThisFrame) TeleportTo((lastSpot + spots.Length - 1) % spots.Length);
            }
        }

        /// <summary>Start the storm afresh: storm time 0 (towering cumulus), off the map on this seed's track (PlaceStorm).</summary>
        public void SpawnStorm()
        {
            if (!storm) return;
            PlaceStorm();
            storm.StormTime = 0;
            storm.ResetVolume();
        }

        /// <summary>When a storm is fully mature, storm minutes: a supercell's first wall cloud, a pulse storm's mature stage.</summary>
        public static double Maturity(StormPlan P) => P.kind == StormKind.Supercell ? (P.cycles?[0].start ?? 30) : P.kind == StormKind.Pulse ? 13 : 20;

        /// <summary>
        /// Put the storm on a track that starts off the map to the west, south-west or south (startBearings) and brings its
        /// updraft onto the map enterBeforeMature minutes before it is fully mature, then across it. The storm moves with its
        /// own environment's motion; the seed picks where along the map's west and south edges it comes in, so each seed
        /// branches out on its own track. Leaves the storm time as it is (SpawnStorm starts it at 0).
        /// </summary>
        public void PlaceStorm()
        {
            if (!storm) return;
            if (storm.Plan == null) storm.Replan();
            var P = storm.Plan;
            float half = sizeM / 2;
            double mature = Maturity(P), tEntry = System.Math.Max(0, mature - enterBeforeMature);
            var m = new Vector2((float)P.motion.x, (float)P.motion.y) * 60f;   // metres per storm minute
            float speed = m.magnitude;
            var u = speed > 1e-3f ? m / speed : new Vector2(0.7071f, 0.7071f);
            // a slow storm still starts a little way off the map (it then comes in a little before it is mature)
            float dist = Mathf.Max(speed * (float)tEntry, 1500f);
            var rng = new StormRng(storm.seed, 77);
            Vector2 bestE = new Vector2(-half, -half), bestS = bestE - u * dist; float bestScore = float.MaxValue;
            // First along the west and south edges, from the allowed bearings. A storm whose own motion cannot bring it in
            // from that side (the pulse setup drifts south-west) then comes in over whichever edge its motion does cross.
            for (int pass = 0; pass < 2 && bestScore > 0f; pass++)
            {
                bestScore = float.MaxValue; FromAllowedSide = pass == 0;
                for (int i = 0; i < 96; i++)
                {
                    // a point round the edge: west (north to south), south (west to east), east (south to north), north (east to west)
                    float a = (float)rng.U() * (pass == 0 ? 0.5f : 1f);
                    int side = Mathf.Min((int)(a * 4f), 3); float f = a * 4f - side;
                    var E = side == 0 ? new Vector2(-half, half - 2f * half * f) : side == 1 ? new Vector2(-half + 2f * half * f, -half)
                          : side == 2 ? new Vector2(half, -half + 2f * half * f) : new Vector2(half - 2f * half * f, half);
                    float inward = side == 0 ? u.x : side == 1 ? u.y : side == 2 ? -u.x : -u.y;
                    var S = E - u * dist;
                    float bearing = (Mathf.Atan2(S.x, S.y) * Mathf.Rad2Deg + 360f) % 360f;
                    // how far the track then runs across the map
                    float across = float.MaxValue;
                    if (u.x > 1e-4f) across = Mathf.Min(across, (half - E.x) / u.x); else if (u.x < -1e-4f) across = Mathf.Min(across, (-half - E.x) / u.x);
                    if (u.y > 1e-4f) across = Mathf.Min(across, (half - E.y) / u.y); else if (u.y < -1e-4f) across = Mathf.Min(across, (-half - E.y) / u.y);
                    // zero for a track that comes in over that edge (from the allowed bearings, first pass) and crosses at
                    // least half the map; otherwise how far off it is
                    float off = pass == 0 ? Mathf.Max(0f, startBearings.x - bearing) + Mathf.Max(0f, bearing - startBearings.y) : 0f;
                    float score = (inward > 0.05f ? 0f : 1000f) + off * 10f + Mathf.Max(0f, Mathf.Min(half, speed * 60f) - across) / 100f;
                    if (score < bestScore) { bestScore = score; bestE = E; bestS = S; StartBearing = bearing; }
                    if (score <= 0f) break;
                }
            }
            storm.PlaceUpdraftAt(new Vector3(bestS.x, storm.origin.y, bestS.y), 0);
            storm.DieLeaving(half);
            TrackStart = new Vector3(bestS.x, storm.origin.y, bestS.y); TrackEntry = new Vector3(bestE.x, storm.origin.y, bestE.y);
            EntryMinutes = speed > 1e-3f ? dist / speed : 0f; MatureMinutes = (float)mature;
        }

        void FixedUpdate()
        {
            if (!tornadoWind || !car) return;
            var wind = InGame ? game.TornadoWindAt(car.transform.position) : storm && storm.isActiveAndEnabled ? storm.TornadoWindAt(car.transform.position) : Vector3.zero;
            if (wind.sqrMagnitude < 1f) return;
            var rel = wind - Vector3.ProjectOnPlane(car.Body.linearVelocity, Vector3.up);
            const float rho = 1.1f, cdA = 3.2f;     // side-on drag area of a pickup, m^2
            car.externalForce += 0.5f * rho * cdA * rel.magnitude * rel;
        }

        public void TeleportTo(int i)
        {
            if (!car || i < 0 || i >= spots.Length) return;
            lastSpot = i;
            car.Teleport(spots[i].position, Quaternion.Euler(0f, spots[i].heading, 0f));
        }

        /// <summary>The section-line road nearest a world position, by name, and how far along the grid it is.</summary>
        public string GridPosition(Vector3 p)
        {
            float x = p.x + sizeM / 2, z = p.z + sizeM / 2;
            if (!sectionGrid)
            {
                var roads = GetComponent<TestMapMinimap>()?.Roads;
                string near = "";
                if (roads != null)
                {
                    var s = roads.Nearest(new Vector2(p.x, p.z));
                    if (s.edge >= 0 && s.off < 400f) near = roads.edges[s.edge].name;
                }
                return $"{near}   ({x / Mile:0.0} mi E, {z / Mile:0.0} mi N)";
            }
            int ix = Mathf.RoundToInt(x / Mile - 0.5f), iz = Mathf.RoundToInt(z / Mile - 0.5f);
            float dx = Mathf.Abs(x - (ix + 0.5f) * Mile), dz = Mathf.Abs(z - (iz + 0.5f) * Mile);
            string road = dx < dz ? $"Ave {ix}" : $"Rd {iz}";
            return $"{road}   ({x / Mile:0.0} mi E, {z / Mile:0.0} mi N)";
        }

        static string Compass(float deg)
        {
            string[] n = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
            return n[Mathf.RoundToInt(((deg % 360) + 360) % 360 / 22.5f) % 16];
        }

        void OnGUI()
        {
            if (big == null)
            {
                big = new GUIStyle(GUI.skin.label) { fontSize = 32, fontStyle = FontStyle.Bold };
                small = new GUIStyle(GUI.skin.label) { fontSize = 15 };
                big.normal.textColor = small.normal.textColor = Color.white;
            }
            if (!car || hudHidden) return;
            float h = Screen.height;
            var p = car.transform.position;
            GUI.Box(new Rect(16, h - 132, 420, 116), GUIContent.none);
            GUI.Label(new Rect(28, h - 130, 400, 44), $"{car.SpeedKph * 0.621371f:0} mph  {Compass(car.transform.eulerAngles.y)}", big);
            GUI.Label(new Rect(28, h - 86, 400, 70), $"{GridPosition(p)}\nelev {(p.y + datum) * 3.28084f:0} ft    camera: {(cameraRig ? cameraRig.ModeName : "")}", small);
            if (cameraRig && cameraRig.mode == TestMapCamera.Mode.Free)
            {
                // where the free camera is (the lines above are the truck's), so a screenshot says where a view was taken
                var c = cameraRig.transform; float e = sizeM / 2;
                float pitch = c.eulerAngles.x > 180f ? 360f - c.eulerAngles.x : -c.eulerAngles.x;
                GUI.Box(new Rect(16, h - 178, 420, 42), GUIContent.none);
                GUI.Label(new Rect(28, h - 175, 400, 40), $"free camera {(c.position.x + e) / Mile:0.0} mi E, {(c.position.z + e) / Mile:0.0} mi N, {(c.position.y + datum) * 3.28084f:0} ft\nlooking {c.eulerAngles.y:0} deg, {pitch:0} deg up    V  truck here", small);
            }

            if (InGame)
            {
                var sc = game.Scenario;
                var lines = new List<string>();
                float g = game.gameMinutes;
                lines.Add($"game clock {Mathf.FloorToInt(g):0}:{Mathf.FloorToInt(g * 60 % 60):00} / {game.gameLength:0} min   {game.minutesPerSecond * 60f:0}x{(game.playing ? "" : "  paused")}   {sc.preset.key} game {sc.seed}");
                lines.Add($"built for EF{sc.topEf} at most{(sc.headline >= 0 ? $", strongest from storm {sc.headline + 1}" : "")}   ·   life cycles {game.pace:0.#}x the game clock");
                for (int i = 0; i < game.Storms.Count; i++)
                {
                    var st = sc.storms[i]; var r = game.Storms[i];
                    double life = game.StormMinutes(i);
                    string name = $"Storm {i + 1} ({st.precip}, {(st.northness > 0.5 ? "north" : "south")} lane)";
                    if (life < 0) { lines.Add($"{name}: starts in {Mathf.FloorToInt((float)-life / game.pace)}:{Mathf.FloorToInt((float)(-life / game.pace) * 60 % 60):00}, from {st.bearing:0} deg"); continue; }
                    if (!game.Alive(i)) { lines.Add($"{name}: gone"); continue; }
                    var u = game.UpdraftWorld(i) - p; u.y = 0;
                    string s = $"{name}: {r.Frame.stage}   updraft {u.magnitude / Mile:0.0} mi {Compass(Mathf.Atan2(u.x, u.z) * Mathf.Rad2Deg)}";
                    if (r.TornadoAmount > 0.15f)
                    {
                        var tv = r.TornadoGroundWorld - p; tv.y = 0;
                        s += $"   TORNADO {tv.magnitude / Mile:0.0} mi {Compass(Mathf.Atan2(tv.x, tv.z) * Mathf.Rad2Deg)}";
                    }
                    lines.Add(s);
                    lines.Add($"      on the map at {st.entryMin:0.0} min, mature at {st.matureMin:0.0} min, its tornadoes: " +
                              string.Join(", ", st.InGame(game.gameLength, game.pace).Select(t => $"EF{t.ef} at {st.startMin + t.formMin / game.pace:0} min")));
                }
                var wind = game.TornadoWindAt(p);
                if (wind.sqrMagnitude > 25f) lines.Add($"tornado wind here {wind.magnitude * 2.237f:0} mph");
                float bh = 24 + 20 * lines.Count;
                GUI.Box(new Rect(Screen.width - 676, 16, 660, bh), GUIContent.none);
                GUI.Label(new Rect(Screen.width - 664, 22, 640, bh), string.Join("\n", lines), small);
            }
            else if (storm && storm.isActiveAndEnabled && storm.Plan != null)
            {
                var lines = new List<string>();
                float t = storm.stormMinutes;
                lines.Add($"storm clock {Mathf.FloorToInt(t):0}:{Mathf.FloorToInt(t * 60 % 60):00} / {storm.Plan.duration:0} min   {storm.minutesPerSecond * 60f:0}x{(storm.playing ? "" : "  paused")}   {storm.preset} seed {storm.seed}");
                lines.Add(storm.Frame.stage);
                lines.Add($"track: started {StartBearing:0} deg off the map, onto it at {EntryMinutes:0.0} min, mature at {MatureMinutes:0.0} min");
                var u = storm.UpdraftWorld(t) - p; u.y = 0;
                string s = $"updraft {u.magnitude / Mile:0.0} mi {Compass(Mathf.Atan2(u.x, u.z) * Mathf.Rad2Deg)}";
                if (storm.TornadoAmount > 0.15f)
                {
                    var tv = storm.TornadoGroundWorld - p; tv.y = 0;
                    s += $"   TORNADO {tv.magnitude / Mile:0.0} mi {Compass(Mathf.Atan2(tv.x, tv.z) * Mathf.Rad2Deg)}";
                }
                lines.Add(s);
                var wind = storm.TornadoWindAt(p);
                if (wind.sqrMagnitude > 25f) lines.Add($"tornado wind here {wind.magnitude * 2.237f:0} mph");
                float bh = 24 + 20 * lines.Count;
                GUI.Box(new Rect(Screen.width - 556, 16, 540, bh), GUIContent.none);
                GUI.Label(new Rect(Screen.width - 544, 22, 520, bh), string.Join("\n", lines), small);
            }

            if (!showHelp) return;
            string help = "W/S or arrows  throttle / brake / reverse    A/D  steer\nSpace  handbrake    R  reset car    L  headlights (auto / on / off)    C  camera (chase / roof; hold right mouse to look)    F  free flight (V: truck here)\n" +
                          "T  storm clock speed    P  pause    [ ]  -/+ 5 min    N  new storm    H  hide help (F1: all HUD)    M  sound on / off\n" +
                          "Tab  storm and lighting panel (24 hour clock)    G  game mode (two storms, one hour)\n" +
                          "Map (bottom right): click to open, click a road to get directions, Esc closes\n";
            for (int i = 0; i < Mathf.Min(9, spots.Length); i++) help += $"\n{i + 1}  {spots[i].name}";
            float ph = small.CalcHeight(new GUIContent(help), 450) + 12;
            GUI.Box(new Rect(16, 16, 470, ph), GUIContent.none);
            GUI.Label(new Rect(28, 22, 450, ph), help, small);
        }
    }
}

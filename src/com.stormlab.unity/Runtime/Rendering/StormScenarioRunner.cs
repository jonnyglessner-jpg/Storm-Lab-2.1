// A whole game on one clock (2.0's DesignedScenarioRunner): lays out a StormScenario from one seed and draws each storm as
// a child StormRenderer drawn alongside the others. A storm's child is switched off before it initiates and after its
// life cycle ends. Each storm's life cycle runs the scenario's pace faster than the game clock while it crosses the ground
// at its real speed. Lightning, when that module is present, is added to each storm through StormCreated.
//
//   var game = gameObject.AddComponent<StormScenarioRunner>();
//   game.Spawn("plains", seed: 1234);
//   game.gameMinutes, game.Storms[i], game.Alive(i), game.TornadoWindAt(world)
using System;
using System.Collections.Generic;
using UnityEngine;

namespace StormLab
{
    [DefaultExecutionOrder(85)]   // before the storms (100)
    public class StormScenarioRunner : MonoBehaviour
    {
        [Header("Scenario")]
        [Tooltip("StormPreset key: the game's environment")]
        public string preset = "plains";
        public int seed = 1;
        [Tooltip("Auto: by lane (the northern storm leans HP); or every storm classic, or every storm HP")]
        public ScenarioPrecip precipitation = ScenarioPrecip.Auto;
        [Tooltip("half the map's width, m (the map is a square centred on the world origin)")]
        public float mapHalfM = 80467.2f / 2f;
        [Tooltip("the ground the storms stand on, world y, m")]
        public float groundY = 0f;
        [Tooltip("a game, minutes")]
        public float gameLength = (float)StormScenario.DefaultGameMinutes;
        [Tooltip("how much faster than the game clock each storm's life cycle runs (storms still cross the ground at their real speed)")]
        public float pace = (float)StormScenario.DefaultPace;
        [Tooltip("storm life-cycle minutes before a storm is fully mature at which it comes onto the map")]
        public float enterBeforeMature = 2f;
        public bool spawnOnStart;

        [Header("Clock")]
        public bool playing = true;
        [Tooltip("game minutes per real second (1/60 = real time)")]
        public float minutesPerSecond = 1f / 60f;
        [Tooltip("game time, minutes")]
        public float gameMinutes;

        [Header("Look")]
        [Tooltip("each storm copies this storm's sun, moon, lighting, night and quality settings every frame (the test map's own storm)")]
        public StormRenderer lookFrom;
        public TornadoModel tornado = TornadoModel.Detailed;

        public StormScenario Scenario { get; private set; }
        public IReadOnlyList<StormRenderer> Storms => storms;
        readonly List<StormRenderer> storms = new List<StormRenderer>();
        /// <summary>raised for each storm the runner creates (the lightning module adds its components here)</summary>
        public static event Action<StormRenderer> StormCreated;

        void Start() { if (spawnOnStart && Scenario == null) Spawn(); }
        void OnDestroy() { Clear(); }

        /// <summary>Lay out a new game from the runner's preset and seed and start it from the beginning.</summary>
        public StormScenario Spawn()
        {
            Clear();
            var pre = StormPreset.Find(preset) ?? StormPreset.All[0];
            preset = pre.key;
            Scenario = new StormScenario(pre, seed, mapHalfM, precipitation, gameLength, pace, enterBeforeMature);
            foreach (var s in Scenario.storms)
            {
                var go = new GameObject($"Storm {s.index + 1} ({s.precip}, lane {(s.northness > 0.5 ? "north" : "south")})");
                go.transform.SetParent(transform, false);
                var r = go.AddComponent<StormRenderer>();
                r.preset = pre.key; r.varied = true; r.seed = s.seed; r.planOverrides = s.overrides;
                r.motionScale = 1.0 / pace; r.drawAlongside = true; r.playing = false; r.tornado = tornado;
                r.dieFrom = s.plan.dieMin;   // as the scenario trimmed it
                CopyLook(r);
                r.Replan();
                r.Tornadoes = s.tornadoes;   // rated for the game
                r.PlaceUpdraftAt(new Vector3((float)s.startX, groundY, (float)s.startZ), 0);
                storms.Add(r);
                StormCreated?.Invoke(r);
            }
            gameMinutes = 0;
            Apply();
            return Scenario;
        }

        /// <summary>A new game from a seed.</summary>
        public StormScenario Spawn(string presetKey, int gameSeed) { preset = presetKey; seed = gameSeed; return Spawn(); }

        /// <summary>A floating-origin shift: every storm (and the ground they stand on) moves with the world.</summary>
        public void ShiftOrigin(Vector3 offset)
        {
            groundY += offset.y;
            foreach (var r in storms) if (r) r.ShiftOrigin(offset);
        }

        /// <summary>Remove the storms (their volumes release their GPU memory).</summary>
        public void Clear()
        {
            foreach (var r in storms)
            {
                if (!r) continue;
                r.gameObject.SetActive(false);
                if (Application.isPlaying) Destroy(r.gameObject); else DestroyImmediate(r.gameObject);
            }
            storms.Clear();
            Scenario = null;
        }

        void Update()
        {
            if (Scenario == null) return;
            if (playing && Application.isPlaying) gameMinutes = Mathf.Min(gameMinutes + Time.deltaTime * minutesPerSecond, gameLength);
            Apply();
        }

        /// <summary>Show every storm at the current game time (call after setting gameMinutes from code).</summary>
        public void Apply()
        {
            if (Scenario == null) return;
            for (int i = 0; i < storms.Count; i++)
            {
                var r = storms[i]; if (!r) continue;
                double t = StormMinutes(i);
                bool alive = t >= 0 && t <= r.Plan.duration;
                if (r.gameObject.activeSelf != alive) r.gameObject.SetActive(alive);
                if (!alive) continue;
                r.playing = false;
                r.StormTime = t;
                // the storm's clock speed, so its lightning picks its clock as the game's speed asks
                r.minutesPerSecond = playing ? minutesPerSecond * pace : 0f;
                r.tornado = tornado;
                CopyLook(r);
            }
        }

        void CopyLook(StormRenderer r)
        {
            r.origin.y = groundY;
            if (!lookFrom || r == lookFrom) return;
            r.sunSource = lookFrom.sunSource; r.sun = lookFrom.sun; r.moon = lookFrom.moon;
            r.sunAzimuth = lookFrom.sunAzimuth; r.sunElevation = lookFrom.sunElevation;
            r.solarHour = lookFrom.solarHour; r.latitude = lookFrom.latitude; r.dayOfYear = lookFrom.dayOfYear; r.moonPhase = lookFrom.moonPhase;
            r.lighting = lookFrom.lighting; r.wallBase = lookFrom.wallBase; r.precip = lookFrom.precip; r.dropletDiameter = lookFrom.dropletDiameter;
            r.nightBrightness = lookFrom.nightBrightness; r.nightFloor = lookFrom.nightFloor; r.purkinje = lookFrom.purkinje;
            r.lightPollution = lookFrom.lightPollution; r.nightFill = lookFrom.nightFill; r.nightAdaptation = lookFrom.nightAdaptation;
            r.exposure = lookFrom.exposure; r.toneMap = lookFrom.toneMap; r.luminanceScale = lookFrom.luminanceScale;
            r.drawBackdrop = lookFrom.drawBackdrop; r.useSceneDepth = lookFrom.useSceneDepth; r.groundSink = lookFrom.groundSink;
            r.quality = lookFrom.quality; r.renderScale = lookFrom.renderScale; r.maxSteps = lookFrom.maxSteps; r.tornadoStep = lookFrom.tornadoStep;
            r.temporalBlend = lookFrom.temporalBlend; r.checkerboard = lookFrom.checkerboard;
        }

        // ---------------------------------------------------------------- queries for the game
        /// <summary>storm i's life-cycle time now, storm minutes (negative before it initiates)</summary>
        public double StormMinutes(int i) => Scenario == null ? 0 : Scenario.storms[i].StormMinutes(gameMinutes, pace);
        public bool Alive(int i) => Scenario != null && storms[i] && StormMinutes(i) >= 0 && StormMinutes(i) <= storms[i].Plan.duration;
        /// <summary>storm i's updraft at cloud base, world m (on its track before and after its life too)</summary>
        public Vector3 UpdraftWorld(int i) => storms[i].UpdraftWorld(Math.Max(0, Math.Min(StormMinutes(i), storms[i].Plan.duration)));
        /// <summary>every live storm's tornado wind at a world position, m/s</summary>
        public Vector3 TornadoWindAt(Vector3 world)
        {
            var sum = Vector3.zero;
            for (int i = 0; i < storms.Count; i++) if (Alive(i)) sum += storms[i].TornadoWindAt(world);
            return sum;
        }
    }
}

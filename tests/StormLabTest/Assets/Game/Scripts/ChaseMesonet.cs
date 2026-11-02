// The roof mesonet: a mast on the truck with anemometer cups, a thermometer and hygrometer in a shield and a barometer.
// Mounted when the player owns one and took it on this chase (ChaseSetup.mountMesonet). It reads the storm every frame
// (the tornado's Rankine wind plus the inflow toward the updraft, the cold pool near the storm, the pressure deficit
// under a tornado) and logs automatically whenever a tornado is within a mile: every second of data earns credits,
// double within half a mile (the user's rule, 2026-11-02). The readout sits at the bottom left of the HUD, above the
// speed box. In free roam the readings show but nothing is earned.
using System.Collections.Generic;
using StormLab.TestMap;
using UnityEngine;

namespace StormLab.Game
{
    public class ChaseMesonet : MonoBehaviour
    {
        public ChaseGame game;
        [Tooltip("credits per real second of data within a mile of a tornado (double within half a mile)")]
        public float creditsPerSecond = 2f;
        [Tooltip("the mast's foot on the truck, in its frame (the cab roof is at about 2 m)")]
        public Vector3 mount = new Vector3(0.45f, 2.0f, -0.1f);
        public bool Mounted { get; private set; }
        /// <summary>the readings now</summary>
        public float WindMps, GustMps, WindFromDeg, TempF, DewF, PressureHpa, TornadoDeficitHpa;
        /// <summary>the nearest tornado, miles (infinity with none), and the logging rate now, credits per second</summary>
        public float NearestTornadoMi = float.PositiveInfinity, LogRate;

        const float Mile = 1609.344f;
        GameObject rig; Transform cups; float cupAngle, gustPhase, pending;
        StormPreset env;
        GUIStyle head, small, status;
        readonly Queue<float> gustWindow = new Queue<float>();

        public void SetMounted(bool on)
        {
            Mounted = on;
            if (rig) rig.SetActive(on);
            else if (on) Build();
        }

        void Build()
        {
            var car = game && game.world ? game.world.car : null;
            if (!car) return;
            rig = new GameObject("Mesonet");
            rig.transform.SetParent(car.transform, false);
            rig.transform.localPosition = mount;
            var metal = new Material(Shader.Find("HDRP/Lit")) { name = "mesonet_metal" }; metal.SetColor("_BaseColor", new Color(0.75f, 0.76f, 0.78f)); metal.SetFloat("_Smoothness", 0.6f);
            var white = new Material(Shader.Find("HDRP/Lit")) { name = "mesonet_white" }; white.SetColor("_BaseColor", new Color(0.95f, 0.95f, 0.95f)); white.SetFloat("_Smoothness", 0.4f);
            var black = new Material(Shader.Find("HDRP/Lit")) { name = "mesonet_black" }; black.SetColor("_BaseColor", new Color(0.05f, 0.05f, 0.05f)); black.SetFloat("_Smoothness", 0.3f);
            GameObject Part(PrimitiveType t, string name, Vector3 pos, Vector3 scale, Material m, Transform parent = null)
            {
                var g = GameObject.CreatePrimitive(t); g.name = name; Destroy(g.GetComponent<Collider>());
                g.transform.SetParent(parent ? parent : rig.transform, false); g.transform.localPosition = pos; g.transform.localScale = scale;
                g.GetComponent<MeshRenderer>().sharedMaterial = m;
                return g;
            }
            Part(PrimitiveType.Cube, "Rack", new Vector3(-0.45f, 0.03f, 0f), new Vector3(1.6f, 0.05f, 0.08f), black);
            Part(PrimitiveType.Cylinder, "Mast", new Vector3(0f, 0.45f, 0f), new Vector3(0.04f, 0.42f, 0.04f), metal);
            Part(PrimitiveType.Cube, "Cross arm", new Vector3(0f, 0.86f, 0f), new Vector3(0.5f, 0.025f, 0.025f), metal);
            // the radiation shield (thermometer and hygrometer) on one end of the arm, the barometer box at the mast's foot
            Part(PrimitiveType.Cylinder, "Shield", new Vector3(-0.22f, 0.78f, 0f), new Vector3(0.09f, 0.07f, 0.09f), white);
            Part(PrimitiveType.Cube, "Logger", new Vector3(0f, 0.12f, 0f), new Vector3(0.14f, 0.12f, 0.1f), white);
            // the anemometer: a hub on the other end with three cups
            var hub = new GameObject("Cups"); hub.transform.SetParent(rig.transform, false); hub.transform.localPosition = new Vector3(0.24f, 0.92f, 0f);
            cups = hub.transform;
            Part(PrimitiveType.Cylinder, "Hub", Vector3.zero, new Vector3(0.025f, 0.03f, 0.025f), metal, cups);
            for (int i = 0; i < 3; i++)
            {
                float a = i * 120f * Mathf.Deg2Rad;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Part(PrimitiveType.Cube, "Arm", dir * 0.06f, new Vector3(0.12f, 0.008f, 0.008f), metal, cups).transform.localRotation = Quaternion.Euler(0f, -i * 120f, 0f);
                Part(PrimitiveType.Sphere, "Cup", dir * 0.12f, new Vector3(0.05f, 0.05f, 0.05f), black, cups);
            }
            // a wind vane
            var vane = Part(PrimitiveType.Cube, "Vane", new Vector3(0f, 1.02f, 0f), new Vector3(0.02f, 0.06f, 0.2f), white);
            vane.name = "Vane";
            rig.SetActive(Mounted);
        }

        void Update()
        {
            var world = game ? game.world : null;
            if (!world || !world.car) return;
            if (env == null || env.key != (world.storm ? world.storm.preset : env.key)) env = StormPreset.Find(world.storm ? world.storm.preset : "plains") ?? StormPreset.All[0];
            var p = world.car.transform.position;
            Read(world, p);
            // the cups turn with the wind, the vane points into it
            if (cups)
            {
                cupAngle += WindMps * 60f * Time.deltaTime;
                cups.localRotation = Quaternion.Euler(0f, cupAngle, 0f);
                var vane = rig.transform.Find("Vane");
                if (vane) vane.rotation = Quaternion.Euler(0f, WindFromDeg, 0f);
            }
            // logging: within a mile of a tornado, double within half a mile
            LogRate = 0f;
            if (Mounted && NearestTornadoMi <= 1f)
            {
                LogRate = creditsPerSecond * (NearestTornadoMi <= 0.5f ? 2f : 1f);
                if (game.EarnsCredits)
                {
                    pending += LogRate * Time.deltaTime;
                    ChaseSetup.chaseMesonetSeconds += Time.deltaTime;
                    ChaseProfile.Current.mesonetSeconds += Time.deltaTime;
                    int whole = Mathf.FloorToInt(pending);
                    if (whole > 0) { pending -= whole; game.Earn(whole, null); }
                }
            }
        }

        /// <summary>The instruments: the storm's wind, temperature, dew point and pressure at world point p.</summary>
        void Read(TestMapWorld world, Vector3 p)
        {
            NearestTornadoMi = float.PositiveInfinity; TornadoDeficitHpa = 0f;
            float nearUpdraftKm = float.PositiveInfinity; Vector3 toUpdraft = Vector3.zero;
            var wind = Vector3.zero;
            foreach (var s in world.LiveStorms())
            {
                var u = s.UpdraftWorld(s.StormTime) - p; u.y = 0f;
                float dkm = u.magnitude / 1000f;
                if (dkm < nearUpdraftKm) { nearUpdraftKm = dkm; toUpdraft = u.normalized; }
                wind += s.TornadoWindAt(p);
                if (s.TornadoAmount > 0.15f)
                {
                    var g = s.TornadoGroundWorld; var d = new Vector2(p.x - g.x, p.z - g.z); float r = Mathf.Max(d.magnitude, 1f);
                    NearestTornadoMi = Mathf.Min(NearestTornadoMi, r / Mile);
                    // the Rankine vortex's pressure deficit: rho vmax^2 (1 - r^2 / 2 rc^2) inside the core, rho vmax^2 rc^2 / 2 r^2 outside
                    int t6 = StormTornadoes.Slot0 * 4;
                    float vmax = s.Frame.param[t6 + 1] > 0f ? s.Frame.param[t6 + 1] * s.TornadoAmount : (float)s.Plan.vTor * s.TornadoAmount;
                    float rc = Mathf.Max(s.Frame.param[t6 + 2] > 0f ? s.Frame.param[t6 + 2] * 1000f : s.Frame.param[14 * 4] * 1000f, 10f);
                    float dp = r < rc ? 1.1f * vmax * vmax * (1f - r * r / (2f * rc * rc)) : 1.1f * vmax * vmax * rc * rc / (2f * r * r);
                    TornadoDeficitHpa += dp / 100f;
                }
            }
            // inflow toward the nearest updraft, stronger close in, with gusts
            if (nearUpdraftKm < float.PositiveInfinity)
            {
                float inflow = 9f * Mathf.Clamp01(1.5f - nearUpdraftKm / 20f);
                wind += toUpdraft * inflow;
            }
            gustPhase += Time.deltaTime;
            float gust = 1f + 0.25f * Mathf.PerlinNoise(gustPhase * 0.7f, 0.3f) - 0.1f;
            var w = wind * gust;
            WindMps = w.magnitude;
            WindFromDeg = WindMps > 0.5f ? (Mathf.Atan2(-w.x, -w.z) * Mathf.Rad2Deg + 360f) % 360f : WindFromDeg;
            gustWindow.Enqueue(WindMps); while (gustWindow.Count > 180) gustWindow.Dequeue();
            GustMps = 0f; foreach (var g in gustWindow) GustMps = Mathf.Max(GustMps, g);
            // the cold pool: the storm's rain-cooled air near its core, a few degrees under the tornado's inflow
            float cool = Mathf.Clamp01(1f - nearUpdraftKm / 8f);
            TempF = (float)env.T0F - 9f * cool - 2f * Mathf.Clamp01(1f - NearestTornadoMi / 2f);
            DewF = Mathf.Min((float)env.TdF - 3f * cool, TempF - 0.5f);
            // the barometer: the standard atmosphere at the truck's elevation, the mesolow under the updraft and the tornado's deficit
            float elevM = p.y + world.datum;
            float std = 1013.25f * Mathf.Pow(1f - 2.25577e-5f * elevM, 5.2559f);
            PressureHpa = std - 3f * cool - TornadoDeficitHpa;
        }

        static string Compass(float deg)
        {
            string[] n = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
            return n[Mathf.RoundToInt(((deg % 360) + 360) % 360 / 22.5f) % 16];
        }

        void OnGUI()
        {
            var world = game ? game.world : null;
            if (!world || world.hudHidden || ChaseGame.InputBlocked) return;
            if (head == null)
            {
                head = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
                small = new GUIStyle(GUI.skin.label) { fontSize = 15 };
                status = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
                head.normal.textColor = small.normal.textColor = Color.white;
            }
            float h = Screen.height;
            if (!Mounted)
            {
                if (ChaseSetup.state != ChaseSetup.State.Chase) return;
                GUI.Box(new Rect(16, h - 132 - 8 - 30, 420, 30), GUIContent.none);
                small.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
                GUI.Label(new Rect(28, h - 132 - 8 - 27, 400, 26), $"No mesonet  ·  Esc > Shop  ·  {ChaseCatalog.GearItem("mesonet").price:n0} cr", small);
                small.normal.textColor = Color.white;
                return;
            }
            const float bh = 150f;
            var r = new Rect(16, h - 132 - 8 - bh, 420, bh);
            GUI.Box(r, GUIContent.none);
            float y = r.y + 6;
            GUI.Label(new Rect(28, y, 400, 22), "MESONET", head); y += 24;
            GUI.Label(new Rect(28, y, 400, 22), $"Wind {WindMps * 2.237f:0} mph from {Compass(WindFromDeg)}    gust {GustMps * 2.237f:0} mph", small); y += 22;
            GUI.Label(new Rect(28, y, 400, 22), $"Temp {TempF:0} °F    Dew point {DewF:0} °F", small); y += 22;
            GUI.Label(new Rect(28, y, 400, 22), $"Pressure {PressureHpa:0.0} hPa{(TornadoDeficitHpa > 0.3f ? $"    falling {TornadoDeficitHpa:0.0} under the tornado" : "")}", small); y += 26;
            string line; Color c;
            if (NearestTornadoMi > 1f) { line = NearestTornadoMi < 1e9f ? $"Nearest tornado {NearestTornadoMi:0.0} mi  ·  logs within 1 mi" : "No tornado on the ground  ·  logs within 1 mi"; c = new Color(0.75f, 0.75f, 0.75f); }
            else if (NearestTornadoMi > 0.5f) { line = $"LOGGING  {NearestTornadoMi:0.00} mi{(game.EarnsCredits ? $"  ·  +{LogRate:0} cr/s" : "  ·  free roam, no credits")}"; c = new Color(0.55f, 1f, 0.55f); }
            else { line = $"LOGGING  {NearestTornadoMi:0.00} mi  ·  DOUBLE{(game.EarnsCredits ? $"  +{LogRate:0} cr/s" : "  ·  free roam, no credits")}"; c = new Color(1f, 0.9f, 0.3f); }
            status.normal.textColor = c;
            GUI.Label(new Rect(28, y, 400, 24), line, status);
        }
    }
}

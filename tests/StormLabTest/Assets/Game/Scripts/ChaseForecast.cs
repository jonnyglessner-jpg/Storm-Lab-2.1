// The morning forecast (user, 2026-11-02): every day starts at 8 am with the day's outlook over the map: when storms
// should initiate, the two storms' lanes (northern and southern) with their likely tracks drawn as cones from where
// they come onto the map, each storm's likely mode (classic or HP) and its tornado probabilities by intensity, so the
// chaser can decide which storm to play for. The chaser then picks the truck stop to start from (click a station on the
// map or its button) and confirms: the day fast-forwards to the first towering cumulus (the storms' initiation hour)
// with the truck parked at that station. The forecast is drawn from the day's actual scenario (ChaseSetup.DaySeed)
// with a forecaster's uncertainty on the numbers.
using System.Collections.Generic;
using System.Linq;
using StormLab.TestMap;
using UnityEngine;

namespace StormLab.Game
{
    public class ChaseForecast : MonoBehaviour
    {
        public ChaseGame game;
        public bool Showing { get; private set; }
        public StormScenario Preview { get; private set; }
        /// <summary>the town whose truck stop the chaser starts from</summary>
        public string Choice { get; private set; } = "";
        public int DaySeed { get; private set; }
        public float StartHour { get; private set; }

        public sealed class Outlook
        {
            public int index; public string lane, mode, shapes; public float initHour, bearing, motionDeg, motionMph;
            public int pTornado, pEf2, pEf4, maxEf, count;
            public Vector2 entry, dir;
        }
        public Outlook[] Outlooks { get; private set; } = new Outlook[0];
        public string Discussion { get; private set; } = "";
        /// <summary>the rendered map and the metres it spans (for the checks)</summary>
        public RenderTexture MapTexture => rt;
        /// <summary>the map as drawn: a plain texture read back from the render (north up, row 0 at the south), since an IMGUI
        /// draw of a render texture comes out upside down on Direct3D (user, 2026-11-02: the tracks ran the wrong way over it)</summary>
        public Texture2D MapImage => mapTex;
        public float MapSpan => mapSpan;

        PlaceTown[] towns = new PlaceTown[0];
        RenderTexture rt; Texture2D mapTex; Material line;
        GUIStyle title, head, body, small, button, tabOn, tabOff, boxStyle;
        Rect mapRect; float mapSpan;
        static readonly Color[] StormColours = { new Color(1f, 0.45f, 0.2f), new Color(0.35f, 0.75f, 1f) };

        void Start() { towns = TestMapPlaces.Load(game && game.world ? game.world.navResource : "TestMapNav"); }

        void OnDestroy() { if (rt) rt.Release(); if (line) Destroy(line); if (mapTex) Destroy(mapTex); }

        /// <summary>The morning: build the day's outlook and show it.</summary>
        public void Open()
        {
            var world = game.world; var p = ChaseProfile.Current;
            if (towns.Length == 0) towns = TestMapPlaces.Load(world.navResource);
            DaySeed = ChaseSetup.DaySeed(p.careerSeed, p.day);
            StartHour = ChaseSetup.StartHourFor(DaySeed);
            var map = ChaseSetup.Map;
            var pre = StormPreset.Find(map.preset) ?? StormPreset.All[0];
            float half = world.sizeM / 2f;
            var runner = world.game;
            Preview = new StormScenario(pre, DaySeed, half, runner ? runner.precipitation : ScenarioPrecip.Auto,
                                        runner ? runner.gameLength : (float)StormScenario.DefaultGameMinutes, runner ? runner.pace : (float)StormScenario.DefaultPace,
                                        runner ? runner.enterBeforeMature : 2f);
            Outlooks = Preview.storms.Select(Forecast).ToArray();
            Discussion = Discuss();
            Choice = TestMapPlaces.TownOf(towns, p.spawnTown)?.key ?? (towns.Length > 0 ? towns[0].key : "");
            RenderMap();
            Showing = true;
            ChaseSetup.morning = true;
        }

        Outlook Forecast(ScenarioStorm s)
        {
            var rng = new System.Random(DaySeed * 7 + s.index * 131);
            int R(int a, int b) => a + rng.Next(b - a + 1);
            var tor = s.InGame(Preview.gameMinutes, Preview.pace).ToList();
            int maxEf = tor.Count > 0 ? tor.Max(t => t.ef) : -1;
            var m = new Vector2((float)s.plan.motion.x, (float)s.plan.motion.y);
            var o = new Outlook
            {
                index = s.index, lane = s.northness > 0.5 ? "northern" : "southern",
                mode = s.precip == Precip.HP ? "HP (rain-wrapped: short views, dangerous near the core)" : "classic supercell (clear views of the base)",
                initHour = StartHour + (float)s.startMin / 60f, bearing = (float)s.bearing,
                motionDeg = (Mathf.Atan2(m.x, m.y) * Mathf.Rad2Deg + 360f) % 360f, motionMph = m.magnitude * 2.237f,
                entry = new Vector2((float)s.entryX, (float)s.entryZ), dir = m.sqrMagnitude > 1e-4f ? m.normalized : Vector2.up,
                maxEf = maxEf, count = tor.Count,
                pTornado = tor.Count > 0 ? R(70, 95) : R(10, 30),
                pEf2 = maxEf >= 2 ? R(45, 75) : maxEf >= 0 ? R(8, 22) : R(2, 8),
                pEf4 = maxEf >= 4 ? R(30, 60) : maxEf == 3 ? R(8, 18) : R(0, 5),
            };
            var shapes = tor.Select(t => t.shape.ToString().ToLowerInvariant()).Distinct().ToList();
            o.shapes = shapes.Count == 0 ? "no tornado expected" : (shapes.Count == 1 ? "likely a " + shapes[0] : "likely " + string.Join(" and ", shapes));
            if (maxEf >= 4) o.shapes += ", a violent tornado possible";
            return o;
        }

        string Discuss()
        {
            var o = Outlooks;
            string init = ChaseSetup.ClockText(StartHour);
            string s = $"Towering cumulus expected from about {init} along the dryline west of the map. Two supercells should mature and cross the map through the afternoon, moving toward {Compass(o.Length > 0 ? o[0].motionDeg : 45f)} at about {(o.Length > 0 ? o[0].motionMph : 30f):0} mph.\n";
            foreach (var k in o)
                s += $"\nThe {k.lane} storm: initiates about {ChaseSetup.ClockText(k.initHour)}, comes onto the map from the {Compass(k.bearing)}, {k.mode}. Tornado {k.pTornado} %, EF2+ {k.pEf2} %, EF4+ {k.pEf4} %; {k.shapes}.";
            s += "\n\nPick which storm to play for, and the truck stop to start from: the day fast-forwards to the first towering cumulus with you parked there.";
            return s;
        }

        static string Compass(float deg)
        {
            string[] n = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };
            return n[Mathf.RoundToInt(((deg % 360) + 360) % 360 / 45f) % 8];
        }

        void RenderMap()
        {
            var world = game.world;
            var mm = world.GetComponent<TestMapMinimap>();
            if (!rt) rt = new RenderTexture(1024, 1024, 0) { name = "forecast map" };
            mapSpan = world.sizeM * 1.04f;
            if (mm)
            {
                float keepSpan = mm.fullSpan; bool keepRadar = mm.showRadar;
                mm.fullSpan = mapSpan; mm.showRadar = false;
                mm.CentreOn(Vector2.zero);
                mm.RenderMap(rt, true);
                mm.fullSpan = keepSpan; mm.showRadar = keepRadar;
            }
            if (!mapTex) mapTex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false) { name = "forecast map" };
            var prev = RenderTexture.active; RenderTexture.active = rt;
            mapTex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); mapTex.Apply();
            RenderTexture.active = prev;
        }

        /// <summary>Head out: the day's storms from this seed, the truck at the chosen station.</summary>
        public void Confirm()
        {
            if (!Showing) return;
            Showing = false;
            game.BeginDay(Choice);
        }

        public void Choose(string townKey) { Choice = townKey; }

        /// <summary>Put away without heading out (the menu, free roam).</summary>
        public void Close() { Showing = false; }

        void Styles()
        {
            if (title != null) return;
            title = new GUIStyle(GUI.skin.label) { fontSize = 34, fontStyle = FontStyle.Bold };
            head = new GUIStyle(GUI.skin.label) { fontSize = 19, fontStyle = FontStyle.Bold };
            body = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            small = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            button = new GUIStyle(GUI.skin.button) { fontSize = 16, fixedHeight = 38 };
            tabOn = new GUIStyle(GUI.skin.button) { fontSize = 14, fixedHeight = 34, fontStyle = FontStyle.Bold };
            tabOff = new GUIStyle(GUI.skin.button) { fontSize = 14, fixedHeight = 34 };
            tabOn.normal.textColor = tabOn.hover.textColor = new Color(1f, 0.9f, 0.4f);
            boxStyle = new GUIStyle(GUI.skin.box);
            title.normal.textColor = head.normal.textColor = body.normal.textColor = Color.white;
            small.normal.textColor = new Color(0.82f, 0.82f, 0.82f);
            var sh = Shader.Find("Hidden/StormLab/TestMapMapDraw");
            if (sh) line = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        }

        Vector2 Pix(Vector2 w) => new Vector2(mapRect.x + mapRect.width * (0.5f + w.x / mapSpan), mapRect.y + mapRect.height * (0.5f - w.y / mapSpan));

        void OnGUI()
        {
            if (!Showing) return;
            Styles();
            GUI.depth = -50;
            float W = Screen.width, H = Screen.height;
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.DrawTexture(new Rect(0, 0, W, H), Texture2D.whiteTexture);
            GUI.color = Color.white;
            float w = Mathf.Min(1500f, W - 40f), h = Mathf.Min(880f, H - 40f);
            var r = new Rect((W - w) * 0.5f, (H - h) * 0.5f, w, h);
            GUI.Box(r, GUIContent.none, boxStyle); GUI.Box(r, GUIContent.none, boxStyle);
            var p = ChaseProfile.Current;
            // the text, left
            float textW = Mathf.Min(560f, w * 0.42f);
            GUILayout.BeginArea(new Rect(r.x + 24, r.y + 18, textW, r.height - 36));
            GUILayout.Label($"Day {p.day}  ·  8:00 am  ·  Morning forecast", title, GUILayout.Height(48));
            if (game.LastDay > 0) GUILayout.Label($"Yesterday (day {game.LastDay}): {game.LastDayCredits:n0} cr, {game.LastDayPhotos} photos.   Credits: {p.credits:n0}   Sanity: {p.sanity:P0}", small);
            else GUILayout.Label($"Credits: {p.credits:n0}   Sanity: {p.sanity:P0}", small);
            GUILayout.Space(6);
            GUILayout.Label("Convective outlook", head);
            GUILayout.Label(Discussion, body);
            GUILayout.Space(8);
            GUILayout.Label("Tornado probabilities", head);
            foreach (var o in Outlooks)
            {
                GUI.color = StormColours[o.index % StormColours.Length];
                GUILayout.Label($"■  {o.lane} storm", body);
                GUI.color = Color.white;
                GUILayout.Label($"      any tornado {o.pTornado} %   ·   EF2 or stronger {o.pEf2} %   ·   EF4 or stronger {o.pEf4} %   ·   mode: {(o.mode.StartsWith("HP") ? "HP" : "classic")}", small);
            }
            GUILayout.Space(10);
            GUILayout.Label("Start from", head);
            foreach (var t in towns)
            {
                if (t.gas == null) continue;
                if (GUILayout.Button($"{t.gas.name}, {t.name}   (fuel {ChaseSetup.Vehicle.FuelName} {t.gas.Price(ChaseSetup.Vehicle.fuel):0.00})", Choice == t.key ? tabOn : tabOff)) Choice = t.key;
            }
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            var chosen = TestMapPlaces.TownOf(towns, Choice);
            GUI.enabled = chosen != null;
            if (GUILayout.Button(chosen != null ? $"Head out from {chosen.name}  ›  first towering cumulus about {ChaseSetup.ClockText(StartHour)}" : "No truck stop on this map", button)) Confirm();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            // the map, right
            float mapSize = Mathf.Min(r.height - 36, r.width - textW - 72);
            mapRect = new Rect(r.xMax - 24 - mapSize, r.y + 18, mapSize, mapSize);
            if (mapTex) GUI.DrawTexture(mapRect, mapTex);
            var e = Event.current;
            if (e.type == EventType.Repaint && line) DrawTracks();
            // the compass and the lanes' names on their tracks
            var compass = new GUIStyle(head) { alignment = TextAnchor.MiddleCenter };
            GUI.color = new Color(0f, 0f, 0f, 0.6f); GUI.DrawTexture(new Rect(mapRect.x + 8, mapRect.y + 8, 36, 36), Texture2D.whiteTexture); GUI.color = Color.white;
            GUI.Label(new Rect(mapRect.x + 8, mapRect.y + 8, 36, 36), "N\n▲", compass);
            foreach (var o in Outlooks)
            {
                var mid = Pix(o.entry + o.dir * (game.world.sizeM * 0.45f));
                var ls = new GUIStyle(small) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, normal = { textColor = StormColours[o.index % StormColours.Length] } };
                GUI.color = new Color(0f, 0f, 0f, 0.55f); GUI.DrawTexture(new Rect(mid.x - 60, mid.y - 10, 120, 20), Texture2D.whiteTexture); GUI.color = Color.white;
                GUI.Label(new Rect(mid.x - 60, mid.y - 10, 120, 20), $"{o.lane} storm", ls);
            }
            // the stations: labels, and a click picks one
            foreach (var t in towns)
            {
                if (t.gas == null) continue;
                var q = Pix(new Vector2(t.gas.x, t.gas.z));
                bool on = t.key == Choice;
                var lr = new Rect(q.x - 90, q.y + 10, 180, 20);
                GUI.color = new Color(0f, 0f, 0f, 0.6f); GUI.DrawTexture(lr, Texture2D.whiteTexture); GUI.color = Color.white;
                GUI.Label(lr, $"{t.name}{(on ? "  ◄ start" : "")}", new GUIStyle(small) { alignment = TextAnchor.MiddleCenter, fontStyle = on ? FontStyle.Bold : FontStyle.Normal, normal = { textColor = on ? new Color(1f, 0.9f, 0.4f) : Color.white } });
                if (e.type == EventType.MouseDown && e.button == 0 && Vector2.Distance(e.mousePosition, q) < 26f) { Choice = t.key; e.Use(); }
            }
            GUI.Label(new Rect(mapRect.x + 8, mapRect.yMax - 26, mapRect.width - 16, 22), "Likely tracks: each storm from where it comes onto the map, with the forecast's spread  ·  click a station to start there", small);
        }

        /// <summary>The storms' likely tracks over the map: a cone from each entry point along the motion, its centre line,
        /// and the stations as squares.</summary>
        void DrawTracks()
        {
            line.mainTexture = Texture2D.whiteTexture;
            line.SetVector("_Grade", new Vector4(0, 1, 0, 0));
            line.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix();
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Color Lin(Color c) => linear ? c.linear : c;
            Vector3 G(Vector2 s) => new Vector3(s.x, Screen.height - s.y, 0f);
            void Seg(Vector2 a, Vector2 b, float wd)
            {
                var d = b - a; float L = d.magnitude; if (L < 1e-3f) return;
                d /= L; var n = new Vector2(-d.y, d.x) * (wd * 0.5f);
                GL.Vertex(G(a + n)); GL.Vertex(G(b + n)); GL.Vertex(G(b - n)); GL.Vertex(G(a - n));
            }
            float reach = game.world.sizeM * 0.95f;
            foreach (var o in Outlooks)
            {
                var c = StormColours[o.index % StormColours.Length];
                var dirL = Rotate(o.dir, 12f); var dirR = Rotate(o.dir, -12f);
                Vector2 e = o.entry, tip = e + o.dir * reach, tl = e + dirL * reach, tr = e + dirR * reach;
                // the cone, faint
                GL.Begin(GL.TRIANGLES); GL.Color(Lin(new Color(c.r, c.g, c.b, 0.18f)));
                GL.Vertex(G(Pix(e))); GL.Vertex(G(Pix(tl))); GL.Vertex(G(Pix(tip)));
                GL.Vertex(G(Pix(e))); GL.Vertex(G(Pix(tip))); GL.Vertex(G(Pix(tr)));
                GL.End();
                GL.Begin(GL.QUADS);
                GL.Color(Lin(new Color(c.r, c.g, c.b, 0.9f)));
                Seg(Pix(e), Pix(tip), 3f);
                GL.Color(Lin(new Color(c.r, c.g, c.b, 0.5f)));
                Seg(Pix(e), Pix(tl), 1.5f); Seg(Pix(e), Pix(tr), 1.5f);
                // the entry point
                var q = Pix(e); GL.Color(Lin(c));
                GL.Vertex(G(q + new Vector2(-6, -6))); GL.Vertex(G(q + new Vector2(6, -6))); GL.Vertex(G(q + new Vector2(6, 6))); GL.Vertex(G(q + new Vector2(-6, 6)));
                GL.End();
            }
            // the stations
            GL.Begin(GL.QUADS);
            foreach (var t in towns)
            {
                if (t.gas == null) continue;
                var q = Pix(new Vector2(t.gas.x, t.gas.z));
                bool on = t.key == Choice;
                float s = on ? 9f : 6f;
                GL.Color(Lin(Color.black)); GL.Vertex(G(q + new Vector2(-s - 2, -s - 2))); GL.Vertex(G(q + new Vector2(s + 2, -s - 2))); GL.Vertex(G(q + new Vector2(s + 2, s + 2))); GL.Vertex(G(q + new Vector2(-s - 2, s + 2)));
                GL.Color(Lin(on ? new Color(1f, 0.9f, 0.3f) : new Color(1f, 0.8f, 0.2f, 0.85f)));
                GL.Vertex(G(q + new Vector2(-s, -s))); GL.Vertex(G(q + new Vector2(s, -s))); GL.Vertex(G(q + new Vector2(s, s))); GL.Vertex(G(q + new Vector2(-s, s)));
            }
            GL.End();
            GL.PopMatrix();
        }

        static Vector2 Rotate(Vector2 v, float deg)
        {
            float a = deg * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }
    }
}

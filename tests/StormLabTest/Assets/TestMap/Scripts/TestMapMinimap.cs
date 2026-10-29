// The test map's GPS and radar map (user, 2026-10-27). A small map in the bottom-right corner follows the truck (heading
// up) with the roads, the KDDC radar's reflectivity (StormRadar) and, when a route is set, the route and the next turn.
// Clicking it opens it full screen (north up): drag to pan, scroll to zoom, click a road to drive there; the route is
// the fastest by road class (TestMapRoads), with turn-by-turn steps, and is planned again when the truck leaves it.
// Esc (or the close button) puts it back in the corner. F1 hides it with the rest of the HUD.
//
// The map is drawn with GL in OnGUI: the base map (Resources/TestMapNavBase.png, tools/testmap/navmap.py), the radar
// image, the roads as lines, the route, the radar's sweep and the truck.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace StormLab.TestMap
{
    [DefaultExecutionOrder(120)]
    public class TestMapMinimap : MonoBehaviour
    {
        public TestMapWorld world;
        [Tooltip("the radar shown; one is made at Start (KDDC, from the nav data) when empty")]
        public StormRadar radar;
        [Tooltip("the corner map's size, px at 1080 lines (it scales with the screen)")]
        public float cornerSize = 340f;
        [Tooltip("metres across the corner map")]
        public float cornerSpanM = 6000f;
        [Tooltip("the corner map turns with the truck (off: north up)")]
        public bool headingUp = true;
        public bool showRadar = true;
        [Range(0f, 1f)] public float radarOpacity = 0.8f;
        [Tooltip("mark each storm's updraft (white ring) and tornado (red) on the map: for checks, a chaser has only the radar")]
        public bool showStormMarkers;
        [Tooltip("the map full screen")]
        public bool expanded;
        [Tooltip("metres across the full-screen map (the mouse wheel zooms it)")]
        public float fullSpan = 30000f;
        [Tooltip("metres off the route, for longer than offRouteSeconds, before the route is planned again")]
        public float offRouteM = 60f, offRouteSeconds = 2.5f;

        public TestMapRoads Roads { get; private set; }
        public TestMapRoads.Route Route { get; private set; }
        /// <summary>metres along the route the truck has come</summary>
        public float RouteAlong { get; private set; }

        Texture2D baseMap; Material mat;
        int routeIndex; float offFor, arrivedAt = -1f; string notice; float noticeUntil;
        float anim;
        Vector2 fullCentre; bool follow = true;
        bool dragging, dragMoved; Vector2 dragStart, dragLast;
        Vector2 cursorWorld; bool cursorIn; float cursorDbz = float.NaN, readbackTimer;
        GUIStyle big, mid, small, tiny, button;
        float styleScale;

        const float Mile = 1609.344f;

        // the view: which world point is at the anchor, metres a pixel, the screen-up direction's compass angle
        struct View { public Rect rect; public Vector2 anchor; public Vector2 centre; public float mpp, yaw; }
        View view;
        // what the map is drawn on: the screen, or a check's texture (RenderMap)
        float canvasW = 1920f, canvasH = 1080f;

        void Start()
        {
            if (!world) world = GetComponent<TestMapWorld>();
            string nav = world ? world.navResource : "TestMapNav";
            Roads = TestMapRoads.Load(nav);
            baseMap = Resources.Load<Texture2D>(nav + "Base");
            var sh = Shader.Find("Hidden/StormLab/TestMapMapDraw");
            if (sh) mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            if (!radar)
            {
                radar = GetComponent<StormRadar>();
                if (!radar) radar = gameObject.AddComponent<StormRadar>();
                if (Roads?.radar != null) { radar.siteName = Roads.radar.name; radar.site = new Vector3(Roads.radar.x, Roads.radar.y, Roads.radar.z); }
                radar.areaCentre = Vector2.zero;
                radar.areaHalfM = world ? world.sizeM / 2 + 24000f : 64000f;
            }
        }

        void OnDestroy() { if (mat) Destroy(mat); }

        Transform Truck => world && world.car ? world.car.transform : null;
        Vector2 TruckXZ => Truck ? new Vector2(Truck.position.x, Truck.position.z) : Vector2.zero;
        Vector2 TruckHeading { get { if (!Truck) return Vector2.up; var f = Truck.forward; var v = new Vector2(f.x, f.z); return v.sqrMagnitude > 1e-4f ? v.normalized : Vector2.up; } }
        float TruckYaw => Truck ? Truck.eulerAngles.y : 0f;

        // ------------------------------------------------------------------ the route
        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame && expanded) expanded = false;
            anim = Mathf.MoveTowards(anim, expanded ? 1f : 0f, Time.unscaledDeltaTime * 5f);
            if (follow) fullCentre = TruckXZ;

            if (Route == null || Roads == null) return;
            if (arrivedAt >= 0f)
            {
                if (Time.unscaledTime - arrivedAt > 6f) { Route = null; arrivedAt = -1f; }
                return;
            }
            // where the truck is along the route: the nearest piece a little behind to well ahead of the last one
            var p = TruckXZ; var pts = Route.pts;
            float bestD = float.MaxValue; int bestI = routeIndex; float bestAlong = RouteAlong;
            for (int i = Mathf.Max(0, routeIndex - 5); i < Mathf.Min(pts.Count - 1, routeIndex + 80); i++)
            {
                Vector2 a = pts[i], d = pts[i + 1] - a; float L2 = d.sqrMagnitude;
                float t = L2 > 1e-4f ? Mathf.Clamp01(Vector2.Dot(p - a, d) / L2) : 0f;
                float off = Vector2.Distance(p, a + d * t);
                if (off < bestD - 0.01f) { bestD = off; bestI = i; bestAlong = Route.dist[i] + t * Mathf.Sqrt(L2); }
            }
            routeIndex = bestI; RouteAlong = bestAlong;
            if (Route.Length - RouteAlong < 30f && bestD < 40f)
            {
                arrivedAt = Time.unscaledTime;
                Notice("You have arrived");
                return;
            }
            offFor = bestD > offRouteM ? offFor + Time.deltaTime : 0f;
            if (offFor > offRouteSeconds) { offFor = 0f; Notice("Recalculating"); PlanTo(Route.destination); }
        }

        void Notice(string s) { notice = s; noticeUntil = Time.unscaledTime + 2.5f; }

        /// <summary>Drive to the road nearest a world point (x, z).</summary>
        public void SetDestination(Vector2 world)
        {
            if (Roads == null) return;
            var to = Roads.Nearest(world);
            if (to.edge < 0 || to.off > 2000f) return;
            arrivedAt = -1f;
            PlanTo(to);
            if (Route == null) Notice("No route");
        }

        void PlanTo(TestMapRoads.Spot to)
        {
            var from = Roads.Nearest(TruckXZ, TruckHeading);
            Route = Roads.Plan(from, to, TruckHeading);
            routeIndex = 0; RouteAlong = 0f; offFor = 0f;
        }

        public void ClearRoute() { Route = null; arrivedAt = -1f; }

        /// <summary>Centre the full-screen map on a world point (x, z) and stop following the truck.</summary>
        public void CentreOn(Vector2 world) { fullCentre = world; follow = false; }

        TestMapRoads.Step NextStep(out float inMetres)
        {
            inMetres = 0f;
            if (Route == null) return null;
            foreach (var s in Route.steps)
                if (s.at > RouteAlong + 3f) { inMetres = s.at - RouteAlong; return s; }
            return Route.steps[Route.steps.Count - 1];
        }

        float SecondsLeft()
        {
            if (Route == null) return 0f;
            int i = Mathf.Clamp(routeIndex, 0, Route.pts.Count - 1);
            return Route.Seconds - Route.time[i] - (RouteAlong - Route.dist[i]) / TestMapRoads.Speed[Route.cls.Count > i ? Route.cls[i] : 2];
        }

        // ------------------------------------------------------------------ view
        Vector2 ToScreen(Vector2 w)
        {
            var d = (w - view.centre) / view.mpp;
            float a = view.yaw * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            // turn the world so the compass direction yaw points up the screen
            var r = new Vector2(d.x * c - d.y * s, d.x * s + d.y * c);
            return new Vector2(view.anchor.x + r.x, view.anchor.y - r.y);
        }

        Vector2 ToWorld(Vector2 sp)
        {
            var r = new Vector2(sp.x - view.anchor.x, view.anchor.y - sp.y);
            float a = -view.yaw * Mathf.Deg2Rad, c = Mathf.Cos(a), s = Mathf.Sin(a);
            return view.centre + new Vector2(r.x * c - r.y * s, r.x * s + r.y * c) * view.mpp;
        }

        Rect CornerRect()
        {
            float k = canvasH / 1080f, sz = cornerSize * k, m = 16f * k;
            return new Rect(canvasW - sz - m, canvasH - sz - m, sz, sz);
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);

        void LayoutView()
        {
            float t = Smooth(anim);
            var c = CornerRect(); var f = new Rect(0, 0, canvasW, canvasH);
            var rect = new Rect(Mathf.Lerp(c.x, f.x, t), Mathf.Lerp(c.y, f.y, t), Mathf.Lerp(c.width, f.width, t), Mathf.Lerp(c.height, f.height, t));
            float cornerYaw = headingUp ? TruckYaw : 0f;
            // the truck sits a quarter of the way up from the corner map's bottom when it turns with the truck
            var cornerAnchor = new Vector2(rect.center.x, rect.center.y + (headingUp ? rect.height * 0.22f : 0f));
            view.rect = rect;
            view.anchor = Vector2.Lerp(cornerAnchor, rect.center, t);
            view.centre = Vector2.Lerp(TruckXZ, fullCentre, t);
            float spanCorner = cornerSpanM / Mathf.Max(c.width, 1f), spanFull = fullSpan / Mathf.Max(f.width, 1f);
            view.mpp = Mathf.Exp(Mathf.Lerp(Mathf.Log(spanCorner), Mathf.Log(spanFull), t));
            view.yaw = Mathf.LerpAngle(cornerYaw, 0f, t);
        }

        // ------------------------------------------------------------------ drawing
        static Color Lin(Color c) => QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;

        void Pass(Texture tex, float desaturate = 0f, float bright = 1f)
        {
            mat.mainTexture = tex ? tex : Texture2D.whiteTexture;
            mat.SetVector("_Grade", new Vector4(desaturate, bright, 0, 0));
            mat.SetPass(0);
        }

        // local GL coordinates (pixels from the map rect's bottom-left)
        Vector3 G(Vector2 screen) => new Vector3(screen.x - view.rect.x, view.rect.yMax - screen.y, 0f);

        void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            GL.TexCoord(ua); GL.Vertex(G(a)); GL.TexCoord(ub); GL.Vertex(G(b));
            GL.TexCoord(uc); GL.Vertex(G(c)); GL.TexCoord(ud); GL.Vertex(G(d));
        }

        // a world rectangle with a texture over it (north up in the texture)
        void Image(Rect w, Texture tex, Color tint, float desaturate, float bright)
        {
            Pass(tex, desaturate, bright);
            GL.Begin(GL.QUADS);
            GL.Color(tint);
            Quad(ToScreen(new Vector2(w.xMin, w.yMin)), ToScreen(new Vector2(w.xMax, w.yMin)), ToScreen(new Vector2(w.xMax, w.yMax)), ToScreen(new Vector2(w.xMin, w.yMax)),
                 new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1));
            GL.End();
        }

        // a thick line between screen points, its ends pushed out half a width so joints close
        void Seg(Vector2 a, Vector2 b, float w)
        {
            var d = b - a; float L = d.magnitude; if (L < 1e-3f) return;
            d /= L; var n = new Vector2(-d.y, d.x) * (w * 0.5f);
            a -= d * (w * 0.5f); b += d * (w * 0.5f);
            GL.Vertex(G(a + n)); GL.Vertex(G(b + n)); GL.Vertex(G(b - n)); GL.Vertex(G(a - n));
        }

        void Polyline(IList<Vector2> worldPts, float w, int from = 0)
        {
            var r = view.rect;
            var prev = ToScreen(worldPts[from]);
            for (int i = from + 1; i < worldPts.Count; i++)
            {
                var s = ToScreen(worldPts[i]);
                if (!(Mathf.Max(prev.x, s.x) < r.xMin - w || Mathf.Min(prev.x, s.x) > r.xMax + w || Mathf.Max(prev.y, s.y) < r.yMin - w || Mathf.Min(prev.y, s.y) > r.yMax + w))
                    Seg(prev, s, w);
                prev = s;
            }
        }

        void DrawMap()
        {
            var r = view.rect;
            float k = canvasH / 1080f;
            GL.PushMatrix();
            GL.Viewport(new Rect(r.x, canvasH - r.yMax, r.width, r.height));
            GL.LoadPixelMatrix(0, r.width, 0, r.height);

            // background, then the base map, dimmed and greyed so the radar's colours stand out
            Pass(null);
            GL.Begin(GL.QUADS); GL.Color(Lin(new Color(0.07f, 0.075f, 0.08f, 1f)));
            GL.Vertex3(0, 0, 0); GL.Vertex3(r.width, 0, 0); GL.Vertex3(r.width, r.height, 0); GL.Vertex3(0, r.height, 0);
            GL.End();
            float half = world ? world.sizeM / 2 : 40233.6f;
            if (baseMap) Image(new Rect(-half, -half, 2 * half, 2 * half), baseMap, Color.white, 0.55f, 0.55f);

            // the roads: gravel thin (and left out when far out), paved and state routes over them
            if (Roads != null)
            {
                float worldR = 0.75f * Mathf.Max(r.width, r.height) * view.mpp;
                Pass(null);
                GL.Begin(GL.QUADS);
                for (int cls = 2; cls >= 0; cls--)
                {
                    float w = cls == 0 ? 3.2f : cls == 1 ? 2.2f : 1.4f;
                    if (cls == 2 && view.mpp > 120f) continue;
                    Color col = cls == 0 ? new Color(0.98f, 0.66f, 0.25f) : cls == 1 ? new Color(0.86f, 0.86f, 0.84f) : new Color(0.62f, 0.6f, 0.55f, view.mpp > 40f ? 0.45f : 0.85f);
                    GL.Color(Lin(col));
                    foreach (var e in Roads.edges)
                    {
                        if (e.cls != cls) continue;
                        var a = e.pts[0]; var b = e.pts[e.pts.Length - 1];
                        if (Vector2.Distance((a + b) * 0.5f, view.centre) > worldR + e.length * 0.5f) continue;
                        Polyline(e.pts, w * Mathf.Max(k, 0.75f));
                    }
                }
                GL.End();
            }

            // the radar over the roads, so the storm stays readable
            if (showRadar && radar && radar.Image) Image(radar.Area, radar.Image, new Color(1, 1, 1, radarOpacity), 0f, 1f);

            // the route: a dark edge and a bright line, from the truck on
            if (Route != null && Route.pts.Count > 1)
            {
                Pass(null);
                int from = Mathf.Clamp(routeIndex, 0, Route.pts.Count - 2);
                var rest = new List<Vector2>(Route.pts.Count - from) { TruckXZ };
                for (int i = from + 1; i < Route.pts.Count; i++) rest.Add(Route.pts[i]);
                GL.Begin(GL.QUADS);
                GL.Color(Lin(new Color(0.02f, 0.16f, 0.32f, 1f))); Polyline(rest, 9f * k);
                GL.Color(Lin(new Color(0.22f, 0.62f, 1f, 1f))); Polyline(rest, 5.5f * k);
                GL.End();
                Dot(Route.destination.point, 9f * k, new Color(0.02f, 0.16f, 0.32f));
                Dot(Route.destination.point, 6f * k, new Color(1f, 0.25f, 0.2f));
            }

            // the radar: its site and the line its antenna is pointing along
            if (showRadar && radar)
            {
                var site = new Vector2(radar.site.x, radar.site.z);
                float az = radar.SweepAzimuth * Mathf.Deg2Rad;
                var tip = site + new Vector2(Mathf.Sin(az), Mathf.Cos(az)) * Mathf.Min(radar.rangeKm * 1000f, 1.5f * radar.areaHalfM);
                Pass(null);
                GL.Begin(GL.QUADS); GL.Color(Lin(new Color(0.55f, 1f, 0.6f, 0.22f)));
                Seg(ToScreen(site), ToScreen(tip), 1.5f * k);
                GL.End();
                Dot(site, 4.5f * k, new Color(0.55f, 1f, 0.6f));
            }

            if (showStormMarkers && world)
                foreach (var st in world.LiveStorms())
                {
                    var up = st.UpdraftWorld(st.StormTime);
                    Dot(new Vector2(up.x, up.z), 7f * k, Color.white); Dot(new Vector2(up.x, up.z), 4.5f * k, new Color(0.1f, 0.1f, 0.1f));
                    if (st.TornadoAmount > 0.15f) { var tg = st.TornadoGroundWorld; Dot(new Vector2(tg.x, tg.z), 5f * k, new Color(1f, 0.1f, 0.6f)); }
                }

            // the truck: an arrow along its heading
            if (Truck)
            {
                var c = ToScreen(TruckXZ);
                var f = ToScreen(TruckXZ + TruckHeading * view.mpp * 10f) - c; f.Normalize();
                var n = new Vector2(-f.y, f.x);
                float s = 11f * k;
                Pass(null);
                GL.Begin(GL.TRIANGLES);
                GL.Color(Lin(new Color(0f, 0f, 0f, 0.85f)));
                Arrow(c, f, n, s * 1.3f);
                GL.Color(Color.white);
                Arrow(c, f, n, s);
                GL.End();
            }

            // a frame round the corner map
            if (anim < 0.99f)
            {
                Pass(null);
                GL.Begin(GL.QUADS); GL.Color(Lin(new Color(0.85f, 0.88f, 0.92f, 0.9f * (1f - anim))));
                float bw = 2f;
                var tl = new Vector2(r.xMin, r.yMin); var tr = new Vector2(r.xMax, r.yMin); var br = new Vector2(r.xMax, r.yMax); var bl = new Vector2(r.xMin, r.yMax);
                Seg(tl + new Vector2(0, bw / 2), tr + new Vector2(0, bw / 2), bw); Seg(bl - new Vector2(0, bw / 2), br - new Vector2(0, bw / 2), bw);
                Seg(tl + new Vector2(bw / 2, 0), bl + new Vector2(bw / 2, 0), bw); Seg(tr - new Vector2(bw / 2, 0), br - new Vector2(bw / 2, 0), bw);
                GL.End();
            }

            GL.PopMatrix();
            GL.Viewport(new Rect(0, 0, canvasW, canvasH));
        }

        void Arrow(Vector2 c, Vector2 f, Vector2 n, float s)
        {
            Vector2 tip = c + f * s, l = c - f * s * 0.7f + n * s * 0.75f, rr = c - f * s * 0.7f - n * s * 0.75f, notch = c - f * s * 0.3f;
            GL.Vertex(G(tip)); GL.Vertex(G(l)); GL.Vertex(G(notch));
            GL.Vertex(G(tip)); GL.Vertex(G(notch)); GL.Vertex(G(rr));
        }

        void Dot(Vector2 w, float radius, Color c)
        {
            var p = ToScreen(w);
            Pass(null);
            GL.Begin(GL.TRIANGLES); GL.Color(Lin(c));
            for (int i = 0; i < 16; i++)
            {
                float a0 = i * Mathf.PI / 8f, a1 = (i + 1) * Mathf.PI / 8f;
                GL.Vertex(G(p)); GL.Vertex(G(p + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * radius)); GL.Vertex(G(p + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius));
            }
            GL.End();
        }

        // ------------------------------------------------------------------ GUI
        void Styles()
        {
            float k = canvasH / 1080f;
            if (big != null && Mathf.Approximately(k, styleScale)) return;
            styleScale = k;
            big = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(26 * k), fontStyle = FontStyle.Bold, wordWrap = false };
            mid = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(17 * k), wordWrap = true };
            small = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(14 * k), wordWrap = true };
            tiny = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(12 * k), alignment = TextAnchor.MiddleCenter };
            button = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(15 * k) };
            big.normal.textColor = mid.normal.textColor = small.normal.textColor = tiny.normal.textColor = Color.white;
        }

        static string Arrow(TestMapRoads.Turn t) => t switch
        {
            TestMapRoads.Turn.Left => "←", TestMapRoads.Turn.Right => "→",
            TestMapRoads.Turn.SlightLeft or TestMapRoads.Turn.CurveLeft => "↖", TestMapRoads.Turn.SlightRight or TestMapRoads.Turn.CurveRight => "↗",
            TestMapRoads.Turn.SharpLeft => "↙", TestMapRoads.Turn.SharpRight => "↘",
            TestMapRoads.Turn.UTurn => "↓", TestMapRoads.Turn.Arrive => "●",
            _ => "↑",
        };

        string ProductName => radar ? $"{radar.siteName} {(radar.product == RadarProduct.BaseReflectivity ? $"{radar.elevationDeg:0.0}° base reflectivity" : "composite reflectivity")}" : "";

        /// <summary>Draw the map (not its text) into a texture as it would be on a screen that size, in the corner or full
        /// screen: for checks, whose camera renders leave out the GUI.</summary>
        public void RenderMap(RenderTexture rt, bool full)
        {
            if (!mat) return;
            float keep = anim;
            anim = full ? 1f : 0f;
            canvasW = rt.width; canvasH = rt.height;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            LayoutView();
            DrawMap();
            RenderTexture.active = prev;
            anim = keep;
        }

        void OnGUI()
        {
            if (world && world.hudHidden) return;
            canvasW = Screen.width; canvasH = Screen.height;
            Styles();
            GUI.depth = expanded || anim > 0f ? -100 : 0;
            LayoutView();
            var e = Event.current;
            if (e.type == EventType.Repaint && mat) DrawMap();
            var r = view.rect;
            float k = canvasH / 1080f;
            bool full = anim > 0.98f;

            // the next turn, on a bar across the top of the map
            var step = NextStep(out float inM);
            float barH = (Route != null ? 64f : 26f) * k;
            float topY = full ? r.y + 50f * k : r.y;
            var bar = new Rect(full ? r.x + 16f * k : r.x, topY, full ? Mathf.Min(560f * k, r.width - 32f * k) : r.width, barH);
            GUI.color = new Color(0, 0, 0, 0.72f); GUI.DrawTexture(bar, Texture2D.whiteTexture); GUI.color = Color.white;
            if (Route != null && step != null)
            {
                string head = arrivedAt >= 0f ? "You have arrived" : step.turn == TestMapRoads.Turn.Arrive ? $"{TestMapRoads.Distance(inM)}  to arrival" : $"{TestMapRoads.Distance(inM)}";
                GUI.Label(new Rect(bar.x + 8 * k, bar.y + 2 * k, 60 * k, barH), Arrow(step.turn), big);
                GUI.Label(new Rect(bar.x + 52 * k, bar.y + 2 * k, bar.width - 60 * k, 34 * k), head, big);
                GUI.Label(new Rect(bar.x + 52 * k, bar.y + 34 * k, bar.width - 60 * k, 28 * k), arrivedAt >= 0f ? "" : TestMapRoads.Say(step.turn, step.road), mid);
            }
            else GUI.Label(new Rect(bar.x + 8 * k, bar.y + 2 * k, bar.width - 16 * k, barH), full ? "Click a road to drive there  ·  drag to pan  ·  scroll to zoom  ·  Esc closes" : (showRadar ? ProductName : "Map") + "  ·  click to open", small);

            // time and distance left, along the bottom
            if (Route != null && arrivedAt < 0f)
            {
                float left = Route.Length - RouteAlong, sec = SecondsLeft();
                var b = full ? new Rect(r.x + 16f * k, bar.yMax + 4 * k, bar.width, 26 * k) : new Rect(r.x, r.yMax - 26 * k, r.width, 26 * k);
                GUI.color = new Color(0, 0, 0, 0.72f); GUI.DrawTexture(b, Texture2D.whiteTexture); GUI.color = Color.white;
                GUI.Label(new Rect(b.x + 8 * k, b.y + 3 * k, b.width - 16 * k, b.height), $"{Mathf.CeilToInt(sec / 60f)} min  ·  {TestMapRoads.Distance(left)}  ·  {(Route.destination.edge >= 0 ? Roads.edges[Route.destination.edge].name : "")}", small);
            }
            if (notice != null && Time.unscaledTime < noticeUntil)
            {
                var nb = new Rect(r.center.x - 120 * k, r.y + (full ? 130f : 70f) * k, 240 * k, 30 * k);
                GUI.color = new Color(0.1f, 0.3f, 0.6f, 0.9f); GUI.DrawTexture(nb, Texture2D.whiteTexture); GUI.color = Color.white;
                GUI.Label(nb, notice, new GUIStyle(tiny) { fontSize = Mathf.RoundToInt(15 * k) });
            }

            if (full) FullScreenControls(k);

            // the mouse on the map, after the buttons have had it
            HandleMouse(e, full, k);
        }

        void FullScreenControls(float k)
        {
            var r = view.rect;
            // the button row
            float x = r.x + 16 * k, y = r.y + 10 * k, h = 32 * k;
            if (GUI.Button(new Rect(x, y, 130 * k, h), showRadar ? "Radar: on" : "Radar: off", button)) showRadar = !showRadar; x += 136 * k;
            if (radar && GUI.Button(new Rect(x, y, 170 * k, h), radar.product == RadarProduct.BaseReflectivity ? "Base 0.5°" : "Composite", button))
                radar.product = radar.product == RadarProduct.BaseReflectivity ? RadarProduct.CompositeReflectivity : RadarProduct.BaseReflectivity;
            x += 176 * k;
            if (radar && GUI.Button(new Rect(x, y, 150 * k, h), radar.palette == RadarPalette.Smooth ? "Colours: smooth" : "Colours: NWS", button))
                radar.palette = radar.palette == RadarPalette.Smooth ? RadarPalette.NWS : RadarPalette.Smooth;
            x += 156 * k;
            if (GUI.Button(new Rect(x, y, 130 * k, h), follow ? "Following" : "Follow truck", button)) follow = true; x += 136 * k;
            if (Route != null && GUI.Button(new Rect(x, y, 130 * k, h), "Clear route", button)) ClearRoute();
            if (GUI.Button(new Rect(r.xMax - 16 * k - 110 * k, y, 110 * k, h), "Close (Esc)", button)) expanded = false;

            // the steps of the route, down the left
            if (Route != null)
            {
                var list = new System.Text.StringBuilder();
                int shown = 0;
                foreach (var s in Route.steps)
                {
                    if (s.at < RouteAlong - 1f && s.turn != TestMapRoads.Turn.Arrive) continue;
                    list.Append($"{Arrow(s.turn)}  {TestMapRoads.Say(s.turn, s.road)}   {TestMapRoads.Distance(Mathf.Max(0f, s.at - RouteAlong))}\n");
                    if (++shown >= 14) break;
                }
                var lr = new Rect(r.x + 16 * k, r.y + 150 * k, 420 * k, (shown + 1) * 22 * k + 12 * k);
                GUI.color = new Color(0, 0, 0, 0.62f); GUI.DrawTexture(lr, Texture2D.whiteTexture); GUI.color = Color.white;
                GUI.Label(new Rect(lr.x + 8 * k, lr.y + 6 * k, lr.width - 16 * k, lr.height), "Directions\n" + list, small);
            }

            // the dBZ scale along the bottom
            if (showRadar)
            {
                float w = 30 * k, lx = r.center.x - 8 * w, ly = r.yMax - 46 * k;
                GUI.color = new Color(0, 0, 0, 0.62f); GUI.DrawTexture(new Rect(lx - 10 * k, ly - 26 * k, 16 * w + 20 * k, 62 * k), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(lx, ly - 25 * k, 16 * w, 22 * k), $"{ProductName}  (dBZ)", tiny);
                // 5 to 80 dBZ, shaded a dBZ at a time in the radar's palette
                var pal = radar ? radar.palette : RadarPalette.NWS;
                float step = w / 5f;
                for (int dbzStep = 0; dbzStep < 75; dbzStep++)
                {
                    GUI.color = StormRadar.ColourFor(5f + dbzStep + 0.5f, pal);
                    GUI.DrawTexture(new Rect(lx + dbzStep * step, ly, step + 0.5f, 12 * k), Texture2D.whiteTexture);
                }
                GUI.color = Color.white;
                for (int i = 0; i < 15; i += 2) GUI.Label(new Rect(lx + i * w - w * 0.5f, ly + 12 * k, w, 18 * k), $"{5 + 5 * i}", tiny);
            }

            // what is under the mouse
            if (cursorIn)
            {
                string at = world ? world.GridPosition(new Vector3(cursorWorld.x, 0, cursorWorld.y)) : "";
                string dbz = showRadar && !float.IsNaN(cursorDbz) ? (cursorDbz > -50f ? $"   {cursorDbz:0} dBZ" : "   no echo") : "";
                var cr = new Rect(r.xMax - 16 * k - 460 * k, r.yMax - 40 * k, 460 * k, 28 * k);
                GUI.color = new Color(0, 0, 0, 0.62f); GUI.DrawTexture(cr, Texture2D.whiteTexture); GUI.color = Color.white;
                GUI.Label(new Rect(cr.x + 8 * k, cr.y + 4 * k, cr.width, cr.height), at + dbz, small);
            }
            // scale: a round number of miles about a fifth of the width
            float target = r.width * 0.18f * view.mpp / Mile;
            float[] nice = { 0.25f, 0.5f, 1f, 2f, 5f, 10f, 20f, 50f };
            float mi = nice[0]; foreach (var v in nice) if (v <= target) mi = v;
            float px = mi * Mile / view.mpp;
            var sb = new Rect(r.x + 16 * k, r.yMax - 40 * k, px, 6 * k);
            GUI.DrawTexture(sb, Texture2D.whiteTexture);
            GUI.Label(new Rect(sb.x, sb.y - 22 * k, 120 * k, 20 * k), mi < 1f ? $"{mi:0.##} mi" : $"{mi:0} mi", small);
        }

        void HandleMouse(Event e, bool full, float k)
        {
            var r = view.rect;
            bool inside = r.Contains(e.mousePosition);
            if (!full)
            {
                cursorIn = false;
                if (e.type == EventType.MouseDown && e.button == 0 && inside && anim < 0.01f)
                {
                    expanded = true; follow = true; fullCentre = TruckXZ;
                    e.Use();
                }
                return;
            }
            cursorIn = inside;
            if (inside) cursorWorld = ToWorld(e.mousePosition);
            if (inside && (readbackTimer -= Time.unscaledDeltaTime) <= 0f) { readbackTimer = 0.1f; ReadDbz(cursorWorld); }
            switch (e.type)
            {
                case EventType.MouseDown when e.button == 0 && inside:
                    dragging = true; dragMoved = false; dragStart = dragLast = e.mousePosition; e.Use(); break;
                case EventType.MouseDrag when dragging:
                    var d = e.mousePosition - dragLast; dragLast = e.mousePosition;
                    if ((e.mousePosition - dragStart).magnitude > 4f * k) dragMoved = true;
                    if (dragMoved) { fullCentre += new Vector2(-d.x, d.y) * view.mpp; follow = false; }
                    e.Use(); break;
                case EventType.MouseUp when dragging:
                    dragging = false;
                    if (!dragMoved) SetDestination(ToWorld(e.mousePosition));
                    e.Use(); break;
                case EventType.ScrollWheel when inside:
                    // zoom about the point under the mouse
                    var before = ToWorld(e.mousePosition);
                    fullSpan = Mathf.Clamp(fullSpan * Mathf.Pow(1.15f, e.delta.y > 0 ? 1f : -1f), 1500f, 160000f);
                    LayoutView();
                    var after = ToWorld(e.mousePosition);
                    fullCentre += before - after; follow = false;
                    e.Use(); break;
            }
        }

        void ReadDbz(Vector2 w)
        {
            if (!radar || !radar.Dbz || !SystemInfo.supportsAsyncGPUReadback) return;
            var a = radar.Area; int n = radar.Dbz.width;
            int ix = Mathf.FloorToInt((w.x - a.xMin) / a.width * n), iy = Mathf.FloorToInt((w.y - a.yMin) / a.height * n);
            if (ix < 0 || iy < 0 || ix >= n || iy >= n) { cursorDbz = float.NaN; return; }
            AsyncGPUReadback.Request(radar.Dbz, 0, ix, 1, iy, 1, 0, 1, TextureFormat.RHalf, req =>
            {
                if (this == null || req.hasError) return;
                var d = req.GetData<ushort>();
                if (d.Length > 0) cursorDbz = Mathf.HalfToFloat(d[0]);
            });
        }
    }
}

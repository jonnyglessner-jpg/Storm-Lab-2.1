// The test map's road network for the GPS (TestMapMinimap): junctions and the named road pieces between them, from
// Resources/TestMapNav.json (tools/testmap/navmap.py), the fastest route between two points on it, and the route's
// turn-by-turn steps. World metres, x east, z north (a Vector2 here is (x, z)).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace StormLab.TestMap
{
    public sealed class TestMapRoads
    {
        [Serializable] public class RadarSite { public string name; public float x, y, z, lat, lon; }
        [Serializable] class Data { public float sizeM; public RadarSite radar; public float[] nodes; public EdgeData[] edges; }
        [Serializable] class EdgeData { public int a, b, cls; public string name; public float[] pts; }

        public sealed class Edge { public int a, b, cls; public string name; public Vector2[] pts; public float length; }

        /// <summary>a point on the network: which piece, how far along it from its a end, the point and its distance from where asked</summary>
        public struct Spot { public int edge; public float along; public Vector2 point; public float off; }

        public enum Turn { Depart, Continue, SlightLeft, SlightRight, Left, Right, SharpLeft, SharpRight, CurveLeft, CurveRight, UTurn, Arrive }

        public sealed class Step { public float at; public Turn turn; public string road; public int cls; }

        public sealed class Route
        {
            public readonly List<Vector2> pts = new List<Vector2>();
            /// <summary>metres and seconds from the start to each point</summary>
            public readonly List<float> dist = new List<float>(), time = new List<float>();
            public readonly List<int> cls = new List<int>();   // class of the piece from point i to i + 1
            public readonly List<Step> steps = new List<Step>();
            public Spot destination;
            internal string roadNow;
            public float Length => dist.Count > 0 ? dist[dist.Count - 1] : 0f;
            public float Seconds => time.Count > 0 ? time[time.Count - 1] : 0f;
        }

        public static readonly string[] ClassNames = { "state route", "paved", "gravel" };
        /// <summary>the speeds the route plans on, m/s: state routes 65 mph, paved county roads 55, gravel 45</summary>
        public static readonly float[] Speed = { 29.06f, 24.59f, 20.12f };

        public readonly Vector2[] nodes;
        public readonly Edge[] edges;
        public readonly List<int>[] adj;
        public readonly RadarSite radar;
        public readonly float sizeM;

        TestMapRoads(Data d)
        {
            sizeM = d.sizeM; radar = d.radar;
            nodes = new Vector2[d.nodes.Length / 2];
            for (int i = 0; i < nodes.Length; i++) nodes[i] = new Vector2(d.nodes[2 * i], d.nodes[2 * i + 1]);
            edges = new Edge[d.edges.Length];
            adj = new List<int>[nodes.Length];
            for (int i = 0; i < nodes.Length; i++) adj[i] = new List<int>(4);
            for (int i = 0; i < edges.Length; i++)
            {
                var e = d.edges[i];
                var pts = new Vector2[e.pts.Length / 2];
                float len = 0f;
                for (int k = 0; k < pts.Length; k++)
                {
                    pts[k] = new Vector2(e.pts[2 * k], e.pts[2 * k + 1]);
                    if (k > 0) len += Vector2.Distance(pts[k - 1], pts[k]);
                }
                edges[i] = new Edge { a = e.a, b = e.b, cls = e.cls, name = e.name, pts = pts, length = len };
                adj[e.a].Add(i); adj[e.b].Add(i);
            }
        }

        /// <summary>The network from Resources (null when navmap.py has not been run).</summary>
        public static TestMapRoads Load(string name = "TestMapNav")
        {
            var t = Resources.Load<TextAsset>(name);
            if (!t) { Debug.LogWarning($"TestMapRoads: Resources/{name}.json is missing: run python -I tools/testmap/navmap.py"); return null; }
            return new TestMapRoads(JsonUtility.FromJson<Data>(t.text));
        }

        // ------------------------------------------------------------------ nearest point
        /// <summary>The point of the network nearest p. With a heading (unit), a piece running across it counts as a
        /// little farther, so a truck at a junction is put on the road it is driving along.</summary>
        public Spot Nearest(Vector2 p, Vector2 heading = default)
        {
            var best = new Spot { edge = -1, off = float.MaxValue };
            float bestScore = float.MaxValue;
            for (int i = 0; i < edges.Length; i++)
            {
                var pts = edges[i].pts;
                float along = 0f;
                for (int k = 0; k + 1 < pts.Length; k++)
                {
                    Vector2 a = pts[k], d = pts[k + 1] - a;
                    float L = d.magnitude;
                    // quick reject
                    if (Mathf.Abs(p.x - a.x) > L + bestScore && Mathf.Abs(p.x - pts[k + 1].x) > bestScore) { along += L; continue; }
                    float t = L > 1e-3f ? Mathf.Clamp01(Vector2.Dot(p - a, d) / (L * L)) : 0f;
                    var q = a + d * t;
                    float off = Vector2.Distance(p, q);
                    float score = off;
                    if (heading != default && L > 1e-3f) score += 15f * (1f - Mathf.Abs(Vector2.Dot(heading, d / L)));
                    if (score < bestScore) { bestScore = score; best = new Spot { edge = i, along = along + L * t, point = q, off = off }; }
                    along += L;
                }
            }
            return best;
        }

        // ------------------------------------------------------------------ routing
        float Cost(int e, float metres) => metres / Speed[edges[e].cls];

        /// <summary>The fastest route from a spot to another (null when none). heading (unit, optional) is the way the
        /// truck faces: setting off the other way costs a U-turn.</summary>
        public Route Plan(Spot from, Spot to, Vector2 heading = default)
        {
            if (from.edge < 0 || to.edge < 0) return null;
            const float UTurn = 30f;   // seconds
            var ef = edges[from.edge];
            // which way along its piece the truck faces
            Vector2 dirAB = PointAt(ef, Mathf.Min(from.along + 5f, ef.length)) - PointAt(ef, Mathf.Max(from.along - 5f, 0f));
            bool facesB = heading == default || Vector2.Dot(heading, dirAB) >= 0f;

            int n = nodes.Length;
            var g = new float[n]; var prevEdge = new int[n]; var done = new bool[n];
            for (int i = 0; i < n; i++) { g[i] = float.MaxValue; prevEdge[i] = -1; }
            var heap = new Heap();
            void Seed(int node, float c) { if (c < g[node]) { g[node] = c; prevEdge[node] = -2; heap.Push(node, c + H(node)); } }
            float H(int node) => Vector2.Distance(nodes[node], to.point) / Speed[0];
            Seed(ef.b, Cost(from.edge, ef.length - from.along) + (facesB ? 0f : UTurn));
            Seed(ef.a, Cost(from.edge, from.along) + (facesB ? UTurn : 0f));

            // straight along the same piece, when the destination is on it
            float direct = float.MaxValue; bool directForward = false;
            if (from.edge == to.edge)
            {
                directForward = to.along >= from.along;
                direct = Cost(from.edge, Mathf.Abs(to.along - from.along)) + (directForward == facesB ? 0f : UTurn);
            }

            var et = edges[to.edge];
            float best = direct; int bestEnd = -1;
            while (heap.Count > 0)
            {
                int u = heap.Pop(out float f);
                if (done[u]) continue;
                done[u] = true;
                if (f >= best) break;
                // reach the destination from this node
                if (u == et.a && g[u] + Cost(to.edge, to.along) < best) { best = g[u] + Cost(to.edge, to.along); bestEnd = u; }
                if (u == et.b && g[u] + Cost(to.edge, et.length - to.along) < best) { best = g[u] + Cost(to.edge, et.length - to.along); bestEnd = u; }
                foreach (int e in adj[u])
                {
                    var ed = edges[e];
                    int v = ed.a == u ? ed.b : ed.a;
                    float c = g[u] + Cost(e, ed.length);
                    if (c < g[v]) { g[v] = c; prevEdge[v] = e; heap.Push(v, c + H(v)); }
                }
            }
            if (best == float.MaxValue) return null;

            // the pieces, from the start
            var r = new Route { destination = to };
            if (bestEnd < 0)
            {
                Add(r, Slice(ef, from.along, to.along), ef.cls, ef.name, false);
            }
            else
            {
                var chain = new List<int>();
                int node = bestEnd;
                while (prevEdge[node] >= 0) { chain.Add(prevEdge[node]); var ed = edges[prevEdge[node]]; node = ed.a == node ? ed.b : ed.a; }
                chain.Reverse();
                // node is now the first junction: off the start piece toward it
                Add(r, Slice(ef, from.along, node == ef.b ? ef.length : 0f), ef.cls, ef.name, false);
                int at = node;
                foreach (int e in chain)
                {
                    var ed = edges[e];
                    bool fwd = ed.a == at;
                    Add(r, Slice(ed, fwd ? 0f : ed.length, fwd ? ed.length : 0f), ed.cls, ed.name, adj[at].Count == 2);
                    at = fwd ? ed.b : ed.a;
                }
                Add(r, Slice(et, at == et.a ? 0f : et.length, to.along), et.cls, et.name, adj[at].Count == 2);
            }
            r.steps.Add(new Step { at = r.Length, turn = Turn.Arrive, road = et.name, cls = et.cls });
            return r;
        }

        /// <summary>Append a piece of road to the route, with a step where it turns onto a new road.</summary>
        static void Add(Route r, List<Vector2> pts, int cls, string name, bool curveOnly)
        {
            if (pts.Count < 2) return;
            if (r.pts.Count == 0)
            {
                r.pts.Add(pts[0]); r.dist.Add(0f); r.time.Add(0f);
                r.steps.Add(new Step { at = 0f, turn = Turn.Depart, road = name, cls = cls });
            }
            else
            {
                // the turn here: the way in (the last 30 m) against the way out (the next 30 m)
                Vector2 din = Heading(r.pts, r.pts.Count - 1, -1), dout = Heading(pts, 0, 1);
                float ang = Vector2.SignedAngle(din, dout);   // counter-clockwise (left) positive
                var t = Classify(ang, curveOnly);
                if (t != Turn.Continue || name != r.roadNow) r.steps.Add(new Step { at = r.Length, turn = t, road = name, cls = cls });
            }
            for (int k = 1; k < pts.Count; k++)
            {
                float d = Vector2.Distance(pts[k - 1], pts[k]);
                if (d < 0.01f) continue;
                r.cls.Add(cls);
                r.pts.Add(pts[k]); r.dist.Add(r.Length + d); r.time.Add(r.Seconds + d / Speed[cls]);
            }
            r.roadNow = name;
        }

        static Turn Classify(float ang, bool curveOnly)
        {
            float a = Mathf.Abs(ang); bool left = ang > 0f;
            if (a < 25f) return Turn.Continue;
            if (curveOnly) return left ? Turn.CurveLeft : Turn.CurveRight;
            if (a < 60f) return left ? Turn.SlightLeft : Turn.SlightRight;
            if (a < 135f) return left ? Turn.Left : Turn.Right;
            if (a < 170f) return left ? Turn.SharpLeft : Turn.SharpRight;
            return Turn.UTurn;
        }

        /// <summary>the direction over about 30 m from point i, forward (+1) along the list or back (-1) into it</summary>
        static Vector2 Heading(List<Vector2> p, int i, int dir)
        {
            int j = i; float d = 0f;
            while (j + dir >= 0 && j + dir < p.Count && d < 30f) { d += Vector2.Distance(p[j], p[j + dir]); j += dir; }
            var v = dir > 0 ? p[j] - p[i] : p[i] - p[j];
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector2.up;
        }

        public static Vector2 PointAt(Edge e, float along)
        {
            float s = 0f;
            for (int k = 0; k + 1 < e.pts.Length; k++)
            {
                float L = Vector2.Distance(e.pts[k], e.pts[k + 1]);
                if (s + L >= along || k + 2 == e.pts.Length) return Vector2.Lerp(e.pts[k], e.pts[k + 1], L > 1e-3f ? Mathf.Clamp01((along - s) / L) : 0f);
                s += L;
            }
            return e.pts[0];
        }

        /// <summary>the piece's centre line from one distance along it to another (either way round)</summary>
        static List<Vector2> Slice(Edge e, float from, float to)
        {
            var o = new List<Vector2> { PointAt(e, from) };
            float s = 0f;
            var cum = new float[e.pts.Length];
            for (int k = 1; k < e.pts.Length; k++) { s += Vector2.Distance(e.pts[k - 1], e.pts[k]); cum[k] = s; }
            if (to >= from) { for (int k = 0; k < e.pts.Length; k++) if (cum[k] > from && cum[k] < to) o.Add(e.pts[k]); }
            else { for (int k = e.pts.Length - 1; k >= 0; k--) if (cum[k] < from && cum[k] > to) o.Add(e.pts[k]); }
            o.Add(PointAt(e, to));
            return o;
        }

        // a binary min-heap of (node, priority)
        sealed class Heap
        {
            readonly List<(int n, float p)> h = new List<(int, float)>();
            public int Count => h.Count;
            public void Push(int n, float p)
            {
                h.Add((n, p));
                int i = h.Count - 1;
                while (i > 0) { int up = (i - 1) / 2; if (h[up].p <= h[i].p) break; (h[up], h[i]) = (h[i], h[up]); i = up; }
            }
            public int Pop(out float p)
            {
                var top = h[0]; p = top.p;
                h[0] = h[h.Count - 1]; h.RemoveAt(h.Count - 1);
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < h.Count && h[l].p < h[m].p) m = l;
                    if (r < h.Count && h[r].p < h[m].p) m = r;
                    if (m == i) break;
                    (h[m], h[i]) = (h[i], h[m]); i = m;
                }
                return top.n;
            }
        }

        // ------------------------------------------------------------------ words
        public static string Say(Turn t, string road) => t switch
        {
            Turn.Depart => $"Head along {road}",
            Turn.Continue => $"Continue onto {road}",
            Turn.SlightLeft => $"Bear left onto {road}",
            Turn.SlightRight => $"Bear right onto {road}",
            Turn.Left => $"Turn left onto {road}",
            Turn.Right => $"Turn right onto {road}",
            Turn.SharpLeft => $"Sharp left onto {road}",
            Turn.SharpRight => $"Sharp right onto {road}",
            Turn.CurveLeft => $"Curve left onto {road}",
            Turn.CurveRight => $"Curve right onto {road}",
            Turn.UTurn => $"Make a U-turn onto {road}",
            _ => "Arrive at your destination",
        };

        /// <summary>a distance the way a US GPS says it: feet under a tenth of a mile, else tenths of a mile</summary>
        public static string Distance(float m)
        {
            float mi = m / 1609.344f;
            if (mi < 0.1f) return $"{Mathf.Max(50f, Mathf.Round(m * 3.28084f / 50f) * 50f):0} ft";
            return mi < 10f ? $"{mi:0.0} mi" : $"{mi:0} mi";
        }
    }
}

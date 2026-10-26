// The game's changes to the web build's plan of a varied storm (user, 2026-10-26), made once when the storm is planned,
// before StormLife trims it. The website's own presets ("web:" keys) and the parity check keep the web build's plan.
//   - No multiple-vortex tornado: a cycle the web build draws as one becomes a wedge (on a low base) or a stovepipe (on a
//     high one, where the web build has no wedges), or failing that the first of the shapes after it that neither
//     neighbouring cycle has, at that shape's size and flare. Wedges keep their multiple-vortex start (StormTornadoes).
//   - A quicker build-up: the clear slot opening and the updraft wrapping round it take about 1.5 min each (BuildUpMin),
//     and the mature mesocyclone of a cycle without a tornado RunUp of its time; every later milestone, and the storm's
//     end, come sooner.
// Not part of the web build's design: StormDesign stays as the website's, and parity does not see this.
using System;

namespace StormLab
{
    public static class StormGame
    {
        /// <summary>the share of its time a non-tornadic cycle's mature stage keeps</summary>
        public const double RunUp = 0.5;
        /// <summary>storm minutes from 7 min into the canonical cycle to touchdown, for a cycle of the web build's mean span
        /// there (9.5 min, cycles.ts: touchdown 13-20 min after the cycle starts); the clear slot opening and the wrap each
        /// take 5/11 of it, about 1.5 min (user, 2026-10-26)</summary>
        public const double BuildUpMin = 3.3, WebBuildUpMin = 9.5;

        static readonly TornadoShape[] LowBaseFor = { TornadoShape.Wedge, TornadoShape.Stovepipe, TornadoShape.Cone, TornadoShape.Rope };
        static readonly TornadoShape[] HighBaseFor = { TornadoShape.Stovepipe, TornadoShape.Cone, TornadoShape.Rope };

        /// <summary>The game's plan from the web build's (a copy; P itself when it has no cycles, as the teaching sequence).</summary>
        public static StormPlan Adapt(StormPlan P)
        {
            if (P == null || P.cycles == null) return P;
            var Q = P.Clone();
            int n = P.cycles.Length;
            Q.cycles = new StormCycle[n];
            double shift = 0;
            for (int i = 0; i < n; i++)
            {
                var c = P.cycles[i];
                var d = new StormCycle
                {
                    start = c.start + shift, end = c.end + shift, shape = c.shape, tornadic = c.tornadic, radius = c.radius,
                    topRatio = c.topRatio, condensation = c.condensation, dust = c.dust, lean = c.lean, phase = c.phase,
                };
                var times = new double[c.times.Length];
                for (int j = 0; j < times.Length; j++) times[j] = c.times[j] + shift;
                if (c.shape == TornadoShape.Multivortex)
                {
                    var prev = i > 0 ? Q.cycles[i - 1].shape : (TornadoShape?)null;
                    var next = i + 1 < n ? P.cycles[i + 1].shape : (TornadoShape?)null;
                    Reshape(d, P.zb > 1.9 ? HighBaseFor : LowBaseFor, prev, next);
                    // the multiple-vortex cycle's extra milestone (its takeover) goes: the rest are the plain cycle's
                    var plain = new double[times.Length - 1];
                    for (int j = 0, k = 0; j < times.Length; j++) if (j != 4) plain[k++] = times[j];
                    times = plain;
                }
                // the build-up: [1] (7 min into the canonical cycle) to [2] (touchdown, or where it would be), the clear slot
                // opening (8-13) and the updraft wrapping round it (13-18) after the wall cloud's last minute of forming; the
                // design maps that span linearly, so all of it is shortened, that minute too. A longer build-up in the web
                // build's draw stays a little longer here (as the square root of its span), so cycles still differ.
                double span = times[2] - times[1];
                double cut = Math.Max(0, span - BuildUpMin * Math.Sqrt(span / WebBuildUpMin));
                for (int j = 2; j < times.Length; j++) times[j] -= cut;
                d.end -= cut; shift -= cut;
                // a cycle without a tornado: its mature stage, [2] to [5] (the canonical 18 -> 29, before occlusion)
                if (!c.tornadic)
                {
                    double t2 = times[2], cut2 = (times[5] - t2) * (1 - RunUp);
                    for (int j = 3; j <= 5; j++) times[j] = t2 + (times[j] - t2) * RunUp;
                    for (int j = 6; j < times.Length; j++) times[j] -= cut2;
                    d.end -= cut2; shift -= cut2;
                }
                d.times = times;
                Q.cycles[i] = d;
            }
            Q.duration = P.duration + shift;
            return Q;
        }

        /// <summary>A multiple-vortex cycle as the first shape of `order` neither neighbour has, at that shape's size and
        /// flare: the web build's draw (cycles.ts) carried over to the new shape's range.</summary>
        static void Reshape(StormCycle d, TornadoShape[] order, TornadoShape? prev, TornadoShape? next)
        {
            var s = order[0];
            foreach (var o in order) if (o != prev && o != next) { s = o; break; }
            d.shape = s;
            // radius: the web build's scale 1.5-3.2 of the setup's tornado radius, to the middle of the new shape's
            double mid = s == TornadoShape.Rope ? 0.525 : s == TornadoShape.Cone ? 1.0 : s == TornadoShape.Stovepipe ? 1.225 : 2.35;
            d.radius = C.Clamp(d.radius * mid / 2.35, s == TornadoShape.Rope ? 0.025 : 0.055, s == TornadoShape.Rope ? 0.075 : 0.65);
            // flare: where the draw fell in the multiple-vortex range (1.8-2.8), in the new shape's
            double u = C.Clamp((d.topRatio - 1.8) / 1.0, 0, 1);
            d.topRatio = s == TornadoShape.Stovepipe ? C.Lerp(1.05, 1.3, u) : s == TornadoShape.Wedge ? C.Lerp(1.1, 1.5, u)
                : s == TornadoShape.Cone ? C.Lerp(2.5, 3.8, u) : C.Lerp(1.8, 2.8, u);
        }

        /// <summary>The plan the game draws for a preset key and seed: the web build's, adapted unless the key names a
        /// website preset ("web:..."), which stays as the web build's for the comparison.</summary>
        public static StormPlan Plan(string presetKey, StormPreset pre, int? seed, PlanOverrides o = null)
        {
            var P = StormDesign.Plan(pre, seed, o);
            return presetKey != null && presetKey.StartsWith(StormPreset.WebPrefix, StringComparison.Ordinal) ? P : Adapt(P);
        }
    }
}

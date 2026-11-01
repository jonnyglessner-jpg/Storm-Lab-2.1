// The game's changes to the web build's plan of a varied storm (user, 2026-10-26), made once when the storm is planned,
// before StormLife trims it. The website's own presets ("web:" keys) and the parity check keep the web build's plan.
//   - No multiple-vortex tornado: a cycle the web build draws as one becomes a wedge (on a low base) or a stovepipe (on a
//     high one, where the web build has no wedges), or failing that the first of the shapes after it that neither
//     neighbouring cycle has, at that shape's size and flare. Wedges keep their multiple-vortex start (StormTornadoes).
//   - A quicker build-up: the clear slot opening and the updraft wrapping round it take about 1.5 min each (BuildUpMin),
//     and the mature mesocyclone of a cycle without a tornado RunUp of its time; every later milestone, and the storm's
//     end, come sooner.
//   - No anticyclonic side (user, 2026-10-27): no anti-mesocyclone lowering at the surge's southern curl and no
//     anticyclonic tornado there, in every game storm (the teaching sequence too).
//   - No wedge on the first cycle (user, 2026-10-31: a wedge needs the storm's second cycle): the first cycle's wedge
//     becomes a stovepipe or cone, as a multiple-vortex cycle on a high base does; a wedge's tornado lives 30-45 min
//     (WedgeLifeMin-Max; the web build's 11-30) from touchdown to gone, its milestones at a wedge's shares of that life.
//   - Each cycle's clear slot, horseshoe and wall cloud sized to its tornado (StormCycle.bigness, Bigness; user,
//     2026-10-31): the web build gives every cycle after the first its violent second tornado's enlargement, whatever
//     the cycle's tornado. Adapt sets it from the web build's funnel; StormTornadoes refines it from the tornado it draws.
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
        /// <summary>a wedge's life from touchdown to gone, storm minutes (user, 2026-10-31: wedges last longer than 11 min,
        /// let them go on for 30+), and the shares of it at its milestones: mature (its multiple-vortex start and the
        /// drillbit's takeover, StormTornadoes.Apply), the rope-out's start (its collar breaking up) and the thin rope</summary>
        public const double WedgeLifeMin = 30, WedgeLifeMax = 45, WedgeMature = 0.06, WedgeRope = 0.72, WedgeThin = 0.9;
        /// <summary>the web build's shortest and longest tornado life, touchdown to gone (cycles.ts)</summary>
        public const double WebLifeMin = 11.5, WebLifeMax = 30.5;

        static readonly TornadoShape[] LowBaseFor = { TornadoShape.Wedge, TornadoShape.Stovepipe, TornadoShape.Cone, TornadoShape.Rope };
        static readonly TornadoShape[] HighBaseFor = { TornadoShape.Stovepipe, TornadoShape.Cone, TornadoShape.Rope };

        /// <summary>The game's plan from the web build's (a copy).</summary>
        public static StormPlan Adapt(StormPlan P)
        {
            if (P == null) return P;
            var Q = P.Clone();
            Q.antiMeso = 0; Q.antiTornado = false;
            if (P.cycles == null) return Q;
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
                var prev = i > 0 ? Q.cycles[i - 1].shape : (TornadoShape?)null;
                var next = i + 1 < n ? P.cycles[i + 1].shape : (TornadoShape?)null;
                if (c.shape == TornadoShape.Multivortex)
                {
                    // (a wedge only from the second cycle on: the first cycle's takes a high base's shapes)
                    Reshape(d, P.zb > 1.9 || i == 0 ? HighBaseFor : LowBaseFor, prev, next, 1.8, 1.0);
                    // the multiple-vortex cycle's extra milestone (its takeover) goes: the rest are the plain cycle's
                    var plain = new double[times.Length - 1];
                    for (int j = 0, k = 0; j < times.Length; j++) if (j != 4) plain[k++] = times[j];
                    times = plain;
                }
                // no wedge on the first cycle (user, 2026-10-31): a stovepipe or cone instead, carrying the wedge's draw
                // (its radius range is the multiple-vortex cycle's, its flare 1.1-1.5)
                if (i == 0 && c.shape == TornadoShape.Wedge) Reshape(d, HighBaseFor, prev, next, 1.1, 0.4);
                // the build-up: [1] (7 min into the canonical cycle) to [2] (touchdown, or where it would be), the clear slot
                // opening (8-13) and the updraft wrapping round it (13-18) after the wall cloud's last minute of forming; the
                // design maps that span linearly, so all of it is shortened, that minute too. A longer build-up in the web
                // build's draw stays a little longer here (as the square root of its span), so cycles still differ.
                double span = times[2] - times[1];
                double cut = Math.Max(0, span - BuildUpMin * Math.Sqrt(span / WebBuildUpMin));
                for (int j = 2; j < times.Length; j++) times[j] -= cut;
                d.end -= cut; shift -= cut;
                // a wedge's life, 30-45 min from touchdown [2] to gone [6] (the web build's 11.5-30.5, carried over in order:
                // its longer draws are the longer wedges), mature, rope and thin at a wedge's shares of it; the fade and the
                // end keep their distance after it, so the next cycle comes that much later
                if (c.tornadic && d.shape == TornadoShape.Wedge)
                {
                    double life = times[6] - times[2], u = C.Clamp((life - WebLifeMin) / (WebLifeMax - WebLifeMin), 0, 1);
                    double W = C.Lerp(WedgeLifeMin, WedgeLifeMax, u), f = times[2], fade = times[7] - times[6], tail = times[8] - times[7];
                    times[3] = f + WedgeMature * W; times[4] = f + WedgeRope * W; times[5] = f + WedgeThin * W; times[6] = f + W;
                    times[7] = times[6] + fade; times[8] = times[7] + tail;
                    double more = times[8] - d.end;
                    d.end += more; shift += more;
                }
                // a cycle without a tornado: its mature stage, [2] to [5] (the canonical 18 -> 29, before occlusion)
                if (!c.tornadic)
                {
                    double t2 = times[2], cut2 = (times[5] - t2) * (1 - RunUp);
                    for (int j = 3; j <= 5; j++) times[j] = t2 + (times[j] - t2) * RunUp;
                    for (int j = 6; j < times.Length; j++) times[j] -= cut2;
                    d.end -= cut2; shift -= cut2;
                }
                d.times = times;
                // its clear slot, horseshoe and wall cloud sized to the web build's funnel for now (StormTornadoes.Build and
                // Rate size them to the tornado the game draws)
                d.bigness = Bigness(d.radius * d.topRatio);
                Q.cycles[i] = d;
            }
            Q.duration = P.duration + shift;
            return Q;
        }

        /// <summary>
        /// A cycle's clear slot, horseshoe and wall cloud sized to its tornado (StormCycle.bigness): the web build's first
        /// cycle (0) suits a funnel whose top is about 0.3 km across in radius (its cones and stovepipes), its second
        /// cycle's enlargement (1) the teaching sequence's mile-wide wedge (top 1.08 km); a rope's is smaller than the first
        /// cycle's, the widest wedge's (top up to 2 km) half as big again as the second's. topKm: the funnel's radius at the
        /// top, km, at its full size (what hangs from the wall cloud).
        /// </summary>
        public static double Bigness(double topKm) => C.Clamp((topKm - 0.3) / 0.78, -0.3, 1.6);

        /// <summary>A multiple-vortex (or first-cycle wedge) cycle as the first shape of `order` neither neighbour has, at
        /// that shape's size and flare: the web build's draw (cycles.ts) carried over to the new shape's range. flareLo,
        /// flareSpan: the draw's own flare range (topRatio), to place it in the new shape's.</summary>
        static void Reshape(StormCycle d, TornadoShape[] order, TornadoShape? prev, TornadoShape? next, double flareLo, double flareSpan)
        {
            var s = order[0];
            foreach (var o in order) if (o != prev && o != next) { s = o; break; }
            d.shape = s;
            // radius: the web build's scale 1.5-3.2 of the setup's tornado radius, to the middle of the new shape's
            double mid = s == TornadoShape.Rope ? 0.525 : s == TornadoShape.Cone ? 1.0 : s == TornadoShape.Stovepipe ? 1.225 : 2.35;
            d.radius = C.Clamp(d.radius * mid / 2.35, s == TornadoShape.Rope ? 0.025 : 0.055, s == TornadoShape.Rope ? 0.075 : 0.65);
            // flare: where the draw fell in its own range (a multiple-vortex cycle's 1.8-2.8, a wedge's 1.1-1.5), in the new shape's
            double u = C.Clamp((d.topRatio - flareLo) / flareSpan, 0, 1);
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

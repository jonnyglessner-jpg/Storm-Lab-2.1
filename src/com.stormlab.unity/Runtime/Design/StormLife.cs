// The game's end of a storm's life: it dies as it leaves the map. The web build's supercell never dies: after its last
// cycle it holds its final state until its time runs out, and its cycles are laid out with no idea where a map ends. Here
// a storm begins to die when its updraft comes within LeadKm of the map's edge on its way out (DyingFrom), and:
//   - Trim: every cycle whose tornado would not be over by then is non-tornadic, so tornadoes happen only on the map; its
//     life ends CollapseMinutes + AnvilMinutes later.
//   - Apply (each frame, after StormDesign.At and StormTornadoes.Apply): the collapse. The wall cloud, inflow tail, hail
//     and flanking line go first; the tower sinks and narrows, the rain and the rotation fade; the anvil goes last.
//   - Vigour: how much of the storm is left (lightning scales its flash rate by it).
// Not part of the web build's design: StormDesign stays as the website's, and parity does not see this.
using System;

namespace StormLab
{
    public static class StormLife
    {
        /// <summary>how far inside the map's edge the updraft is when the storm begins to die, km</summary>
        public const double LeadKm = 3;
        /// <summary>storm minutes the core takes to collapse, and the anvil to fade after it</summary>
        public const double CollapseMinutes = 22, AnvilMinutes = 14;

        /// <summary>Storm minutes at which a storm whose updraft is at (startX, startZ) at storm time 0 (world m, x east, z
        /// north) and moves with `motion` (m/s) times motionScale comes within LeadKm of the edge of the square map of
        /// half-width mapHalfM on its way out; infinity when it does not move.</summary>
        public static double DyingFrom(double startX, double startZ, Vec2 motion, double motionScale, double mapHalfM)
        {
            double vx = motion.x * 60 * motionScale, vz = motion.y * 60 * motionScale;   // m per storm minute
            // the map less LeadKm all round: on a track that runs along an edge, a tornado to one side of the updraft is
            // then still over the map
            double t = double.PositiveInfinity, h = mapHalfM - LeadKm * 1000;
            if (vx > 1e-6) t = Math.Min(t, (h - startX) / vx); else if (vx < -1e-6) t = Math.Min(t, (-h - startX) / vx);
            if (vz > 1e-6) t = Math.Min(t, (h - startZ) / vz); else if (vz < -1e-6) t = Math.Min(t, (-h - startZ) / vz);
            return double.IsInfinity(t) ? t : Math.Max(0, t);
        }

        /// <summary>The plan for a storm that begins to die at dieMin (storm minutes), as a trimmed copy. A storm whose own
        /// life ends before then dies the same way after its last cycle (the web build's would simply be gone). Infinity
        /// or NaN (no map): P itself.</summary>
        public static StormPlan Trim(StormPlan P, double dieMin)
        {
            if (P == null || double.IsNaN(dieMin) || double.IsInfinity(dieMin)) return P;
            dieMin = Math.Min(dieMin, P.duration - 4);   // (the web build's duration is the last cycle's end + 4)
            var Q = P.Clone();
            Q.dieMin = dieMin;
            Q.duration = dieMin + CollapseMinutes + AnvilMinutes;
            if (P.cycles != null)
            {
                // the cycles stay (the design reads the second's start), but a tornado must be over before the storm dies
                Q.cycles = new StormCycle[P.cycles.Length];
                for (int i = 0; i < P.cycles.Length; i++)
                {
                    var c = P.cycles[i];
                    Q.cycles[i] = new StormCycle
                    {
                        start = c.start, end = c.end, times = c.times, shape = c.shape, tornadic = c.tornadic && GoneMin(c) <= dieMin,
                        radius = c.radius, topRatio = c.topRatio, condensation = c.condensation, dust = c.dust, lean = c.lean, phase = c.phase,
                    };
                }
            }
            else if (P.kind == StormKind.Supercell && P.tornadic)
                Q.tornadic = dieMin >= P.duration - 4;   // the teaching sequence: its fixed cycles cannot be moved to fit
            return Q;
        }

        /// <summary>when a cycle's tornado is gone, storm minutes (its milestone after the thinning rope)</summary>
        public static double GoneMin(StormCycle c) => c.times[c.times.Length - 3];

        /// <summary>how much of the storm is left at storm time t: 1 until it begins to die, 0 once its core has collapsed</summary>
        public static double Vigour(StormPlan P, double t) => P == null || t <= P.dieMin ? 1 : 1 - Smooth((t - P.dieMin) / CollapseMinutes);

        /// <summary>The collapse, on the frame StormDesign.At (and StormTornadoes.Apply) filled for storm time t.</summary>
        public static void Apply(StormPlan P, double t, StormFrame F)
        {
            if (P == null || t <= P.dieMin) return;
            double d = Smooth((t - P.dieMin) / CollapseMinutes);                       // the core: 0 .. 1
            double early = Smooth(1.6 * (t - P.dieMin) / CollapseMinutes);              // what goes first
            double a = Smooth((t - P.dieMin - 0.6 * CollapseMinutes) / (0.4 * CollapseMinutes + AnvilMinutes));   // the anvil
            var p = F.param;
            void Mul(int slot, int c, double k) => p[slot * 4 + c] = (float)(p[slot * 4 + c] * k);
            float zb = p[1 * 4];
            // tower: sinks toward the base, narrows, and is gone
            p[2 * 4] = (float)(zb + 1 + (p[2 * 4] - zb - 1) * (1 - 0.6 * d));
            Mul(2, 1, 1 - 0.35 * d); Mul(2, 2, 1 - d); Mul(2, 3, 1 - d);
            Mul(3, 2, 1 - early); Mul(3, 3, 1 - d);                 // overshooting top, mesocyclone spin
            Mul(4, 3, (1 - 0.5 * d) * (1 - a)); Mul(5, 2, 1 - d);   // anvil, mammatus
            Mul(6, 3, 1 - early); Mul(7, 0, 1 - early); Mul(7, 2, 1 - early);   // wall cloud, its lowering and tail cloud
            Mul(8, 3, 1 - early);                                   // inflow tail
            Mul(9, 3, 1 - d); Mul(10, 3, 1 - early);                // forward-flank rain, hail
            Mul(11, 0, 1 - early); Mul(11, 2, 1 - d);               // clear slot, rear-flank rain
            Mul(12, 2, 1 - early);                                  // flanking line
            Mul(13, 2, 1 - early); Mul(15, 2, 1 - early);           // any tornado still on the ground, its debris
            Mul(16, 3, 1 - a);                                      // the late thin anvil
            Mul(18, 3, 1 - early); Mul(19, 2, 1 - early); Mul(19, 3, 1 - early);   // the surge's horseshoe, anticyclonic side
            if (p.Length > 28 * 4 + 2) Mul(28, 2, 1 - early);        // a satellite tornado (the detailed tornado's slot)
            F.stage = d < 1 ? "Storm dying as it leaves" : "Anvil fading";
            F.caption = d < 1
                ? "The storm is leaving the area. Cut off from its inflow, the updraft weakens: the wall cloud and the flanking towers go first, then the tower sinks and the rain thins out."
                : "Only the anvil is left, spreading thin and fading.";
        }

        static double Smooth(double x) { x = Math.Max(0, Math.Min(1, x)); return x * x * (3 - 2 * x); }
    }
}

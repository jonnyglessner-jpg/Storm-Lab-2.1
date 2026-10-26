// C# port of StormLab's designed storm (src/storm3d/design.ts, web build f5bd687). Kept line for line with the
// TypeScript so the two can be diffed.
//
// The designed storm: a supercell built from the conceptual model forecasters use (Lemon & Doswell 1979 and later
// work), not solved from equations. The setup's environment decides how strong, how rotating, how wet and which way
// the storm goes; a timed lifecycle decides when each feature appears.
//
// Coordinates are km in the storm's frame (it moves with the storm): x east, y north, z up. The updraft sits at `up`,
// offset upstream of the volume's centre so the anvil has room downstream.
using System;

namespace StormLab
{
    public enum StormKind { Supercell, Pulse, Capped, None }

    /// <summary>Fixed properties of a storm for one setup.</summary>
    public sealed class StormPlan
    {
        public StormKind kind;
        /// <summary>cloud base and storm top (equilibrium level), km</summary>
        public double zb, el;
        /// <summary>storm motion, m/s, and its unit vector; left normal</summary>
        public Vec2 motion, fwd, lft;
        /// <summary>anvil direction (storm-relative wind aloft)</summary>
        public Vec2 adir;
        /// <summary>updraft tilt, km per km of height</summary>
        public Vec2 tilt;
        /// <summary>updraft radius, km</summary>
        public double R;
        public double striation;
        /// <summary>precipitation scales</summary>
        public double ffd, hail, rfd, wrap, slot, tail;
        public double anvilLen;
        /// <summary>wall cloud radius and lowering, km; rotation rate, rad/s</summary>
        public double Rw, lower, spin;
        /// <summary>mesocyclone's visible spin, rad/s (counter-clockwise)</summary>
        public double mesoSpin;
        /// <summary>tornado: whether the storm makes one, its type (0 slender .. 1 wedge), ground radius km</summary>
        public bool tornadic; public double torType, torR;
        /// <summary>anticyclonic circulation at the southern curl of the RFD surge: anti-mesocyclone strength (0..1), and
        /// whether this storm (rarely) puts down an anticyclonic tornado there</summary>
        public double antiMeso; public bool antiTornado;
        /// <summary>total length of the lifecycle, min</summary>
        public double duration;
        /// <summary>Null for the original teaching sequence; otherwise a reproducible varied storm.</summary>
        public int? seed;
        public StormCycle[] cycles;
        /// <summary>updraft speed, m/s, freezing level, km, and the median tornado's peak wind, m/s (StormDiag; read by
        /// lightning and the game, not by the design)</summary>
        public double w, fzlKm, vTor;
        /// <summary>the sounding's LCL, m, and the precipitation lean -1 (LP) .. 1 (HP) (read by StormTornadoes)</summary>
        public double lclM, hpness;
        /// <summary>(game, StormLife) storm minutes at which it begins to die as it leaves the map; infinity: never</summary>
        public double dieMin = double.PositiveInfinity;
        /// <summary>(game, StormLife) a copy to trim; its cycles are shared until replaced</summary>
        public StormPlan Clone() => (StormPlan)MemberwiseClone();

        /// <summary>Where the storm frame's origin is over the ground at storm time t (min), km from where it started.</summary>
        public Vec2 Travel(double tMin) => new Vec2(motion.x * 0.06 * tMin, motion.y * 0.06 * tMin);
        /// <summary>The updraft's centre in the storm frame (km), as stormAt places it.</summary>
        public Vec2 Updraft => new Vec2(-adir.x * 16, -adir.y * 16);
    }

    /// <summary>One moment of the storm, as numbers the GPU builder and renderer read.</summary>
    public sealed class StormFrame
    {
        /// <summary>packed parameters for the GPU (StormSpin.NSLOT x float4: the design's, the tornado's and the angles)</summary>
        public float[] param = new float[StormSpin.NSLOT * 4];
        /// <summary>what's happening now, for captions</summary>
        public string stage = "", caption = "";
        /// <summary>tornado on the ground (0..1)</summary>
        public double tornado;
        /// <summary>Active cycle (zero-based), or -1 while the storm is organising.</summary>
        public int cycleIndex;
    }

    /// <summary>
    /// What a game may decide about a storm that the web build takes from its environment (StormScenario): its precipitation
    /// mode, and that its cycles put down tornadoes. Null in Plan keeps the web build's storm exactly.
    /// </summary>
    public sealed class PlanOverrides
    {
        public Precip? precip;
        public bool forceTornadic;
        public override string ToString() => $"{precip?.ToString() ?? "env"}{(forceTornadic ? "+tor" : "")}";
    }

    public static class StormDesign
    {
        /// <summary>Number of float4 slots in the parameter block (must match StormCommon.hlsl).</summary>
        public const int NSLOT = 23;

        static Vec2 Unit(double u, double v) { double m = Math.Sqrt(u * u + v * v); if (m == 0) m = 1; return new Vec2(u / m, v / m); }
        static double Ramp(double t, double a, double b) { double x = C.Clamp((t - a) / (b - a), 0, 1); return x * x * (3 - 2 * x); }

        /// <summary>Visual headroom beneath the storm; the sounding's physical LCL remains unchanged.</summary>
        public const double CLOUD_BASE_LIFT_KM = 0.2;

        public static StormPlan Plan(Environment E, StormDiag S, int? seed = null, PlanOverrides o = null)
        {
            double zb = C.Clamp(E.pc.lcl / 1000, 0.5, 2.6) + CLOUD_BASE_LIFT_KM, el = C.Clamp((E.pc.el ?? 12000) / 1000, 8, 15.5);
            var rm = E.bk.rm; var motion = new Vec2(rm.x, rm.y);
            var fwd = Unit(rm.x, rm.y); var lft = new Vec2(-fwd.y, fwd.x);
            var adir = Unit(WindProfile.At(E.w.u, 10000) - rm.x, WindProfile.At(E.w.v, 10000) - rm.y);
            var sh = Unit(WindProfile.At(E.w.u, 6000) - E.w.u[0], WindProfile.At(E.w.v, 6000) - E.w.v[0]);
            double tiltAmt = 0.1 * C.Clamp(E.shear6 / 25, 0, 1.4);
            StormKind kind = !S.forms ? (S.capped ? StormKind.Capped : StormKind.None)
                : S.mode == StormMode.Pulse || S.mode == StormMode.Multicell ? StormKind.Pulse : StormKind.Supercell;
            var precip = o?.precip ?? S.precip;
            bool lp = precip == Precip.LP, hp = precip == Precip.HP;
            double R = C.Clamp(2.2 + 1.3 * C.Clamp((S.w - 20) / 30, 0, 1), 2.0, 3.5) * (lp ? 0.85 : 1);
            double rcKm = S.ptor > 0 ? S.TornadoRadiusYd(S.vMed) * 0.9144 / 1000 : 0.1;
            double meso = C.Clamp(S.vLow / 20, 0.3, 1.2);
            bool tornadic = kind == StormKind.Supercell && (S.ptor >= 0.3 || o != null && o.forceTornadic);
            double torR = C.Clamp(rcKm, 0.03, 0.45);
            var cycles = seed == null || kind != StormKind.Supercell ? null : StormCycles.Make(seed.Value, tornadic, torR, zb);
            return new StormPlan
            {
                kind = kind, zb = zb, el = el, motion = motion, fwd = fwd, lft = lft, adir = adir,
                tilt = kind == StormKind.Supercell ? new Vec2(fwd.x * tiltAmt, fwd.y * tiltAmt) : new Vec2(sh.x * tiltAmt, sh.y * tiltAmt), R = R,
                striation = (lp ? 1 : hp ? 0.3 : 0.65) * C.Clamp(S.vMid / 18, 0.4, 1.2),
                ffd = lp ? 0.35 : hp ? 1.5 : 1, hail = lp ? 0.6 : hp ? 1.1 : 1, rfd = lp ? 0.25 : hp ? 1.4 : 0.9,
                wrap = hp ? 0.75 : 0, slot = hp ? 0.45 : lp ? 0.8 : 1, tail = lp ? 0.6 : hp ? 0.85 : 1,
                anvilLen = C.Clamp(26 + 1.0 * E.srAnvil, 20, 52),
                Rw = C.Clamp(1.4 + 1.1 * meso, 1.3, 2.8) * (hp ? 1.2 : 1), lower = C.Clamp(0.25 + 0.3 * meso, 0.2, 0.6) * C.Clamp(1.6 - zb / 2, 0.6, 1.3),
                spin = 1.5625 * (0.04 + 0.08 * meso), mesoSpin = 0.025 + 0.03 * meso,
                tornadic = tornadic,
                torType = rcKm < 0.07 ? 0.15 : rcKm < 0.18 ? 0.5 : 0.9, torR = torR,
                antiMeso = kind == StormKind.Supercell && S.ptor >= 0.3 ? C.Clamp(E.srh1 / 300, 0.4, 1) : 0,
                antiTornado = kind == StormKind.Supercell && E.stp >= 4,
                duration = cycles != null ? cycles[cycles.Length - 1].end + 4 : kind == StormKind.Supercell ? 125 : 70,
                seed = seed, cycles = cycles,
                w = S.w, fzlKm = S.fzl / 1000, vTor = S.vMed, lclM = E.pc.lcl, hpness = o?.precip == Precip.HP ? Math.Max(S.hpness, 0.8) : o?.precip == Precip.Classic ? C.Clamp(S.hpness, -0.3, 0.3) : S.hpness
            };
        }

        /// <summary>A preset's environment, diagnosis and plan in one step.</summary>
        public static StormPlan Plan(StormPreset preset, int? seed = null, PlanOverrides o = null)
        {
            var E = Environment.Make(preset.ToEnv());
            return Plan(E, StormDiag.Make(E), seed, o);
        }

        /// <summary>Wall-cloud / RFD clear-slot / tornado cycle, starting at 30 min, then every 38 min.</summary>
        const double CYCLE0 = 30, CYCLE = 38;

        static readonly string[] ShapeName = { "rope", "cone", "stovepipe", "wedge", "multivortex" };
        static readonly double[] ShapeKind = { 0.12, 0.5, 0.8, 0.96, 0.9 };

        /// <summary>Everything at storm time t (min). `clock` is wall-clock seconds, for motion inside features (spin, sway).</summary>
        public static StormFrame At(StormPlan P, double t, double clock, StormFrame into = null)
        {
            var F = into ?? new StormFrame();
            var a = F.param;
            Array.Clear(a, 0, a.Length);   // (the slots after NSLOT are StormTornadoes')
            void Set(int slot, double x, double y, double z, double w) { a[slot * 4] = (float)x; a[slot * 4 + 1] = (float)y; a[slot * 4 + 2] = (float)z; a[slot * 4 + 3] = (float)w; }
            double fx = P.fwd.x, fy = P.fwd.y, lx = P.lft.x, ly = P.lft.y;
            // updraft 16 km upstream of the volume's centre along the anvil: the late anvil spreads in every direction, so the
            // updraft sits close to the middle of the volume, with a little more room downwind
            var up = new Vec2(-P.adir.x * 16, -P.adir.y * 16);
            Vec2 At2(double f, double l) => new Vec2(up.x + f * fx + l * lx, up.y + f * fy + l * ly);
            string stage = "", caption = ""; double torOn = 0;
            int cycleIndex;
            if (P.cycles != null) { cycleIndex = -1; for (int i = 0; i < P.cycles.Length; i++) if (t >= P.cycles[i].start) cycleIndex = i; }
            else cycleIndex = t < CYCLE0 ? -1 : Math.Min((int)Math.Floor((t - CYCLE0) / CYCLE), 1);
            var cycle = P.cycles != null && cycleIndex >= 0 ? P.cycles[cycleIndex] : null;
            double cycleStart = P.cycles?[0].start ?? CYCLE0;
            double secondStart = P.cycles?[1].start ?? CYCLE0 + CYCLE;

            // ---- defaults: nothing
            double towerTop = P.zb + 0.5, towerR = 1, towerAmt = 0, strat = 0, overshoot = 0;
            double anvilAmt = 0, anvilLen = 4, back = 1, mamm = 0, thinAnvil = 0, ffdPull = 0, bigF = 0, flankMature = 0, torKindOut = P.torType;
            // second tornado (see below): central core radius, multi-vortex amount, breakdown into a collar, mode; sub-vortex orbit and radius
            double t4x = 0, t4y = 0, t4z = 0, t4w = 0, t5x = 0, t5y = 0;
            double wallAmt = 0, lower = 0, wallSpin = 0, tailCloud = 0; var wc = At2(-1.0, -0.8);
            double tailAmt = 0, ffdAmt = 0, hailAmt = 0, slot = 0, slotR = P.Rw + 1, rfdAmt = 0;
            double flankAmt = 0, flankN = 0, mesoSpin = 0, middleGrowth = 0;
            double torAmt = 0, torGround = 0.1, torTop = 0.3, cond = 0, rope = 0, debris = 0;
            Vec2 torBot = wc, torTopP = wc;
            // rear-flank downdraft surge: a bulge of outflow (centre rcB, radius Rs) whose gust front curls round from its
            // northern end (under the wall cloud, where the tornado forms) to its southern end (anticyclonic side)
            Vec2 rcB = wc; double Rs = 0, horse = 0; Vec2 curlS = wc; double antiAmt = 0, antiTor = 0;

            if (P.kind == StormKind.Supercell)
            {
                towerTop = P.zb + 1 + (P.el + 0.4 - P.zb - 1) * Ramp(t, 0, 14);
                // Broadening begins during organisation, well before the first wall cloud, and
                // continues slowly into maturity independently of individual RFD/wall-cloud cycles.
                middleGrowth = Ramp(t, 12, 44);
                // The second tornado cycle (from 68 min) is the violent one: a broader updraft, a much larger clear slot and
                // horseshoe, and a bigger, longer-lived tornado. `big` eases all of that in as the second cycle begins.
                double big = Ramp(t, secondStart - 4, secondStart + 8);
                bigF = big;
                double towerR0 = P.R * (0.8 + 0.2 * Ramp(t, 4, 20) + 0.25 * Ramp(t, 16, 44)) * (1 + 0.18 * Ramp(t, 12, 40));
                towerR = towerR0 * (1 + 0.35 * big);
                towerAmt = 1;
                overshoot = Ramp(t, 14, 20) * (0.7 + 0.3 * Math.Sin(t * 0.9));
                // Lateral spreading follows the tower reaching the tropopause, rather than
                // starting as a separate high cloud while the tower is still rising below it.
                anvilAmt = Ramp(t, 14, 24); anvilLen = 5 + P.anvilLen * Ramp(t, 14, 55);
                // Once the first tornado cycle is over the anvil keeps spreading far downwind as a thin veil of ice, which casts
                // only a faint shadow. The volume reaches at least 106 km downwind of the updraft, so the extra reach stops short of that.
                thinAnvil = Ramp(t, secondStart, secondStart + 25);
                // the forward-flank rain then retreats through the second cycle, well pulled in by its end
                ffdPull = Ramp(t, secondStart + 19, secondStart + 38);
                anvilLen += Math.Max(0, Math.Min(50, 95 - (5 + P.anvilLen))) * thinAnvil;
                // backshear: the anvil spreads upwind against the wind aloft too, over the flanking line, by up to 3 updraft radii
                back = P.R * (0.8 + 2.2 * Ramp(t, 16, 40)) + 38 * thinAnvil;   // late: upwind too, well past the updraft
                mamm = 0.8 * Ramp(t, 32, 48);
                strat = P.striation * Ramp(t, 20, 32);
                ffdAmt = P.ffd * Ramp(t, 14, 24); hailAmt = P.hail * Ramp(t, 18, 28);
                tailAmt = P.tail * Ramp(t, 24, 34);
                flankAmt = Ramp(t, 18, 30); flankN = 6;
                // the flanking line matures into bigger, thicker towers through the second cycle
                flankMature = Ramp(t, secondStart, secondStart + 22);
                mesoSpin = P.mesoSpin * Ramp(t, 16, 30) * (1 + 0.5 * big);
                if (t >= cycleStart)
                {
                    // two cycles only: after the second the storm stays in it, so the last tornado can take its time to dissipate
                    int k = cycle != null ? (cycle.shape == TornadoShape.Multivortex ? 1 : 0) : cycleIndex;
                    double tau = cycle != null ? StormCycles.CycleTime(cycle, t) : (t - CYCLE0) - k * CYCLE;
                    bool produces = cycle != null ? cycle.tornadic : P.tornadic;
                    // the wall cloud forms under the rear-right of the updraft base, then occludes rearward and fades
                    double occ = Ramp(tau, 24, 34);
                    wc = At2(-1.0 - 2.2 * occ, -0.8 + 1.4 * occ);
                    // The first cycle's wall cloud and slot fade out as it occludes (30-36 min in). In the second, the horseshoe and
                    // wall cloud stay through the long rope-out of the last tornado, and fade away only after it is gone.
                    double fadeFrom = k == 1 ? 44 : 30, fadeTo = k == 1 ? 52 : 36;
                    wallAmt = Ramp(tau, 0, 7) * (1 - Ramp(tau, fadeFrom, fadeTo));
                    lower = P.lower * Ramp(tau, 0, 9) * (1 - (k == 1 ? Ramp(tau, fadeFrom, fadeTo) : 0));
                    // Keep a stable phase rate through RFD formation and shrink-out. Ramping a rate
                    // multiplied by the global clock makes the texture jump, then appear to stall.
                    wallSpin = P.spin;
                    tailCloud = wallAmt * Ramp(tau, 4, 10);
                    slot = P.slot * Ramp(tau, 8, 17) * (1 - Ramp(tau, fadeFrom, fadeTo));
                    slotR = (P.Rw + 0.8 + 2.2 * Ramp(tau, 12, 30)) * (1 + 0.8 * big);
                    rfdAmt = P.rfd * Ramp(tau, 7, 15);
                    if (cycle != null) rfdAmt *= 1 - Ramp(tau, fadeFrom, fadeTo);
                    // The surge, seen from below, is a horseshoe: the base's edge curls round the clear slot, closed on the east
                    // (inflow) side and open to the rear, where the RFD comes down. Its arms reach back to the west: the wall cloud
                    // and the tornado sit on the inner side of the northern arm, the anticyclonic side at the tip of the southern
                    // arm. Angles are in the storm's frame (0 = downstream, +90 = left of the motion, i.e. north for a NE mover).
                    Vec2 Dir(double th) => new Vec2(Math.Cos(th) * fx + Math.Sin(th) * lx, Math.Cos(th) * fy + Math.Sin(th) * ly);
                    double slotGrowth = Ramp(tau, 8, 18);
                    // One anchor for the northern curl, rotating wall cloud and tornado. The curl is at the
                    // northern end of the visible arc (+60 deg), just inside its rolled edge.
                    var dn = Dir(60 * Math.PI / 180); var dsth = Dir(-170 * Math.PI / 180);
                    var wcRaw = wc;
                    // The anticyclonic tornado keeps its size and timing but sits at the southern tip of the (now larger) horseshoe
                    {
                        double tR = towerR, sc = 1 + 0.9 * big;
                        double gR = Math.Min(Math.Min(P.Rw * sc * (0.55 + 0.198 * Ramp(tau, 9, 24)), tR * 0.462 * sc), Math.Max(tR * 0.9 - 0.85, 0.3)) * (0.2 + 0.8 * slotGrowth);
                        var rc = new Vec2(wcRaw.x - 0.88 * gR * dn.x, wcRaw.y - 0.88 * gR * dn.y);
                        // Keep the entire compact rim (including its lip) underneath the main updraft,
                        // including late in occlusion. Move the shared northern-curl anchor with it.
                        double dx = rc.x - up.x, dy = rc.y - up.y;
                        double reach = Math.Max(tR * 0.9 - gR - 0.65, 0.15);
                        double fit = Math.Min(1, reach / Math.Max(Math.Sqrt(dx * dx + dy * dy), 0.001));
                        rc = new Vec2(up.x + dx * fit, up.y + dy * fit);
                        Rs = gR; rcB = rc;
                        wc = new Vec2(rc.x + 0.88 * gR * dn.x, rc.y + 0.88 * gR * dn.y);
                        curlS = new Vec2(rc.x + gR * dsth.x, rc.y + gR * dsth.y);
                    }
                    var curlN = wc;
                    horse = slot * Ramp(tau, 10, 18);
                    antiAmt = P.antiMeso * Ramp(tau, 14, 19) * (1 - Ramp(tau, 27, 32));
                    if (P.antiTornado && (cycle != null ? cycleIndex == 1 && produces : k == 1)) antiTor = Ramp(tau, 21, 22.5) * (1 - Ramp(tau, 25, 27));
                    if (produces)
                    {
                        // In the second cycle it forms earlier, lasts about 8 minutes longer and is much bigger: a violent wedge
                        double torBig = cycle != null ? 0 : big;
                        double form = Ramp(tau, 18 - 3 * torBig, 20.5 - 3 * torBig), die = Ramp(tau, 29 + 7 * torBig, 31.5 + 6 * torBig);
                        // the second tornado grows to a radius of 0.8 km, about a mile wide
                        double torSize = cycle != null ? cycle.radius / P.torR : 1 + big * (0.8 / P.torR - 1);
                        double torKind = cycle != null ? ShapeKind[(int)cycle.shape] : P.torType + (0.95 - P.torType) * big;
                        torKindOut = torKind;
                        torAmt = form * (1 - die);
                        rope = Ramp(tau, 24 + 8 * torBig, 29 + 8 * torBig);
                        cond = Ramp(tau, 17.5 - 3 * torBig, 20 - 3 * torBig) * (P.zb - CLOUD_BASE_LIFT_KM < 1.4 ? 1 : 0.75 + 0.25 * torBig) * (1 - 0.45 * Ramp(tau, 28 + 8 * torBig, 31 + 8 * torBig));
                        debris = Ramp(tau, 19 - 3 * torBig, 20.5 - 3 * torBig) * (1 - Ramp(tau, 28 + 7 * torBig, 31 + 6 * torBig));
                        double shrink = 1 - 0.75 * rope;
                        torGround = P.torR * torSize * shrink * (0.6 + 0.4 * form);
                        torTop = (P.torR * torSize * (torKind > 0.7 ? (big > 0 ? 1.2 : 1.4) : 2.6) + 0.12) * (1 - 0.6 * rope);
                        if (cycle != null) torTop = cycle.radius * cycle.topRatio * (1 - 0.6 * rope);
                        if (k == 1)
                        {
                            // The second tornado's life: it starts as a small multiple-vortex tornado; a central vortex like the first
                            // tornado forms, swallows the sub-vortices and grows to a mile wide; then the wedge breaks down into a
                            // collar cloud, exposing the central vortex, which ropes out.
                            double takeover = Ramp(tau, 22, 29), takeoverS = takeover * takeover * (3 - 2 * takeover);
                            double wedgeR = (0.3 - 0.2 * Ramp(tau, 21, 22.5)) + 0.7 * takeoverS;
                            t4x = Ramp(tau, 20, 22) * Math.Max(P.torR * 1.2, 0.09); t4y = Ramp(tau, 15, 17) * (1 - Ramp(tau, 21.5, 27.5));
                            t4z = Ramp(tau, 31.5, 34.5); t4w = 1 + Ramp(tau, 21.5, 23);
                            t5x = 0.9; t5y = 0.07;   // the sub-vortices orbit at about 90% of the wedge's radius
                            // It ropes out slowly: the rope thins and lifts, the dust settles, and only then does the funnel fade away
                            rope = Ramp(tau, 32, 41);
                            debris = Ramp(tau, 16, 17.5) * (1 - Ramp(tau, 35, 41)) * (0.55 + 0.45 * takeoverS) * (1 - 0.35 * t4z);   // lighter dust while it is small, and as the wedge breaks down
                            cond = Ramp(tau, 14.5, 17) * (1 - 0.5 * Ramp(tau, 36, 44));
                            torAmt = Ramp(tau, 15, 17.5) * (1 - Ramp(tau, 39, 45));
                            t4x *= 1 - 0.5 * rope;
                            torGround = wedgeR * (1 - 0.75 * rope);
                            torTop = (wedgeR * 1.2 + 0.12) * (1 - 0.6 * rope);
                            if (cycle != null)
                            {
                                // Reuse the detailed takeover/collar animation at this cycle's own size.
                                double scale = cycle.radius / 0.8;
                                torGround *= scale; torTop *= scale; t4x *= scale;
                                t5y *= Math.Sqrt(scale);
                            }
                        }
                        if (cycle != null) { cond *= cycle.condensation; debris *= cycle.dust; }
                        // tornado under the rear-right part of the wall cloud; it's left behind as the storm occludes (rope out)
                        torTopP = curlN;   // on the inner side of the horseshoe's northern arm
                        double drift = (cycle?.lean ?? 0.45) * rope;
                        torBot = new Vec2(torTopP.x + (-fx * 0.8 - lx * 0.6) * drift, torTopP.y + (-fy * 0.8 - ly * 0.6) * drift);
                        if (cycle != null)
                        {
                            double wander = 0.05 * form * Math.Sin(tau * 0.45 + cycle.phase);
                            torBot = new Vec2(torBot.x + lx * wander, torBot.y + ly * wander);
                        }
                        torOn = torAmt;
                    }
                    if (antiTor > 0.15) { stage = "Anticyclonic tornado"; caption = "Rare: a second tornado at the southern curl of the rear-flank downdraft surge, turning clockwise, while the main tornado turns counter-clockwise at the northern curl."; }
                    else if (k == 1 && torAmt > 0.15 && rope <= 0.4 && tau < 22) { stage = "Multiple-vortex tornado"; caption = "The tornado starts small and made of several sub-vortices orbiting a common centre, each spinning faster than the circulation as a whole."; }
                    else if (k == 1 && torAmt > 0.15 && rope <= 0.4 && tau < 29) { stage = "Central vortex takes over"; caption = "A single central vortex forms, draws in the sub-vortices and widens quickly into a large wedge."; }
                    else if (k == 1 && torAmt > 0.15 && rope <= 0.4 && tau < 31.5) { stage = "Mile-wide wedge"; caption = "The tornado is at full size: a mile-wide wedge, dark with debris, filling the horseshoe."; }
                    else if (k == 1 && torAmt > 0.15 && rope <= 0.4) { stage = "Wedge breaking down"; caption = "The wedge breaks down into a broad collar cloud, exposing the slender central vortex underneath it."; }
                    else if (torAmt > 0.15) { stage = rope > 0.4 ? "Tornado roping out" : "Tornado on the ground"; caption = rope > 0.4 ? "The rear-flank downdraft has wrapped around the circulation and cut off its warm inflow. The tornado thins into a rope and is left behind as the storm occludes." : "The tornado forms at the northern curl of the rear-flank downdraft surge, where the surge meets the warm inflow under the wall cloud." + (antiAmt > 0.3 ? " At the surge's southern curl the air turns the other way: an anti-mesocyclone." : ""); }
                    else if (tau < 8) { stage = cycleIndex > 0 ? "New wall cloud" : "Wall cloud forming"; caption = "A small lowering develops beneath the rotating updraft, gradually growing and deepening into a wall cloud before the rear-flank surge arrives."; }
                    else if (tau < 13) { stage = "Clear slot opening"; caption = "The rear-flank downdraft reaches the wall cloud and begins clearing cloud from the back of the updraft."; }
                    else if (tau < 18) { stage = "Updraft wrapping around the clear slot"; caption = "The rotating updraft wraps around the widening clear slot. Its open rear admits sunlight into a deep horseshoe, with the wall cloud at the northern curl."; }
                    else if (tau < 29) { stage = P.tornadic ? "Mature mesocyclone" : "Mature supercell"; caption = "The rear-flank downdraft surges around the wall cloud and the horseshoe-shaped clear slot widens." + (antiAmt > 0.3 ? " Cyclonic rotation curls up at its northern end and anticyclonic rotation at its southern end." : "") + (P.tornadic ? "" : " Strong low-level rotation, but no tornado this cycle."); }
                    else { stage = "Occlusion"; caption = "The gust front wraps around the old circulation, which weakens and drifts to the rear. A new updraft base and wall cloud take over to the east."; }
                    if (cycle != null && antiTor <= 0.15)
                    {
                        string name = ShapeName[(int)cycle.shape];
                        if (torAmt > 0.15 && k == 0 && rope <= 0.4)
                        {
                            stage = char.ToUpperInvariant(name[0]) + name.Substring(1) + " tornado";
                            caption = $"This cycle produces a {name} tornado. Its funnel, dust and lifetime differ from the other circulations in this storm.";
                        }
                        else if (!produces && tau >= 18 && tau < 29)
                        {
                            stage = "Non-tornadic cycle";
                            caption = "The wall cloud rotates and the rear-flank surge wraps around it, but this circulation occludes without producing a tornado.";
                        }
                        else if (k == 1 && stage == "Mile-wide wedge")
                        {
                            stage = "Mature multivortex tornado";
                            caption = "The central circulation has absorbed the smaller vortices and reached its greatest width for this cycle.";
                        }
                        else if (k == 1 && stage == "Central vortex takes over")
                        {
                            caption = "A central vortex forms, draws in the orbiting vortices and widens into a broader funnel.";
                        }
                        if (t >= cycle.end && cycleIndex == P.cycles.Length - 1)
                        {
                            stage = "Final circulation fading";
                            caption = "The last circulation has occluded. Its wall cloud and clear slot fade beneath the lingering storm and anvil.";
                        }
                    }
                }
                else if (t < 12) { stage = "Towering cumulus"; caption = "A boundary-layer thermal breaks the cap and rises as a hard, cauliflower tower."; }
                else if (t < 22) { stage = "Cumulonimbus"; caption = "The tower reaches the tropopause and spreads out as an anvil downwind. Rain and hail start to fall on the forward flank."; }
                else { stage = "Supercell organising"; caption = "Wind shear tilts the inflow's spin into the updraft: the updraft rotates (a mesocyclone), the base flattens and striates, and an inflow tail streams in from the east."; }
            }
            else if (P.kind == StormKind.Pulse)
            {
                double life = 1 - Ramp(t, 34, 52), sink = Ramp(t, 34, 50);
                towerTop = P.zb + 1 + (P.el - P.zb - 1) * Ramp(t, 0, 13) * (1 - 0.6 * sink); towerR = P.R * 0.8 * (0.5 + 0.5 * Ramp(t, 3, 15)) * (1 - 0.35 * sink); towerAmt = 0.25 + 0.75 * life;
                overshoot = 0.5 * Ramp(t, 13, 18) * life;
                anvilAmt = Ramp(t, 11, 20) * (1 - 0.6 * Ramp(t, 50, 70)); anvilLen = 4 + 10 * Ramp(t, 11, 40); back = 3;
                ffdAmt = 1.4 * Ramp(t, 16, 22) * (1 - Ramp(t, 40, 55)); hailAmt = 0.4 * Ramp(t, 18, 24) * (1 - Ramp(t, 32, 40));
                stage = t < 13 ? "Towering cumulus" : t < 22 ? "Mature storm" : t < 34 ? "Heavy rain" : t < 50 ? "Collapsing" : "Dissipating anvil";
                caption = t < 13 ? "A tower rises in hot, humid air with almost no wind." : t < 34 ? "Without shear, the rain falls straight back down through the updraft: a short burst of torrential rain and small hail." : "The rain-cooled downdraft chokes off the updraft. The tower collapses and only the anvil is left.";
            }
            else if (P.kind == StormKind.Capped)
            {
                double pulse = Math.Abs(Math.Sin(t / 7));
                towerTop = P.zb + 0.8 + 1.4 * pulse; towerR = 1.6; towerAmt = 0.9;
                stage = "Capped"; caption = "Cumulus try to grow but the warm layer aloft stops them: they bubble up and fall back all afternoon.";
            }
            else { stage = "Clear"; caption = "Not enough instability for storms."; }

            // ---- pack (order must match StormCommon.hlsl)
            double sx = P.zb;
            Vec2 flank; { double u = -fx - 0.3 * lx, v = -fy - 0.3 * ly, m = Math.Sqrt(u * u + v * v); flank = new Vec2(u / m, v / m); }
            Vec2 tdir; { double u = fx - 0.7 * lx, v = fy - 0.7 * ly, m = Math.Sqrt(u * u + v * v); tdir = new Vec2(u / m, v / m); }
            var ffdC = P.kind == StormKind.Pulse ? up : At2((P.ffd < 0.5 ? 9 : 6) * (1 - 0.25 * ffdPull), (P.ffd < 0.5 ? 6 : 4.5) * (1 - 0.25 * ffdPull));
            var hailC = P.kind == StormKind.Pulse ? up : At2(2.8, 2.2);
            Set(0, fx, fy, lx, ly);
            Set(1, sx, P.el, clock, t);
            Set(2, towerTop, towerR, towerAmt, strat);
            Set(3, P.tilt.x, P.tilt.y, overshoot, mesoSpin);
            Set(4, P.adir.x, P.adir.y, anvilLen, anvilAmt);
            Set(5, P.el - 2.4, P.el + 0.25, mamm, back);
            // Wall-cloud growth starts before the slot radius grows; retain a substantial lowering
            // at RFD arrival, then blend its footprint into the northern curl as the horseshoe expands.
            double wallRadius = Rs > 0 ? Math.Min(P.Rw * (1 + 0.5 * bigF) * (0.2 + 0.8 * wallAmt), towerR * 0.34 * (1 + 0.5 * bigF)) : P.Rw;
            Set(6, wc.x, wc.y, wallRadius, wallAmt);
            Set(7, lower, wallSpin, tailCloud, flankMature);
            Set(8, tdir.x, tdir.y, 14, tailAmt);
            // As the anvil spreads after the first tornado cycle, the forward-flank rain area grows with it (size scale x1.6)
            Set(9, ffdC.x, ffdC.y, (P.kind == StormKind.Pulse ? 0.6 : P.ffd > 1.2 ? 1.35 : 1) * (1 + 0.6 * thinAnvil * (1 - 0.75 * ffdPull)), ffdAmt);
            Set(10, hailC.x, hailC.y, 2.0, hailAmt);
            Set(11, slot, slotR, rfdAmt, P.wrap);
            Set(12, flank.x, flank.y, flankAmt, flankN);
            Set(13, torBot.x, torBot.y, torAmt, torKindOut);
            Set(14, torGround, torTop, cond, rope);
            Set(15, torTopP.x, torTopP.y, debris, 0);
            Set(16, up.x, up.y, middleGrowth, thinAnvil);
            // slots 17 (grid) and 20 (build pass) are filled by the renderer
            Set(18, rcB.x, rcB.y, Rs, horse);
            Set(19, curlS.x, curlS.y, antiAmt, antiTor);
            Set(21, t4x, t4y, t4z, t4w);
            Set(22, t5x, t5y, 0, 0);
            F.stage = stage; F.caption = caption; F.tornado = torOn; F.cycleIndex = cycleIndex;
            return F;
        }
    }
}

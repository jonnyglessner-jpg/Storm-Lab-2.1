// The game's storm structure from its environment (user, 2026-11-07; a game adaptation beside the port, which parity
// does not see; StormRenderer.structure = Environmental). The web build lays every supercell out at one scale: its
// rear-flank surge held under the updraft tower (a rim of 1-2 km), its wall cloud 1.3 km behind the updraft, its
// forward-flank core 7.5 km off whatever the winds. So the updraft and downdrafts sat close together, the hook on radar
// was tiny beside the core, the clear slot was too small for a big tornado, and as the slot grew the clamp pushed the wall
// cloud sideways (user: "more akin to an occlusion"). Here the frame the design gives (after StormTornadoes and StormTilt)
// is re-laid from the storm's environment (StormEnv, from the sounding and hodograph the plan was made from):
//
//   - the mesocyclone is 3-10 km across (Stull ch. 14), wider with stronger low-level storm-relative inflow (METR 4403
//     §15: 40 kt and more, very large supercells; under 25 kt, mini-supercells) and in HP storms, narrower in LP ones;
//     the updraft tower is at least as wide as its mesocyclone, fatter with fat CAPE (normalised CAPE 0.3: short and fat;
//     0.1: tall and thin, Stull) and in strong deep shear (Dennis and Kumjian 2017, METR 4403 §32);
//   - the forward-flank core lies where the wind aloft carries the precipitation: a few km beyond the updraft's edge plus
//     a quarter km per m/s of anvil-level storm-relative wind, less in an HP storm (its rain falls back near the updraft
//     and wraps round it) and more in an LP one (Rasmussen and Straka 1998); the hail core just outside the updraft,
//     further in a stronger mid-level storm-relative wind (hail falls nearest the updraft, Stull ch. 14);
//   - the rear-flank surge keeps the web build's size and place (user, 2026-11-08: the separation was meant for the
//     forward flank, not the RFD), only never smaller than the tornado it holds (the clear slot about twice the funnel's
//     top), and it grows rearward from a wall cloud that stays put at the web build's place under the updraft's rear
//     flank, 1.3 km behind it: nothing shoves the wall cloud while the slot opens. The wall cloud and tornado drift back with the occlusion only
//     in the back half of the tornado's life, as it ropes out (Huang and Xue: the occlusion is the shrinking stage);
//   - the tower rises to the equilibrium level at about half the parcel's sqrt(2 CAPE) (Stull 14.8), so a weak-CAPE
//     Dixie tower takes longer to reach the anvil than a High Plains one (the web build's 14 min for every storm);
//   - the slot's, horseshoe's and wall cloud's amounts are averaged over a couple of storm minutes, so the build-up's
//     milestones (StormGame.Adapt's quick build-up, StormCycles.CycleTime's piecewise mapping) ease into each other.
// Pure C# (no UnityEngine) so validation/parity/csharp can list it (-- --structure).
using System;

namespace StormLab
{
    /// <summary>What the structure needs from the storm's environment (Environment, StormDiag), kept on the plan.</summary>
    public sealed class StormEnv
    {
        /// <summary>CAPE J/kg; normalised CAPE m/s^2 (CAPE over the depth from the LFC to the EL); LCL and EL, km</summary>
        public double cape, ncape, lclKm, elKm;
        /// <summary>0-6 km shear; storm-relative winds 0-1 km, 4-6 km and 9-11 km, m/s; 0-1 and 0-3 km SRH, m^2/s^2</summary>
        public double shear6, srLow, srMid, srAnvil, srh1, srh3;

        public static StormEnv From(Environment E)
        {
            var pc = E.pc;
            double el = pc.el ?? 12000, lfc = pc.lfc ?? pc.lcl;
            return new StormEnv
            {
                cape = pc.cape, ncape = pc.cape / Math.Max(el - lfc, 2000), lclKm = pc.lcl / 1000, elKm = el / 1000,
                shear6 = E.shear6, srLow = E.srLow, srMid = E.srMid, srAnvil = E.srAnvil, srh1 = E.srh1, srh3 = E.srh3,
            };
        }
        public override string ToString() =>
            $"CAPE {cape:0} (n {ncape:0.00}), LCL {lclKm:0.00} km, EL {elKm:0.0} km, shear6 {shear6:0} m/s, SR 0-1 {srLow:0} / 4-6 {srMid:0} / 9-11 {srAnvil:0} m/s, SRH1 {srh1:0}";
    }

    public static class StormStructure
    {
        static double Ramp(double t, double a, double b) { double x = C.Clamp((t - a) / (b - a), 0, 1); return x * x * (3 - 2 * x); }

        /// <summary>The storm's scales from its environment, km: the mesocyclone's radius, the tower's radius at maturity
        /// (at least the web build's), the forward-flank core's and the hail core's distance from the updraft.</summary>
        public static (double meso, double tower, double ffd, double hail) Scales(StormPlan P)
        {
            var e = P.env;
            double hp = C.Clamp(P.hpness, -1, 1), hpP = Math.Max(hp, 0), lp = Math.Max(-hp, 0);
            double inflow = C.Clamp((e.srLow - 9) / 9, -0.6, 1.2);
            double rotate = C.Clamp((e.srh3 - 150) / 250, -0.4, 0.8);
            double meso = C.Clamp(2.5 + 0.8 * inflow + 0.4 * rotate + 0.9 * hpP - 0.7 * lp, 1.6, 4.8);
            double fat = C.Clamp((e.ncape - 0.1) / 0.2, 0, 1);
            double width = C.Lerp(0.9, 1.15, fat) * (1 + 0.08 * C.Clamp((e.shear6 - 20) / 15, 0, 1));
            double tower = C.Clamp(Math.Max(P.R, 1.05 * meso + 0.4) * width, P.R, 5.6);
            double ffd = tower + 2.0 + 0.25 * e.srAnvil * (1 - 0.35 * hpP) + 3.5 * lp;
            double hail = tower + 0.8 + 0.06 * e.srMid;
            return (meso, tower, C.Clamp(ffd, tower + 3, 24), hail);
        }

        /// <summary>Seconds of storm time the tower takes to reach the equilibrium level, min: (EL - base) at half of
        /// sqrt(2 CAPE) for its mean rise (0.45 of it: the top slows under the EL), within 9-20 min (the web build: 14).</summary>
        public static double RiseMin(StormPlan P)
        {
            double w = 0.5 * Math.Sqrt(2 * Math.Max(P.env.cape, 100));
            return C.Clamp((P.el - P.zb) * 1000 / (0.45 * w) / 60, 9, 20);
        }

        /// <summary>the design's own time for the early tower and anvil: the rise RiseMin long instead of 14 min, the
        /// difference eased out by 45 min</summary>
        public static double TowerTime(StormPlan P, double t)
        {
            // the design's 14 min reached at t1; the lead (or lag) d1 = 14 - t1 then eased out by 45 min. Monotone: the
            // lead is at most 6 min and eases over at least 25 (slope >= 1 - 6 x 1.5 / 25)
            double t1 = RiseMin(P), s = 14 / t1;
            if (t <= t1) return t * s;
            return t + (14 - t1) * (1 - Ramp(t, t1, 45));
        }

        static readonly StormFrame[] scratch = { new StormFrame(), new StormFrame(), new StormFrame(), new StormFrame(), new StormFrame(), new StormFrame() };
        static readonly double[] filterOff = { -2, -1, 0, 1, 2 }, filterW = { 1 / 16.0, 4 / 16.0, 6 / 16.0, 4 / 16.0, 1 / 16.0 };
        /// <summary>storm minutes between the filter's samples (the build-up's milestones eased over about 3 min)</summary>
        public const double FilterMin = 0.75;
        /// <summary>slots (index * 4 + component) of the amounts eased in time: the wall cloud's radius and amount, its
        /// lowering and tail cloud, the slot, the rear-flank rain, the horseshoe (and the radius, by amount)</summary>
        static readonly int[] eased = { 6 * 4 + 3, 7 * 4, 7 * 4 + 2, 11 * 4, 11 * 4 + 2, 18 * 4 + 3 };

        /// <summary>Re-lay the frame (after StormDesign.At, StormTornadoes.Apply and StormTilt.Apply) from the storm's
        /// environment. Marks the frame (S_spin4.w = 1) for the shaders' and the radar's structure-aware parts.</summary>
        /// <summary>
        /// (user, 2026-11-08: the storm "unnaturally dark" at noon and in the afternoon; the column under the base went near
        /// black, about 1/140 of the sky's brightness against a real thick base's 1/20-1/50) Rain's extinction for light, as
        /// a share of its extinction to the eye. Raindrops scatter about 87 % of what they intercept forward (g ~ 0.87,
        /// geometric optics), and absorb next to nothing, so light diffusing into a rain shaft from the sun, the horizon
        /// and the sky is carried on through it, losing about (1 - g) of the extinction a straight look through it does;
        /// 0.3 leaves margin for the wider angles. Used for every light path through rain (the light grid's sun, sky and
        /// ground light, the scene's sun cookie, the tornado's own sun and horizon tests, so a funnel and the cloud round
        /// it stay lit alike); what the eye sees through rain keeps the full extinction.
        /// </summary>
        public const float RainTransport = 0.3f;

        /// <summary>the game's own slot in the parameter block (S_game): xyz the storm's noise offset</summary>
        public const int Slot = StormSpin.Slot0 + StormSpin.Slots;

        /// <summary>this storm's offset into the noise tiles (S_game.xyz), from its seed: no two storms share a texture</summary>
        public static void NoiseOffset(StormPlan P, StormFrame F)
        {
            if (P?.seed == null) return;
            var r = new StormRng(P.seed.Value, 530);
            F.param[Slot * 4] = (float)r.U(); F.param[Slot * 4 + 1] = (float)r.U(); F.param[Slot * 4 + 2] = (float)r.U();
        }

        /// <summary>the occluding wall cloud's drift against the updraft, as a velocity: from rope-out the
        /// mesocyclone falls back and to the left of the motion at up to OccBack and OccLeft of the storm's speed, so its
        /// tornado slows to about 0.65 of the storm's speed and curves about 50 degrees left (the classic hook at the end
        /// of a tornado track), over at least OccRampMin storm minutes</summary>
        public const double OccBack = 0.6, OccLeft = 0.5, OccRampMin = 4;
        /// <summary>the occlusion's furthest drift, km: the web build's (At2(-1 - 2.2 occ, -0.8 + 1.4 occ), 2.6 km)</summary>
        public const double OccMaxKm = 2.6;
        /// <summary>the mature wall cloud's place from the updraft, km along and left of the motion: the web build's</summary>
        public const double WallBack = -1.0, WallLeft = -0.8;

        /// <summary>The occlusion's offset of the wall cloud (and the tornado hung from it) from its mature place, km
        /// back (negative: rearward along the motion) and left. (user, 2026-11-08: the tornadoes "take very sudden turns
        /// and move insanely fast when the occlusion starts": a fixed offset of up to 0.8 of the mesocyclone's radius put
        /// in over the few minutes of the rope-out ran the tornado backwards at twice the storm's speed.) The drift is
        /// integrated from a velocity that ramps in from the rope-out over the rest of the tornado's life (OccRampMin at
        /// least) and eases off over the cycle's last 2 min; it levels off by about 1.1 of the mesocyclone's radius (a
        /// tanh, so the velocity fades rather than stops), and the wall cloud stays under the base.</summary>
        public static (double back, double left) Occlusion(StormPlan P, StormCycle cyc, int ci, double t)
        {
            var T = cyc.times; int n = T.Length;
            if (n < 5) return (0, 0);
            double ta = T[n - 5], tb = Math.Max(T[n - 3], ta + OccRampMin), te = T[n - 1];
            if (ci + 1 < P.cycles.Length) te = Math.Min(te, P.cycles[ci + 1].start);
            if (t <= ta || te <= ta) return (0, 0);
            double V = Math.Sqrt(P.motion.x * P.motion.x + P.motion.y * P.motion.y) * 0.06;   // km per storm minute
            // the integral of the drift's ramp, minus its taper at the end, from ta to t (midpoint rule)
            double t1 = Math.Min(t, te), s = 0; const int N = 24; double h = (t1 - ta) / N;
            for (int k = 0; k < N; k++)
            {
                double u = ta + (k + 0.5) * h;
                s += Ramp(u, ta, tb) * (1 - Ramp(u, te - 2, te));
            }
            s *= h * V;   // km the wall cloud would drift at the storm's full speed
            double D = OccMaxKm, d =D * Math.Tanh(s * Math.Sqrt(OccBack * OccBack + OccLeft * OccLeft) / D);
            double k1 = d / Math.Sqrt(OccBack * OccBack + OccLeft * OccLeft);
            return (-OccBack * k1, OccLeft * k1);
        }

        public static void Apply(StormPlan P, double t, double clock, StormFrame F)
        {
            if (P == null || P.env == null) return;
            NoiseOffset(P, F);
            if (P.kind != StormKind.Supercell) return;
            var a = F.param;
            var (meso, tower, dFfd, dHail) = Scales(P);
            double fx = P.fwd.x, fy = P.fwd.y, lx = P.lft.x, ly = P.lft.y;
            double ux = a[16 * 4], uy = a[16 * 4 + 1];

            // ---- the amounts that build up through a cycle, eased over a few storm minutes
            // (the wall cloud's radius weighted by its amount: before a cycle the design keeps a radius for no cloud)
            var sum = new double[eased.Length]; double wr = 0, ww = 0;
            for (int k = 0; k < filterOff.Length; k++)
            {
                double tk = Math.Max(t + filterOff[k] * FilterMin, 0);
                var Fk = k == 2 ? F : StormDesign.At(P, Math.Min(tk, P.duration), clock, scratch[k]);
                for (int j = 0; j < eased.Length; j++) sum[j] += filterW[k] * Fk.param[eased[j]];
                double wk = filterW[k] * (Fk.param[6 * 4 + 3] + 1e-4);
                wr += wk * Fk.param[6 * 4 + 2]; ww += wk;
            }
            for (int j = 0; j < eased.Length; j++) a[eased[j]] = (float)sum[j];
            a[6 * 4 + 2] = (float)(wr / ww);

            // ---- the tower and anvil on the storm's own rise time
            {
                var Ft = StormDesign.At(P, TowerTime(P, t), clock, scratch[5]);
                var b = Ft.param;
                a[2 * 4] = b[2 * 4];               // top
                a[3 * 4 + 2] = b[3 * 4 + 2];       // overshooting top
                a[4 * 4 + 2] = b[4 * 4 + 2]; a[4 * 4 + 3] = b[4 * 4 + 3];   // anvil length and growth
                a[5 * 4 + 3] = b[5 * 4 + 3];       // backshear
            }

            // ---- the tower: as wide as its mesocyclone, growing into it as the storm organises (slot 16 z)
            // (the web build's tower already widens through its life; it is only kept from being narrower than this)
            double grown = C.Clamp(a[16 * 4 + 2], 0, 1);
            double towerR = a[2 * 4 + 1];
            double R = Math.Max(towerR, tower * (0.6 + 0.4 * grown));
            double kR = R / Math.Max(towerR, 0.3);
            a[2 * 4 + 1] = (float)R;

            // ---- the cores, carried off by the wind aloft: the direction the design drops them, the distance the
            // environment gives, from the tower's own downshear flank while the storm is young (slot 4 w)
            double g = C.Clamp(a[4 * 4 + 3], 0, 1);
            void Place(int slot, double dist)
            {
                double dx = a[slot * 4] - ux, dy = a[slot * 4 + 1] - uy, m = Math.Sqrt(dx * dx + dy * dy);
                if (m < 0.1) return;
                // (never closer than the design and StormTilt put it: the structure only separates)
                double d = Math.Max(m, C.Lerp(0.9 * R, dist, 0.35 + 0.65 * g));
                a[slot * 4] = (float)(ux + dx / m * d); a[slot * 4 + 1] = (float)(uy + dy / m * d);
            }
            if (a[9 * 4 + 3] > 0) Place(9, dFfd);
            if (a[10 * 4 + 3] > 0) Place(10, dHail);
            // the hail core about as wide as the updraft it falls beside
            a[10 * 4 + 2] = (float)Math.Max(a[10 * 4 + 2], 0.55 * R);

            // ---- the cycle: the rear-flank surge, its horseshoe, the wall cloud and the tornado
            if (P.cycles == null || t < P.cycles[0].start) { a[32 * 4 + 3] = 1; return; }
            int ci = 0; for (int i = 0; i < P.cycles.Length; i++) if (t >= P.cycles[i].start) ci = i;
            var cyc = P.cycles[ci];
            double tau = StormCycles.CycleTime(cyc, t);
            bool multi = cyc.shape == TornadoShape.Multivortex;
            double big = double.IsNaN(cyc.bigness) ? (ci > 0 ? 1 : 0) : cyc.bigness;
            double slotGrowth = Ramp(tau, 8, 18);
            double torAmt = a[13 * 4 + 2], torTop = a[14 * 4 + 1], rope = a[14 * 4 + 3];

            // the wall cloud stays under the updraft's rear flank (rear and to the right of its centre, where the inflow
            // meets the surge), and falls back with the occlusion only from its tornado's rope-out on
            // (the web build's own place for it, At2(-1.0, -0.8): 1.3 km behind the updraft, before its fit to the tower
            // shoves it; user, 2026-11-08: the separation was meant for the forward flank only, not the RFD)
            var (ob, ol) = Occlusion(P, cyc, ci, t);
            double wf = WallBack + ob, wl = WallLeft + ol;
            double wcx = ux + wf * fx + wl * lx, wcy = uy + wf * fy + wl * ly;
            _ = rope; _ = tau; _ = multi; _ = big; _ = meso;

            // the surge: the web build's size (its horseshoe round the updraft's rear flank), only never under twice the
            // tornado's top, so a big tornado has the slot it needs
            double Rs = Math.Max(a[18 * 4 + 2], (1.9 * torTop * Ramp(torAmt, 0.02, 0.4) + 0.7) * (0.25 + 0.75 * slotGrowth));
            // its centre behind the wall cloud: the wall cloud is the surge's northern curl (60 degrees left of the motion)
            double dn = 60 * Math.PI / 180, ds = -170 * Math.PI / 180;
            double dnx = Math.Cos(dn) * fx + Math.Sin(dn) * lx, dny = Math.Cos(dn) * fy + Math.Sin(dn) * ly;
            double dsx = Math.Cos(ds) * fx + Math.Sin(ds) * lx, dsy = Math.Cos(ds) * fy + Math.Sin(ds) * ly;
            double rcx = wcx - 0.88 * Rs * dnx, rcy = wcy - 0.88 * Rs * dny;
            if (a[18 * 4 + 2] > 0)
            {
                a[18 * 4] = (float)rcx; a[18 * 4 + 1] = (float)rcy; a[18 * 4 + 2] = (float)Rs;
                a[19 * 4] = (float)(rcx + Rs * dsx); a[19 * 4 + 1] = (float)(rcy + Rs * dsy);
            }
            // the clear slot's own radius (slot 11 y) with the surge
            a[11 * 4 + 1] = (float)Math.Max(a[11 * 4 + 1], Rs + 0.8);

            // the wall cloud: as big as the design makes it for a tower this size, and round the tornado's top
            double wallAmt = a[6 * 4 + 3];
            double wallR = Math.Max(a[6 * 4 + 2], (1.3 * torTop + 0.35) * Ramp(torAmt, 0.02, 0.3));
            a[6 * 4 + 2] = (float)Math.Min(wallR, 0.45 * R + 0.6);
            // move the wall cloud and everything hung from it (the tornado's top and foot, a satellite)
            double ddx = wcx - a[6 * 4], ddy = wcy - a[6 * 4 + 1];
            a[6 * 4] = (float)wcx; a[6 * 4 + 1] = (float)wcy;
            if (torAmt > 0 || a[15 * 4] != 0 || a[15 * 4 + 1] != 0)
            {
                a[13 * 4] += (float)ddx; a[13 * 4 + 1] += (float)ddy;
                a[15 * 4] += (float)ddx; a[15 * 4 + 1] += (float)ddy;
            }
            if (a[28 * 4 + 2] > 0) { a[28 * 4] += (float)ddx; a[28 * 4 + 1] += (float)ddy; }
            _ = wallAmt;
            a[32 * 4 + 3] = 1;
        }
    }
}

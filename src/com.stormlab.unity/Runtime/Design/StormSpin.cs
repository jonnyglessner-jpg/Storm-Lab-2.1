// How far each turning feature has turned, added up frame by frame. The web build turns the mesocyclone and the detailed
// tornado by rate x clock (clock: wall seconds since the storm was made). That holds only while a rate stays fixed: the
// speed seen is rate + clock x (how fast the rate changes), so twenty minutes into a game a slowly ramping rate dominates.
// The mesocyclone's spin-up (16-30 storm min) then looked several times faster than its rate, and slowed right down when
// the ramp ended; a widening wedge (its rate is wind / radius, falling about ninefold) turned fast and backwards; suction
// vortices orbited too fast while the tornado formed. Here each angle grows by rate x frame step, so a change of rate is
// a change of speed and nothing else; the shaders read the angles (slots 29-31, S_spin, S_spin2 and S_spin3).
//
// Real rates (user, 2026-10-27: "everything must match real world speeds"): each feature turns at a real tangential wind
// over its own radius, v / r, so its edge moves at that wind in real time. The web build's rates were picked by eye and
// turned the mesocyclone's 3 km edge at several hundred m/s (a 20-60x time-lapse). Sources in ../meteorology: Stull
// ch. 14-15 (mesocyclone 3-10 km across; tornado winds 18 to >140 m/s, Rankine core and outer flow), METR4403 NOTES
// (suction vortices, satellite and anticyclonic tornadoes). Website: the web build's own rates, for the comparison.
// Not part of the web build's design: StormDesign stays as the website's, and parity does not see this.
using System;

namespace StormLab
{
    /// <summary>how fast the clouds turn: real-world winds, or the web build's rates (for the website comparison)</summary>
    public enum SpinRates { Real, Website }

    public sealed class StormSpin
    {
        /// <summary>first of the angle slots (S_spin), and how many</summary>
        public const int Slot0 = StormTornadoes.NSLOT, Slots = 3;
        /// <summary>the whole parameter block with the angle slots (must match StormCommon.hlsl)</summary>
        public const int NSLOT = Slot0 + Slots;

        // ---- real tangential winds, m/s
        /// <summary>mesocyclone at the updraft's edge, per rad/s of the web build's rate: its 0.034-0.092 at full strength
        /// gives 14-37 m/s, the rotational velocity of weak to strong mesocyclones</summary>
        public const double MesoWind = 400;
        /// <summary>wall cloud (the low-level mesocyclone) at its rim, per rad/s of the web build's rate: 0.10-0.21 gives
        /// 12-26 m/s</summary>
        public const double WallWind = 120;
        /// <summary>the rear-flank surge wrapping cyclonically round the low-level mesocyclone, at the clear slot's rim</summary>
        public const double RfdWind = 12;
        /// <summary>the anti-mesocyclone's lowering at its rim (weaker than the cyclonic side)</summary>
        public const double AntiMesoWind = 10;
        /// <summary>a suction vortex about its own axis, over the parent's flow</summary>
        public const double SubVortexWind = 30;
        /// <summary>sub-vortices travel round the parent at about half its wind (Agee et al. 1977)</summary>
        public const double OrbitFrac = 0.5;
        /// <summary>a satellite tornado (mostly EF0-EF2) and an anticyclonic tornado (mostly EF0-EF1) at their visible edge</summary>
        public const double SatelliteWind = 35, AntiTornadoWind = 30;
        /// <summary>the anticyclonic tornado's visible radius low down, km (StormMarch.compute antiTornadoField)</summary>
        const double AntiTornadoR = 0.035;
        /// <summary>no texture turns faster than this, rad/s (a 20 m rope at 80 m/s)</summary>
        const double MaxRate = 4.0;

        const int N = 11;
        const double TwoPi = 2 * Math.PI;
        // mesocyclone, funnel skin, central core, suction-vortex wisps, suction-vortex orbit, dust whirl, wall cloud,
        // RFD lip, anti-mesocyclone (clockwise), satellite tornado, anticyclonic tornado (clockwise)
        readonly double[] angle = new double[N];
        readonly double[] rate = new double[N];
        double lastClock = double.NaN, lastT = double.NaN;

        /// <summary>this frame's rates, rad/s, counter-clockwise positive, in the order above (for checks)</summary>
        public double Rate(int i) => rate[i];

        /// <summary>
        /// Advance the angles to wall clock `clock` (s) at storm time tMin and write them into the frame. Call after
        /// StormDesign.At, StormTornadoes.Apply and StormLife.Apply, so the rates are this frame's (a dying storm's spin
        /// winds down with it).
        /// A call that does not move the clock forward (a still posed at a chosen clock, or a clock set back) puts each
        /// angle at rate x clock, as the web build would show that moment; calling again at the same moment changes nothing.
        /// </summary>
        public void Advance(StormFrame F, double tMin, double clock, SpinRates rates = SpinRates.Real)
        {
            var a = F.param;
            if (rates == SpinRates.Website) WebRates(a); else RealRates(a);
            double dt = clock - lastClock;
            bool same = dt == 0 && tMin == lastT;
            if (!same)
            {
                if (double.IsNaN(dt) || dt <= 0) for (int i = 0; i < N; i++) angle[i] = rate[i] * clock % TwoPi;
                else for (int i = 0; i < N; i++) angle[i] = (angle[i] + rate[i] * dt) % TwoPi;
            }
            lastClock = clock; lastT = tMin;
            int s = Slot0 * 4;
            for (int i = 0; i < N; i++) a[s + i] = (float)angle[i];
            a[s + N] = 0;
        }

        // the web build's rates (its StormMarch / StormCommon constants), unchanged
        void WebRates(float[] a)
        {
            double spin = C.Clamp(a[StormTornadoes.Slot0 * 4], 0.02, 2.0);
            rate[0] = a[3 * 4 + 3];
            rate[1] = spin;
            rate[2] = Math.Min(spin * 1.4, 2.5);
            rate[3] = Math.Min(3.0 * spin, 3.0);
            rate[4] = 0.55 * spin;
            rate[5] = 0.65 * spin;
            rate[6] = a[7 * 4 + 1];
            rate[7] = 0.06;
            rate[8] = -0.16;
            rate[9] = 1.2;
            rate[10] = -0.8;
        }

        // wind / radius for each feature, radii in km from the frame
        void RealRates(float[] a)
        {
            // mesocyclone: solid-body inside the updraft (S_tilt.w the web rate, S_tower.y the radius)
            rate[0] = MesoWind * a[3 * 4 + 3] / (Math.Max(a[2 * 4 + 1], 0.5) * 1000);
            // tornado: the funnel's skin turns at the wind at its edge, v / r (StormTornadoes, S_tor6.x)
            double skin = C.Clamp(a[StormTornadoes.Slot0 * 4], 0.02, MaxRate);
            double vmax = a[23 * 4 + 1], rc = Math.Max(a[23 * 4 + 2], 0.01);
            rate[1] = skin;
            // the central core: solid-body inside the radius of maximum wind (Rankine), never slower than the skin
            rate[2] = C.Clamp(vmax / (rc * 1000), skin, MaxRate);
            // a suction vortex about its own axis (S_tor5.y its radius)
            rate[3] = Math.Min(SubVortexWind / (Math.Max(a[22 * 4 + 1], 0.02) * 1000), MaxRate);
            // the sub-vortices' orbit: about half the parent's wind at the funnel's edge, where they orbit
            rate[4] = OrbitFrac * skin;
            // the dust whirl, out beyond the core where the wind falls as 1 / r (Rankine): v(r) / r = vmax rc / r^2
            double dr = Math.Max(a[14 * 4] * a[23 * 4 + 3] + 0.06, rc);
            rate[5] = Math.Min(vmax * rc / (dr * dr * 1000), skin);
            // wall cloud at its rim (S_wall2.y the web rate, S_wall.z the radius)
            rate[6] = WallWind * a[7 * 4 + 1] / (Math.Max(a[6 * 4 + 2], 0.5) * 1000);
            // the horseshoe's lip round the clear slot (S_rfdb.z its radius)
            rate[7] = RfdWind / (Math.Max(a[18 * 4 + 2], 1.0) * 1000);
            // the anti-mesocyclone's lowering, clockwise (radius as StormCommon.hlsl draws it from S_anti.z)
            rate[8] = -AntiMesoWind / ((0.45 + 0.4 * a[19 * 4 + 2]) * 1000);
            // satellite tornado (S_tor11.w its radius)
            rate[9] = Math.Min(SatelliteWind / (Math.Max(a[28 * 4 + 3], 0.01) * 1000), MaxRate);
            // anticyclonic tornado, clockwise
            rate[10] = -AntiTornadoWind / (AntiTornadoR * 1000);
        }

        /// <summary>Start again from rate x clock at the next Advance (a new plan, or a seek).</summary>
        public void Reset() { lastClock = double.NaN; lastT = double.NaN; }
    }
}

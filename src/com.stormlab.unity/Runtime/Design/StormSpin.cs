// How far each turning feature has turned, added up frame by frame. The web build turns the mesocyclone and the detailed
// tornado by rate x clock (clock: wall seconds since the storm was made). That holds only while a rate stays fixed: the
// speed seen is rate + clock x (how fast the rate changes), so twenty minutes into a game a slowly ramping rate dominates.
// The mesocyclone's spin-up (16-30 storm min) then looked several times faster than its rate, and slowed right down when
// the ramp ended; a widening wedge (its rate is wind / radius, falling about ninefold) turned fast and backwards; suction
// vortices orbited too fast while the tornado formed. Here each angle grows by rate x frame step, so a change of rate is
// a change of speed and nothing else; the shaders read the angles (slots 29-31, S_spin, S_spin2 and S_spin3). The last
// value (S_spin3.w) is a distance, not an angle: how far the inflow has carried the beaver tail's air toward the updraft,
// km, added up the same way (user, 2026-10-28: the beaver tail "needs to look as if it is flowing into the storm").
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
        public const int Slot0 = StormTornadoes.NSLOT, Slots = 4;
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
        /// <summary>the storm-relative inflow along the beaver tail, m/s (low-level inflow into a supercell is about 10-20
        /// m/s; Stull ch. 14)</summary>
        public const double InflowWind = 15;
        /// <summary>no texture turns faster than this, rad/s (a 20 m rope at 80 m/s)</summary>
        const double MaxRate = 4.0;
        /// <summary>where the funnel meets the wall cloud (the junction) the cloud's surface moves at this share of the
        /// funnel's edge wind: close to the tornado's speed but under it (user, 2026-10-31)</summary>
        public const double WallJunctionShare = 0.85;
        /// <summary>the wall cloud's inner ring turns this share of the way from the rim's rate to the junction's (must
        /// match StormCommon.hlsl nearField, where the ring's frame is pure at the radius whose rate this is)</summary>
        public const double WallRingShare = 0.05;

        const int N = 13;
        const double TwoPi = 2 * Math.PI;
        // mesocyclone, funnel skin, central core, suction-vortex wisps, suction-vortex orbit, dust whirl, wall cloud,
        // RFD lip, anti-mesocyclone (clockwise), satellite tornado, anticyclonic tornado (clockwise), the wall cloud's
        // inner ring (a little faster than the rim, WallRingShare of the way to the junction's rate: S_spin4.x), the wall
        // cloud at its junction with the funnel (S_spin4.y)
        readonly double[] angle = new double[N];
        readonly double[] rate = new double[N];
        double flow, flowRate, junctionR;
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
            // (the web build's beaver tail does not flow: 0 keeps its look for the comparison)
            flowRate = rates == SpinRates.Website ? 0 : InflowWind / 1000;
            double dt = clock - lastClock;
            bool same = dt == 0 && tMin == lastT;
            if (!same)
            {
                if (double.IsNaN(dt) || dt <= 0) { for (int i = 0; i < N; i++) angle[i] = rate[i] * clock % TwoPi; flow = flowRate * clock; }
                else { for (int i = 0; i < N; i++) angle[i] = (angle[i] + rate[i] * dt) % TwoPi; flow += flowRate * dt; }
            }
            lastClock = clock; lastT = tMin;
            // slots 29-31: the first eleven angles, then the flow (S_spin3.w); slot 32: the wall cloud's inner ring and
            // junction angles, and the junction's radius (km) the shader tapers from
            int s = Slot0 * 4;
            for (int i = 0; i < 11; i++) a[s + i] = (float)angle[i];
            a[s + 11] = (float)flow;
            a[s + 12] = (float)angle[11];
            a[s + 13] = (float)angle[12];
            a[s + 14] = (float)junctionR;
        }

        /// <summary>where the funnel meets the wall cloud: the cloud-base radius plus the collar's flare (as StormMarch.compute
        /// draws it), at least 0.1 km and at most half the wall cloud's radius; the wall cloud turns at the junction's rate
        /// inside it (StormCommon.hlsl nearField)</summary>
        static double JunctionRadius(float[] a)
        {
            double rw = Math.Max(a[6 * 4 + 2], 0.5), rb = a[14 * 4 + 1];
            double flare = Math.Min(0.25 * rw + 0.3 * rb, 1.6 * rb + 0.1) * (a[21 * 4 + 3] > 0.5 ? 0.35 : 1.0);
            return C.Clamp(rb + flare, 0.1, 0.5 * rw);
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
            rate[12] = rate[1];
            rate[11] = rate[6] + WallRingShare * (rate[12] - rate[6]);
            junctionR = JunctionRadius(a);
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
            // the dust whirl, at the debris column's rim out beyond the core. The wind there falls off slower than Rankine's
            // 1 / r: mobile-radar profiles of real tornadoes (Wurman & Gill 2000, Wurman & Alexander 2005) decay about as
            // r^-0.6 outside the radius of maximum wind, so v(r) / r = vmax (rc / r)^0.6 / r. At 1 / r a 350 m cone's rim
            // (0.4 km) turned at 12 m/s, slower than its dust rose (user, 2026-10-31: lots of rising motion, not enough
            // rotation); at r^-0.6 it turns at about 22 m/s.
            double dr = Math.Max(a[14 * 4] * a[23 * 4 + 3] + 0.06, rc);
            rate[5] = Math.Min(vmax * Math.Pow(rc / dr, 0.6) / (dr * 1000), skin);
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
            // The wall cloud at its junction with the funnel (user, 2026-10-31: where the funnel meets the wall cloud the
            // cloud turns almost at the tornado's speed but under it, tapering off to the rim's). The tornado's speed as
            // seen is the funnel's surface at its edge, the edge wind skin x ground radius (StormTornadoes); the junction
            // sits at the cloud-base radius plus the collar's flare, so its surface moves at WallJunctionShare of that
            // wind, v / r here. Turning the junction at the skin's own rate moved its wider circle several times faster
            // than the funnel's surface and the wall cloud read as outrunning the tornado (user, 2026-10-31).
            junctionR = JunctionRadius(a);
            double edgeWind = skin * Math.Max(a[14 * 4], 0.01) * 1000;
            rate[12] = Math.Min(WallJunctionShare * edgeWind / (junctionR * 1000), skin);
            // The inner ring: the excess over the rim's rate dies away steeply from the junction (as 1 / r^3 in
            // StormCommon.hlsl nearField), so the ring between is only a little faster than the rim. A ring at the
            // geometric mean of the rates (76 s a turn against the rim's 8 min, for a cone) turned a 1.2 km radius at
            // 100 m/s and read as a band racing round half way out (user's capture, 2026-10-31). The shader crossfades
            // the texture of three rigid frames (the junction's, this ring, the rim).
            rate[11] = rate[6] + WallRingShare * (rate[12] - rate[6]);
        }

        /// <summary>Start again from rate x clock at the next Advance (a new plan, or a seek).</summary>
        public void Reset() { lastClock = double.NaN; lastT = double.NaN; }
    }
}

// How far each turning feature has turned, added up frame by frame. The web build turns the mesocyclone and the detailed
// tornado by rate x clock (clock: wall seconds since the storm was made). That holds only while a rate stays fixed: the
// speed seen is rate + clock x (how fast the rate changes), so twenty minutes into a game a slowly ramping rate dominates.
// The mesocyclone's spin-up (16-30 storm min) then looked several times faster than its rate, and slowed right down when
// the ramp ended; a widening wedge (its rate is wind / radius, falling about ninefold) turned fast and backwards; suction
// vortices orbited too fast while the tornado formed. Here each angle grows by rate x frame step, so a change of rate is
// a change of speed and nothing else; the shaders read the angles (slots 29-30, S_spin and S_spin2).
// Not part of the web build's design: StormDesign stays as the website's, and parity does not see this.
using System;

namespace StormLab
{
    public sealed class StormSpin
    {
        /// <summary>first of the angle slots (S_spin), and how many</summary>
        public const int Slot0 = StormTornadoes.NSLOT, Slots = 2;
        /// <summary>the whole parameter block with the angle slots (must match StormCommon.hlsl)</summary>
        public const int NSLOT = Slot0 + Slots;

        const int N = 6;
        const double TwoPi = 2 * Math.PI;
        // mesocyclone, funnel skin, central core, suction-vortex wisps, suction-vortex orbit, dust whirl
        readonly double[] angle = new double[N];
        readonly double[] rate = new double[N];
        double lastClock = double.NaN, lastT = double.NaN;

        /// <summary>
        /// Advance the angles to wall clock `clock` (s) at storm time tMin and write them into the frame. Call after
        /// StormDesign.At, StormTornadoes.Apply and StormLife.Apply, so the rates are this frame's (a dying storm's spin
        /// winds down with it). mesoScale: the mesocyclone's speed over the web build's rate.
        /// A call that does not move the clock forward (a still posed at a chosen clock, or a clock set back) puts each
        /// angle at rate x clock, as the web build would show that moment; calling again at the same moment changes nothing.
        /// </summary>
        public void Advance(StormFrame F, double tMin, double clock, double mesoScale)
        {
            var a = F.param;
            double spin = C.Clamp(a[StormTornadoes.Slot0 * 4], 0.02, 2.0);
            rate[0] = a[3 * 4 + 3] * mesoScale;
            rate[1] = spin;
            rate[2] = Math.Min(spin * 1.4, 2.5);
            rate[3] = Math.Min(3.0 * spin, 3.0);
            rate[4] = 0.55 * spin;
            rate[5] = 0.65 * spin;
            double dt = clock - lastClock;
            bool same = dt == 0 && tMin == lastT;
            if (!same)
            {
                if (double.IsNaN(dt) || dt <= 0) for (int i = 0; i < N; i++) angle[i] = rate[i] * clock % TwoPi;
                else for (int i = 0; i < N; i++) angle[i] = (angle[i] + rate[i] * dt) % TwoPi;
            }
            lastClock = clock; lastT = tMin;
            int s = Slot0 * 4;
            a[s] = (float)angle[0]; a[s + 1] = (float)angle[1]; a[s + 2] = (float)angle[2]; a[s + 3] = (float)angle[3];
            a[s + 4] = (float)angle[4]; a[s + 5] = (float)angle[5]; a[s + 6] = 0; a[s + 7] = 0;
        }

        /// <summary>Start again from rate x clock at the next Advance (a new plan, or a seek).</summary>
        public void Reset() { lastClock = double.NaN; lastT = double.NaN; }
    }
}

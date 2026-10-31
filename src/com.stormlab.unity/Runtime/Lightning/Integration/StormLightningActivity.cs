// Storm-driven lightning activity: flash rate and flash-type split from the designed storm's plan and its cycles. The
// rules are StormLab 2.0's (DesignedLightning, LightningPolarity), timed to this storm's lifecycle (StormDesign).
// Presentation estimates, not an electrification solver.
using System;
using UnityEngine;

namespace StormLab.Lightning
{

    /// <summary>Look controls for storm-driven lightning. Changing any of them changes the flash history.</summary>
    [Serializable]
    public sealed class StormLightningTuning
    {
        [Tooltip("Multiplies every storm-driven flash rate.")]
        [Min(0)] public float rateScale = 1;
        [Tooltip("Total flashes per minute of a mature supercell with a 35 m/s, 3 km updraft.")]
        [Min(0)] public float referencePerMinute = 35;
        [Tooltip("Rate of a pulse storm relative to a supercell with the same updraft.")]
        [Range(0, 1)] public float pulseFactor = .4f;
        [Tooltip("Rate increase as each wall cloud develops, as a fraction of the rate, for a cycle of strength 1.")]
        [Range(0, 3)] public float jumpWallCloud = .4f;
        [Tooltip("Further increase in the minutes just before touchdown (the lightning jump), peaking about half a minute " +
            "before the tornado forms, for a cycle of strength 1.")]
        [Range(0, 3)] public float jumpPreTornado = .9f;
        [Tooltip("How far the rate falls below its baseline late in a tornado's life (the lull as it ropes out).")]
        [Range(0, 1)] public float tornadoLull = .2f;
        [Tooltip("Share of in-cloud flashes that become crawlers at the peak of a cycle with a violent tornado (wedge, " +
            "multiple-vortex, or the teaching sequence's second tornado). None in weaker cycles.")]
        [Range(0, .5f)] public float crawlerShare = .2f;
        [Tooltip("Share of in-cloud flashes that run out under and through the anvil as crawlers once it has spread (from " +
            "about 20 min), more as the storm ages. The violent-cycle share (crawlerShare) applies where it is higher.")]
        [Range(0, .6f)] public float anvilCrawlerShare = .3f;
        [Tooltip("Strong updrafts on the Plains: extra positive share of ground flashes (inverted-polarity storms).")]
        [Range(0, 1)] public float invertedPolarityBoost = .2f;
    }

    /// <summary>Flash-type split: about 20 % of flashes reach the ground, under 10-12 % on the High Plains, about 9 % of
    /// those positive (StormLab 2.0, docs/meteorology/cloud-microphysics.md there).</summary>
    public static class LightningPolarity
    {
        public static void Split(ClimoRegion region, double w, double rain, double firstRain, double decay,
            StormLightningTuning k, out float ground, out float positive)
        {
            double baseGround = region == ClimoRegion.Plains ? .12 : region == ClimoRegion.Dixie ? .22 : .2;
            // Strong updrafts lift the main negative charge, so more flashes stay in the cloud.
            double lift = C.Clamp(1.25 - w / 80, .6, 1.1);
            ground = (float)C.Clamp(baseGround * (.3 + .7 * rain) * (1 + .5 * firstRain) * lift * (1 + .6 * decay), 0, .6);
            double plains = region == ClimoRegion.Plains ? 1 : region == ClimoRegion.Midwest ? .5 : .2;
            positive = (float)C.Clamp(.09 + k.invertedPolarityBoost * plains * Ramp(w, 35, 55) * (1 - decay) + .25 * decay, 0, .6);
        }
        public static double Ramp(double t, double a, double b) { double x = C.Clamp((t - a) / (b - a), 0, 1); return x * x * (3 - 2 * x); }
    }

    /// <summary>One circulation as lightning sees it, in storm minutes.</summary>
    public struct LightningCycle
    {
        public double wall, form, end, life, strength;
        public bool tornado, violent;
    }

    /// <summary>Lightning from the designed storm's life story: updraft size and speed, age and cycles. Depends only on the
    /// plan and the storm time, so it replays. Each cycle heightens the activity: the rate climbs as the wall cloud
    /// develops, climbs further in the minutes before touchdown (peaking just before the tornado forms), then tapers off
    /// as the tornado runs its course and dips below the baseline as it ropes out. Stronger cycles surge more; the
    /// heightened moments carry flashes further from the updraft (reach), and violent tornadoes add crawlers. Once the
    /// anvil has spread, a share of the in-cloud flashes are anvil crawlers whatever the cycle (anvilCrawlerShare).</summary>
    public static class StormLightningActivity
    {
        /// <summary>The plan's circulations: its seeded cycles, or the teaching sequence's two (30 and 68 min).</summary>
        public static LightningCycle[] Cycles(StormPlan P)
        {
            if (P.kind != StormKind.Supercell) return Array.Empty<LightningCycle>();
            if (P.cycles == null)
            {
                // StormDesign's teaching sequence: tornado 18-31 min into the first cycle; the second forms at 15, a
                // violent wedge to 45
                double s1 = P.torType > 0.7 ? 1.3 : P.torType > 0.3 ? 1.0 : 0.7;
                return new[]
                {
                    new LightningCycle { wall = 30, form = 48, end = 66, life = 13, tornado = P.tornadic, strength = P.tornadic ? s1 : .4 },
                    new LightningCycle { wall = 68, form = 83, end = 120, life = 30, tornado = P.tornadic, violent = P.tornadic, strength = P.tornadic ? 1.65 : .4 },
                };
            }
            var o = new LightningCycle[P.cycles.Length];
            for (int i = 0; i < o.Length; i++)
            {
                var c = P.cycles[i];
                // times: start, +7, form, mature, (multivortex mid), rope, thin, gone, fade, end
                double form = c.times[2], gone = c.times[c.times.Length - 3];
                double s = c.shape == TornadoShape.Rope ? .55 : c.shape == TornadoShape.Cone ? .85 : c.shape == TornadoShape.Stovepipe ? 1.05
                    : c.shape == TornadoShape.Wedge ? 1.5 : 1.4;
                s *= C.Clamp(Math.Sqrt(c.radius / 0.25), 0.8, 1.2);
                o[i] = new LightningCycle
                {
                    wall = c.start, form = form, end = c.end, life = Math.Max(gone - form, 1), tornado = c.tornadic,
                    violent = c.tornadic && (c.shape == TornadoShape.Wedge || c.shape == TornadoShape.Multivortex),
                    strength = c.tornadic ? s : .4
                };
            }
            return o;
        }

        /// <summary>The activity at storm time tMin (minutes). Rates are flashes per storm minute.</summary>
        public static LightningActivity At(StormPlan P, LightningCycle[] cycles, double tMin, StormLightningTuning k, ClimoRegion region)
        {
            if (P == null || tMin < 0 || tMin > P.duration + 5 || (P.kind != StormKind.Supercell && P.kind != StormKind.Pulse))
                return LightningActivity.None;
            // Flash rate grows with the updraft's width and speed and with the depth of cloud colder than freezing, where
            // graupel and ice collide; compressed so a big Plains updraft stays well under the ceiling.
            double size = (P.R / 3) * Math.Pow(C.Clamp((P.w - 8) / 27, 0, 3), .75);
            double coldDepth = LightningPolarity.Ramp(P.el - P.fzlKm, 2, 6);
            double growth, live, rain, firstRain, jump = 0, reach = 0, crawl = 0, anvilCrawl, lull = 0, strength = 1;
            double t = tMin;
            if (P.kind == StormKind.Supercell)
            {
                // first flashes as the tower passes the charging zone (tower to 14 min, anvil and rain 14-24)
                growth = LightningPolarity.Ramp(t, 8, 22);
                // the storm lingers to the end of its plan, with its activity easing in the last minutes
                live = 1 - 0.6 * LightningPolarity.Ramp(t, P.duration - 12, P.duration + 4);
                rain = LightningPolarity.Ramp(t, 14, 24);
                firstRain = LightningPolarity.Ramp(t, 16, 22) * (1 - LightningPolarity.Ramp(t, 28, 40));
                // anvil crawlers: flashes run out under the anvil once it has spread (14-24 min), more as the storm ages
                // and the anvil's stratiform region grows
                anvilCrawl = LightningPolarity.Ramp(t, 20, 36) * (.65 + .35 * LightningPolarity.Ramp(t, 55, 95));
                foreach (var c in cycles)
                {
                    double s = c.strength;
                    double end = Math.Max(c.end, c.tornado ? c.form + c.life : 0);
                    if (t >= c.wall - 5 && t <= end) strength = s;
                    // wall cloud: the rate climbs as it forms and lowers (7 min, StormDesign's wallAmt)
                    double wall = k.jumpWallCloud * s * LightningPolarity.Ramp(t, c.wall - 1, c.wall + 7);
                    double pre = 0, taper, peak;
                    if (c.tornado)
                    {
                        double f = c.form, L = c.life;
                        // the jump: 5 min before touchdown to half a minute before
                        peak = LightningPolarity.Ramp(t, f - 5, f - .5);
                        pre = k.jumpPreTornado * s * peak;
                        // tapers off through the tornado's life, gone by the time it lifts
                        taper = 1 - LightningPolarity.Ramp(t, f, f + .8 * L);
                        lull = Math.Max(lull, LightningPolarity.Ramp(t, f + .4 * L, f + .85 * L) * (1 - LightningPolarity.Ramp(t, f + L, f + L + 4)));
                        if (c.violent) crawl = Math.Max(crawl, peak * (1 - LightningPolarity.Ramp(t, f + .5 * L, f + L)));
                    }
                    else taper = 1 - LightningPolarity.Ramp(t, c.wall + 24, c.end);
                    double cycleJump = (wall + pre) * taper;
                    jump = Math.Max(jump, cycleJump);
                    reach = Math.Max(reach, C.Clamp(cycleJump / 1.3, 0, 1));
                }
            }
            else
            {
                // pulse storm (StormDesign): towering cumulus to 13 min, mature 13-22, heavy rain 22-34, collapsing 34-50
                growth = LightningPolarity.Ramp(t, 9, 16);
                live = 1 - LightningPolarity.Ramp(t, 26, 45);
                rain = LightningPolarity.Ramp(t, 15, 24);
                firstRain = LightningPolarity.Ramp(t, 18, 23) * (1 - LightningPolarity.Ramp(t, 30, 40));
                anvilCrawl = .5 * LightningPolarity.Ramp(t, 20, 30);
            }
            double kind = P.kind == StormKind.Pulse ? k.pulseFactor : 1;
            double vigour = .75 + .25 * strength;
            double rate = k.rateScale * k.referencePerMinute * kind * size * coldDepth * growth * live * vigour *
                (1 + jump / (1 + .25 * jump)) * (1 - k.tornadoLull * lull);
            LightningPolarity.Split(region, P.w * live, rain, firstRain, 1 - live, k, out float ground, out float positive);
            ground = Math.Min(.6f, ground * (1 + .3f * (float)reach));
            return new LightningActivity((float)Math.Max(0, rate), ground, positive, (float)(reach * live),
                (float)(Math.Max(k.crawlerShare * crawl, k.anvilCrawlerShare * anvilCrawl) * live));
        }
    }
}

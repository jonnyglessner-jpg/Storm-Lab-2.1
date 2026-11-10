// C# port of StormLab's presets (src/presets.ts, src/units.ts), and the game's setups taken from them.
// StormLab: https://github.com/jonnyglessner-jpg/StormLab (MIT).
//
// Website: the web build's seven presets, unchanged (the parity check and the website comparison use these).
// All: the setups the game offers (user's choice, 2026-10-25): plains, LP (the web build's "hail" environment, renamed),
// Dixie and Ohio. The web build's own LP, the capped bust and the summer pulse storm are left out. A key "web:<key>"
// (StormRenderer.preset) picks a website preset by its own key, so "web:lp" is still the web build's LP.
using System;

namespace StormLab
{
    [Serializable]
    public class StormPreset
    {
        public string key, name, blurb, watch;
        public double T0F, TdF, moistFt, lapse, cap, llj, shear, upper;   // winds in knots
        /// <summary>(game) the hodograph's low-level curvature (EnvInput.curve); 0 for every web build preset</summary>
        public double curve;

        public EnvInput ToEnv() => new EnvInput
        {
            T0F = T0F, TdF = Math.Min(TdF, T0F), lapse = lapse, cap = cap, moist = moistFt * C.FT,
            llj = llj * C.KT, shear = shear * C.KT, upper = upper * C.KT, curve = curve
        };

        /// <summary>the web build's presets (src/presets.ts), in its order</summary>
        public static readonly StormPreset[] Website =
        {
            new StormPreset { key = "plains", name = "Plains tornado outbreak", T0F = 84, TdF = 68, moistFt = 4000, lapse = 7.8, cap = 1.0, llj = 44, shear = 55, upper = 80,
                blurb = "Deep Gulf moisture, a strong low-level jet and south-west flow aloft.",
                watch = "A classic supercell: a rock-hard updraft, a hook on radar and a tornado-producing wall cloud. Storms move north-east at 30-35 mph." },
            new StormPreset { key = "hail", name = "Big hail day", T0F = 92, TdF = 66, moistFt = 2000, lapse = 8.8, cap = 1.0, llj = 22, shear = 55, upper = 70,
                blurb = "Steep mid-level lapse rates and big instability, but weak low-level winds.",
                watch = "Huge updrafts and large hail with a high, dry cloud base. Plenty of rotation aloft, little near the ground, so tornadoes are unlikely." },
            new StormPreset { key = "dixie", name = "Dixie Alley HP", T0F = 75, TdF = 68, moistFt = 4000, lapse = 6.0, cap = 0, llj = 50, shear = 60, upper = 58,
                blurb = "Low clouds, modest instability and very strong wind shear.",
                watch = "A heavy-precipitation supercell. Rain wraps around the mesocyclone, so tornadoes are hidden in rain and hard to see coming." },
            new StormPreset { key = "ohio", name = "Ohio Valley spring", T0F = 82, TdF = 66, moistFt = 3000, lapse = 6.8, cap = 0.5, llj = 34, shear = 50, upper = 75,
                blurb = "Moderate instability with strong shear ahead of a spring system.",
                watch = "Supercells that rotate well but run on less fuel than out west. Watch whether the low-level rotation is enough for a tornado." },
            new StormPreset { key = "lp", name = "High Plains LP", T0F = 90, TdF = 57, moistFt = 0, lapse = 8.5, cap = 0.5, llj = 22, shear = 48, upper = 95,
                blurb = "Dry air, a high cloud base and fierce winds aloft.",
                watch = "A low-precipitation supercell: a sculpted, corkscrew updraft with little rain, the kind photographers chase. Large hail, few tornadoes." },
            new StormPreset { key = "capped", name = "Cap holds (bust)", T0F = 90, TdF = 68, moistFt = 0, lapse = 8.0, cap = 4.0, llj = 36, shear = 50, upper = 80,
                blurb = "Every ingredient for big storms, under a strong inversion.",
                watch = "Huge CAPE and shear, and nothing happens. The warm layer aloft stops surface air from rising: the classic bust day." },
            new StormPreset { key = "pulse", name = "Summer pulse storm", T0F = 90, TdF = 72, moistFt = 1500, lapse = 6.0, cap = 0, llj = 8, shear = 10, upper = 24,
                blurb = "Hot, humid and almost no wind.",
                watch = "Without shear the storm rains into its own updraft: it towers up, dumps heavy rain and small hail, and collapses within the hour." },
        };

        /// <summary>the game's setups</summary>
        public static readonly StormPreset[] All =
        {
            Website[0],
            Renamed(Website[1], "lp", "High Plains LP"),
            Website[2],
            Website[3],
        };

        /// <summary>a game setup or, with "web:" in front, a website preset by its key; null when there is none. A game
        /// key with a day after it ("plains~1234", DayKey) is that setup's environment on that day (Day); one with a
        /// legendary event after it ("plains@moore-2013", LegendaryEvents.EnvKey) is that event's own sounding.</summary>
        public static StormPreset Find(string key)
        {
            if (key == null) return null;
            if (key.IndexOf(LegendaryEvents.EnvSep) > 0) return LegendaryEvents.OfEnvKey(key)?.environment;
            int tilde = key.IndexOf(DaySep);
            if (tilde > 0 && int.TryParse(key.Substring(tilde + 1), out int day))
            {
                var b = Find(key.Substring(0, tilde));
                return b == null || key.StartsWith(WebPrefix, StringComparison.Ordinal) ? b : Day(b, day);
            }
            bool web = key.StartsWith(WebPrefix, StringComparison.Ordinal);
            string k = web ? key.Substring(WebPrefix.Length) : key;
            return Array.Find(web ? Website : All, p => p.key == k);
        }
        public const string WebPrefix = "web:";
        public const char DaySep = '~';
        /// <summary>the setup's key without its day or its legendary event ("plains~1234", "plains@moore-2013" -> "plains")</summary>
        public static string BaseKey(string key) { int i = key?.IndexOfAny(new[] { DaySep, LegendaryEvents.EnvSep }) ?? -1; return i > 0 ? key.Substring(0, i) : key; }
        public static string DayKey(string baseKey, int day) => BaseKey(baseKey) + DaySep + day;

        /// <summary>
        /// (game; user, 2026-11-07: "a variety of wind shear profiles, CAPE profiles, and moisture profiles (variety of
        /// LCLs)") the setup's environment on one day: the same kind of day, its numbers drawn about the setup's (its own
        /// stream, StormRng 520). Surface temperature and dew point set the LCL (125 m a degree C of spread, Stull) and with
        /// the mid-level lapse rate and the moist layer's depth the CAPE and its shape (tall and thin or short and fat); the
        /// low-level jet and the hodograph's curvature the low-level shear and SRH; the deep shear the organisation and the
        /// updraft's tilt; the wind aloft the anvil-level storm-relative wind, which decides how far the precipitation is
        /// carried from the updraft (Rasmussen and Straka 1998: under 18 m/s HP, over 28 m/s LP; METR 4403 notes). The
        /// draws are held where the setup keeps making supercells (deep shear at least 36 kt). Key: DayKey.
        /// </summary>
        public static StormPreset Day(StormPreset b, int day)
        {
            var r = new StormRng(day, 520);
            double spread = Math.Max(b.T0F - b.TdF, 2);
            double t0 = b.T0F + r.Range(-4, 4);
            // the spread (LCL) varies by up to a third either way, more for a moist setup's small spread
            double sp = C.Clamp(spread * (1 + r.Range(-0.35, 0.35)) + r.Range(-1.5, 1.5), 3, 40);
            var d = new StormPreset
            {
                key = DayKey(b.key, day), name = b.name, blurb = b.blurb, watch = b.watch,
                T0F = t0, TdF = t0 - sp,
                moistFt = Math.Max(0, b.moistFt + r.Range(-1200, 1200)),
                lapse = C.Clamp(b.lapse + r.Range(-0.7, 0.7), 5.6, 9.4),
                cap = Math.Max(0, b.cap + r.Range(-0.4, 0.4)),
                llj = Math.Max(15, b.llj * (1 + r.Range(-0.25, 0.25))),
                shear = Math.Max(36, b.shear * (1 + r.Range(-0.18, 0.18))),
                upper = Math.Max(30, b.upper * (1 + r.Range(-0.12, 0.25))),
                curve = C.Clamp(b.curve + r.Range(-0.6, 1.0), -0.6, 1.0),
            };
            CapLcl(d);
            return d;
        }

        /// <summary>the highest cloud base a day may draw, m (user, 2026-11-08: "we need to limit the top to 2km"; the LP
        /// days reached 2.5 km)</summary>
        public const double MaxLclM = 2000;

        /// <summary>A day's dew-point spread narrowed (the dew point raised) until its parcel's LCL is at most MaxLclM.
        /// Bisects on the spread with the environment's own parcel, so the cap holds whatever the mixed layer does.</summary>
        static void CapLcl(StormPreset d)
        {
            double Lcl(double spread) { d.TdF = d.T0F - spread; return Environment.Make(d.ToEnv()).pc.lcl; }
            double hi = d.T0F - d.TdF;
            if (Lcl(hi) <= MaxLclM) { d.TdF = d.T0F - hi; return; }
            double lo = 3;
            for (int i = 0; i < 14; i++) { double m = 0.5 * (lo + hi); if (Lcl(m) <= MaxLclM) lo = m; else hi = m; }
            d.TdF = d.T0F - lo;
        }

        static StormPreset Renamed(StormPreset p, string key, string name) => new StormPreset
        {
            key = key, name = name, blurb = p.blurb, watch = p.watch,
            T0F = p.T0F, TdF = p.TdF, moistFt = p.moistFt, lapse = p.lapse, cap = p.cap, llj = p.llj, shear = p.shear, upper = p.upper,
        };

    }
}

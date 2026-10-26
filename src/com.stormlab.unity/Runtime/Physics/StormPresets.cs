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

        public EnvInput ToEnv() => new EnvInput
        {
            T0F = T0F, TdF = Math.Min(TdF, T0F), lapse = lapse, cap = cap, moist = moistFt * C.FT,
            llj = llj * C.KT, shear = shear * C.KT, upper = upper * C.KT
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

        /// <summary>a game setup or, with "web:" in front, a website preset by its key; null when there is none</summary>
        public static StormPreset Find(string key)
        {
            if (key == null) return null;
            bool web = key.StartsWith(WebPrefix, StringComparison.Ordinal);
            string k = web ? key.Substring(WebPrefix.Length) : key;
            return Array.Find(web ? Website : All, p => p.key == k);
        }
        public const string WebPrefix = "web:";

        static StormPreset Renamed(StormPreset p, string key, string name) => new StormPreset
        {
            key = key, name = name, blurb = p.blurb, watch = p.watch,
            T0F = p.T0F, TdF = p.TdF, moistFt = p.moistFt, lapse = p.lapse, cap = p.cap, llj = p.llj, shear = p.shear, upper = p.upper,
        };

    }
}

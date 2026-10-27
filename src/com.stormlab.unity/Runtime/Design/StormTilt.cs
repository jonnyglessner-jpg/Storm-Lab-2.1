// The game's tilted updraft (user, 2026-10-27; a game adaptation beside the port, which parity does not see). The web
// build tilts the updraft by at most 0.14 km a km and drops its rain at fixed places round the updraft's base, so the
// rain crowds the base and the hook. Here the updraft leans as far as its storm-relative wind carries the rising air
// (METR 4403: its slope is about the storm-relative wind over the updraft speed; with the shear's mean storm-relative
// wind, half the 0-6 km shear, over the plan's updraft speed), and the rain falls out where the tilted updraft takes it
// (Stull: hail and graupel fall nearest the updraft, then rain, then the anvil; an LP storm's strongly tilted updraft
// drops its precipitation well away from it):
//   - the forward-flank rain and the hail core move downshear by the extra lean over the heights they form at (rain and
//     graupel melting just above the freezing level, hail in its growth zone higher up), and by the drift as they fall
//     through the storm-relative wind (taken as rising linearly to half the 0-6 km shear at 6 km): rain falls at about
//     5 m/s (v = 3.778 D^0.67 for 1.5 mm drops, meteorology/Rainfall Mathematics), so it drifts for a quarter of an
//     hour and lands well off; 1-inch hail at about 23 m/s (Stull: w = sqrt((8/3)(g/C_D)(rho_i/rho_a) R), C_D 0.55), so
//     it lands nearest. An HP storm's rain falls back closer (half the drift at the most). The shield spreads with it;
//   - the updraft's base is rain-free on its inflow side (the vault and inflow notch), and a tornado on the ground has
//     clear air round it: widest for a classic storm and a violent tornado, narrower but still there in an HP storm,
//     whose rain wraps round outside it (the bear's cage). The volume's build and the radar both read these from
//     ClearParams (StormVolume.precipParams: x the rain-free base's radius, y the tornado's clear radius, km).
// Pure C# (no UnityEngine) so validation/parity/csharp can list it (-- --tilt).
using System;

namespace StormLab
{
    public static class StormTilt
    {
        /// <summary>The leaning updraft's slope, km a km: half the 0-6 km shear over the plan's updraft speed. The plan
        /// keeps the shear only in its own tilt (0.1 x shear / 25 m/s, capped at 35 m/s).</summary>
        public static double Slope(StormPlan P)
        {
            double tilt0 = Math.Sqrt(P.tilt.x * P.tilt.x + P.tilt.y * P.tilt.y);
            double shear6 = tilt0 / 0.1 * 25;
            return C.Clamp(0.5 * shear6 / Math.Max(P.w, 8), Math.Max(tilt0, 0.15), 0.75);
        }

        /// <summary>terminal fall speeds, m/s: rain (1.5 mm mean drops) and 1-inch hail</summary>
        public const double RainFall = 5.0, HailFall = 23.0;

        /// <summary>How far, km, the tilt and the fall carry the forward-flank rain and the hail core beyond where the web
        /// build drops them (along their own direction from the updraft).</summary>
        public static (double rain, double hail) Shift(StormPlan P)
        {
            double tilt0 = Math.Sqrt(P.tilt.x * P.tilt.x + P.tilt.y * P.tilt.y), extra = Math.Max(Slope(P) - tilt0, 0);
            double shear6 = tilt0 / 0.1 * 25, hp = C.Clamp(P.hpness, 0, 1);
            double hRain = Math.Max(P.fzlKm - P.zb, 1.0) + 1.5, hHail = hRain + 1.0;
            // km = mean storm-relative wind over the fall (m/s) x fall time (s) / 1000
            double Drift(double h, double v) => 0.25 * shear6 * Math.Min(h / 6, 1) * (h * 1000 / v) / 1000;
            double rain = extra * hRain + Drift(hRain, RainFall) * (1 - 0.5 * hp);
            double hail = 0.6 * extra * hHail + Drift(hHail, HailFall);
            return (Math.Min(rain, 8), Math.Min(hail, 4));
        }

        /// <summary>Move the frame's rain to where the tilted updraft drops it and lean the updraft (slots 3, 9, 10).</summary>
        public static void Apply(StormPlan P, StormFrame F)
        {
            if (P.kind != StormKind.Supercell) return;
            var a = F.param;
            double ux = a[16 * 4], uy = a[16 * 4 + 1];
            var (rain, hail) = Shift(P);
            Push(a, 9 * 4, ux, uy, rain);
            Push(a, 10 * 4, ux, uy, hail);
            double tilt0 = Math.Sqrt(P.tilt.x * P.tilt.x + P.tilt.y * P.tilt.y);
            // the shield spreads as it is carried further
            a[9 * 4 + 2] = (float)(a[9 * 4 + 2] * (1 + 0.6 * Math.Max(Slope(P) - tilt0, 0)));
            // the updraft leans over as the storm does (S_tilt, km a km along the storm's motion)
            double s = Slope(P), k = tilt0 > 1e-6 ? s / tilt0 : 0;
            a[3 * 4] = (float)(P.tilt.x * k); a[3 * 4 + 1] = (float)(P.tilt.y * k);
        }

        static void Push(float[] a, int i, double ux, double uy, double by)
        {
            double dx = a[i] - ux, dy = a[i + 1] - uy, m = Math.Sqrt(dx * dx + dy * dy);
            if (m < 0.1) return;
            a[i] = (float)(a[i] + dx / m * by); a[i + 1] = (float)(a[i + 1] + dy / m * by);
        }

        /// <summary>The rain-free base's radius and the tornado's clear radius, km, for the volume's build and the radar.
        /// The base: half the tower's radius, wider as it leans more (its rain is carried further off), narrower in an HP
        /// storm; centred forward and right of the updraft, on its inflow side, so the hook behind it keeps its rain. The
        /// tornado: 0.35 km plus 0.15 a rating step, 1.6 times that in a classic storm, shrinking to 1 times in an HP one.</summary>
        public static (double baseR, double torR) Clear(StormPlan P, StormFrame F)
        {
            if (P.kind != StormKind.Supercell) return (0, 0);
            var a = F.param;
            double R = a[2 * 4 + 1], hp = C.Clamp(P.hpness, 0, 1);
            double baseR = 0.5 * R * (0.7 + 0.6 * Slope(P)) * (1 - 0.3 * hp);
            double tor = a[13 * 4 + 2], ef = a[24 * 4 + 2] > 0 ? a[24 * 4 + 2] : 1;
            double torR = tor > 0.1 ? (0.35 + 0.15 * ef) * (1.6 - 0.6 * hp) * C.Clamp((tor - 0.1) / 0.3, 0, 1) : 0;
            return (baseR, torR);
        }
    }
}

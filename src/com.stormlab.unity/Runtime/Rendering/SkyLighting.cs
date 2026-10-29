// Sun, moon, sky and night light for the storm (from StormLab 2.0, SkyLighting.cs). Light levels are relative to the
// reference evening sun (15 degrees up) the looks were calibrated at; at night they are physical (a full moon gives about
// 5e-6 of it) and an adaptation gain brings them back to a level a player can see by.
using UnityEngine;

namespace StormLab
{
    /// <summary>How the skin handles night (StormRenderer's Night settings).</summary>
    public struct NightSettings
    {
        /// <summary>moon phase: 0 new, 0.25 first quarter, 0.5 full, 0.75 last quarter</summary>
        public float moonPhase;
        /// <summary>how bright a moonlit night is shown, relative to 1.0's evening sun (physically about 5e-6)</summary>
        public float nightBrightness;
        /// <summary>the dimmest a night is ever shown (a moonless night far from towns)</summary>
        public float nightFloor;
        /// <summary>night blue shift 0..1</summary>
        public float purkinje;
        /// <summary>town light on the haze and on cloud bases, 0 (none) .. 1 (a large town nearby)</summary>
        public float lightPollution;
        /// <summary>apply the adaptation gain (off when the game's own exposure adapts to the dark)</summary>
        public bool adapt;
    }

    /// <summary>One frame's lighting, as the march and the light grids take it.</summary>
    public struct SkyLight
    {
        /// <summary>the light the light grids are computed for: the sun, or the moon once the sun is 6 degrees down</summary>
        public Vector3 keyDir;
        public bool keyIsMoon;
        /// <summary>the key light's colour before the horizon cuts it (rgb); w its elevation, degrees</summary>
        public Vector4 keyC;
        /// <summary>sky colour (rgb) and level (w), 1 at 1.0's evening sun</summary>
        public Vector4 skyC;
        /// <summary>the sky dome's dimming below 1.0's twilight floor, 1 by day</summary>
        public float dim;
        /// <summary>town light radiance (relative units)</summary>
        public float townLight;
        /// <summary>the share of townLight on the horizon and the haze: 1 for an even town light everywhere, the open
        /// country's (StormRenderer.TownFloor) when the towns are given and their light falls on the bases above them</summary>
        public float townSky;
        /// <summary>light level of the scene relative to 1.0's evening sun</summary>
        public float level;
        /// <summary>exposure gain of night adaptation, 1 by day</summary>
        public float gain;
        /// <summary>the blue shift: purkinje x (0 by day .. 1 on a moonlit night)</summary>
        public float night;
        /// <summary>0 by day .. 1 on a moonlit night (or darker)</summary>
        public float dark;
    }

    public static class SkyLighting
    {
        /// <summary>above this scene level (sunset, sun about 0.5 degrees up) nothing adapts and the day look is untouched</summary>
        public const float DayLevel = 0.1f;
        /// <summary>scene level of a night under a full moon well up</summary>
        public const float MoonLevel = 1e-5f;
        /// <summary>a full moon's light, relative to 1.0's evening sun: 0.25 lux against about 50,000</summary>
        public const float FullMoon = 5e-6f;
        /// <summary>starlight and airglow, as a sky level</summary>
        public const float StarSky = 2e-7f;
        /// <summary>town light radiance at lightPollution = 1: a few times a moonlit cloud base's (towns light low cloud orange)</summary>
        public const float TownLight = 1e-5f;

        static readonly float SkyNorm = 0.35f + 2.5f * Mathf.Sin(15f * Mathf.Deg2Rad);
        /// <summary>1.0's floor on the sky level, reached with the sun 3.4 degrees down</summary>
        static readonly float SkyFloor = 0.2f / SkyNorm;
        static readonly float FloorEl = Mathf.Asin((0.2f - 0.35f) / 2.5f) * Mathf.Rad2Deg;

        static float Air(float e) { float ec = Mathf.Max(e, -0.5f); return 1f / (Mathf.Max(Mathf.Sin(ec * Mathf.Deg2Rad), 0f) + 0.50572f * Mathf.Pow(ec + 6.07995f, -1.6364f)); }

        /// <summary>
        /// The colour of sunlight that has crossed the air at elevation elDeg, before the horizon cuts it off (2.0's StormSkin.SunColours
        /// without its cut-off): Kasten-Young air mass, extinction fitted so 15 degrees gives 1.0's (1, 0.82, 0.6) x 0.95.
        /// </summary>
        public static Vector3 SunColour(float elDeg)
        {
            float m = Air(elDeg), m15 = Air(15f), k = m / m15;
            float bright = Mathf.Exp(-0.06f * (m - m15));
            return new Vector3(0.95f * bright, 0.95f * 0.82f * Mathf.Pow(0.82f, k - 1f) * bright, 0.95f * 0.6f * Mathf.Pow(0.6f, k - 1f) * bright);
        }

        /// <summary>1.0's sky level for a light source at sin(elevation) s, without its floor</summary>
        static float SkyDay(float s) => Mathf.Max(0.35f + 2.5f * s, 0f) / SkyNorm;

        /// <summary>
        /// Sky light left after sunset, relative to 1.0's floor (sun 3.4 degrees down): log10 of the measured fall of the
        /// sky's illuminance over twilight (about 1/10 by 6 degrees down, 1/30,000 by 12, starlight by 18).
        /// </summary>
        static float Twilight(float elDeg)
        {
            float[] el = { 0f, -3f, -6f, -9f, -12f, -15f, -18f };
            float[] lg = { 0f, -0.8f, -2.0f, -3.3f, -4.6f, -5.5f, -6.3f };
            float L(float e)
            {
                if (e >= 0f) return 0f;
                for (int i = 1; i < el.Length; i++) if (e >= el[i]) return Mathf.Lerp(lg[i - 1], lg[i], (e - el[i - 1]) / (el[i] - el[i - 1]));
                return lg[lg.Length - 1];
            }
            return Mathf.Pow(10f, L(elDeg) - L(FloorEl));
        }

        /// <summary>fraction of the moon's disc that is lit</summary>
        public static float MoonLit(float phase) => 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * phase));

        static float Ss(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }

        /// <summary>the key light's colour normalised as 1.0's warm horizon was (StormMarch.compute: warm)</summary>
        public static Vector3 Warm(SkyLight L)
        {
            float r = Mathf.Max(L.keyC.x, 1e-20f);
            return new Vector3(0.95f * L.keyC.x / r, 0.82f * L.keyC.y / r / 0.82f, 0.66f * L.keyC.z / r / 0.6f);
        }

        /// <summary>sky radiance in direction d: StormMarch.compute's skyRad, kept in step with it</summary>
        public static Vector3 SkyRad(Vector3 d, SkyLight L, float aur)
        {
            var warm = Warm(L);
            float glowK = Ss(-6f, 1.5f, L.keyC.w) * L.dim, skyK = L.skyC.w;
            float el = Mathf.Clamp01(d.z), cg = Vector3.Dot(d, L.keyDir);
            float spread = Mathf.Lerp(2f, 3f, Mathf.Clamp01(skyK));
            Vector3 hz = Vector3.Lerp(new Vector3(0.78f, 0.8f, 0.84f) * Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(skyK)) * L.dim, warm * glowK, Mathf.Pow(Mathf.Max(cg, 0f), spread) * 0.8f);
            float up = 1f - Mathf.Pow(1f - el, 3f);
            Vector3 glow = Vector3.Scale(warm, new Vector3(1f / 0.95f, 0.85f / 0.82f, 0.6f / 0.66f)) * (0.5f * aur * Mathf.Pow(Mathf.Max(cg, 0f), 24f) * glowK);
            return hz * (1f - up) + new Vector3(0.33f, 0.47f, 0.72f) * (skyK * up) + glow + new Vector3(1f, 0.75f, 0.5f) * (L.townLight * L.townSky * 0.5f * Mathf.Pow(1f - el, 6f));
        }

        /// <summary>
        /// The sky in the four parts StormVolume.compute's SkyVis measures the visibility of, in its directions: the cap
        /// (zenith and 4 at 25 degrees), the ring (6 at 50), the horizon (8 at 82) and the horizon weighted cos^2 toward the
        /// key light's bearing. The glow round the key light counts a quarter: it is a peak ~15 degrees wide that 8
        /// directions would oversample.
        /// </summary>
        public static void Bands(SkyLight L, out Vector4 zen, out Vector4 mid, out Vector4 hor, out Vector4 horK)
        {
            const float aur = 0.25f;
            Vector3 z = SkyRad(new Vector3(0, 0, 1), L, aur);
            for (int i = 0; i < 4; i++) { float a = i * 1.5708f + 0.7854f; z += SkyRad(new Vector3(Mathf.Cos(a) * 0.4226f, Mathf.Sin(a) * 0.4226f, 0.9063f), L, aur); }
            Vector3 m = Vector3.zero;
            for (int j = 0; j < 6; j++) { float a = j * 1.0472f; m += SkyRad(new Vector3(Mathf.Cos(a) * 0.766f, Mathf.Sin(a) * 0.766f, 0.6428f), L, aur); }
            var kd = new Vector2(L.keyDir.x + 1e-5f, L.keyDir.y).normalized;
            Vector3 h = Vector3.zero, hk = Vector3.zero; float wk = 0f;
            for (int k = 0; k < 8; k++)
            {
                float a = k * 0.7854f; var hd = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                Vector3 s = SkyRad(new Vector3(hd.x * 0.9903f, hd.y * 0.9903f, 0.1392f), L, aur);
                float w = Mathf.Max(Vector2.Dot(hd, kd), 0f); w *= w;
                h += s; hk += w * s; wk += w;
            }
            zen = z * 0.2f; mid = m / 6f; hor = h / 8f; horK = hk / Mathf.Max(wk, 1e-4f);
        }

        /// <summary>The lighting for a sun and moon direction (volume frame: x east, y north, z up).</summary>
        public static SkyLight Evaluate(Vector3 sunVol, Vector3 moonVol, NightSettings n)
        {
            var o = new SkyLight();
            float sunEl = Mathf.Asin(Mathf.Clamp(sunVol.z, -1f, 1f)) * Mathf.Rad2Deg;
            float moonEl = Mathf.Asin(Mathf.Clamp(moonVol.z, -1f, 1f)) * Mathf.Rad2Deg;
            // the moon's light falls off steeply away from full (the lit part shrinks and its shadows lengthen)
            float lit = MoonLit(n.moonPhase);
            float moon = FullMoon * lit * lit * lit;
            float moonUp = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-1f, 1.5f, moonEl));

            // the key light: the sun until it is 6 degrees down (by then it lights nothing in the volume, even 18 km up),
            // then the moon while it is up
            o.keyIsMoon = sunEl < -6f && moonEl > -1f && moon > 0f;
            Vector3 kc;
            if (o.keyIsMoon)
            {
                // moonlight is sunlight reflected by grey rock: a little redder than the sun's
                kc = SunColour(moonEl) * moon; kc = Vector3.Scale(kc, new Vector3(1f, 0.97f, 0.92f));
                o.keyDir = moonVol; o.keyC = new Vector4(kc.x, kc.y, kc.z, moonEl);
            }
            else
            {
                kc = SunColour(sunEl);
                o.keyDir = sunVol; o.keyC = new Vector4(kc.x, kc.y, kc.z, sunEl);
            }

            // the sky: 1.0's level down to its floor, then twilight's fall, plus moonlit sky and starlight
            float sinEl = Mathf.Clamp(sunVol.z, -1f, 1f);
            float sunSky = sunEl >= FloorEl ? Mathf.Min(SkyDay(sinEl), 1.3f / SkyNorm) : SkyFloor * Twilight(sunEl);
            float moonSky = moon * SkyDay(Mathf.Max(moonVol.z, 0f)) * moonUp;
            float skyK = sunSky + moonSky + StarSky;
            o.skyC = new Vector4(0.42f * skyK, 0.52f * skyK, 0.68f * skyK, skyK);
            o.dim = Mathf.Min(skyK / SkyFloor, 1f);
            o.townLight = n.lightPollution * TownLight; o.townSky = 1f;

            // the scene's light level: half sky, half the key light at the ground (by day the two are about equal)
            float keyGround = o.keyIsMoon ? moon * moonUp : SunColour(sunEl).magnitude / SunColour(15f).magnitude * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-1f, 1.5f, sunEl));
            o.level = 0.5f * skyK + 0.5f * keyGround + 0.25f * o.townLight;

            // adaptation: above DayLevel nothing changes. Below it the shown level falls as a power of the real one, chosen
            // so a moonlit night shows at nightBrightness, and never below nightFloor
            o.gain = 1f; o.night = 0f; o.dark = 0f;
            if (o.level < DayLevel)
            {
                float nb = Mathf.Clamp(n.nightBrightness, 1e-4f, 0.95f * DayLevel);
                float e = Mathf.Log(nb / DayLevel) / Mathf.Log(MoonLevel / DayLevel);
                float shown = Mathf.Max(DayLevel * Mathf.Pow(o.level / DayLevel, e), Mathf.Min(n.nightFloor, nb));
                if (n.adapt) o.gain = shown / o.level;
                o.dark = Mathf.Clamp01(Mathf.Log(DayLevel / o.level) / Mathf.Log(DayLevel / MoonLevel));
                o.night = o.dark * n.purkinje;
            }
            return o;
        }
    }
}

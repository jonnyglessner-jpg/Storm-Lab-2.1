// Thunder from a flash's channel geometry. Every short segment of every channel (and every return stroke) sends a
// pressure pulse to the listener; summed by arrival time they give thunder its shape: a channel stretch that runs across
// the line of sight arrives all at once (a clap), one running toward the listener is drawn out (a roll), and the far end
// of a long channel arrives seconds after the near end. Each pulse falls off as 1/distance (spherical spreading), and air
// absorbs the high frequencies first, so a close strike is a ripping crack and a distant one a low grumble. A seeded
// character per flash (echo length, crackle, bass, rolling) varies flashes of the same geometry.
// Plain C# (System.Numerics), so validation/audio renders it outside Unity.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace StormLab.Audio
{
    public sealed class ThunderInput
    {
        /// <summary>channel polylines, world metres (y up); channel 0 the main one</summary>
        public Vector3[][] channels;
        /// <summary>each channel's share of the current, 0..1 (main channel 1, branches less)</summary>
        public float[] weights;
        /// <summary>return-stroke times, seconds after the first</summary>
        public float[] strokes = { 0f };
        public Vector3 observer;
        public float temperatureC = 20f;
        public uint seed = 1;
        /// <summary>a flash inside the cloud (or a crawler): softer crack, more rumble</summary>
        public bool aloft;
        /// <summary>a positive ground flash: one big stroke with a long continuing current, louder</summary>
        public bool positive;
    }

    public sealed class Thunder
    {
        public float[] samples;
        public int sampleRate;
        /// <summary>seconds from the flash to the first sound</summary>
        public float delaySeconds;
        /// <summary>distance to the nearest point of the channel, m</summary>
        public float nearestM;
        /// <summary>how far the limiter turned the loudest moment down, dB (0: not at all)</summary>
        public float limitedDb;
        /// <summary>the flash's character, for readouts</summary>
        public string character;
    }

    public static class ThunderSynth
    {
        public const int SampleRate = 24000;
        /// <summary>thunder is not played from a flash farther than this, m (it is rarely heard past ~25 km)</summary>
        public const float AudibleRangeM = 25000f;
        /// <summary>overall level: a ground strike about 1 km away peaks near full scale</summary>
        public static float Gain = 1f;
        /// <summary>no thunder peaks above this (-3 dBFS), leaving room for the wind, rain and other thunder</summary>
        public const float Ceiling = 0.7f;
        const int EnvRate = 1000;          // envelope bins per second
        const float PulseSeconds = 0.003f; // a segment's own pulse length

        public static Thunder Synthesize(ThunderInput input)
        {
            float c = 331.3f + 0.606f * input.temperatureC;
            var rng = new Rng(Rng.Hash(input.seed, 0x7468756eu));
            // the flash's character
            float echo = rng.Range(0.8f, 3.6f);          // the rumble's echo (ground, hills, cloud base), s
            float crackle = rng.Range(0.5f, 1.4f) * (input.aloft ? 0.35f : 1f);
            float bass = rng.Range(0.75f, 1.35f) * (input.positive ? 1.3f : 1f);
            float rollHz = rng.Range(0.35f, 1.4f), rollDepth = rng.Range(0f, 0.55f);
            float strokeFade = rng.Range(0.35f, 0.8f);
            float loud = input.positive ? 1.6f : input.aloft ? 0.75f : 1f;

            // ---- segments: arrival time, spread and the pressure they bring
            var segs = new List<(float t, float spread, float p, float d)>(2048);
            float nearest = float.MaxValue, first = float.MaxValue, last = 0f;
            for (int ch = 0; ch < input.channels.Length; ch++)
            {
                var pts = input.channels[ch]; if (pts == null || pts.Length < 2) continue;
                float w = input.weights != null && ch < input.weights.Length ? input.weights[ch] : (ch == 0 ? 1f : 0.35f);
                for (int i = 0; i + 1 < pts.Length; i++)
                {
                    var a = pts[i]; var b = pts[i + 1];
                    var seg = b - a; float len = seg.Length(); if (len < 1e-3f) continue;
                    var mid = (a + b) * 0.5f; var los = mid - input.observer; float d = Math.Max(los.Length(), 30f);
                    nearest = Math.Min(nearest, d);
                    if (d > AudibleRangeM * 1.3f) continue;
                    float t = d / c, spread = Math.Abs(Vector3.Dot(seg, los / d)) / c;
                    segs.Add((t - spread * 0.5f, spread, w * len / d, d));
                    first = Math.Min(first, t - spread * 0.5f); last = Math.Max(last, t + spread * 0.5f);
                }
            }
            if (segs.Count == 0 || nearest > AudibleRangeM) return null;
            var strokes = input.strokes != null && input.strokes.Length > 0 ? input.strokes : new[] { 0f };
            int nStrokes = input.positive ? 1 : Math.Min(strokes.Length, 6);
            float strokeEnd = strokes[nStrokes - 1] - strokes[0];

            float tail = echo * 3.5f + 1f;
            float seconds = Math.Min(last - first + strokeEnd + tail, 40f);
            int nb = (int)(seconds * EnvRate) + 2;
            var eH = new float[nb]; var eM = new float[nb]; var eL = new float[nb];
            var nwaves = new List<(int at, int len, float amp)>();
            for (int k = 0; k < nStrokes; k++)
            {
                float sa = (k == 0 ? 1f : (float)Math.Pow(strokeFade, k)) * loud;
                float st = strokes[k] - strokes[0];
                foreach (var s in segs)
                {
                    float hiW = (float)Math.Exp(-s.d / 1500f) * crackle;
                    float midW = 0.8f * (float)Math.Exp(-s.d / 6500f);
                    float loW = bass * (float)Math.Exp(-s.d / 30000f);
                    float spreadBins = Math.Max(s.spread * EnvRate, 1f);
                    // a segment across the line of sight lands in one bin (a clap); one along it is smeared thin
                    float per = s.p * sa * Math.Min(1f, PulseSeconds * EnvRate / spreadBins);
                    int b0 = (int)((s.t - first + st) * EnvRate), b1 = Math.Min(nb - 1, b0 + (int)spreadBins);
                    for (int b = b0; b <= b1; b++) { eH[b] += per * hiW; eM[b] += per * midW; eL[b] += per * loW; }
                    // the shock itself near the channel: an N-wave, longer the farther it has run
                    if (s.d < 2500f && k < 2 && !input.aloft)
                        nwaves.Add(((int)((s.t - first + st) * SampleRate), (int)(SampleRate * 0.0015f * (float)Math.Pow(s.d / 100f, 0.25)), s.p * sa * crackle));
                }
            }
            // echoes: the low and middle bands ring on, the low one longest
            Echo(eL, echo, 1.6f); Echo(eM, echo * 0.5f, 0.6f);
            SmoothEnv(eM, 0.012f); SmoothEnv(eL, 0.05f);

            // ---- render
            int n = (int)(seconds * SampleRate);
            var o = new float[n];
            Svf hi = default, md = default, lo = default, lo2 = default;
            hi.Set(1700f, 0.6f, SampleRate); md.Set(320f, 0.7f, SampleRate); lo.Set(95f, 0.7f, SampleRate); lo2.Set(55f, 0.9f, SampleRate);
            Pink pink = default; Brown brown = default; Wander roll = default;
            float near = Dsp.Clamp01(1f - nearest / 4000f);
            for (int i = 0; i < n; i++)
            {
                float fb = i * (float)EnvRate / SampleRate; int b = (int)fb; float f = fb - b;
                if (b + 1 >= nb) break;
                float h = eH[b] + (eH[b + 1] - eH[b]) * f, m = eM[b] + (eM[b + 1] - eM[b]) * f, l = eL[b] + (eL[b + 1] - eL[b]) * f;
                float r = 1f - rollDepth + rollDepth * 1.8f * roll.Next(ref rng, 1f / rollHz, SampleRate);
                hi.Process(rng.N());
                // crackle: sparse spikes in the high band, as the channel's kinks arrive one by one
                float spike = rng.U() < 0.04f + 0.3f * near ? rng.N() * 3f : 0f;
                float x = (hi.high + spike * 0.4f) * h * 2.2f
                        + md.BandNorm(pink.Next(ref rng)) * m * 2.4f * r
                        + Low(ref lo, ref lo2, brown.Next(ref rng)) * l * 3.4f * r;
                o[i] = x;
            }
            foreach (var w in nwaves)
            {
                int len = Math.Max(w.len, 8);
                for (int j = 0; j < len && w.at + j < n; j++)
                    if (w.at + j >= 0) o[w.at + j] += w.amp * 6f * (1f - 2f * j / (float)len);
            }
            // level: physical 1/d throughout (the 1 km reference gives Gain); the closest strikes' peaks are limited cleanly
            // (a look-ahead limiter, no clipping: a soft clipper here crackled like a blown speaker)
            float g = Gain;
            for (int i = 0; i < n; i++) o[i] *= g;
            float limited = Dsp.LimitBuffer(o, Ceiling, SampleRate);
            // fade the last 0.3 s
            int fade = Math.Min(n, (int)(0.3f * SampleRate));
            for (int i = 0; i < fade; i++) o[n - 1 - i] *= i / (float)fade;
            return new Thunder
            {
                samples = o, sampleRate = SampleRate, delaySeconds = first, nearestM = nearest, limitedDb = limited,
                character = $"echo {echo:0.0} s, crackle {crackle:0.00}, bass {bass:0.00}, roll {rollHz:0.0} Hz x{rollDepth:0.00}, strokes {nStrokes}"
            };
        }

        static float Low(ref Svf a, ref Svf b, float x) { a.Process(x); b.Process(x); return 0.6f * a.low + 0.6f * b.low; }

        static void Echo(float[] e, float tau, float gain)
        {
            float k = (float)Math.Exp(-1.0 / (tau * EnvRate)), y = 0f, inGain = gain / (tau * EnvRate) * 6f;
            for (int i = 0; i < e.Length; i++) { y = y * k + e[i] * inGain; e[i] += y; }
        }

        static void SmoothEnv(float[] e, float tau)
        {
            float k = 1f - (float)Math.Exp(-1.0 / (tau * EnvRate)), y = 0f;
            for (int i = 0; i < e.Length; i++) { y += k * (e[i] - y); e[i] = y; }
        }
    }
}

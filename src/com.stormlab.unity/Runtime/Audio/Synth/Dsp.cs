// Small DSP pieces the storm's procedural sounds are built from. Plain C# (no UnityEngine), so the synths can be rendered
// to WAV outside Unity (validation/audio) and run on Unity's audio thread.
using System;

namespace StormLab.Audio
{
    /// <summary>A procedural sound: fills interleaved frames. Parameters are plain fields the game thread writes and the
    /// audio thread reads (each a single float, so a torn read cannot happen); synths smooth them per sample.</summary>
    public interface ISynth
    {
        /// <summary>Add nothing, overwrite: write `frames` frames of `channels` interleaved samples into data.</summary>
        void Render(float[] data, int frames, int channels);
    }

    /// <summary>xorshift32: fast, deterministic noise.</summary>
    public struct Rng
    {
        uint s;
        public Rng(uint seed) { s = seed == 0 ? 0x9e3779b9u : seed; }
        public uint Next() { uint x = s; x ^= x << 13; x ^= x >> 17; x ^= x << 5; return s = x; }
        /// <summary>uniform 0..1</summary>
        public float U() => (Next() >> 8) * (1f / 16777216f);
        /// <summary>uniform -1..1</summary>
        public float N() => U() * 2f - 1f;
        public float Range(float a, float b) => a + (b - a) * U();
        public int Int(int n) => (int)(U() * n) % Math.Max(1, n);
        public static uint Hash(uint a, uint b)
        {
            uint x = unchecked(a ^ (b * 0x9e3779b9u));
            x ^= x >> 16; x = unchecked(x * 0x7feb352du); x ^= x >> 15;
            x = unchecked(x * 0x846ca68bu); return x ^ (x >> 16);
        }
    }

    /// <summary>Pink noise (Paul Kellet's economy filter), about unit RMS.</summary>
    public struct Pink
    {
        float b0, b1, b2;
        public float Next(ref Rng r)
        {
            float w = r.N();
            b0 = 0.99765f * b0 + w * 0.0990460f;
            b1 = 0.96300f * b1 + w * 0.2965164f;
            b2 = 0.57000f * b2 + w * 1.0526913f;
            return (b0 + b1 + b2 + w * 0.1848f) * 0.55f;
        }
    }

    /// <summary>Brown (red) noise: integrated white noise with a leak, about unit RMS.</summary>
    public struct Brown
    {
        float y;
        public float Next(ref Rng r) { y = 0.997f * y + 0.06f * r.N(); return y * 3.2f; }
    }

    /// <summary>One-pole low-pass; High() gives the matching high-pass.</summary>
    public struct OnePole
    {
        float y, a;
        public void Set(float cutoffHz, float fs) => a = 1f - (float)Math.Exp(-2.0 * Math.PI * Math.Min(cutoffHz, fs * 0.45f) / fs);
        public float Low(float x) => y += a * (x - y);
        public float High(float x) => x - Low(x);
        public float Value => y;
    }

    /// <summary>State-variable filter (Simper's trapezoidal SVF): stable at any cutoff and Q, cheap to retune per sample.</summary>
    public struct Svf
    {
        float ic1, ic2, a1, a2, a3, k;
        public float low, band, high;
        public void Set(float cutoffHz, float q, float fs)
        {
            float g = (float)Math.Tan(Math.PI * Math.Min(Math.Max(cutoffHz, 5f), fs * 0.45f) / fs);
            k = 1f / Math.Max(q, 0.05f);
            a1 = 1f / (1f + g * (g + k)); a2 = g * a1; a3 = g * a2;
        }
        public float Process(float x)
        {
            float v3 = x - ic2, v1 = a1 * ic1 + a2 * v3, v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = 2f * v1 - ic1; ic2 = 2f * v2 - ic2;
            low = v2; band = v1; high = x - k * v1 - v2;
            return band;
        }
        /// <summary>band-pass normalised to unit gain at the centre</summary>
        public float BandNorm(float x) { Process(x); return band * k; }
        public void Reset() { ic1 = ic2 = 0; }
    }

    /// <summary>Exponential smoothing of a control value toward a target.</summary>
    public struct Smooth
    {
        public float value;
        float a;
        public void Set(float seconds, float fs) => a = 1f - (float)Math.Exp(-1.0 / Math.Max(seconds * fs, 1.0));
        public float Toward(float target) => value += a * (target - value);
    }

    /// <summary>A slowly wandering value 0..1 (gusts, flutter): smoothed random targets picked every `period` seconds.</summary>
    public struct Wander
    {
        float target, value, count, vel;
        public float Next(ref Rng r, float period, float fs)
        {
            if ((count -= 1f) <= 0f) { target = r.U(); count = period * fs * r.Range(0.5f, 1.5f); }
            // critically damped: no corners where a new target is picked
            float w = 6.2832f / Math.Max(period * fs, 1f);
            vel += w * w * (target - value) - 2f * w * vel;
            value += vel;
            return value;
        }
    }

    public static class Dsp
    {
        public static float Clamp01(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
        public static float SmoothStep(float a, float b, float x) { float t = Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
        public static float Db(float db) => (float)Math.Pow(10.0, db / 20.0);
        /// <summary>soft limiter (a tanh approximation): about linear below 0.5, never past 1</summary>
        public static float Tanh(float x)
        {
            if (x > 3f) return 1f; if (x < -3f) return -1f;
            float x2 = x * x; return x * (27f + x2) / (27f + 9f * x2);
        }
        /// <summary>A look-ahead peak limiter for a finished mono buffer: the gain eases down just before each peak and
        /// recovers slowly after it, so no sample passes ceiling and nothing is clipped. Returns the deepest cut, dB.</summary>
        public static float LimitBuffer(float[] x, float ceiling, float fs, float attackS = 0.004f, float releaseS = 0.25f)
        {
            int n = x.Length; var g = new float[n];
            for (int i = 0; i < n; i++) { float a = Math.Abs(x[i]); g[i] = a > ceiling ? ceiling / a : 1f; }
            float kr = 1f - (float)Math.Exp(-1.0 / (releaseS * fs)), ka = 1f - (float)Math.Exp(-1.0 / (attackS * fs));
            // down at once, back up slowly; then, running backwards, down ahead of each peak
            float y = 1f;
            for (int i = 0; i < n; i++) { y = Math.Min(g[i], y + (1f - y) * kr); g[i] = y; }
            y = 1f;
            for (int i = n - 1; i >= 0; i--) { y = Math.Min(g[i], y + (1f - y) * ka); g[i] = y; }
            float min = 1f;
            for (int i = 0; i < n; i++) { x[i] *= g[i]; min = Math.Min(min, g[i]); }
            return 20f * (float)Math.Log10(min);
        }
        /// <summary>equal-power pan gains for p -1 (left) .. 1 (right)</summary>
        public static void Pan(float p, out float l, out float r)
        {
            double a = (Clamp01((p + 1f) * 0.5f)) * Math.PI * 0.5;
            l = (float)Math.Cos(a); r = (float)Math.Sin(a);
        }
    }

    /// <summary>A peak limiter for a live stream (the final mix): it looks 2 ms ahead, so the gain is already down when a
    /// peak arrives, and releases slowly. Thunder on top of the tornado roar and the rain dips the level for a moment
    /// instead of clipping the output.</summary>
    public sealed class Limiter
    {
        public float ceiling = 0.9f;
        /// <summary>the lowest gain since it was last read (1: no limiting)</summary>
        public float Deepest { get { float d = deepest; deepest = 1f; return d; } }
        readonly int la; readonly float att, rel;
        float[] buf, cut; int pos, ch; float env = 1f, deepest = 1f;

        public Limiter(float fs, float lookaheadS = 0.002f, float releaseS = 0.2f)
        {
            la = Math.Max(1, (int)(lookaheadS * fs));
            att = 1f - (float)Math.Exp(-4.0 / la);
            rel = 1f - (float)Math.Exp(-1.0 / (releaseS * fs));
            cut = new float[la]; for (int i = 0; i < la; i++) cut[i] = 1f;
        }

        public void Process(float[] data, int frames, int channels)
        {
            if (buf == null || ch != channels) { ch = channels; buf = new float[la * channels]; pos = 0; }
            for (int i = 0; i < frames; i++)
            {
                float a = 0f;
                for (int c = 0; c < channels; c++) a = Math.Max(a, Math.Abs(data[i * channels + c]));
                // each sample's own cut rides the delay with it
                float leaving = cut[pos];
                cut[pos] = a > ceiling ? ceiling / a : 1f;
                // the deepest cut anything in the delay needs: the gain eases down to it before that peak comes out,
                // and the sample leaving now is never let past the ceiling
                float need = 1f; for (int k = 0; k < la; k++) if (cut[k] < need) need = cut[k];
                env += (need - env) * (need < env ? att : rel);
                float g = Math.Min(env, leaving);
                if (g < deepest) deepest = g;
                int w = pos * channels;
                for (int c = 0; c < channels; c++)
                {
                    float d = buf[w + c]; buf[w + c] = data[i * channels + c];
                    float y = d * g; data[i * channels + c] = y > 1f ? 1f : y < -1f ? -1f : y;
                }
                pos = (pos + 1) % la;
            }
        }
    }
}

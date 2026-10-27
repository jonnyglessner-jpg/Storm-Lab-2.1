// Country ambience on the plains: a soft breeze bed, bird calls (western meadowlark, red-winged blackbird, sparrow chips,
// mourning dove). Calls are rendered once to clips (Bird) and played at places round the listener.
using System;

namespace StormLab.Audio
{
    public enum BirdKind { Meadowlark, Blackbird, Sparrow, Dove }

    public static class AmbienceSynth
    {
        public const int BirdRate = 32000;

        // ------------------------------------------------------------------ birds
        /// <summary>One call of a bird of this kind, mono at BirdRate. Different seeds give different calls.</summary>
        public static float[] Bird(BirdKind kind, uint seed)
        {
            var r = new Rng(Rng.Hash(seed, 0x62697264u));
            var notes = new System.Collections.Generic.List<Note>();
            float t = 0.02f;
            switch (kind)
            {
                case BirdKind.Meadowlark:
                {
                    // two or three clear, slurred whistles falling in pitch, then a quick descending gurgle
                    float top = r.Range(2600f, 3300f);
                    int whistles = 2 + r.Int(2);
                    for (int i = 0; i < whistles; i++)
                    {
                        float d = r.Range(0.16f, 0.32f), f = top * r.Range(0.85f, 1.05f);
                        notes.Add(new Note(t, d, f, f * r.Range(0.78f, 0.92f), 0.9f, 0f, 0f, 0.08f)); t += d + r.Range(0.03f, 0.09f);
                    }
                    int gurgle = 3 + r.Int(4); float g = top * 0.85f;
                    for (int i = 0; i < gurgle; i++)
                    {
                        float d = r.Range(0.05f, 0.1f); float f = g * r.Range(0.9f, 1.15f);
                        notes.Add(new Note(t, d, f, f * r.Range(0.75f, 1.2f), 0.75f, 0f, 0f, 0.12f)); t += d + 0.015f; g *= 0.94f;
                    }
                    break;
                }
                case BirdKind.Blackbird:
                {
                    // 'conk-la-REE': two rich short notes and a buzzing trill
                    for (int i = 0; i < 2; i++)
                    {
                        float f = r.Range(1500f, 2400f), d = r.Range(0.05f, 0.09f);
                        notes.Add(new Note(t, d, f, f * 1.1f, 0.7f, 0f, 0f, 0.35f)); t += d + r.Range(0.04f, 0.08f);
                    }
                    float tf = r.Range(3200f, 3900f), td = r.Range(0.55f, 0.95f);
                    notes.Add(new Note(t, td, tf, tf * 0.95f, 1f, r.Range(38f, 60f), r.Range(300f, 550f), 0.25f, rise: 0.3f));
                    break;
                }
                case BirdKind.Sparrow:
                {
                    // a run of sharp downward chips, sometimes ending in a short trill
                    int chips = 3 + r.Int(6); float gap = r.Range(0.09f, 0.2f);
                    for (int i = 0; i < chips; i++)
                    {
                        float f = r.Range(5200f, 6800f);
                        notes.Add(new Note(t, r.Range(0.025f, 0.045f), f, f * r.Range(0.55f, 0.7f), r.Range(0.6f, 1f), 0f, 0f, 0.05f)); t += gap;
                    }
                    if (r.U() < 0.5f)
                    {
                        float f = r.Range(4000f, 5000f);
                        notes.Add(new Note(t, r.Range(0.3f, 0.6f), f, f * 0.9f, 0.7f, r.Range(18f, 28f), r.Range(600f, 1200f), 0.05f));
                    }
                    break;
                }
                default:
                {
                    // the mourning dove: a soft, low 'coo-OOO, oo, oo, oo'
                    float f = r.Range(470f, 560f);
                    notes.Add(new Note(t, 0.3f, f * 0.92f, f * 1.18f, 0.6f, 0f, 0f, 0.04f)); t += 0.38f;
                    notes.Add(new Note(t, 0.55f, f * 1.18f, f * 0.95f, 1f, 5f, 8f, 0.04f)); t += 0.75f;
                    for (int i = 0; i < 3; i++) { notes.Add(new Note(t, 0.42f, f, f * 0.93f, 0.8f, 4f, 6f, 0.04f)); t += 0.56f; }
                    break;
                }
            }
            float end = 0f; foreach (var n in notes) end = Math.Max(end, n.t + n.d);
            var o = new float[(int)((end + 0.05f) * BirdRate)];
            foreach (var n in notes) n.Render(o, BirdRate, ref r);
            Normalize(o, kind == BirdKind.Dove ? 0.5f : 0.7f);
            return o;
        }

        readonly struct Note
        {
            public readonly float t, d, f0, f1, amp, fmHz, fmDev, harm, rise;
            public Note(float t, float d, float f0, float f1, float amp, float fmHz, float fmDev, float harm, float rise = 0.15f)
            { this.t = t; this.d = d; this.f0 = f0; this.f1 = f1; this.amp = amp; this.fmHz = fmHz; this.fmDev = fmDev; this.harm = harm; this.rise = rise; }

            public void Render(float[] o, int fs, ref Rng r)
            {
                int s0 = (int)(t * fs), n = (int)(d * fs);
                double ph = 0, fm = 0;
                for (int i = 0; i < n && s0 + i < o.Length; i++)
                {
                    float u = i / (float)n;
                    // a quick swell and a smooth fall
                    float env = u < rise ? Dsp.SmoothStep(0f, rise, u) : Dsp.SmoothStep(1f, rise, u);
                    fm += 2 * Math.PI * fmHz / fs;
                    float f = Dsp.Lerp(f0, f1, u * u * (3f - 2f * u)) + fmDev * (float)Math.Sin(fm);
                    ph += 2 * Math.PI * f / fs;
                    o[s0 + i] += amp * env * ((float)Math.Sin(ph) + harm * (float)Math.Sin(2 * ph) + 0.3f * harm * (float)Math.Sin(3 * ph) + 0.002f * r.N() * env);
                }
            }
        }

        static void Normalize(float[] o, float peak)
        {
            float m = 1e-6f; foreach (var x in o) m = Math.Max(m, Math.Abs(x));
            float g = peak / m; for (int i = 0; i < o.Length; i++) o[i] *= g;
            int fade = Math.Min(o.Length, 200); for (int i = 0; i < fade; i++) o[o.Length - 1 - i] *= i / (float)fade;
        }
    }

    /// <summary>A soft breeze over the grass, always there under everything else.</summary>
    public sealed class BreezeSynth : ISynth
    {
        public float level = 0.035f;
        readonly float fs;
        Rng rng; Pink pl, pr; Svf fl, fr; Wander w; int retune;
        public BreezeSynth(float sampleRate, uint seed = 4) { fs = sampleRate; rng = new Rng(seed); }
        public void Render(float[] data, int frames, int channels)
        {
            for (int i = 0; i < frames; i++)
            {
                float g = w.Next(ref rng, 4f, fs);
                if (--retune <= 0) { retune = 64; fl.Set(350f + 700f * g, 0.6f, fs); fr.Set(380f + 720f * g, 0.6f, fs); }
                float a = level * (0.4f + 0.9f * g);
                float L = fl.BandNorm(pl.Next(ref rng)) * a, R = fr.BandNorm(pr.Next(ref rng)) * a;
                if (channels == 1) data[i] = 0.5f * (L + R);
                else { data[i * channels] = L; data[i * channels + 1] = R; for (int c = 2; c < channels; c++) data[i * channels + c] = 0.5f * (L + R); }
            }
        }
    }
}

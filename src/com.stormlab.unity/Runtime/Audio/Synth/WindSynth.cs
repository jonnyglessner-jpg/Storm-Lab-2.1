// Tornado wind at the player: silent in calm air, a gusty rush from a few m/s, moaning and whistling round the truck as it
// rises, then the low roar, debris strikes and buffeting of a tornado's core. Driven by the wind speed (m/s) only.
using System;

namespace StormLab.Audio
{
    public sealed class WindSynth : ISynth
    {
        /// <summary>wind speed at the player, m/s (StormRenderer.TornadoWindAt)</summary>
        public float speed;
        /// <summary>the side the wind blows from, seen from the listener: -1 left .. 1 right</summary>
        public float pan;
        /// <summary>wind speeds the sound starts at and reaches full intensity at, m/s</summary>
        public float startMs = 3f, fullMs = 80f;

        readonly float fs;
        Rng rng;
        Pink pinkL, pinkR, pinkH;
        Brown brown;
        Svf rushL, rushR, howl1, howl2, roarF;
        OnePole airL, airR;
        Wander gust, gust2, flutter;
        Smooth amount, panS;
        readonly Hit[] hits = new Hit[16];
        float thudPhase, thudAmp, thudFreq;
        int retune;

        struct Hit { public Svf f; public float amp, decay, l, r; }

        public WindSynth(float sampleRate, uint seed = 1)
        {
            fs = sampleRate; rng = new Rng(seed);
            amount.Set(0.6f, fs); panS.Set(0.8f, fs);
            airL.Set(5000f, fs); airR.Set(5000f, fs);
        }

        /// <summary>0..1: how strong the wind sound is now (0 silent)</summary>
        public float Amount => amount.value;
        public static float Intensity(float speed, float start, float full) => Dsp.Clamp01((speed - start) / (full - start));

        public void Render(float[] data, int frames, int channels)
        {
            float target = Intensity(speed, startMs, fullMs);
            for (int i = 0; i < frames; i++)
            {
                float s = amount.Toward(target);
                float p = panS.Toward(pan);
                if (s < 1e-4f) { for (int c = 0; c < channels; c++) data[i * channels + c] = 0f; continue; }
                float g = gust.Next(ref rng, Dsp.Lerp(2.6f, 1.1f, s), fs);
                float g2 = gust2.Next(ref rng, 0.9f, fs);
                float fl = flutter.Next(ref rng, 0.12f, fs);
                if (--retune <= 0)
                {
                    // the filters follow the gusts (every 32 samples is plenty)
                    retune = 32;
                    float bright = s * (0.75f + 0.5f * g);
                    float fc = 260f + 2600f * (float)Math.Pow(bright, 1.3);
                    rushL.Set(fc, 0.55f, fs); rushR.Set(fc * 1.07f, 0.55f, fs);
                    float hf = (330f + 520f * s) * (0.8f + 0.45f * g);
                    howl1.Set(hf, 16f, fs); howl2.Set(hf * (2.25f + 0.3f * g2), 22f, fs);
                    roarF.Set(70f + 170f * s, 0.8f, fs);
                }
                // gusts swing the level more as the wind gets up
                float depth = 0.3f + 0.4f * s;
                float gl = (1f - depth) + depth * 1.7f * g;
                float level = (float)Math.Pow(s, 1.1) * gl;

                // broadband rush, decorrelated left and right, with a little high air
                float nl = pinkL.Next(ref rng), nr = pinkR.Next(ref rng);
                float rl = rushL.BandNorm(0.8f * nl + 0.2f * nr) + 0.25f * airL.High(nl) * s;
                float rr = rushR.BandNorm(0.8f * nr + 0.2f * nl) + 0.25f * airR.High(nr) * s;

                // moaning and whistling round the body and mirrors
                float nh = pinkH.Next(ref rng);
                float hw = (howl1.BandNorm(nh) * (0.6f + 0.4f * g2) + 0.5f * howl2.BandNorm(nh) * g) * (float)Math.Pow(s, 1.4) * 0.55f;

                // the low roar of the vortex, fluttering
                float roar = roarF.Process(brown.Next(ref rng)) * RoarGain(s) * (0.7f + 0.6f * fl);

                // debris strikes on the truck: short bands of noise, more and harder in the core
                float rate = 60f * (float)Math.Pow(Math.Max(0f, s - 0.3f) / 0.7f, 2.0);
                if (rate > 0f && rng.U() < rate / fs) Strike(s);
                float dl = 0f, dr = 0f;
                for (int h = 0; h < hits.Length; h++)
                {
                    if (hits[h].amp < 1e-4f) continue;
                    float x = hits[h].f.BandNorm(rng.N()) * hits[h].amp;
                    hits[h].amp *= hits[h].decay;
                    dl += x * hits[h].l; dr += x * hits[h].r;
                }

                // buffeting: low thumps as gusts slam the body
                if (thudAmp < 1e-3f && s > 0.25f && rng.U() < 2.5f * s * s / fs)
                { thudAmp = 0.5f * s * rng.Range(0.5f, 1f); thudFreq = rng.Range(38f, 65f); thudPhase = 0f; }
                float thud = 0f;
                if (thudAmp > 1e-4f) { thud = (float)Math.Sin(thudPhase) * thudAmp; thudPhase += 6.2832f * thudFreq / fs; thudAmp *= 1f - 18f / fs; }

                Dsp.Pan(p * 0.45f, out float pl, out float pr);
                float common = hw + roar + thud;
                float L = (rl * 0.55f * level + common * gl) * pl * 1.41f + dl;
                float R = (rr * 0.55f * level + common * gl) * pr * 1.41f + dr;
                L = Dsp.Tanh(L); R = Dsp.Tanh(R);
                if (channels == 1) data[i] = 0.5f * (L + R);
                else { data[i * channels] = L; data[i * channels + 1] = R; for (int c = 2; c < channels; c++) data[i * channels + c] = 0.5f * (L + R); }
            }
        }

        static float RoarGain(float s) => 1.3f * s * s;

        void Strike(float s)
        {
            for (int h = 0; h < hits.Length; h++)
            {
                if (hits[h].amp >= 1e-4f) continue;
                hits[h].f.Reset();
                hits[h].f.Set(rng.Range(350f, 3800f), rng.Range(1.5f, 6f), fs);
                hits[h].amp = rng.Range(0.08f, 0.5f) * s;
                hits[h].decay = (float)Math.Exp(-1.0 / (rng.Range(0.004f, 0.045f) * fs));
                Dsp.Pan(rng.N(), out hits[h].l, out hits[h].r);
                return;
            }
        }
    }
}

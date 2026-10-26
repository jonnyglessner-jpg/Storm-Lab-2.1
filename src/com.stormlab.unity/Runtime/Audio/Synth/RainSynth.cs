// Rain (and hail) where the player is: scattered drops ticking on the truck and the grass in light rain, a dense hiss with
// a low roar under it in heavy rain, and hailstones clattering on metal in the hail core. Driven by `rain` and `hail`,
// 0..1, the storm volume's precipitation at the player (StormAudio).
using System;

namespace StormLab.Audio
{
    public sealed class RainSynth : ISynth
    {
        /// <summary>precipitation at the player, 0 (dry) .. 1 (the storm's heaviest)</summary>
        public float rain;
        /// <summary>hail at the player, 0 .. 1</summary>
        public float hail;

        readonly float fs;
        Rng rng;
        Pink pinkL, pinkR, pinkLo;
        Svf hissL, hissR, roar;
        OnePole hpL, hpR;
        Smooth rainS, hailS;
        readonly Drop[] drops = new Drop[48];

        struct Drop { public float phase, freq, glide, amp, decay, noise, l, r; public Svf body; public bool hail; }

        public RainSynth(float sampleRate, uint seed = 2)
        {
            fs = sampleRate; rng = new Rng(seed);
            rainS.Set(1.5f, fs); hailS.Set(0.8f, fs);
            hpL.Set(900f, fs); hpR.Set(900f, fs);
            hissL.Set(4200f, 0.5f, fs); hissR.Set(4700f, 0.5f, fs);
            roar.Set(420f, 0.6f, fs);
        }

        public float Amount => rainS.value;

        public void Render(float[] data, int frames, int channels)
        {
            float tr = Dsp.Clamp01(rain), th = Dsp.Clamp01(hail);
            for (int i = 0; i < frames; i++)
            {
                float r = rainS.Toward(tr), h = hailS.Toward(th);
                if (r < 1e-4f && h < 1e-4f) { for (int c = 0; c < channels; c++) data[i * channels + c] = 0f; continue; }
                // the hiss: many small drops, too many to hear apart; grows faster than the count of distinct drops
                float hissAmt = 0.32f * (float)Math.Pow(r, 0.9);
                float nl = pinkL.Next(ref rng), nr = pinkR.Next(ref rng);
                float L = hissL.BandNorm(hpL.High(nl)) * hissAmt, R = hissR.BandNorm(hpR.High(nr)) * hissAmt;
                // heavy rain's low roar on the ground and the roof
                float lo = roar.BandNorm(pinkLo.Next(ref rng)) * 0.35f * r * r;
                L += lo; R += lo;

                // distinct drops: a few a second in a sprinkle, hundreds in a downpour
                float dropRate = 6f + 900f * (float)Math.Pow(r, 1.6);
                if (r > 0.01f && rng.U() < dropRate / fs) Spawn(false, r);
                float hailRate = 160f * (float)Math.Pow(h, 1.3);
                if (h > 0.01f && rng.U() < hailRate / fs) Spawn(true, h);
                for (int d = 0; d < drops.Length; d++)
                {
                    if (drops[d].amp < 1e-4f) continue;
                    ref var q = ref drops[d];
                    q.phase += 6.2832f * q.freq / fs; q.freq *= q.glide;
                    float tone = (float)Math.Sin(q.phase);
                    float x = (tone * (1f - q.noise) + q.body.BandNorm(rng.N()) * q.noise) * q.amp;
                    q.amp *= q.decay;
                    L += x * q.l; R += x * q.r;
                }
                L = Dsp.Tanh(L); R = Dsp.Tanh(R);
                if (channels == 1) data[i] = 0.5f * (L + R);
                else { data[i * channels] = L; data[i * channels + 1] = R; for (int c = 2; c < channels; c++) data[i * channels + c] = 0.5f * (L + R); }
            }
        }

        void Spawn(bool isHail, float strength)
        {
            for (int d = 0; d < drops.Length; d++)
            {
                if (drops[d].amp >= 1e-4f) continue;
                ref var q = ref drops[d];
                q.hail = isHail; q.phase = 0f; q.body.Reset();
                if (isHail)
                {
                    // a stone on sheet metal: a bright knock with a short ring, now and then a big one
                    bool big = rng.U() < 0.15f;
                    q.freq = big ? rng.Range(700f, 1400f) : rng.Range(1800f, 5200f); q.glide = 1f;
                    q.body.Set(big ? rng.Range(500f, 1200f) : rng.Range(2500f, 7000f), 3f, fs);
                    q.noise = 0.55f;
                    q.amp = rng.Range(0.15f, 0.45f) * (big ? 1.6f : 1f) * (0.5f + 0.5f * strength);
                    q.decay = (float)Math.Exp(-1.0 / (rng.Range(0.012f, big ? 0.07f : 0.035f) * fs));
                }
                else
                {
                    // a raindrop: a falling 'plink' on the truck or a soft tick on the grass
                    bool metal = rng.U() < 0.4f;
                    q.freq = metal ? rng.Range(2200f, 5000f) : rng.Range(1200f, 3000f);
                    q.glide = 1f - rng.Range(0f, 30f) / fs;
                    q.body.Set(rng.Range(1500f, 6000f), 1.2f, fs);
                    q.noise = metal ? 0.3f : 0.75f;
                    q.amp = rng.Range(0.02f, 0.12f) * (0.6f + 0.4f * strength);
                    q.decay = (float)Math.Exp(-1.0 / (rng.Range(0.002f, metal ? 0.012f : 0.006f) * fs));
                }
                Dsp.Pan(rng.N(), out q.l, out q.r);
                return;
            }
        }
    }
}

// Rain (and hail) where the player is: scattered drops ticking on the truck and the grass in light rain, a dense hiss with
// a low roar under it in heavy rain, and in the hail core big stones landing round the truck with blunt thuds, like
// baseballs dropped on the ground. Driven by `rain` and `hail`, 0..1, the storm volume's precipitation at the player
// (StormAudio).
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
        readonly Thud[] thuds = new Thud[24];

        struct Drop { public float phase, freq, glide, amp, decay, noise, l, r; public Svf body; }
        // a hailstone hitting the ground: a short low thump falling in pitch, with a muffled whump of earth under it
        struct Thud { public float phase, freq, glide, amp, decay, dirt, dirtDecay, l, r, bounceAmp; public int bounceIn; public Svf earth; }

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
                if (r > 0.01f && rng.U() < dropRate / fs) Spawn(r);
                // hailstones landing within earshot: a couple a second at the edge of the core, a steady pounding in it
                float hailRate = 2f + 26f * (float)Math.Pow(h, 1.2);
                if (h > 0.01f && rng.U() < hailRate / fs) Land(h);
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
                for (int k = 0; k < thuds.Length; k++)
                {
                    ref var q = ref thuds[k];
                    if (q.bounceIn > 0 && --q.bounceIn == 0) Hit(ref q, q.bounceAmp, 1.25f);
                    if (q.amp < 1e-4f && q.dirt < 1e-4f) continue;
                    q.phase += 6.2832f * q.freq / fs; q.freq *= q.glide;
                    q.earth.Process(rng.N());
                    // (low-passed noise is quiet: about x4 brings the whump up beside the thump)
                    float x = (float)Math.Sin(q.phase) * q.amp + q.earth.low * 4f * q.dirt;
                    q.amp *= q.decay; q.dirt *= q.dirtDecay;
                    L += x * q.l; R += x * q.r;
                }
                L = Dsp.Tanh(L); R = Dsp.Tanh(R);
                if (channels == 1) data[i] = 0.5f * (L + R);
                else { data[i * channels] = L; data[i * channels + 1] = R; for (int c = 2; c < channels; c++) data[i * channels + c] = 0.5f * (L + R); }
            }
        }

        void Spawn(float strength)
        {
            for (int d = 0; d < drops.Length; d++)
            {
                if (drops[d].amp >= 1e-4f) continue;
                ref var q = ref drops[d];
                q.phase = 0f; q.body.Reset();
                // a raindrop: a falling 'plink' on the truck or a soft tick on the grass
                bool metal = rng.U() < 0.4f;
                q.freq = metal ? rng.Range(2200f, 5000f) : rng.Range(1200f, 3000f);
                q.glide = 1f - rng.Range(0f, 30f) / fs;
                q.body.Set(rng.Range(1500f, 6000f), 1.2f, fs);
                q.noise = metal ? 0.3f : 0.75f;
                q.amp = rng.Range(0.02f, 0.12f) * (0.6f + 0.4f * strength);
                q.decay = (float)Math.Exp(-1.0 / (rng.Range(0.002f, metal ? 0.012f : 0.006f) * fs));
                Dsp.Pan(rng.N(), out q.l, out q.r);
                return;
            }
        }

        /// <summary>a hailstone lands somewhere round the truck: near ones loud and fuller, far ones quiet and duller</summary>
        void Land(float strength)
        {
            for (int k = 0; k < thuds.Length; k++)
            {
                ref var q = ref thuds[k];
                if (q.amp >= 1e-4f || q.dirt >= 1e-4f || q.bounceIn > 0) continue;
                float near = rng.U(); near *= near;            // most land a little way off, a few right beside
                float amp = (0.08f + 0.36f * near) * (0.6f + 0.4f * strength) * rng.Range(0.7f, 1.3f);
                q.earth.Reset();
                q.earth.Set(Dsp.Lerp(260f, 750f, near) * rng.Range(0.85f, 1.15f), 0.7f, fs);
                Hit(ref q, amp, 1f);
                // on hard ground a stone sometimes bounces once
                q.bounceIn = rng.U() < 0.3f ? (int)(rng.Range(0.12f, 0.3f) * fs) : 0;
                q.bounceAmp = amp * rng.Range(0.15f, 0.3f);
                Dsp.Pan(rng.N() * 0.9f, out q.l, out q.r);
                return;
            }
        }

        void Hit(ref Thud q, float amp, float pitch)
        {
            q.phase = 0f;
            q.freq = rng.Range(65f, 150f) * pitch;
            q.glide = 1f - rng.Range(4f, 9f) / fs;             // the thump drops about a third over its 50 ms
            q.amp = amp;
            q.decay = (float)Math.Exp(-1.0 / (rng.Range(0.03f, 0.065f) * fs));
            q.dirt = amp * rng.Range(0.6f, 1f);
            q.dirtDecay = (float)Math.Exp(-1.0 / (rng.Range(0.008f, 0.018f) * fs));
        }
    }
}

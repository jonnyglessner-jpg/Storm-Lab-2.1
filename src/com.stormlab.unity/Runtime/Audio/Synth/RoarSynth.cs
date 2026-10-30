// The tornado's roar heard from afar (user, 2026-10-30: "much much meaner", loud a mile away, deafening close by without
// clipping). WindSynth is the wind at the truck, which the Rankine flow makes nothing a mile out; this is the sound the
// vortex itself radiates: the deep rumble of the core, the waterfall body of the roar and the tearing of a violent one,
// surging with the suction vortices. It falls off as 1 / distance from fullLevelM out (-6 dB per doubling), the air takes
// its highs with distance (low-passed), and it fades out altogether by fadeEndM. Close in, the mix is driven harder into
// a soft clipper, so it gets denser and more brutal rather than louder than full scale. Driven by the tornado's
// strength (0..1), its size (0 a rope .. 1 a wide wedge), its distance (m) and the side it is on. Its character follows
// both (user, 2026-10-30): a small one is a waterfall, broad hiss over a thin rumble; a violent one a jet engine, a dense
// low-mid roar with tearing and a wavering whine; a big wedge carries the low frequencies, its rumble down near 30 Hz.
using System;

namespace StormLab.Audio
{
    public sealed class RoarSynth : ISynth
    {
        /// <summary>the tornado's violence, 0..1 (StormAudio: its peak wind)</summary>
        public float strength;
        /// <summary>its size, 0 a rope .. 1 a wide wedge (StormAudio: the funnel's foot, 15 m to 600 m across, in log)</summary>
        public float size;
        /// <summary>distance from the listener to the tornado's foot, m</summary>
        public float distanceM = 1e9f;
        /// <summary>the side it is on, seen from the listener: -1 left .. 1 right</summary>
        public float pan;
        /// <summary>full level this near, m; the roar fades out between fadeStartM and fadeEndM</summary>
        public float fullLevelM = 400f, fadeStartM = 4000f, fadeEndM = 8000f;

        readonly float fs;
        Rng rng;
        Pink pk1, pk2, pk3;
        Brown br1;
        Svf sub, body1, body2, body1R, body2R, tear1, tear2, whine;
        OnePole airL, airR, hissHp;
        Wander surge, turb, flut, gustT, pulse, whineW;
        Smooth amount, nearS, panS;
        int retune;

        public RoarSynth(float sampleRate, uint seed = 3)
        {
            fs = sampleRate; rng = new Rng(seed);
            amount.Set(0.4f, fs); nearS.Set(0.4f, fs); panS.Set(0.8f, fs);
            airL.Set(5000f, fs); airR.Set(5000f, fs);
        }

        /// <summary>0..1: how loud the roar is now (0 silent): strength and distance together</summary>
        public float Amount => amount.value;

        /// <summary>the roar's gain by distance: full inside fullM, 1 / distance beyond, gone by fadeEndM</summary>
        public static float DistanceGain(float d, float fullM, float fadeStartM, float fadeEndM)
            => Math.Min(1f, fullM / Math.Max(d, 1f)) * (1f - Dsp.SmoothStep(fadeStartM, fadeEndM, d));
        /// <summary>the roar's loudness for a tornado's strength (a weak one is already well heard)</summary>
        public static float Loudness(float strength) => (float)Math.Pow(Dsp.Clamp01(strength), 0.6);

        public void Render(float[] data, int frames, int channels)
        {
            float target = Loudness(strength) * DistanceGain(distanceM, fullLevelM, fadeStartM, fadeEndM);
            float nearT = Dsp.Clamp01(fullLevelM / Math.Max(distanceM, 1f));
            float st = Dsp.Clamp01(strength), sz = Dsp.Clamp01(size);
            for (int i = 0; i < frames; i++)
            {
                float s = amount.Toward(target);
                float n = nearS.Toward(nearT);
                float p = panS.Toward(pan);
                if (s < 1e-4f) { for (int c = 0; c < channels; c++) data[i * channels + c] = 0f; continue; }
                float sg = surge.Next(ref rng, 3.5f, fs);     // slow swells of the whole roar
                float tb = turb.Next(ref rng, 0.5f, fs);      // turbulence in the body
                float fl = flut.Next(ref rng, 0.09f, fs);     // fast flutter
                float gt = gustT.Next(ref rng, 0.25f, fs);    // the tearing comes in gusts
                float pu = pulse.Next(ref rng, 0.8f + 0.7f * sz, fs);   // suction vortices passing: about once a second, slower round a wide one
                float ww = whineW.Next(ref rng, 1.6f, fs);              // the jet whine wavers
                if (--retune <= 0)
                {
                    retune = 32;
                    // the air takes the highs with distance: 5 kHz close, about 1 kHz a mile out
                    float fc = Math.Min(5000f, Math.Max(220f, 5000f * (float)Math.Pow(300f / Math.Max(distanceM, 300f), 0.9)));
                    airL.Set(fc, fs); airR.Set(fc, fs);
                    // the rumble sits lower the bigger the vortex: near 65 Hz for a rope, 30 Hz for a wide wedge
                    sub.Set(Dsp.Lerp(62f, 30f, sz) + 8f * st, 0.9f, fs);
                    // the body too: a small funnel's waterfall is up at 160-400 Hz, a wedge's roar down at 80-200 Hz
                    float lowK = Dsp.Lerp(1.5f, 0.75f, sz);
                    float b1 = 105f * lowK * (0.9f + 0.2f * tb), b2 = 250f * lowK * (0.9f + 0.25f * tb);
                    body1.Set(b1, 1.1f, fs); body2.Set(b2, 1.0f, fs);
                    body1R.Set(b1 * 1.06f, 1.1f, fs); body2R.Set(b2 * 0.95f, 1.0f, fs);
                    tear1.Set((650f + 650f * st) * (0.85f + 0.35f * gt), 1.6f, fs);
                    tear2.Set(1500f + 1000f * st, 2.2f, fs);
                    // the jet's whine: a narrow band wavering about 1.3-2.4 kHz, violent vortices only
                    whine.Set(1300f + 1100f * ww, 9f, fs);
                    hissHp.Set(700f, fs);
                }
                // the core's rumble, swelling, heavier the bigger the vortex; the body, a turbulent waterfall; the hiss of a
                // small one; the tearing and whine of a violent one, in gusts; all of it pulsing as the suction vortices
                // come round (more so the stronger it is)
                float rumble = sub.BandNorm(br1.Next(ref rng)) * (0.75f + 0.5f * sg) * Dsp.Lerp(0.3f, 2.2f, sz);
                float n1 = pk1.Next(ref rng), n3 = pk3.Next(ref rng);
                float bodyMod = (0.55f + 0.45f * tb) * (0.85f + 0.3f * fl);
                float bodyL = (body1.BandNorm(n1) + 0.7f * body2.BandNorm(n1)) * bodyMod;
                float bodyR = (body1R.BandNorm(n3) + 0.7f * body2R.BandNorm(n3)) * bodyMod;
                float n2 = pk2.Next(ref rng);
                float hiss = hissHp.High(n2) * (0.6f + 0.35f * tb) * (1f - 0.6f * st) * (1f - 0.6f * sz);
                float tear = (0.8f * tear1.BandNorm(n2) + 0.5f * tear2.BandNorm(n2)) * (0.35f + 0.65f * gt) * (0.3f + 0.7f * st);
                float jet = whine.BandNorm(n2) * (0.5f + 0.5f * gt) * st * st * 0.6f;
                float pulsing = 1f - 0.3f * st + 0.6f * st * pu;
                // far off it is one source: narrow and mono; close in it is all round the truck, the two sides decorrelated
                float width = 0.15f + 0.85f * n;
                float body = 0.5f * (bodyL + bodyR);
                // a violent one's body is denser (the jet's broadband roar): driven a little harder below
                float bodyK = 0.85f + 0.3f * st;
                float mL = airL.Low(Dsp.Lerp(body, bodyL, width) * bodyK + 0.7f * tear + hiss + jet) + 1.2f * rumble;
                float mR = airR.Low(Dsp.Lerp(body, bodyR, width) * bodyK + 0.7f * tear + hiss + jet) + 1.2f * rumble;
                // level by strength and distance, then driven harder the nearer it is, into a soft clipper: denser, not
                // louder (the output is pulled back a little close in, so it sits near -5 dBFS RMS, deafening but with
                // headroom for the wind, the debris strikes and thunder; the master limiter catches what piles on top)
                // (user, 2026-10-30: the first version was too loud, "split the difference": half the level, -6 dB, the
                // same density close in)
                float drive = s * pulsing * (1f + 1.0f * n) * 0.75f;
                float post = (0.9f - 0.25f * n) * 0.5f;
                Dsp.Pan(p * (1f - 0.75f * n) * 0.6f, out float pl, out float pr);
                float L = Dsp.Tanh(mL * drive * pl * 1.41f) * post;
                float R = Dsp.Tanh(mR * drive * pr * 1.41f) * post;
                if (channels == 1) data[i] = 0.5f * (L + R);
                else { data[i * channels] = L; data[i * channels + 1] = R; for (int c = 2; c < channels; c++) data[i * channels + c] = 0.5f * (L + R); }
            }
        }
    }
}

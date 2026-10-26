// The chase truck: a V8 (firing pulses through exhaust and intake resonances, louder and brighter under throttle) on a
// six-speed automatic, tyre squeal when the tyres slide, road roar and wind rush with speed, and a thump when the
// suspension bottoms out.
using System;

namespace StormLab.Audio
{
    /// <summary>A six-speed automatic: engine rpm from road speed and throttle.</summary>
    public sealed class Gearbox
    {
        public static readonly float[] Ratios = { 4.17f, 2.34f, 1.52f, 1.14f, 0.87f, 0.69f };
        public float finalDrive = 3.73f, wheelRadius = 0.42f, idleRpm = 700f, redline = 5600f;
        public int gear { get; private set; }
        public float rpm { get; private set; } = 700f;
        float shiftTimer;

        /// <summary>speed m/s (signed: reverse below 0), throttle 0..1, dt s</summary>
        public void Step(float speed, float throttle, float dt)
        {
            float wheelRpm = Math.Abs(speed) / (2f * (float)Math.PI * wheelRadius) * 60f;
            float up = Dsp.Lerp(1900f, 5200f, throttle), down = Dsp.Lerp(1100f, 2600f, throttle);
            shiftTimer -= dt;
            if (shiftTimer <= 0f)
            {
                float here = wheelRpm * Ratios[gear] * finalDrive;
                if (gear < Ratios.Length - 1 && here > up && speed > 0) { gear++; shiftTimer = 0.45f; }
                else if (gear > 0 && here < down) { gear--; shiftTimer = 0.35f; }
            }
            // the torque converter lets the engine run above the wheels' speed at low speed and full throttle
            float locked = wheelRpm * Ratios[gear] * finalDrive;
            float slip = Dsp.Lerp(idleRpm, 2400f, throttle) * Dsp.Clamp01(1f - locked / 2600f);
            float target = Math.Min(Math.Max(Math.Max(locked, slip), idleRpm + 150f * throttle), redline);
            // the rpm follows quickly, dipping through a shift
            rpm += (target - rpm) * Dsp.Clamp01(dt * (shiftTimer > 0.2f ? 6f : 10f));
        }
    }

    public sealed class TruckSynth : ISynth
    {
        public float rpm = 700f;
        /// <summary>throttle 0..1</summary>
        public float load;
        /// <summary>road speed, m/s</summary>
        public float speed;
        /// <summary>how hard the tyres slide, 0 .. 1</summary>
        public float skid;
        /// <summary>set by the game for one suspension thump of this size, 0..1 (the synth clears it)</summary>
        public float bump;
        public float engineVolume = 1f, tyreVolume = 1f, roadVolume = 1f;

        readonly float fs;
        Rng rng;
        Smooth rpmS, loadS, speedS, skidS;
        Svf exhaust, exhaust2, intake, squeal1, squeal2, squeal3, road, rumbleF;
        OnePole body, air;
        Pink pinkI, pinkR, pinkA;
        Brown brownR;
        Wander sq, sqA;
        float phase, pulse, pulseDecay, bumpPhase, bumpAmp;
        int cylinder, retune;
        readonly float[] cylAmp = new float[8];

        public TruckSynth(float sampleRate, uint seed = 3)
        {
            fs = sampleRate; rng = new Rng(seed);
            rpmS.Set(0.05f, fs); loadS.Set(0.08f, fs); speedS.Set(0.2f, fs); skidS.Set(0.06f, fs);
            rpmS.value = 700f;
            // a cross-plane V8's uneven pulses: each cylinder a little different
            for (int i = 0; i < 8; i++) cylAmp[i] = 0.75f + 0.5f * rng.U();
            pulseDecay = (float)Math.Exp(-1.0 / (0.0025 * fs));
        }

        public void Render(float[] data, int frames, int channels)
        {
            for (int i = 0; i < frames; i++)
            {
                float r = rpmS.Toward(rpm), l = loadS.Toward(load), v = speedS.Toward(speed), k = skidS.Toward(skid);
                if (--retune <= 0)
                {
                    retune = 32;
                    exhaust.Set(70f + r * 0.022f, 2.2f, fs);
                    exhaust2.Set(260f + r * 0.06f, 3f, fs);
                    intake.Set(500f + r * 0.25f, 1.5f, fs);
                    body.Set(900f + 2600f * l + r * 0.3f, fs);
                    road.Set(180f + v * 9f, 0.7f, fs);
                    rumbleF.Set(40f + v * 1.2f, 0.9f, fs);
                    air.Set(400f + v * 25f, fs);
                }
                // ---- engine: four firings a revolution, each a short pulse into the exhaust's resonances
                float fire = r / 60f * 4f;
                phase += fire / fs;
                if (phase >= 1f) { phase -= 1f; cylinder = (cylinder + 1) & 7; pulse += cylAmp[cylinder] * (0.6f + 0.4f * rng.U()); }
                float exc = pulse; pulse *= pulseDecay;
                float ex = exhaust.BandNorm(exc) * 0.9f + exhaust2.BandNorm(exc) * (0.25f + 0.35f * l);
                float rough = body.Low(ex + exc * 0.15f);
                float intakeN = intake.BandNorm(pinkI.Next(ref rng)) * (0.02f + 0.12f * l) * Dsp.Clamp01(r / 4000f);
                float engine = (rough * (0.35f + 0.65f * l) + intakeN) * engineVolume * 0.55f;

                // ---- tyres: road roar and air with speed; squeal while sliding
                float vs = Dsp.Clamp01(v / 40f);
                float roadN = (road.BandNorm(pinkR.Next(ref rng)) * 0.12f + rumbleF.Process(brownR.Next(ref rng)) * 0.15f) * vs * roadVolume;
                float airN = air.Low(pinkA.Next(ref rng)) * 0.12f * vs * vs * roadVolume;
                float squeal = 0f;
                if (k > 1e-3f)
                {
                    float w = sq.Next(ref rng, 0.18f, fs), wa = sqA.Next(ref rng, 0.07f, fs);
                    if (retune == 32)
                    {
                        float f0 = 950f + 500f * w + 300f * Dsp.Clamp01(v / 30f);
                        squeal1.Set(f0, 40f, fs); squeal2.Set(f0 * 2.02f, 50f, fs); squeal3.Set(f0 * 1.51f, 30f, fs);
                    }
                    float n = rng.N();
                    squeal = (squeal1.BandNorm(n) + 0.5f * squeal2.BandNorm(n) + 0.3f * squeal3.BandNorm(n)) * 1.6f * k * (0.6f + 0.6f * wa) * tyreVolume;
                    squeal += road.BandNorm(rng.N()) * 0.1f * k * tyreVolume;   // the scrub under it
                }

                // ---- suspension thump
                if (bump > 0f) { bumpAmp = Math.Max(bumpAmp, Dsp.Clamp01(bump) * 0.6f); bumpPhase = 0f; bump = 0f; }
                float thump = 0f;
                if (bumpAmp > 1e-4f) { thump = (float)Math.Sin(bumpPhase) * bumpAmp; bumpPhase += 6.2832f * 55f / fs; bumpAmp *= 1f - 25f / fs; }

                float x = Dsp.Tanh(engine + roadN + airN + squeal + thump);
                for (int c = 0; c < channels; c++) data[i * channels + c] = x;
            }
        }
    }
}

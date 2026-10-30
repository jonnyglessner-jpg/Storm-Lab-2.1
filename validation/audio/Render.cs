// Renders the storm's procedural sounds (Runtime/Audio/Synth) to WAV files and checks their levels: the wind and rain grow
// with their drivers, thunder falls off with distance, nothing clips or goes NaN.
//   dotnet run --project validation/audio [-- <out dir>]      (default validation/audio/out)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using StormLab.Audio;
using StormLab.Lightning;

static class Render
{
    const int Fs = 48000;
    static int failures;

    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "../../../out");
        Directory.CreateDirectory(dir);
        Wind(dir); Roar(dir); Rain(dir); Thunder(dir); Truck(dir); Ambience(dir); MixLimiter();
        Console.WriteLine(failures == 0 ? "audio checks: all passed" : $"audio checks: {failures} FAILED");
        Environment.Exit(failures == 0 ? 0 : 1);
    }

    static void Check(bool ok, string what) { if (!ok) { failures++; Console.WriteLine("  FAIL " + what); } }

    /// <summary>run a synth for `seconds`, calling drive(t) every 10 ms; stereo</summary>
    static float[] Run(ISynth s, float seconds, Action<float> drive)
    {
        int n = (int)(seconds * Fs), block = Fs / 100;
        var o = new float[n * 2]; var buf = new float[block * 2];
        for (int i = 0; i < n; i += block)
        {
            drive(i / (float)Fs);
            int m = Math.Min(block, n - i);
            s.Render(buf, m, 2);
            Array.Copy(buf, 0, o, i * 2, m * 2);
        }
        return o;
    }

    static float Rms(float[] x, int from, int to) { double e = 0; for (int i = from; i < to; i++) e += x[i] * x[i]; return (float)Math.Sqrt(e / Math.Max(1, to - from)); }
    static float Peak(float[] x) => x.Length == 0 ? 0 : x.Max(v => Math.Abs(v));
    static string Db(float a) => a < 1e-6f ? "  -inf" : $"{20 * Math.Log10(a),6:0.0}";
    static void Sane(float[] x, string what) { Check(x.All(v => !float.IsNaN(v) && !float.IsInfinity(v)), what + ": finite"); Check(Peak(x) <= 1.0001f, what + ": within full scale"); }
    /// <summary>share of samples pushed against full scale (a clipper or soft clipper flattening the waveform)</summary>
    static float Hot(float[] x) => x.Count(v => Math.Abs(v) > 0.95f) / (float)Math.Max(1, x.Length);

    static void Wind(string dir)
    {
        Console.WriteLine("wind: 0 -> 100 m/s over 50 s (RMS dBFS per 10 m/s)");
        var w = new WindSynth(Fs, 7);
        var o = Run(w, 50f, t => { w.speed = t * 2f; w.pan = (float)Math.Sin(t * 0.3); });
        Sane(o, "wind"); Console.WriteLine($"  flattened samples {Hot(o):P3}, peak {Db(Peak(o))}"); Wav(Path.Combine(dir, "wind_sweep_0-100ms.wav"), o, 2, Fs);
        float prev = -1; var line = "";
        for (int v = 0; v < 100; v += 10)
        {
            int a = (int)(v / 2f * Fs) * 2, b = (int)((v + 10) / 2f * Fs) * 2;
            float r = Rms(o, a, b); line += $"  {v}-{v + 10}:{Db(r)}";
            // (gusts are random: allow 1.5 dB either way between neighbouring bands)
            if (v >= 10) Check(r > prev * 0.84f, $"wind louder at {v}-{v + 10} m/s than below");
            prev = r;
        }
        Console.WriteLine(line);
        Check(Rms(o, 0, (int)(1.0f * Fs) * 2) < 1e-4f, "wind silent in calm air");
    }

    static void Roar(string dir)
    {
        // the roar heard from afar: a violent tornado (strength 0.9) approached from 8 km to 40 m over 60 s (distance
        // falling geometrically), and a weak one (0.3) the same way. Loud a mile out, deafening close in, never clipping.
        Console.WriteLine("roar: strength 0.9, 8 km -> 40 m over 60 s (RMS dBFS by distance)");
        var r = new RoarSynth(Fs, 5);
        float DistAt(float t) => 8000f * (float)Math.Pow(40f / 8000f, Math.Min(t / 60f, 1f));
        var o = Run(r, 60f, t => { r.strength = 0.9f; r.size = 0.8f; r.distanceM = DistAt(t); r.pan = 0.3f; });
        Sane(o, "roar"); Console.WriteLine($"  flattened samples {Hot(o):P3}, peak {Db(Peak(o))}"); Wav(Path.Combine(dir, "roar_approach_strength0.9.wav"), o, 2, Fs);
        float prev = -1; var line = ""; float atMile = 0f;
        foreach (var d in new[] { 6400f, 3200f, 1609f, 800f, 400f, 200f, 100f, 50f })
        {
            float t = 60f * (float)(Math.Log(d / 8000f) / Math.Log(40f / 8000f));
            int b = (int)(t * Fs) * 2, a = Math.Max(0, b - Fs * 2);
            float x = Rms(o, a, b); line += $"  {d:0}m:{Db(x)}";
            if (d == 1609f) atMile = x;
            // (inside full level the roar only gets denser: allow it level within 1.5 dB)
            Check(d < 400f ? x > prev * 0.84f : x > prev, $"roar louder at {d} m than farther out"); prev = x;
        }
        Console.WriteLine(line);
        Check(atMile > Dsp.Db(-30f), $"roar at a mile at least -30 dBFS RMS (strength 0.9): {Db(atMile)}");
        Check(Hot(o) < 0.02f, "roar never flattens against full scale for long (under 2 % of samples)");
        // a weak tornado: quieter, but still heard a mile out
        var w = new RoarSynth(Fs, 11);
        var ow = Run(w, 60f, t => { w.strength = 0.3f; w.size = 0.1f; w.distanceM = DistAt(t); w.pan = -0.5f; });
        Sane(ow, "weak roar"); Wav(Path.Combine(dir, "roar_approach_strength0.3.wav"), ow, 2, Fs);
        float tm = 60f * (float)(Math.Log(1609f / 8000f) / Math.Log(40f / 8000f));
        float weakMile = Rms(ow, (int)(tm * Fs) * 2 - Fs * 2, (int)(tm * Fs) * 2);
        Console.WriteLine($"  strength 0.3 at a mile: {Db(weakMile)} dBFS; close: {Db(Rms(ow, ow.Length - Fs * 4, ow.Length))}");
        Check(weakMile > Dsp.Db(-40f) && weakMile < atMile, "a weak tornado's roar is heard a mile out, below a violent one's");

        // character by size and strength (user, 2026-10-30): a rope is a waterfall (hiss over a thin rumble), a violent wedge
        // a jet engine with the low frequencies; measured 300 m out: energy below 90 Hz against above 700 Hz
        float LowShare(float st, float sz, uint seed)
        {
            var c = new RoarSynth(Fs, seed);
            var oc = Run(c, 12f, t => { c.strength = st; c.size = sz; c.distanceM = 300f; });
            Wav(Path.Combine(dir, $"roar_300m_strength{st:0.0}_size{sz:0.0}.wav"), oc, 2, Fs);
            OnePole lp = default, hp = default; lp.Set(90f, Fs); hp.Set(700f, Fs);
            double lo = 0, hi = 0;
            for (int i = 2 * Fs; i < 12 * Fs; i++) { float x = 0.5f * (oc[i * 2] + oc[i * 2 + 1]); float l = lp.Low(x), h = hp.High(x); lo += l * l; hi += h * h; }
            return (float)(10 * Math.Log10(lo / Math.Max(hi, 1e-12)));
        }
        float rope = LowShare(0.25f, 0.05f, 21), cone = LowShare(0.6f, 0.45f, 22), wedge = LowShare(0.95f, 1f, 23);
        Console.WriteLine($"  low (<90 Hz) over high (>700 Hz) at 300 m: rope {rope:0.0} dB, cone {cone:0.0} dB, wedge {wedge:0.0} dB");
        Check(wedge > cone && cone > rope, "the bigger and stronger the tornado, the more of its roar is low");
        Check(wedge - rope > 12f, $"a wedge's roar carries at least 12 dB more low over high than a rope's ({wedge - rope:0.0} dB)");
    }

    static void Rain(string dir)
    {
        Console.WriteLine("rain: 0 -> 1 over 40 s, then hail (RMS dBFS)");
        var r = new RainSynth(Fs, 9);
        var o = Run(r, 48f, t => { r.rain = Math.Min(1f, t / 40f); r.hail = t > 41f ? 0.9f : 0f; });
        Sane(o, "rain"); Console.WriteLine($"  flattened samples {Hot(o):P3}, peak {Db(Peak(o))}"); Wav(Path.Combine(dir, "rain_sweep_then_hail.wav"), o, 2, Fs);
        float prev = -1; var line = "";
        foreach (var lvl in new[] { 0.05f, 0.15f, 0.3f, 0.5f, 0.75f, 1f })
        {
            int at = (int)(lvl * 40f * Fs) * 2, a = Math.Max(0, at - Fs * 2), b = Math.Min(o.Length, at);
            float x = Rms(o, a, b); line += $"  {lvl:0.00}:{Db(x)}";
            Check(x > prev, $"rain louder at {lvl}"); prev = x;
        }
        Console.WriteLine(line + $"   hail:{Db(Rms(o, 43 * Fs * 2, 48 * Fs * 2))}");

        // hail alone, then in light rain: blunt thuds, low and dull (baseballs on the ground), not knocks on metal
        var hr = new RainSynth(Fs, 13);
        var ho = Run(hr, 24f, t => { hr.hail = t < 12f ? 0.3f + 0.7f * t / 12f : 1f; hr.rain = t < 12f ? 0f : 0.25f; });
        Sane(ho, "hail"); Wav(Path.Combine(dir, "hail_thuds.wav"), ho, 2, Fs);
        OnePole lp = default, hp = default; lp.Set(300f, Fs); hp.Set(1500f, Fs);
        double lo = 0, hi = 0;
        for (int i = 0; i < 12 * Fs; i++) { float x = 0.5f * (ho[i * 2] + ho[i * 2 + 1]); float l = lp.Low(x), h = hp.High(x); lo += l * l; hi += h * h; }
        float lowShare = (float)(10 * Math.Log10(lo / Math.Max(hi, 1e-12)));
        Console.WriteLine($"  hail alone: below 300 Hz {lowShare:0.0} dB over above 1.5 kHz, flattened {Hot(ho):P3}, peak {Db(Peak(ho))}, RMS {Db(Rms(ho, 0, 12 * Fs * 2))}");
        Check(lowShare > 10f, "hail is low thuds (energy below 300 Hz well over that above 1.5 kHz)");
        Check(Hot(ho) == 0f, "hail never flattens against full scale");
    }

    static void Thunder(string dir)
    {
        Console.WriteLine("thunder: peak / RMS dBFS by distance, per flash (and synthesis time)");
        var settings = new LightningSettings();
        var kinds = new[] { FlashKind.NegativeGround, FlashKind.NegativeGround, FlashKind.PositiveGround, FlashKind.InCloud, FlashKind.NegativeGround, FlashKind.Crawler };
        var dists = new[] { 100f, 400f, 1500f, 4000f, 8000f, 15000f, 24000f };
        var all = new List<float>();
        for (int k = 0; k < kinds.Length; k++)
        {
            var e = new FlashEvent(k, 10, (uint)(1000 + 7919 * k), kinds[k], 0.4f);
            var flash = LightningFlash.Create(e, settings, new Vector3(0, 0, 0), 0f);
            var line = $"  {kinds[k],-15}";
            float prev = float.MaxValue, prevNear = 0f; string character = "";
            foreach (var d in dists)
            {
                // the observer due east of where the channel comes lowest (a ground strike's attachment), at head height
                var low = flash.Channels[0].points.OrderBy(p => p.Y).First();
                var input = ThunderFlash.From(flash, new Vector3(low.X + d, 1.6f, low.Z));
                var sw = Stopwatch.StartNew();
                var th = ThunderSynth.Synthesize(input);
                sw.Stop();
                if (th == null) { line += $"  {d / 1000:0.#}km: none"; continue; }
                Sane(th.samples, $"thunder {k} at {d} m"); Check(Hot(th.samples) == 0f, $"thunder {k} at {d} m never flattened against full scale");
                float rms = Rms(th.samples, 0, th.samples.Length), pk = Peak(th.samples);
                line += $"  {th.nearestM / 1000:0.#}km:{Db(pk)}/{Db(rms)}" + (th.limitedDb < -0.05f ? $" lim{th.limitedDb:0}" : "") + $" {th.samples.Length / (float)th.sampleRate:0}s";
                Check(pk <= ThunderSynth.Ceiling + 1e-4f, $"thunder {k} at {d} m peaks under the ceiling");
                // (an in-cloud flash's nearest point stays aloft: compare only where the channel is clearly farther)
                if (th.nearestM > prevNear * 1.3f) { Check(rms < prev, $"thunder {k} quieter at {th.nearestM:0} m than nearer"); prev = rms; prevNear = th.nearestM; }
                character = th.character;
                if (d == 1500f || d == 8000f) Wav(Path.Combine(dir, $"thunder_{k}_{kinds[k]}_{d / 1000:0.#}km.wav"), th.samples, 1, th.sampleRate);
                if (d == 1500f || d == 4000f) { all.AddRange(th.samples); all.AddRange(new float[th.sampleRate]); }
            }
            Console.WriteLine(line); Console.WriteLine("      " + character);
        }
        Wav(Path.Combine(dir, "thunder_variety.wav"), all.ToArray(), 1, ThunderSynth.SampleRate);
    }

    static void Truck(string dir)
    {
        Console.WriteLine("truck: idle, full-throttle run to 35 m/s, lift off, slide, brake");
        var s = new TruckSynth(Fs, 3); var g = new Gearbox();
        float v = 0, last = 0; var gears = new List<string>();
        var o = Run(s, 30f, t =>
        {
            float dt = t - last; last = t;
            float throttle = t < 2f ? 0f : t < 16f ? 1f : t < 20f ? 0.2f : 0f;
            v = Math.Max(0, t < 16f ? v + dt * (t < 2f ? 0f : 3.4f * (1f - v / 50f)) : t < 24f ? v - dt * 0.4f : v - dt * 7f);
            g.Step(v, throttle, dt);
            s.rpm = g.rpm; s.load = throttle; s.speed = v;
            s.skid = t > 20f && t < 23f ? 0.9f : t > 24f && v > 1f ? 0.6f : 0f;
            if (Math.Abs(t - 18f) < 0.005f) s.bump = 1f;
            if (gears.Count == 0 || gears[^1] != $"{g.gear + 1}") gears.Add($"{g.gear + 1}");
        });
        Sane(o, "truck"); Console.WriteLine($"  flattened samples {Hot(o):P3}, peak {Db(Peak(o))}"); Wav(Path.Combine(dir, "truck_run_skid.wav"), o, 2, Fs);
        Console.WriteLine($"  gears {string.Join(">", gears)}   idle {Db(Rms(o, 0, 2 * Fs * 2))}  full throttle {Db(Rms(o, 10 * Fs * 2, 14 * Fs * 2))}  slide {Db(Rms(o, 21 * Fs * 2, 23 * Fs * 2))}");
        Check(gears.Count >= 4, "the gearbox shifts up through the run");
    }

    static void Ambience(string dir)
    {
        Console.WriteLine("ambience: birds and breeze");
        var birds = new List<float>();
        foreach (BirdKind k in Enum.GetValues(typeof(BirdKind)))
            for (uint s = 1; s <= 3; s++)
            {
                var c = AmbienceSynth.Bird(k, s); Sane(c, $"bird {k} {s}");
                birds.AddRange(c); birds.AddRange(new float[AmbienceSynth.BirdRate / 2]);
            }
        Wav(Path.Combine(dir, "birds.wav"), birds.ToArray(), 1, AmbienceSynth.BirdRate);
        var b = new BreezeSynth(Fs); var o = Run(b, 10f, _ => { }); Sane(o, "breeze");
        Wav(Path.Combine(dir, "breeze.wav"), o, 2, Fs);
        Console.WriteLine($"  breeze {Db(Rms(o, 0, o.Length))} dBFS");
    }

    static void MixLimiter()
    {
        Console.WriteLine("mix limiter: rumble with claps up to 4x full scale, in 1024-frame blocks");
        var lim = new Limiter(Fs); var rng = new Rng(5);
        int n = 6 * Fs, block = 1024; var o = new float[n * 2]; var buf = new float[block * 2];
        for (int i = 0; i < n; i += block)
        {
            int m = Math.Min(block, n - i);
            for (int j = 0; j < m; j++)
            {
                float t = (i + j) / (float)Fs, clap = (t % 1.5f) < 0.08f ? 4f * (1f - (t % 1.5f) / 0.08f) : 0f;
                float x = 0.3f * (float)Math.Sin(2 * Math.PI * 60 * t) + clap * rng.N() * 0.5f;
                buf[j * 2] = x; buf[j * 2 + 1] = x * 0.8f;
            }
            lim.Process(buf, m, 2); Array.Copy(buf, 0, o, i * 2, m * 2);
        }
        float pk = Peak(o), deepest = lim.Deepest;
        Console.WriteLine($"  peak {Db(pk)} dBFS (ceiling {Db(lim.ceiling)}), deepest cut {20 * Math.Log10(deepest):0.0} dB, flattened {Hot(o):P3}");
        Check(pk <= lim.ceiling * 1.01f, "the mix limiter holds the ceiling");
        Check(Hot(o) == 0f, "the mix limiter never flattens the waveform");
        Check(Rms(o, 0, (int)(0.02f * Fs) * 2) > 0.1f, "the mix limiter passes quiet sound untouched");
    }

    static void Wav(string path, float[] x, int channels, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = x.Length * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + bytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate);
        w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(bytes);
        foreach (var v in x) w.Write((short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767f));
    }
}

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
        Wind(dir); Rain(dir); Thunder(dir); Truck(dir); Ambience(dir);
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

    static void Wind(string dir)
    {
        Console.WriteLine("wind: 0 -> 100 m/s over 50 s (RMS dBFS per 10 m/s)");
        var w = new WindSynth(Fs, 7);
        var o = Run(w, 50f, t => { w.speed = t * 2f; w.pan = (float)Math.Sin(t * 0.3); });
        Sane(o, "wind"); Wav(Path.Combine(dir, "wind_sweep_0-100ms.wav"), o, 2, Fs);
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

    static void Rain(string dir)
    {
        Console.WriteLine("rain: 0 -> 1 over 40 s, then hail (RMS dBFS)");
        var r = new RainSynth(Fs, 9);
        var o = Run(r, 48f, t => { r.rain = Math.Min(1f, t / 40f); r.hail = t > 41f ? 0.9f : 0f; });
        Sane(o, "rain"); Wav(Path.Combine(dir, "rain_sweep_then_hail.wav"), o, 2, Fs);
        float prev = -1; var line = "";
        foreach (var lvl in new[] { 0.05f, 0.15f, 0.3f, 0.5f, 0.75f, 1f })
        {
            int at = (int)(lvl * 40f * Fs) * 2, a = Math.Max(0, at - Fs * 2), b = Math.Min(o.Length, at);
            float x = Rms(o, a, b); line += $"  {lvl:0.00}:{Db(x)}";
            Check(x > prev, $"rain louder at {lvl}"); prev = x;
        }
        Console.WriteLine(line + $"   hail:{Db(Rms(o, 43 * Fs * 2, 48 * Fs * 2))}");
    }

    static void Thunder(string dir)
    {
        Console.WriteLine("thunder: peak / RMS dBFS by distance, per flash (and synthesis time)");
        var settings = new LightningSettings();
        var kinds = new[] { FlashKind.NegativeGround, FlashKind.NegativeGround, FlashKind.PositiveGround, FlashKind.InCloud, FlashKind.NegativeGround, FlashKind.Crawler };
        var dists = new[] { 400f, 1500f, 4000f, 8000f, 15000f, 24000f };
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
                Sane(th.samples, $"thunder {k} at {d} m");
                float rms = Rms(th.samples, 0, th.samples.Length), pk = Peak(th.samples);
                line += $"  {th.nearestM / 1000:0.#}km:{Db(pk)}/{Db(rms)} {th.samples.Length / (float)th.sampleRate:0}s {sw.ElapsedMilliseconds}ms";
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
        Sane(o, "truck"); Wav(Path.Combine(dir, "truck_run_skid.wav"), o, 2, Fs);
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

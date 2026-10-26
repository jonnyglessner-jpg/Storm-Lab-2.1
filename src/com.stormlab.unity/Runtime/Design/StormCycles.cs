// C# port of StormLab's seeded storm cycles (src/storm3d/cycles.ts) and its PRNG (src/physics/random.ts).
// Seeded, illustrative storm stories: visual scenarios, not tornado forecasts.
using System;

namespace StormLab
{
    public enum TornadoShape { Rope, Cone, Stovepipe, Wedge, Multivortex }

    /// <summary>mulberry32, as random.ts: the same seed gives the same numbers as the web build.</summary>
    public sealed class Mulberry
    {
        uint s;
        public Mulberry(int seed) { s = unchecked((uint)seed); }
        public double Next()
        {
            unchecked
            {
                s += 0x6D2B79F5u;
                uint t = (s ^ (s >> 15)) * (1u | s);
                t = (t + ((t ^ (t >> 7)) * (61u | t))) ^ t;
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }
    }

    public sealed class StormCycle
    {
        public double start, end;
        /// <summary>Actual storm minutes at canonical lifecycle milestones.</summary>
        public double[] times;
        public TornadoShape shape;
        public bool tornadic;
        public double radius, topRatio, condensation, dust, lean, phase;
    }

    public static class StormCycles
    {
        static readonly TornadoShape[] HighBase = { TornadoShape.Rope, TornadoShape.Cone, TornadoShape.Stovepipe, TornadoShape.Rope, TornadoShape.Multivortex };
        static readonly TornadoShape[] LowBase = { TornadoShape.Rope, TornadoShape.Cone, TornadoShape.Stovepipe, TornadoShape.Wedge, TornadoShape.Multivortex };

        public static StormCycle[] Make(int seed, bool potential, double radius, double baseKm)
        {
            var random = new Mulberry(seed);
            double Between(double a, double b) => a + (b - a) * random.Next();
            int count = 2 + (int)Math.Floor(random.Next() * 3);
            var cycles = new StormCycle[count];
            double start = Between(28, 34);
            for (int i = 0; i < count; i++)
            {
                var shapes = baseKm > 1.9 ? HighBase : LowBase;
                var shape = shapes[(int)Math.Floor(random.Next() * shapes.Length)];
                // Avoid repeating a silhouette in consecutive cycles.
                if (i > 0 && shape == cycles[i - 1].shape) shape = shapes[(Array.IndexOf(shapes, shape) + 1) % shapes.Length];
                bool tornadic = potential && (i == 0 || random.Next() > 0.25);
                bool multi = shape == TornadoShape.Multivortex;
                double form = start + Between(13, 20);
                double mature = form + Between(1.5, 3.5);
                double rope = mature + (shape == TornadoShape.Rope ? Between(2, 5) : Between(5, 15));
                double thin = rope + Between(3, 7);
                double gone = thin + Between(2, 5);
                double fade = gone + 2;
                double end = fade + Between(4, 7);
                double scale = shape == TornadoShape.Rope ? Between(0.35, 0.7) : shape == TornadoShape.Cone ? Between(0.7, 1.3)
                    : shape == TornadoShape.Stovepipe ? Between(0.85, 1.6) : Between(1.5, 3.2);
                double r = C.Clamp(radius * scale, shape == TornadoShape.Rope ? 0.025 : 0.055, shape == TornadoShape.Rope ? 0.075 : 0.65);
                var c = new StormCycle
                {
                    start = start, end = end, shape = shape, tornadic = tornadic, radius = r,
                    // Every interpolation interval is strictly positive, including very brief ropes.
                    times = multi
                        ? new[] { start, start + 7, form, mature, mature + (rope - mature) * 0.38, rope, thin, gone, fade, end }
                        : new[] { start, start + 7, form, mature, rope, thin, gone, fade, end }
                };
                // (the draws below come in the same order as the object literal in cycles.ts)
                c.topRatio = shape == TornadoShape.Stovepipe ? Between(1.05, 1.3) : shape == TornadoShape.Wedge ? Between(1.1, 1.5)
                    : shape == TornadoShape.Cone ? Between(2.5, 3.8) : Between(1.8, 2.8);
                c.condensation = C.Clamp(1.18 - baseKm * 0.16 + Between(-0.08, 0.1), 0.65, 1);
                c.dust = Between(0.35, 1); c.lean = Between(0.15, 0.85); c.phase = Between(0, Math.PI * 2);
                cycles[i] = c;
                start = end + Between(1, 5);
            }
            return cycles;
        }

        static readonly double[] CanonicalMulti = { 0, 7, 15, 17.5, 23, 32, 39, 45, 46, 52 };
        static readonly double[] Canonical = { 0, 7, 18, 20.5, 24, 29, 31.5, 33, 38 };

        /// <summary>Map an irregular cycle onto the established smooth animation, preserving its feature ordering.</summary>
        public static double CycleTime(StormCycle cycle, double t)
        {
            var canonical = cycle.shape == TornadoShape.Multivortex ? CanonicalMulti : Canonical;
            if (t <= cycle.start) return 0;
            for (int i = 1; i < cycle.times.Length; i++)
            {
                if (t < cycle.times[i])
                {
                    double u = (t - cycle.times[i - 1]) / (cycle.times[i] - cycle.times[i - 1]);
                    return canonical[i - 1] + u * (canonical[i] - canonical[i - 1]);
                }
            }
            return canonical[canonical.Length - 1];
        }
    }
}

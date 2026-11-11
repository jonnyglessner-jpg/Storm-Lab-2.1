using System.Collections.Generic;
// Dumps the C# port's parameter block in the same layout as dump_web.ts, for the parity check.
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using StormLab;

static class DumpCs
{
    static string F(double v) => double.IsNaN(v) ? "null" : v.ToString("R", CultureInfo.InvariantCulture);
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--tornadoes") { Tornadoes(); return; }
        if (args.Length > 0 && args[0] == "--scenario") { Scenarios(); return; }
        if (args.Length > 0 && args[0] == "--life") { Life(); return; }
        if (args.Length > 0 && (args[0] == "--wedges" || args[0] == "--shapes")) { Shapes(); return; }
        if (args.Length > 0 && args[0] == "--slots") { Slots(); return; }
        if (args.Length > 0 && args[0] == "--runup") { RunUp(); return; }
        if (args.Length > 0 && args[0] == "--gameframes") { GameFrames(args[1], int.Parse(args[2]), int.Parse(args[3]), double.Parse(args[4], CultureInfo.InvariantCulture), double.Parse(args[5], CultureInfo.InvariantCulture)); return; }
        if (args.Length > 0 && args[0] == "--spin") { Spin(args.Length > 1 ? args[1] : "plains"); return; }
        if (args.Length > 0 && args[0] == "--rise") { Rise(); return; }
        if (args.Length > 0 && args[0] == "--structure") { Structure(); return; }
        if (args.Length > 0 && args[0] == "--lcl") { Lcl(); return; }
        if (args.Length > 0 && args[0] == "--wallforms") { WallForms(); return; }
        if (args.Length > 0 && args[0] == "--track") { Track(args.Length > 1 ? args[1] : null); return; }
        if (args.Length > 0 && args[0] == "--anti")
        {
            // game storms have no anticyclonic side (StormGame.Adapt, user 2026-10-27): storm minutes with an anti-mesocyclone
            // lowering (S_anti.z) or an anticyclonic tornado (S_anti.w) in the web build's plans and the game's
            var F = new StormFrame(); bool ok = true;
            foreach (var pre in StormPreset.All)
            {
                double wm = 0, wt = 0, gm = 0, gt = 0;
                for (int seed = 0; seed <= 120; seed++)
                {
                    var P = StormDesign.Plan(pre, seed == 0 ? (int?)null : seed);
                    foreach (var (plan, game) in new[] { (P, false), (StormGame.Adapt(P), true) })
                        for (double t = 0; t <= plan.duration; t += 0.1)
                        {
                            StormDesign.At(plan, t, 0, F);
                            double m = F.param[19 * 4 + 2] > 0 ? 0.1 : 0, tt = F.param[19 * 4 + 3] > 0 ? 0.1 : 0;
                            if (game) { gm += m; gt += tt; } else { wm += m; wt += tt; }
                        }
                }
                ok &= gm == 0 && gt == 0;
                Console.WriteLine($"{pre.key}: web anti-meso {wm:0} min, anticyclonic tornado {wt:0} min; game {gm:0} / {gt:0} min");
            }
            Console.WriteLine(ok ? "PASS" : "FAIL");
            return;
        }
        if (args.Length > 0 && args[0] == "--wall")
        {
            // the wall cloud's radius (S_wall.z) and lowering (S_wall2.x), km, where it is at least half formed
            foreach (var pre in StormPreset.All)
            {
                var rs = new System.Collections.Generic.List<double>(); var ls = new System.Collections.Generic.List<double>();
                for (int seed = 1; seed <= 60; seed++)
                {
                    var P = StormDesign.Plan(pre, seed); var F = new StormFrame();
                    for (double t = 0; t <= P.duration; t += 1)
                    {
                        StormDesign.At(P, t, 0, F);
                        if (F.param[6 * 4 + 3] >= 0.5) { rs.Add(F.param[6 * 4 + 2]); ls.Add(F.param[7 * 4]); }
                    }
                }
                rs.Sort(); ls.Sort();
                double Q(System.Collections.Generic.List<double> v, double q) => v.Count == 0 ? double.NaN : v[(int)(q * (v.Count - 1))];
                Console.WriteLine($"{pre.key,-7} radius km 5/50/95%: {Q(rs, 0.05):0.00} {Q(rs, 0.5):0.00} {Q(rs, 0.95):0.00} (min {Q(rs, 0):0.00} max {Q(rs, 1):0.00}); lowering km: {Q(ls, 0.05):0.00} {Q(ls, 0.5):0.00} {Q(ls, 0.95):0.00}");
            }
            return;
        }
        if (args.Length > 0 && args[0] == "--tilt")
        {
            // the game's tilted updraft (StormTilt): its slope against the web build's, how far it carries the rain and
            // hail, and the clear air round the base and a tornado, at each setup's first tornado (or mature cycle)
            foreach (var pre in StormPreset.All)
            {
                var P = StormGame.Plan(pre.key, pre, 1, null); var F = new StormFrame();
                double t = P.cycles != null ? P.cycles[0].times[3] : 50;
                StormDesign.At(P, t, 0, F);
                var tors = StormTornadoes.Build(P, StormTornadoes.RegionOf(pre.key));
                StormTornadoes.Apply(P, tors, t, F);
                double tilt0 = Math.Sqrt(P.tilt.x * P.tilt.x + P.tilt.y * P.tilt.y);
                var (rain, hail) = StormTilt.Shift(P);
                var (baseR, torR) = StormTilt.Clear(P, F);
                Console.WriteLine($"{pre.key,-7} shear6 {tilt0 / 0.1 * 25:0} m/s, updraft {P.w:0} m/s, hp {P.hpness:0.00}: slope {StormTilt.Slope(P):0.00} (web {tilt0:0.00}); " +
                                  $"rain {rain:0.0} km and hail {hail:0.0} km further off; rain-free base {baseR:0.0} km, tornado clear {torR:0.00} km (EF{F.param[24 * 4 + 2]:0})");
            }
            return;
        }
        if (args.Length > 0 && args[0] == "--motion")
        {
            foreach (var pre in StormPreset.All)
            {
                var P = StormDesign.Plan(pre, 1);
                double az = (Math.Atan2(P.motion.x, P.motion.y) * 180 / Math.PI + 360) % 360, sp = Math.Sqrt(P.motion.x * P.motion.x + P.motion.y * P.motion.y);
                string mature = P.cycles != null ? $"first wall cloud {P.cycles[0].start:0.0} min" : P.kind.ToString();
                Console.WriteLine($"{pre.key,-7} {P.kind,-9} {StormDiag.Make(StormLab.Environment.Make(pre.ToEnv())).precip,-9} moves toward {az:0} deg at {sp:0.0} m/s ({sp * 0.06:0.00} km/min); {mature}");
            }
            return;
        }
        var sb = new StringBuilder("{");
        bool first = true;
        // the parity dump: the web build's own presets, by its keys
        foreach (var pre in StormPreset.Website)
        foreach (var seed in new int?[] { null, 42, 7, 2026 })
        {
            var P = StormDesign.Plan(pre, seed);
            if (!first) sb.Append(','); first = false;
            sb.Append($"\"{pre.key}|{(seed.HasValue ? seed.Value.ToString() : "teach")}\":{{\"duration\":{F(P.duration)},\"kind\":\"{P.kind.ToString().ToLowerInvariant()}\",\"cycles\":");
            sb.Append(P.cycles == null ? "null" : "[" + string.Join(",", P.cycles.Select(c => $"\"{c.shape.ToString().ToLowerInvariant()}\"")) + "]");
            sb.Append(",\"frames\":[");
            bool ff = true;
            for (double t = 0; t <= P.duration; t += 1.7)
            {
                var f = StormDesign.At(P, t, 123.4);
                if (!ff) sb.Append(','); ff = false;
                sb.Append($"{{\"t\":{F(t)},\"p\":[{string.Join(",", f.param.Take(StormDesign.NSLOT * 4).Select(x => F(x)))}],\"stage\":\"{f.stage}\"}}");
            }
            sb.Append("]}");
        }
        sb.Append('}');
        Console.Out.Write(sb.ToString());
    }

    // The tornado characters StormTornadoes gives a few storms (a readable list, for review; not part of the parity check)
    static void Tornadoes()
    {
        foreach (var key in new[] { "plains", "dixie", "lp", "ohio" })
        {
            var pre = StormPreset.All.First(p => p.key == key);
            for (int seed = 1; seed <= 6; seed++)
            {
                var P = StormGame.Adapt(StormDesign.Plan(pre, seed));
                var ch = StormTornadoes.Build(P, StormTornadoes.RegionOf(key));
                if (ch == null) continue;
                for (int k = 0; k < ch.Length; k++)
                {
                    var t = ch[k];
                    if (t == null) { Console.WriteLine($"{key,-6} seed {seed} cycle {k}: no tornado"); continue; }
                    string phases = string.Join(" > ", t.phases.Select(p => p.shape.ToString()));
                    Console.WriteLine($"{key,-6} seed {seed} cycle {k}: web {t.webShape,-11} -> {t.shape,-13} EF{t.ef} {t.vmax:0} m/s, path {t.widthM:0} m, ground {t.groundKm * 1000:0} m top {t.topKm * 1000:0} m, " +
                        $"{t.skin} albedo {t.albedo:0.00}, {t.soilName} soil, sub-vortices {t.subVortices}{(t.collar ? ", collar" : "")}{(t.satellite ? ", satellite" : "")}; life {t.LifeMin:0.0} min: {phases}");
                }
            }
        }
    }

    /// <summary>
    /// A game storm's tornado slots, frame by frame (--gameframes preset gameSeed storm fromGameMin toGameMin): storm time,
    /// stage, life fraction, the funnel's ground and top radius (m), and the detailed tornado's core, multi-vortex amount,
    /// breakdown and mode (S_tor4), as the game draws them (its plan, rated tornadoes, life).
    /// </summary>
    static void GameFrames(string key, int gameSeed, int storm, double g0, double g1)
    {
        var pre = StormPreset.Find(key);
        var sc = new StormScenario(pre, gameSeed, 80467.2 / 2);
        var st = sc.storms[storm];
        var F = new StormFrame();
        Console.WriteLine($"{key} game {gameSeed} storm {storm + 1}: seed {st.seed}; " + string.Join(", ", st.tornadoes.Select((t, i) => t == null ? $"{i}: none" : $"{i}: EF{t.ef} {t.shape} ground {t.groundKm * 1000:0} top {t.topKm * 1000:0} m, form {t.formMin:0.0}, rope {t.ropeMin:0.0}, gone {t.goneMin:0.0}, sub {t.subVortices}, phases {string.Join(">", t.phases.Select(p => p.shape + "@" + p.startFrac.ToString("0.00")))}")));
        Console.WriteLine("game min | storm min | stage | ground m | top m | core m | multi | breakdown | mode");
        for (double g = g0; g <= g1 + 1e-9; g += (g1 - g0) / 40)
        {
            double t = st.StormMinutes(g, sc.pace);
            StormDesign.At(st.plan, t, 0, F); StormTornadoes.Apply(st.plan, st.tornadoes, t, F); StormLife.Apply(st.plan, t, F);
            var a = F.param;
            Console.WriteLine($"{g:0.000} | {t:0.000} | {F.stage} | {a[14 * 4] * 1000:0} | {a[14 * 4 + 1] * 1000:0} | {a[21 * 4] * 1000:0} | {a[21 * 4 + 1]:0.00} | {a[21 * 4 + 2]:0.00} | {a[21 * 4 + 3]:0.00}");
        }
    }

    /// <summary>
    /// The game's plan beside the web build's (StormGame.Adapt), 300 seeds per setup: multiple-vortex cycles left, and the
    /// mean storm minutes per cycle each stage is shown, sampled every 0.05 min (the clear slot and the wrap should take
    /// half as long, every tornado stage as long as before).
    /// </summary>
    static void RunUp()
    {
        string[] watch = { "Wall cloud forming", "New wall cloud", "Clear slot opening", "Updraft wrapping around the clear slot", "Mature mesocyclone", "Non-tornadic cycle", "Occlusion" };
        foreach (var pre in StormPreset.All)
        {
            int multiWeb = 0, multiGame = 0, cycles = 0; double durWeb = 0, durGame = 0;
            var web = new System.Collections.Generic.Dictionary<string, double>(); var game = new System.Collections.Generic.Dictionary<string, double>();
            var shapes = new System.Collections.Generic.Dictionary<string, int>();
            var F = new StormFrame();
            for (int seed = 1; seed <= 300; seed++)
            {
                var P = StormDesign.Plan(pre, seed); var Q = StormGame.Adapt(P);
                if (P.cycles == null) continue;
                cycles += P.cycles.Length; durWeb += P.duration; durGame += Q.duration;
                for (int i = 0; i < P.cycles.Length; i++)
                {
                    if (P.cycles[i].shape == TornadoShape.Multivortex) { multiWeb++; string k = Q.cycles[i].shape.ToString(); shapes[k] = shapes.TryGetValue(k, out int v) ? v + 1 : 1; }
                    if (Q.cycles[i].shape == TornadoShape.Multivortex) multiGame++;
                }
                foreach (var (plan, acc) in new[] { (P, web), (Q, game) })
                    for (double t = 0; t <= plan.duration; t += 0.05)
                    {
                        StormDesign.At(plan, t, 0, F);
                        string st = F.stage.EndsWith(" tornado") || F.stage.Contains("tornado") || F.stage.Contains("vortex") || F.stage.Contains("wedge") || F.stage.Contains("Wedge") ? "(tornado on the ground)" : F.stage;
                        acc[st] = (acc.TryGetValue(st, out double a) ? a : 0) + 0.05;
                    }
            }
            Console.WriteLine($"{pre.key}: {cycles} cycles in 300 storms; multiple-vortex cycles {multiWeb} -> {multiGame} (now {string.Join(", ", shapes.Select(kv => kv.Key + " " + kv.Value))}); mean storm {durWeb / 300:0.0} -> {durGame / 300:0.0} min");
            Console.WriteLine("| stage | web build, min per cycle | game | share |");
            Console.WriteLine("|---|---|---|---|");
            foreach (var st in watch.Concat(new[] { "(tornado on the ground)" }))
            {
                double a = web.TryGetValue(st, out double x) ? x / cycles : 0, b = game.TryGetValue(st, out double y) ? y / cycles : 0;
                Console.WriteLine($"| {st} | {a:0.00} | {b:0.00} | {(a > 0 ? b / a : 0):0.00} |");
            }
            Console.WriteLine();
        }
    }

    /// <summary>
    /// How fast the tornado's textures are seen to move up and down, m/s (+ up), on a game's clock (as --spin): the funnel's
    /// skin half way up and the dust column 100 m up, sampled in units of their radius. The old way rose as
    /// (z - v x clock) / r, so a radius that changed slid the texture by clock x d(1 / r); StormSpin's rises (S_spin5) as
    /// z / r - rise. Per tornado stage, the fastest downward and upward speeds each way. The fix holds if the new speeds stay
    /// near the rise itself (20 m/s skin, 15 m/s dust) plus the funnel's own stretch (z / r x dr/dt).
    /// </summary>
    static void Rise()
    {
        double pace = StormScenario.DefaultPace, dt = 0.25;
        bool ok = true;
        Console.WriteLine("| storm | stage | storm min | skin old down / up | skin new down / up | dust old down / up | dust new down / up |");
        Console.WriteLine("|---|---|---|---|---|---|---|");
        foreach (var pre in StormPreset.All)
        {
            var region = StormTornadoes.RegionOf(pre.key);
            int found = 0;
            for (int seed = 1; seed <= 60 && found < 2; seed++)
            {
                var P = StormGame.Adapt(StormDesign.Plan(pre, seed));
                var ch = StormTornadoes.Build(P, region);
                if (ch == null || !ch.Any(c => c != null)) continue;
                found++;
                var F = new StormFrame(); var spin = new StormSpin();
                double prevC = double.NaN, prevRm = 0, prevDr = 0, prevRs = 0, prevRd = 0;
                string stage = null; double t0 = 0;
                double[] ext = new double[8];
                void Flush(double t1)
                {
                    if (stage == null || !stage.Contains("ornado")) return;
                    Console.WriteLine($"| {pre.key} {seed} | {stage} | {t0:0.0}-{t1:0.0} | {ext[0]:0} / {ext[1]:0} | {ext[2]:0} / {ext[3]:0} | {ext[4]:0} / {ext[5]:0} | {ext[6]:0} / {ext[7]:0} |");
                    // the new speeds: the rise plus the stretch; a slide of the old kind would be hundreds of m/s
                    ok &= ext[2] > -60 && ext[3] < 80 && ext[6] > -60 && ext[7] < 80;
                }
                for (double clock = 0; ; clock += dt)
                {
                    double t = clock * pace / 60;
                    if (t > P.duration) { Flush(t); break; }
                    StormDesign.At(P, t, clock, F); StormTornadoes.Apply(P, ch, t, F); StormLife.Apply(P, t, F);
                    spin.Advance(F, t, clock);
                    var a = F.param;
                    bool tor = a[13 * 4 + 2] > 0;
                    double rm = Math.Max(0.5 * (a[14 * 4] + a[14 * 4 + 1]), 0.02);
                    double bd = a[21 * 4 + 3] > 0.5 ? a[21 * 4 + 2] : 0.0;
                    double dr = Math.Max(a[14 * 4] * a[23 * 4 + 3] * (1.0 - 0.7 * bd) + 0.06, 0.08);
                    double rs = spin.Rise(0), rd = spin.Rise(2);
                    if (F.stage != stage) { Flush(t); stage = F.stage; t0 = t; Array.Clear(ext, 0, ext.Length); }
                    if (tor && !double.IsNaN(prevC))
                    {
                        // where the texture at height z last frame is now: the height with the same noise coordinate
                        double zs = Math.Max(0.5 * (a[1 * 4] - 0.6 * a[7 * 4]), 0.15), zd = 0.1;
                        double oldS = ((zs - 0.02 * prevC) / prevRm * rm + 0.02 * clock - zs) / dt * 1000;
                        double newS = ((zs / prevRm - prevRs + rs) * rm - zs) / dt * 1000;
                        double oldD = ((zd - 0.015 * prevC) / prevDr * dr + 0.015 * clock - zd) / dt * 1000;
                        double newD = ((zd / prevDr - prevRd + rd) * dr - zd) / dt * 1000;
                        double[] v = { oldS, newS, oldD, newD };
                        for (int i = 0; i < 4; i++) { ext[2 * i] = Math.Min(ext[2 * i], v[i]); ext[2 * i + 1] = Math.Max(ext[2 * i + 1], v[i]); }
                    }
                    prevC = clock; prevRm = rm; prevDr = dr; prevRs = rs; prevRd = rd;
                }
            }
        }
        Console.WriteLine(ok ? "rise: OK" : "rise: FAIL (a texture slides faster than its rise and stretch allow)");
    }

    /// <summary>
    /// The speed things are seen to turn, rad/s, the web build's way (angle = rate x clock, so seen speed = d(rate x clock)/dt)
    /// and StormSpin's (the rate), through the life of a storm with a wedge, on a game's clock (real time, life cycle at
    /// StormScenario's pace, wall clock from storm time 0). Per stage: the mesocyclone, the funnel's skin and the suction
    /// vortices' orbit, and how much of the stage the web build's skin turned backwards (clockwise). "now" is StormSpin's
    /// real-world rate. Then every feature's real rate as seconds per turn, per stage (tornado parts while one is down).
    /// </summary>
    static void Spin(string key)
    {
        var pre = StormPreset.Find(key); var region = StormTornadoes.RegionOf(key);
        double pace = StormScenario.DefaultPace, dt = 0.25;
        int found = 0;
        for (int seed = 1; seed <= 400 && found < 2; seed++)
        {
            var P = StormGame.Adapt(StormDesign.Plan(pre, seed));
            var ch = StormTornadoes.Build(P, region);
            if (ch == null || !ch.Any(c => c != null && c.shape == FunnelShape.Wedge && c.subVortices > 0)) continue;
            found++;
            Console.WriteLine($"{key} seed {seed}: " + string.Join(", ", ch.Select((c, i) => c == null ? $"cycle {i + 1} none" : $"cycle {i + 1} {c.shape} EF{c.ef}{(c.subVortices > 0 ? $" ({c.subVortices} vortices)" : "")}")));
            Console.WriteLine("| stage | storm min | meso web | meso now | skin web | skin now | orbit web | orbit now | skin web backwards |");
            Console.WriteLine("|---|---|---|---|---|---|---|---|---|");
            var F = new StormFrame(); var spin = new StormSpin();
            string stage = null; double t0 = 0; int n = 0, back = 0;
            double sMw = 0, sMn = 0, sSw = 0, sSn = 0, sOw = 0, sOn = 0;
            var all = new System.Collections.Generic.List<string>(); var sum = new double[13]; int nt = 0;
            string[] names = { "meso", "skin", "core", "wisps", "orbit", "dust", "wall", "RFD lip", "anti-meso", "satellite", "anti tor", "wall ring", "wall junction" };
            string Turn(double r) => r <= 1e-9 ? "-" : 2 * Math.PI / r >= 120 ? $"{2 * Math.PI / r / 60:0.0} min" : $"{2 * Math.PI / r:0.0} s";
            double prevM = double.NaN, prevS = 0, prevO = 0, prevC = 0;
            void Flush(double t1)
            {
                if (stage == null || n == 0) return;
                Console.WriteLine($"| {stage} | {t0:0.0}-{t1:0.0} | {sMw / n:0.000} | {sMn / n:0.000} | {sSw / n:0.000} | {sSn / n:0.000} | {sOw / n:0.000} | {sOn / n:0.000} | {back * 100.0 / n:0}% |");
                all.Add($"| {stage} | " + string.Join(" | ", sum.Select((v, i) => Turn(Math.Abs(v) / ((i >= 1 && i <= 5 || i >= 11) ? Math.Max(nt, 1) : n)))) + " |");
            }
            for (double clock = 0; ; clock += dt)
            {
                double t = clock * pace / 60;
                if (t > P.duration) { Flush(t); break; }
                StormDesign.At(P, t, clock, F); StormTornadoes.Apply(P, ch, t, F); StormLife.Apply(P, t, F);
                spin.Advance(F, t, clock);
                double m = F.param[3 * 4 + 3], s = F.param[StormTornadoes.Slot0 * 4];
                bool tor = s > 0;
                double sk = C.Clamp(s, 0.02, 2.0), or = 0.55 * sk;
                if (!double.IsNaN(prevM))
                {
                    string st = F.stage;
                    if (st != stage) { Flush(t); stage = st; t0 = t; n = 0; back = 0; sMw = sMn = sSw = sSn = sOw = sOn = 0; Array.Clear(sum, 0, sum.Length); nt = 0; }
                    n++;
                    sMw += (m * clock - prevM * prevC) / dt; sMn += spin.Rate(0);
                    if (tor) nt++;
                    for (int i = 0; i < sum.Length; i++) if (tor || i < 1 || (i > 5 && i < 11)) sum[i] += spin.Rate(i);
                    if (tor)
                    {
                        double skW = (sk * clock - prevS * prevC) / dt, orW = (or * clock - prevO * prevC) / dt;
                        sSw += skW; sSn += spin.Rate(1); sOw += orW; sOn += spin.Rate(4);
                        if (skW < 0) back++;
                    }
                }
                prevM = m; prevS = sk; prevO = or; prevC = clock;
            }
            Console.WriteLine();
            Console.WriteLine("Real rates, time per turn:");
            Console.WriteLine("| stage | " + string.Join(" | ", names) + " |");
            Console.WriteLine("|---|" + string.Concat(names.Select(_ => "---|")));
            foreach (var l in all) Console.WriteLine(l);
            Console.WriteLine();
        }
    }

    // Shapes (--shapes, --wedges): every tornado within its shape's limits (StormTornadoes, user 2026-10-29), free storms
    // and rated games: visible width at the ground (2 x ground radius) between MinWidthM and MaxWidthM at its rating, the
    // rating between MinEf and MaxEf, an EF5 only a wedge at least WedgeEf5MinM wide or a drillbit, a wedge's phases at its
    // width, every other phase within its own shape's width; no wedge on the first cycle, and a wedge's life 30-45 min
    // (user, 2026-10-31; a game's wedge may be cut short by the map, so games are checked for the cycle only). Prints
    // each shape's count, width range (yd), ratings and life range (min).
    static void Shapes()
    {
        const double yd = StormTornadoes.Yd;
        bool ok = true;
        var names = Enum.GetNames(typeof(FunnelShape));
        foreach (var pre in StormPreset.All)
        {
            var region = StormTornadoes.RegionOf(pre.key);
            var widths = new System.Collections.Generic.Dictionary<FunnelShape, System.Collections.Generic.List<double>>();
            var efs = new System.Collections.Generic.Dictionary<FunnelShape, int[]>();
            var lives = new System.Collections.Generic.Dictionary<FunnelShape, System.Collections.Generic.List<double>>();
            int bad = 0, n = 0; var why = new System.Collections.Generic.List<string>();
            void Check(TornadoCharacter t, string where, bool free)
            {
                if (t == null) return;
                n++;
                if (!widths.ContainsKey(t.shape)) { widths[t.shape] = new System.Collections.Generic.List<double>(); efs[t.shape] = new int[6]; lives[t.shape] = new System.Collections.Generic.List<double>(); }
                double D = 2000 * t.groundKm; widths[t.shape].Add(D); efs[t.shape][t.ef]++; lives[t.shape].Add(t.LifeMin);
                string f = null;
                double lo = t.shape == FunnelShape.Wedge && t.ef >= 5 ? StormTornadoes.WedgeEf5MinM : StormTornadoes.MinWidthM(t.shape);
                if (t.shape == FunnelShape.Wedge && t.cycle == 0) f = "wedge on the first cycle";
                else if (t.shape == FunnelShape.Wedge && free && (t.LifeMin < StormGame.WedgeLifeMin - 1e-6 || t.LifeMin > StormGame.WedgeLifeMax + 1e-6)) f = $"wedge life {t.LifeMin:0.0} min";
                else if (D < lo - 1 || D > StormTornadoes.MaxWidthM(t.shape, t.ef) + 1) f = $"width {D / yd:0} yd";
                else if (t.ef < StormTornadoes.MinEf(t.shape) || t.ef > StormTornadoes.MaxEf(t.shape)) f = $"EF{t.ef}";
                else if (t.ef >= 5 && t.shape != FunnelShape.Wedge && t.shape != FunnelShape.Drillbit) f = "EF5 shape";
                else if (t.phases != null)
                    foreach (var ph in t.phases)
                    {
                        if (ph.shape == t.shape && (Math.Abs(ph.groundKm - t.groundKm) > 1e-9 || Math.Abs(ph.topKm - t.topKm) > 1e-9)) f = "own phase size";
                        else if (ph.shape != t.shape && 2000 * ph.groundKm > StormTornadoes.MaxWidthM(ph.shape, t.ef) + 1) f = $"{ph.shape} phase {2000 * ph.groundKm / yd:0} yd";
                    }
                if (f != null) { bad++; if (why.Count < 6) why.Add($"{where}: EF{t.ef} {t.shape} {f}"); }
            }
            for (int seed = 1; seed <= 800; seed++)
            {
                var P = StormGame.Adapt(StormDesign.Plan(pre, seed));
                var ch = StormTornadoes.Build(P, region); if (ch == null) continue;
                foreach (var t in ch) Check(t, $"seed {seed}", true);
            }
            int nFree = n;
            for (int seed = 1; seed <= 300; seed++)
            {
                var sc = new StormScenario(pre, seed, 80467.2 / 2);
                foreach (var st in sc.storms) foreach (var t in st.tornadoes ?? new TornadoCharacter[0]) Check(t, $"game {seed} storm {st.index + 1}", false);
            }
            ok &= bad == 0;
            Console.WriteLine($"{pre.key}: {nFree} tornadoes in 800 free storms, {n - nFree} in 300 games; out of limits {bad}");
            foreach (var kv in widths.OrderBy(k => (int)k.Key))
            {
                var w = kv.Value; w.Sort();
                var l = lives[kv.Key]; l.Sort();
                Console.WriteLine($"  {names[(int)kv.Key],-13} {w.Count,5}: width yd min/50/max {w[0] / yd,5:0} {w[w.Count / 2] / yd,5:0} {w[w.Count - 1] / yd,5:0}; EF0-5 {string.Join("/", efs[kv.Key])}; life min {l[0]:0.0}-{l[l.Count - 1]:0.0} (median {l[l.Count / 2]:0.0})");
            }
            foreach (var w in why) Console.WriteLine("  " + w);
        }
        Console.WriteLine(ok ? "PASS" : "FAIL");
    }

    // Slots (--slots): each cycle's clear slot, horseshoe and wall cloud sized to its tornado (StormCycle.bigness,
    // StormGame.Bigness, user 2026-10-31), free storms and games: at the tornado's full size (half way from mature to the
    // rope-out), per shape, the cycle's bigness, the surge (horseshoe) radius, the wall-cloud radius and the slot radius,
    // km, and the funnel's top over the wall cloud's radius (it must hang inside it: FAIL over 0.8) and over the surge's.
    static void Slots()
    {
        bool ok = true;
        var names = Enum.GetNames(typeof(FunnelShape));
        foreach (var pre in StormPreset.All)
        {
            var region = StormTornadoes.RegionOf(pre.key);
            var rows = new System.Collections.Generic.Dictionary<FunnelShape, System.Collections.Generic.List<(double b, double rs, double rw, double sl, double top, double g)>>();
            int bad = 0, n = 0; var why = new System.Collections.Generic.List<string>();
            var F = new StormFrame();
            void Check(StormPlan P, TornadoCharacter[] ch, string where)
            {
                if (ch == null) return;
                for (int k = 0; k < ch.Length; k++)
                {
                    var t = ch[k]; if (t == null) continue;
                    var c = P.cycles[k];
                    double tm = (c.times[3] + c.times[c.times.Length - 5]) / 2;
                    StormDesign.At(P, tm, 0, F); StormTornadoes.Apply(P, ch, tm, F);
                    var a = F.param;
                    double rs = a[18 * 4 + 2], rw = a[6 * 4 + 2], sl = a[11 * 4 + 1], top = a[14 * 4 + 1], g = a[14 * 4];
                    if (!rows.ContainsKey(t.shape)) rows[t.shape] = new System.Collections.Generic.List<(double, double, double, double, double, double)>();
                    rows[t.shape].Add((c.bigness, rs, rw, sl, top, g)); n++;
                    if (double.IsNaN(c.bigness)) { bad++; if (why.Count < 6) why.Add($"{where} cycle {k}: no bigness"); }
                    else if (top > 0.8 * rw) { bad++; if (why.Count < 6) why.Add($"{where} cycle {k}: {t.shape} top {top:0.00} km over a {rw:0.00} km wall cloud"); }
                }
            }
            for (int seed = 1; seed <= 300; seed++)
            {
                var P = StormGame.Adapt(StormDesign.Plan(pre, seed));
                Check(P, StormTornadoes.Build(P, region), $"seed {seed}");
            }
            for (int seed = 1; seed <= 100; seed++)
            {
                var sc = new StormScenario(pre, seed, 80467.2 / 2);
                foreach (var st in sc.storms) Check(st.plan, st.tornadoes, $"game {seed} storm {st.index + 1}");
            }
            ok &= bad == 0;
            Console.WriteLine($"{pre.key}: {n} tornadoes (300 free storms, 100 games); out of place {bad}");
            Console.WriteLine("  shape           n | bigness min/50/max | surge km | wall km | slot km | top / wall | top / surge");
            foreach (var kv in rows.OrderBy(k => (int)k.Key))
            {
                var r = kv.Value;
                string Q(Func<(double b, double rs, double rw, double sl, double top, double g), double> f)
                {
                    var v = r.Select(f).OrderBy(x => x).ToList();
                    return $"{v[0],5:0.00} {v[v.Count / 2],5:0.00} {v[v.Count - 1],5:0.00}";
                }
                Console.WriteLine($"  {names[(int)kv.Key],-13} {r.Count,5} | {Q(x => x.b)} | {Q(x => x.rs)} | {Q(x => x.rw)} | {Q(x => x.sl)} | {Q(x => x.top / x.rw)} | {Q(x => x.top / x.rs)}");
            }
            foreach (var w in why) Console.WriteLine("  " + w);
        }
        Console.WriteLine(ok ? "PASS" : "FAIL");
    }

    // StormLife in games: every tornado on the ground must be over the map, and the storms die as they leave it
    static void Life()
    {
        const double half = 80467.2 / 2;
        foreach (var pre in StormPreset.All)
        {
            int games = 300, torMinutes = 0, outside = 0, dying = 0, tornadoes = 0, lostTornadic = 0, gameTors = 0;
            double worstOut = 0, dieMin = 1e9, dieMax = 0, goneMin = 1e9, goneMax = 0;
            var F = new StormFrame();
            for (int seed = 1; seed <= games; seed++)
            {
                var sc = new StormScenario(pre, seed, half);
                gameTors += sc.TornadoCount;
                foreach (var st in sc.storms)
                {
                    var P = st.plan; var u = P.Updraft;
                    if (!double.IsInfinity(P.dieMin))
                    {
                        dying++;
                        double gDie = st.startMin + P.dieMin / sc.pace, gGone = st.startMin + P.duration / sc.pace;
                        dieMin = Math.Min(dieMin, gDie); dieMax = Math.Max(dieMax, gDie); goneMin = Math.Min(goneMin, gGone); goneMax = Math.Max(goneMax, gGone);
                        var full = StormGame.Adapt(StormDesign.Plan(sc.preset, st.seed, st.overrides));
                        if (full.cycles != null) for (int i = 0; i < full.cycles.Length; i++) if (full.cycles[i].tornadic && !P.cycles[i].tornadic) lostTornadic++;
                    }
                    bool on = false;
                    for (double t = 0; t <= P.duration; t += 0.25)
                    {
                        // (the game's pipeline: its structure moves the wall cloud and tornado, StormStructure)
                        StormDesign.At(P, t, 0, F); StormTornadoes.Apply(P, st.tornadoes, t, F); StormTilt.Apply(P, F); StormStructure.Apply(P, t, 0, F); StormLife.Apply(P, t, F);
                        double amt = F.param[13 * 4 + 2];
                        if (amt <= 0.15) { on = false; continue; }
                        if (!on) tornadoes++;
                        on = true;
                        torMinutes++;
                        // the tornado's ground point, world m: the updraft started at (startX, startZ) at storm time 0
                        double x = st.startX + (F.param[13 * 4] - u.x + P.motion.x * 0.06 * t / sc.pace) * 1000;
                        double z = st.startZ + (F.param[13 * 4 + 1] - u.y + P.motion.y * 0.06 * t / sc.pace) * 1000;
                        double outBy = Math.Max(Math.Abs(x), Math.Abs(z)) - half;
                        if (outBy > 0)
                        {
                            outside++; worstOut = Math.Max(worstOut, outBy);
                            if (System.Environment.GetEnvironmentVariable("LIFE_DEBUG") != null && outside <= 3)
                                Console.WriteLine($"  off: {pre.key} game {seed} storm {st.index} t {t:0.0}/{P.duration:0.0} die {P.dieMin:0.0} rope {F.param[14 * 4 + 3]:0.00} amt {amt:0.00} at ({x / 1000:0.0}, {z / 1000:0.0}) km, tornado {F.param[13 * 4] - u.x:0.0},{F.param[13 * 4 + 1] - u.y:0.0} from the updraft, start ({st.startX / 1000:0.0}, {st.startZ / 1000:0.0}) onMap {st.onMap}");
                        }
                    }
                }
            }
            // the test map's single storm (a mirror of TestMapWorld.PlaceStorm's track: off the map W-S, onto it 2 min
            // before its first wall cloud, at real speed), seeds 1-300
            int sTors = 0, sOut = 0, sSamples = 0, sDying = 0, sCut = 0; double sWorst = 0;
            for (int seed = 1; seed <= 300; seed++)
            {
                var full = StormGame.Adapt(StormGame.Design(pre, seed));
                var (sx, sz) = TestMapStart(full, seed, half);
                var P = StormLife.Trim(full, StormLife.DyingFrom(sx, sz, full.motion, 1, half));
                var chars = StormTornadoes.Build(P, StormTornadoes.RegionOf(pre.key));
                if (!double.IsInfinity(P.dieMin)) sDying++;
                if (full.cycles != null) for (int i = 0; i < full.cycles.Length; i++) if (full.cycles[i].tornadic && !P.cycles[i].tornadic) sCut++;
                var u = P.Updraft; bool on = false;
                for (double t = 0; t <= P.duration; t += 0.25)
                {
                    StormDesign.At(P, t, 0, F); StormTornadoes.Apply(P, chars, t, F); StormTilt.Apply(P, F); StormStructure.Apply(P, t, 0, F); StormLife.Apply(P, t, F);
                    if (F.param[13 * 4 + 2] <= 0.15) { on = false; continue; }
                    if (!on) sTors++;
                    on = true; sSamples++;
                    double x = sx + (F.param[13 * 4] - u.x + P.motion.x * 0.06 * t) * 1000, z = sz + (F.param[13 * 4 + 1] - u.y + P.motion.y * 0.06 * t) * 1000;
                    double outBy = Math.Max(Math.Abs(x), Math.Abs(z)) - half;
                    if (outBy > 0) { sOut++; sWorst = Math.Max(sWorst, outBy); }
                }
            }
            Console.WriteLine($"{pre.key,-7}  test-map storm, 300 seeds: dying on the map {sDying * 100.0 / 300:0}%, tornadoes {sTors}, samples off the map {sOut} of {sSamples} (worst {sWorst / 1000:0.0} km out); tornadic cycles cut {sCut}");
            Console.WriteLine($"{pre.key,-7}: {games} games; storms dying on the map {dying * 100.0 / (2 * games):0}%, from game min {dieMin:0}-{dieMax:0}, gone by {goneMin:0}-{goneMax:0}; " +
                $"tornadoes on the ground {tornadoes} ({gameTors} counted in-game), samples off the map {outside} of {torMinutes} (worst {worstOut / 1000:0.0} km out); tornadic cycles cut {lostTornadic}");
        }
    }

    // A mirror of TestMapWorld.PlaceStorm (world m): where the test map's storm starts (its updraft at storm time 0).
    static (double, double) TestMapStart(StormPlan P, int seed, double half)
    {
        double mature = P.kind == StormKind.Supercell ? (P.cycles?[0].start ?? 30) : P.kind == StormKind.Pulse ? 13 : 20;
        double tEntry = Math.Max(0, mature - 2);
        double mx = P.motion.x * 60, mz = P.motion.y * 60, speed = Math.Sqrt(mx * mx + mz * mz);
        double ux = speed > 1e-3 ? mx / speed : 0.7071, uz = speed > 1e-3 ? mz / speed : 0.7071;
        double dist = Math.Max(speed * tEntry, 1500);
        var rng = new StormRng(seed, 77);
        double bEx = -half, bEz = -half, bSx = bEx - ux * dist, bSz = bEz - uz * dist, bestScore = double.MaxValue;
        for (int pass = 0; pass < 2 && bestScore > 0; pass++)
        {
            bestScore = double.MaxValue;
            for (int i = 0; i < 96; i++)
            {
                float a = (float)rng.U() * (pass == 0 ? 0.5f : 1f);
                int side = Math.Min((int)(a * 4f), 3); float f = a * 4f - side;
                double Ex = side == 0 ? -half : side == 1 ? -half + 2 * half * f : side == 2 ? half : half - 2 * half * f;
                double Ez = side == 0 ? half - 2 * half * f : side == 1 ? -half : side == 2 ? -half + 2 * half * f : half;
                double inward = side == 0 ? ux : side == 1 ? uz : side == 2 ? -ux : -uz;
                double Sx = Ex - ux * dist, Sz = Ez - uz * dist;
                double bearing = (Math.Atan2(Sx, Sz) * 180 / Math.PI + 360) % 360;
                double across = double.MaxValue;
                if (ux > 1e-4) across = Math.Min(across, (half - Ex) / ux); else if (ux < -1e-4) across = Math.Min(across, (-half - Ex) / ux);
                if (uz > 1e-4) across = Math.Min(across, (half - Ez) / uz); else if (uz < -1e-4) across = Math.Min(across, (-half - Ez) / uz);
                double off = pass == 0 ? Math.Max(0, 165 - bearing) + Math.Max(0, bearing - 285) : 0;
                double score = (inward > 0.05 ? 0 : 1000) + off * 10 + Math.Max(0, Math.Min(half, speed * 60) - across) / 100;
                if (score < bestScore) { bestScore = score; bEx = Ex; bEz = Ez; bSx = Sx; bSz = Sz; }
                if (score <= 0) break;
            }
        }
        return (bSx, bSz);
    }

    // StormScenario statistics over many game seeds (a readable summary for review; not part of the parity check)
    static void Scenarios()
    {
        const double half = 80467.2 / 2;
        foreach (var key in StormPreset.All.Select(p => p.key))
        {
            var pre = StormPreset.All.First(p => p.key == key);
            int n = 400, allTor = 0, headNorth = 0, heads = 0, hp0 = 0, hp1 = 0, onMap = 0;
            double turnMin = 999, turnMax = -999, spdMin = 1e9, spdMax = 0;
            var tiers = new int[6]; var maxEf = new int[7];
            double closeMin = 1e9, closeSum = 0, bMin = 999, bMax = -1, offMin = 1e9, entMin = 1e9, entMax = 0, matMin = 1e9, matMax = 0, gapMin = 1e9, gapMax = 0;
            for (int seed = 1; seed <= n; seed++)
            {
                var sc = new StormScenario(pre, seed, half);
                tiers[sc.topEf]++; maxEf[sc.MaxEf + 1]++;
                if (sc.EveryStormTornadic) allTor++;
                if (sc.headline >= 0) { heads++; if (sc.storms[sc.headline].northness > 0.5) headNorth++; }
                foreach (var st in sc.storms)
                {
                    if (st.northness > 0.5 && st.precip == Precip.HP) hp1++;
                    if (st.northness < 0.5 && st.precip == Precip.HP) hp0++;
                    bMin = Math.Min(bMin, st.bearing); bMax = Math.Max(bMax, st.bearing);
                    if (st.onMap) onMap++;
                    double az = (Math.Atan2(st.plan.motion.x, st.plan.motion.y) * 180 / Math.PI + 360) % 360, spd = Math.Sqrt(st.plan.motion.x * st.plan.motion.x + st.plan.motion.y * st.plan.motion.y);
                    turnMin = Math.Min(turnMin, az); turnMax = Math.Max(turnMax, az); spdMin = Math.Min(spdMin, spd); spdMax = Math.Max(spdMax, spd);
                    double off = Math.Max(Math.Abs(st.startX), Math.Abs(st.startZ)) - half; if (!st.onMap) offMin = Math.Min(offMin, off);
                    entMin = Math.Min(entMin, st.entryMin); entMax = Math.Max(entMax, st.entryMin);
                    matMin = Math.Min(matMin, st.matureMin); matMax = Math.Max(matMax, st.matureMin);
                }
                double close = sc.ClosestApproachM(); closeMin = Math.Min(closeMin, close); closeSum += close;
                double gap = Math.Abs(sc.storms[0].laneM - sc.storms[1].laneM); gapMin = Math.Min(gapMin, gap); gapMax = Math.Max(gapMax, gap);
            }
            Console.WriteLine($"{key,-6}: built-for tiers EF2 {tiers[2] * 100.0 / n:0}%, EF3 {tiers[3] * 100.0 / n:0}%, EF4 {tiers[4] * 100.0 / n:0}%, EF5 {tiers[5] * 100.0 / n:0}%; " +
                $"strongest in game EF0-5: {string.Join("/", Enumerable.Range(1, 6).Select(i => maxEf[i]))}, none {maxEf[0]}; every storm tornadic {allTor * 100.0 / n:0}%; headline north {(heads > 0 ? headNorth * 100.0 / heads : 0):0}%; " +
                $"HP north {hp1 * 100.0 / n:0}% south {hp0 * 100.0 / n:0}%; lanes {gapMin / 1609.344:0.0}-{gapMax / 1609.344:0.0} mi apart, closest the storms come {closeMin / 1609.344:0.0} mi (mean {closeSum / n / 1609.344:0.0}); starts {bMin:0}-{bMax:0} deg, {onMap * 100.0 / (2 * n):0}% over the map, the rest >= {offMin / 1000:0.0} km off; motions toward {turnMin:0}-{turnMax:0} deg at {spdMin * 2.237:0}-{spdMax * 2.237:0} mph; on the map {entMin:0.0}-{entMax:0.0} min, mature {matMin:0.0}-{matMax:0.0} min (game)");
        }
        var one = new StormScenario(StormPreset.All[0], 1, half);
        Console.WriteLine("plains game 1: " + one);
        foreach (var st in one.storms)
            Console.WriteLine($"  storm {st.index + 1}: seed {st.seed}, starts {st.startMin:0.0} min at {st.bearing:0} deg, lane {st.laneM / 1000:0.0} km (north {st.northness:0}), {st.precip}; tornadoes in the game: " +
                string.Join(", ", st.InGame(one.gameMinutes, one.pace).Select(t => $"EF{t.ef} {t.shape} at {st.startMin + t.formMin / one.pace:0.0} min")));
    }

    // the game's structure from the environment (StormStructure): per setup and day, the environment, the scales, and
    // over the storm's life the separations seen, the surge against the tornado, how far the wall cloud moves while the
    // slot opens (it must not be pushed: near 0), and the largest jump per 0.1 storm min of the eased amounts
    /// <summary>(user, 2026-11-08: "the tornadoes now take very sudden turns and move insanely fast when the occlusion
    /// starts") Each tornado's ground track through the game's pipeline: its speed over the ground as a share of the
    /// storm's, its slowest progress along the motion (negative: it runs backwards), and its sharpest turn, degrees per
    /// storm minute, over half-minute legs. An occluding tornado slows and curves left; it never outruns the storm much,
    /// runs backwards or turns faster than about 30 degrees a minute.</summary>
    static void Track(string only)
    {
        bool ok = true; double allFast = 0, allTurn = 0, allBack = 9;
        var web = System.Environment.GetEnvironmentVariable("TRACK_WEB");
        foreach (var b in StormPreset.All)
        {
            if (only != null && b.key != only) continue;
            double fast = 0, turn = 0, back = 9, totalTurn = 0, left = -99; string fastAt = "", turnAt = "";
            int n = 0;
            foreach (int day in new[] { 0, 11, 222, 3333, 44444 })
            {
                var pre = day == 0 ? b : StormPreset.Day(b, day);
                var key = day == 0 ? b.key : pre.key;
                for (int seed = 1; seed <= 6; seed++)
                {
                    var P = StormGame.Plan(key, pre, seed, null);
                    if (P.kind != StormKind.Supercell) continue;
                    var ch = StormTornadoes.Build(P, StormTornadoes.RegionOf(key));
                    var F = new StormFrame();
                    double V = Math.Sqrt(P.motion.x * P.motion.x + P.motion.y * P.motion.y) * 0.06;   // km/min
                    const double dt = 0.5;
                    double? px = null, py = null, ph = null; double first = double.NaN, heading0 = 0;
                    for (double t = 0; t <= P.duration; t += dt)
                    {
                        StormDesign.At(P, t, 0, F); StormTornadoes.Apply(P, ch, t, F); StormTilt.Apply(P, F); if (web == null) StormStructure.Apply(P, t, 0, F); StormLife.Apply(P, t, F);
                        var a = F.param;
                        if (a[13 * 4 + 2] <= 0.15) { if (px != null && !double.IsNaN(first)) totalTurn = Math.Max(totalTurn, Math.Abs(heading0)); px = null; ph = null; first = double.NaN; continue; }
                        double gx = a[13 * 4] + P.motion.x * 0.06 * t, gy = a[13 * 4 + 1] + P.motion.y * 0.06 * t;
                        if (px != null)
                        {
                            double vx = (gx - px.Value) / dt, vy = (gy - py.Value) / dt, sp = Math.Sqrt(vx * vx + vy * vy);
                            double along = (vx * P.fwd.x + vy * P.fwd.y) / V;
                            double h = Math.Atan2(vx * P.lft.x + vy * P.lft.y, vx * P.fwd.x + vy * P.fwd.y) * 180 / Math.PI;
                            if (sp / V > fast) { fast = sp / V; fastAt = $"{key} seed {seed} t {t:0.0} rope {a[14 * 4 + 3]:0.00}"; }
                            back = Math.Min(back, along); left = Math.Max(left, h);
                            if (double.IsNaN(first)) first = h;
                            heading0 = h - first;
                            if (ph != null)
                            {
                                double dh = h - ph.Value; while (dh > 180) dh -= 360; while (dh < -180) dh += 360;
                                if (Math.Abs(dh) / dt > turn) { turn = Math.Abs(dh) / dt; turnAt = $"{key} seed {seed} t {t:0.0} rope {a[14 * 4 + 3]:0.00} heading {h:0} deg"; }
                            }
                            ph = h; n++;
                        }
                        px = gx; py = gy;
                    }
                }
            }
            Console.WriteLine($"{b.key,-10} fastest {fast:0.00}x the storm ({fastAt}); slowest along the motion {back:0.00}x; sharpest turn {turn:0} deg/min ({turnAt}); furthest left {left:0} deg off the motion; {n} legs");
            allFast = Math.Max(allFast, fast); allTurn = Math.Max(allTurn, turn); allBack = Math.Min(allBack, back);
        }
        ok = allFast <= 1.3 && allBack >= 0.15 && allTurn <= 30;
        Console.WriteLine($"all: fastest {allFast:0.00}x, slowest along {allBack:0.00}x, sharpest {allTurn:0} deg/min");
        Console.WriteLine(ok ? "PASS" : "FAIL");
    }

    /// <summary>The spread of the storms' LCLs (and CAPE, the base) over the game's day environments: per setup, its
    /// website preset and 400 drawn days.</summary>
    static void Lcl()
    {
        foreach (var b in StormPreset.All)
        {
            var l = new List<double>(); var cp = new List<double>();
            double web = StormGame.Plan(b.key, b, 1, null).env.lclKm;
            for (int day = 1; day <= 400; day++)
            {
                var pre = StormPreset.Day(b, day);
                var P = StormGame.Plan(pre.key, pre, 1, null);
                if (P.env == null) continue;
                l.Add(P.env.lclKm * 1000); cp.Add(P.env.cape);
            }
            l.Sort(); cp.Sort();
            double Q(List<double> x, double q) => x[(int)Math.Round(q * (x.Count - 1))];
            Console.WriteLine($"{b.key,-8} web preset LCL {web * 1000:0} m; days: LCL min {Q(l, 0):0} / 10% {Q(l, 0.1):0} / median {Q(l, 0.5):0} / 90% {Q(l, 0.9):0} / max {Q(l, 1):0} m; CAPE {Q(cp, 0):0}-{Q(cp, 1):0} (median {Q(cp, 0.5):0}) J/kg");
        }
    }

    /// <summary>The spread of the wall clouds' forms (StormStructure.WallForm, WallSize) over the game's day
    /// environments: per setup, 400 drawn days, each storm's first two cycles.</summary>
    static void WallForms()
    {
        foreach (var b0 in StormPreset.All)
        {
            int n = 0, bowl = 0, block = 0, tier = 0, cone = 0, tilted = 0; double wide = 0, deep = 0, wMin = 9, wMax = 0, dMin = 9, dMax = 0;
            for (int day = 1; day <= 400; day++)
            {
                var pre = StormPreset.Day(b0, day);
                var P = StormGame.Plan(pre.key, pre, day, null);
                if (P.env == null || P.kind != StormKind.Supercell) continue;
                for (int c = 0; c < 2; c++)
                {
                    var (fb, fc, ft, fs) = StormStructure.WallForm(P, c);
                    var (w, d) = StormStructure.WallSize(P, c, fb, fc);
                    n++;
                    if (fc > 0.5) cone++; else if (fs > 0.6) tier++; else if (fb > 0.6) block++; else bowl++;
                    if (Math.Abs(ft) > 0.3) tilted++;
                    wide += w; deep += d; wMin = Math.Min(wMin, w); wMax = Math.Max(wMax, w); dMin = Math.Min(dMin, d); dMax = Math.Max(dMax, d);
                }
            }
            Console.WriteLine($"{b0.key,-8} {n} wall clouds: bowl {100.0 * bowl / n:0} %, block {100.0 * block / n:0} %, tiered {100.0 * tier / n:0} %, cone {100.0 * cone / n:0} %; tilted {100.0 * tilted / n:0} %; width x {wMin:0.00}-{wMax:0.00} (mean {wide / n:0.00}), depth x {dMin:0.00}-{dMax:0.00} (mean {deep / n:0.00})");
        }
    }

    static void Structure()
    {
        bool ok = true;
        foreach (var b in StormPreset.All)
        {
            foreach (int day in new[] { 0, 11, 222, 3333, 44444 })
            {
                var pre = day == 0 ? b : StormPreset.Day(b, day);
                var key = day == 0 ? b.key : pre.key;
                double maxPush = 0, minRatio = 99, maxJump = 0; double rsWeb = 0, wallWeb = 0, wallNow = 0; double ffdD = 0, hailD = 0, rsMax = 0, torMax = 0, rMax = 0, towerTopAt = -1;
                string precip = "", jumpAt = "";
                for (int seed = 1; seed <= 6; seed++)
                {
                    var P = StormGame.Plan(key, pre, seed, null);
                    if (P.kind != StormKind.Supercell) { precip = P.kind.ToString(); continue; }
                    var ch = StormTornadoes.Build(P, StormTornadoes.RegionOf(key));
                    var F = new StormFrame(); float[] last = null;
                    precip = P.hpness > 0.5 ? "HP" : P.hpness < -0.35 ? "LP" : "classic";
                    for (double t = 0; t <= P.duration; t += 0.1)
                    {
                        StormDesign.At(P, t, 0, F); StormTornadoes.Apply(P, ch, t, F); StormTilt.Apply(P, F);
                        if (F.param[18 * 4 + 3] > 0.3) rsWeb = Math.Max(rsWeb, F.param[18 * 4 + 2]);
                        double wfx = F.param[24] - F.param[64], wfy = F.param[25] - F.param[65];
                        StormStructure.Apply(P, t, 0, F);
                        var a = F.param;
                        double ux = a[64], uy = a[65];
                        if (a[27] > 0.5) { wallWeb = Math.Max(wallWeb, Math.Sqrt(wfx * wfx + wfy * wfy)); wallNow = Math.Max(wallNow, Math.Sqrt((a[24] - ux) * (a[24] - ux) + (a[25] - uy) * (a[25] - uy))); }
                        double D(int s) => Math.Sqrt((a[s * 4] - ux) * (a[s * 4] - ux) + (a[s * 4 + 1] - uy) * (a[s * 4 + 1] - uy));
                        if (towerTopAt < 0 && a[8] >= P.el) towerTopAt = t;
                        if (a[39] > 0.9) ffdD = Math.Max(ffdD, D(9));
                        if (a[43] > 0.9) hailD = Math.Max(hailD, D(10));
                        rMax = Math.Max(rMax, a[9]);
                        if (a[18 * 4 + 3] > 0.3) rsMax = Math.Max(rsMax, a[18 * 4 + 2]);
                        if (a[13 * 4 + 2] > 0.3) { torMax = Math.Max(torMax, a[14 * 4 + 1]); minRatio = Math.Min(minRatio, a[18 * 4 + 2] / Math.Max(a[14 * 4 + 1], 0.01)); }
                        if (last != null)
                        {
                            // while the slot grows (its radius up) with the wall cloud formed and no rope-out yet: the wall cloud's step
                            if (a[18 * 4 + 2] > last[18 * 4 + 2] + 1e-4 && a[6 * 4 + 3] > 0.5 && a[14 * 4 + 3] < 0.05 && last[6 * 4 + 3] > 0.5)
                            {
                                double mx = Math.Sqrt(Math.Pow(a[24] - last[24] - (ux - last[64]), 2) + Math.Pow(a[25] - last[25] - (uy - last[65]), 2));
                                maxPush = Math.Max(maxPush, mx);
                            }
                            foreach (int j in new[] { 6 * 4 + 3, 11 * 4, 18 * 4 + 3, 6 * 4 + 2 })
                            {
                                double dj = Math.Abs(a[j] - last[j]) * (j == 6 * 4 + 2 ? Math.Min(a[27], last[27]) : 1);   // (a radius as seen: by the amount)
                                if (dj > maxJump) { maxJump = dj; jumpAt = $"slot {j / 4}.{"xyzw"[j % 4]} at {t:0.0} min ({last[j]:0.00} -> {a[j]:0.00})"; }
                            }
                        }
                        last = (float[])a.Clone();
                    }
                    if (seed == 1) Console.WriteLine($"{key,-14} {precip,-7} {P.env}");
                }
                var sc = StormStructure.Scales(StormGame.Plan(key, pre, 1, null));
                Console.WriteLine($"    meso {sc.meso:0.0} tower R {rMax:0.0} km (top at EL by {towerTopAt:0.0} min); FFD core {ffdD:0.0} km, hail {hailD:0.0} km from the updraft; surge up to {rsMax:0.0} km (web {rsWeb:0.0}), wall cloud up to {wallNow:0.0} km from the updraft (web {wallWeb:0.0}), tornado top up to {torMax:0.00} km, surge/top >= {minRatio:0.0}; wall cloud pushed {maxPush * 1000:0} m/0.1 min while the slot grows; largest eased step {maxJump:0.000} ({jumpAt})");
                ok &= minRatio >= 1.8 || minRatio == 99;
                ok &= maxPush < 0.02;
            }
        }
        Console.WriteLine(ok ? "PASS" : "FAIL");
    }
}

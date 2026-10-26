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
        if (args.Length > 0 && args[0] == "--wedges") { Wedges(); return; }
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
                var P = StormDesign.Plan(pre, seed);
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

    // Wedges: visible width at the ground (2 x ground radius) must be half a mile to two miles, free storms and rated games
    static void Wedges()
    {
        const double mi = 1609.344;
        foreach (var pre in StormPreset.All)
        {
            var region = StormTornadoes.RegionOf(pre.key);
            var widths = new System.Collections.Generic.List<double>(); var webR = new System.Collections.Generic.List<double>(); var efs = new int[6]; int bad = 0;
            for (int seed = 1; seed <= 800; seed++)
            {
                var P = StormDesign.Plan(pre, seed);
                var ch = StormTornadoes.Build(P, region); if (ch == null) continue;
                foreach (var t in ch)
                {
                    if (t == null || t.shape != FunnelShape.Wedge) continue;
                    double D = 2000 * t.groundKm; widths.Add(D); efs[t.ef]++; webR.Add(t.webGroundKm);
                    if (D < StormTornadoes.WedgeMinM - 1 || D > StormTornadoes.WedgeMaxM + 1) bad++;
                    if (t.phases != null) foreach (var ph in t.phases) if (ph.shape == FunnelShape.Wedge && Math.Abs(ph.groundKm - t.groundKm) > 1e-9) bad++;
                }
            }
            int gWedges = 0, gBad = 0; double gMin = 1e9, gMax = 0;
            for (int seed = 1; seed <= 300; seed++)
            {
                var sc = new StormScenario(pre, seed, 80467.2 / 2);
                foreach (var st in sc.storms) foreach (var t in st.tornadoes ?? new TornadoCharacter[0])
                {
                    if (t == null || t.shape != FunnelShape.Wedge) continue;
                    double D = 2000 * t.groundKm; gWedges++; gMin = Math.Min(gMin, D); gMax = Math.Max(gMax, D);
                    if (D < StormTornadoes.WedgeMinM - 1 || D > StormTornadoes.WedgeMaxM + 1) gBad++;
                }
            }
            widths.Sort(); webR.Sort(); if (webR.Count > 0) Console.WriteLine($"  web wedge radius km min/5/50/95/max {webR[0]:0.000} {webR[(int)(0.05 * (webR.Count - 1))]:0.000} {webR[webR.Count / 2]:0.000} {webR[(int)(0.95 * (webR.Count - 1))]:0.000} {webR[webR.Count - 1]:0.000}");
            double Q(double q) => widths.Count == 0 ? double.NaN : widths[(int)(q * (widths.Count - 1))] / mi;
            Console.WriteLine($"{pre.key,-7}: {widths.Count} wedges, width mi min/5/50/95/max {Q(0):0.00} {Q(0.05):0.00} {Q(0.5):0.00} {Q(0.95):0.00} {Q(1):0.00}; EF0-5 {string.Join("/", efs)}; out of range {bad}; " +
                $"games: {gWedges} wedges, {gMin / mi:0.00}-{gMax / mi:0.00} mi, out of range {gBad}");
        }
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
                        var full = StormDesign.Plan(pre, st.seed, st.overrides);
                        if (full.cycles != null) for (int i = 0; i < full.cycles.Length; i++) if (full.cycles[i].tornadic && !P.cycles[i].tornadic) lostTornadic++;
                    }
                    bool on = false;
                    for (double t = 0; t <= P.duration; t += 0.25)
                    {
                        StormDesign.At(P, t, 0, F); StormLife.Apply(P, t, F);
                        double amt = F.param[13 * 4 + 2];
                        if (amt <= 0.15) { on = false; continue; }
                        if (!on) tornadoes++;
                        on = true;
                        torMinutes++;
                        // the tornado's ground point, world m: the updraft started at (startX, startZ) at storm time 0
                        double x = st.startX + (F.param[13 * 4] - u.x + P.motion.x * 0.06 * t / sc.pace) * 1000;
                        double z = st.startZ + (F.param[13 * 4 + 1] - u.y + P.motion.y * 0.06 * t / sc.pace) * 1000;
                        double outBy = Math.Max(Math.Abs(x), Math.Abs(z)) - half;
                        if (outBy > 0) { outside++; worstOut = Math.Max(worstOut, outBy); }
                    }
                }
            }
            // the test map's single storm (a mirror of TestMapWorld.PlaceStorm's track: off the map W-S, onto it 2 min
            // before its first wall cloud, at real speed), seeds 1-300
            int sTors = 0, sOut = 0, sSamples = 0, sDying = 0, sCut = 0; double sWorst = 0;
            for (int seed = 1; seed <= 300; seed++)
            {
                var full = StormDesign.Plan(pre, seed);
                var (sx, sz) = TestMapStart(full, seed, half);
                var P = StormLife.Trim(full, StormLife.DyingFrom(sx, sz, full.motion, 1, half));
                if (!double.IsInfinity(P.dieMin)) sDying++;
                if (full.cycles != null) for (int i = 0; i < full.cycles.Length; i++) if (full.cycles[i].tornadic && !P.cycles[i].tornadic) sCut++;
                var u = P.Updraft; bool on = false;
                for (double t = 0; t <= P.duration; t += 0.25)
                {
                    StormDesign.At(P, t, 0, F); StormLife.Apply(P, t, F);
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
            int n = 400, allTor = 0, headNorth = 0, heads = 0, hp0 = 0, hp1 = 0;
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
                    double off = Math.Max(Math.Abs(st.startX), Math.Abs(st.startZ)) - half; offMin = Math.Min(offMin, off);
                    entMin = Math.Min(entMin, st.entryMin); entMax = Math.Max(entMax, st.entryMin);
                    matMin = Math.Min(matMin, st.matureMin); matMax = Math.Max(matMax, st.matureMin);
                }
                double close = sc.ClosestApproachM(); closeMin = Math.Min(closeMin, close); closeSum += close;
                double gap = Math.Abs(sc.storms[0].laneM - sc.storms[1].laneM); gapMin = Math.Min(gapMin, gap); gapMax = Math.Max(gapMax, gap);
            }
            Console.WriteLine($"{key,-6}: built-for tiers EF2 {tiers[2] * 100.0 / n:0}%, EF3 {tiers[3] * 100.0 / n:0}%, EF4 {tiers[4] * 100.0 / n:0}%, EF5 {tiers[5] * 100.0 / n:0}%; " +
                $"strongest in game EF0-5: {string.Join("/", Enumerable.Range(1, 6).Select(i => maxEf[i]))}, none {maxEf[0]}; every storm tornadic {allTor * 100.0 / n:0}%; headline north {(heads > 0 ? headNorth * 100.0 / heads : 0):0}%; " +
                $"HP north {hp1 * 100.0 / n:0}% south {hp0 * 100.0 / n:0}%; lanes {gapMin / 1609.344:0.0}-{gapMax / 1609.344:0.0} mi apart, closest the storms come {closeMin / 1609.344:0.0} mi (mean {closeSum / n / 1609.344:0.0}); starts {bMin:0}-{bMax:0} deg, >= {offMin / 1000:0.0} km off; on the map {entMin:0.0}-{entMax:0.0} min, mature {matMin:0.0}-{matMax:0.0} min (game)");
        }
        var one = new StormScenario(StormPreset.All[0], 1, half);
        Console.WriteLine("plains game 1: " + one);
        foreach (var st in one.storms)
            Console.WriteLine($"  storm {st.index + 1}: seed {st.seed}, starts {st.startMin:0.0} min at {st.bearing:0} deg, lane {st.laneM / 1000:0.0} km (north {st.northness:0}), {st.precip}; tornadoes in the game: " +
                string.Join(", ", st.InGame(one.gameMinutes, one.pace).Select(t => $"EF{t.ef} {t.shape} at {st.startMin + t.formMin / one.pace:0.0} min")));
    }
}

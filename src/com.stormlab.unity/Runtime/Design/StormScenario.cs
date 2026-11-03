// A game scenario (StormLab 2.0's StormScenario, on the web build's storms): the storms of one hour-long game, laid out
// from one seed. Two supercells start a few minutes apart in lanes across the day's mean storm motion, their tracks
// crossing the map's centre line 25-31 miles apart (never under 25: MinSeparationM, user 2026-11-02), the pair shifted up
// to 6 miles either way. The day's motion is the preset's turned up to 15 degrees and sped up to 10 % either way, and
// each storm's is a few degrees and a few per cent off the day's (StormGame.Deviate turns the storm with it), so the
// tracks fan a little; while both storms are alive they never come closer than the minimum (the lanes are pushed apart
// until they do not). About a third of storms initiate over the map itself (towering cumulus up to 12 km in from the
// upwind edge); the rest start off the map to the west, south-west or south and come onto it 2-10 life-cycle minutes
// before they are fully mature (the first wall cloud), then cross it. Every storm puts down at least one tornado during
// the game. How strong a game
// gets is decided once: in about 60 % of games no tornado exceeds EF2, in 20 % the strongest is an EF3, and in 20 % an
// EF4 or (rarely) an EF5. The strongest tornado belongs to one storm, most likely the northernmost (HeadlineWeight); the
// others are capped at that rating. Precipitation mode by lane: the northern storm most likely goes HP, the southern one
// is almost always classic (LaneMode), unless the scenario sets one mode for every storm.
//
// Pacing: the web build's storms take about 30 min to their first wall cloud and live 2-3 h. A game is an hour, so each
// storm's life cycle runs Pace times faster than the game clock while it still crosses the ground at its real speed
// (StormRenderer.motionScale = 1 / Pace). Ratings come from StormTornadoes; a scenario rating resizes the funnel to suit.
// Each storm dies as it leaves the map (StormLife): its plan is trimmed so no tornado outlasts its time on the map.
//
// Frame: world metres, the map a square of half-width mapHalfM centred on the world origin, x east and z north. Times:
// game minutes from the start; a storm's life-cycle time is Pace x (game time - its start).
using System;
using System.Collections.Generic;
using System.Linq;

namespace StormLab
{
    public sealed class ScenarioStorm
    {
        public int index;
        /// <summary>the web build's seed for this storm (StormDesign.Plan)</summary>
        public int seed;
        /// <summary>game minutes when it initiates (towering cumulus)</summary>
        public double startMin;
        /// <summary>its lane: distance from the map's centre line across the storm motion, m; 0 southernmost .. 1 northernmost</summary>
        public double laneM, northness;
        public Precip precip;
        public PlanOverrides overrides;
        public StormPlan plan;
        /// <summary>each circulation's tornado (rated for the game), as StormRenderer.Tornadoes</summary>
        public TornadoCharacter[] tornadoes;
        /// <summary>where its updraft is at its start and where its track crosses the map's upwind edge, world x/z, m; its
        /// start's compass bearing from the map's centre, degrees; game minutes when it is on the map and when it is fully
        /// mature. onMap: it initiates over the map (its start is inside the edge crossing, entryMin is its start)</summary>
        public double startX, startZ, entryX, entryZ, bearing, entryMin, matureMin;
        public bool onMap;
        /// <summary>its life-cycle time at game time g, storm minutes (negative before it initiates)</summary>
        public double StormMinutes(double gameMin, double pace) => (gameMin - startMin) * pace;
        /// <summary>its updraft at game time g, world x/z, m (as StormRenderer.UpdraftWorld with motionScale 1/pace)</summary>
        public (double x, double z) UpdraftAt(double gameMin, double pace)
        {
            double life = StormMinutes(gameMin, pace);
            return (startX + plan.motion.x * 60 * life / pace, startZ + plan.motion.y * 60 * life / pace);
        }
        public bool AliveAt(double gameMin, double pace) { double life = StormMinutes(gameMin, pace); return life >= 0 && life <= plan.duration; }
        /// <summary>its tornadoes that form during the game</summary>
        public IEnumerable<TornadoCharacter> InGame(double gameMinutes, double pace) =>
            tornadoes == null ? Enumerable.Empty<TornadoCharacter>() : tornadoes.Where(t => t != null && t.formMin <= (gameMinutes - startMin) * pace - 1);
    }

    public enum ScenarioPrecip { Auto, Classic, HP }

    public sealed class StormScenario
    {
        /// <summary>a game, minutes, and how much faster than the game clock each storm's life cycle runs</summary>
        public const double DefaultGameMinutes = 60, DefaultPace = 2.5;
        /// <summary>no storm starts later than this into the game, minutes</summary>
        public const double LatestStartMin = 12;
        public const int StormCount = 2;
        public const double MileM = 1609.344;
        /// <summary>2.0's 30-mile game square: its lane jitter (±0.03 of it) is still drawn as 2.0 drew it</summary>
        public const double GameSquareM = 30 * MileM;
        /// <summary>the storms are never closer than this, m (25 miles, user 2026-11-02): their tracks cross the map's centre
        /// line at least this far apart across the day's mean motion (up to LaneGapMaxM, the pair shifted up to LaneShiftM
        /// either way), and while both are alive their updrafts never come closer (ClosestApproachM; the lanes are pushed
        /// apart until they do not)</summary>
        public const double MinSeparationM = 25 * MileM, LaneGapMaxM = 31 * MileM, LaneShiftM = 6 * MileM;
        /// <summary>every lane crosses at least this much of the map, m: the pair's shift gives way first, then its gap</summary>
        public const double MinChordM = 40000;
        /// <summary>the day's storm motion: the preset's turned up to this, degrees, and sped up to this share either way
        /// (stream 510); each storm's is off the day's by up to these (stream 800 + i)</summary>
        public const double DayTurnDeg = 15, DaySpeedSpread = 0.10, StormTurnDeg = 8, StormSpeedSpread = 0.08;
        /// <summary>where a storm initiates: this share of them over the map, OnMapMinM-OnMapMaxM in from the upwind edge
        /// (never past a third of their crossing); the rest off the map, coming onto it the scenario's enterBeforeMature plus
        /// up to EnterSpreadMin life-cycle minutes before their first wall cloud</summary>
        public const double OnMapShare = 0.35, OnMapMinM = 500, OnMapMaxM = 12000, EnterSpreadMin = 8;

        public readonly int seed;
        public readonly StormPreset preset;
        public readonly ClimoRegion region;
        public readonly ScenarioPrecip precipitation;
        public readonly double gameMinutes, pace, mapHalfM;
        /// <summary>the strongest rating the game is built to reach (2: no tornado above EF2); the storm that carries it (-1: none)</summary>
        public readonly int topEf, headline;
        /// <summary>the day's mean storm motion, m/s (the preset's turned and sped a little): the lanes lie across it</summary>
        public readonly Vec2 dayMotion;
        public readonly List<ScenarioStorm> storms = new List<ScenarioStorm>();

        public StormScenario(StormPreset preset, int seed, double mapHalfM, ScenarioPrecip precipitation = ScenarioPrecip.Auto,
            double gameMinutes = DefaultGameMinutes, double pace = DefaultPace, double enterBeforeMature = 2)
        {
            this.seed = seed; this.preset = preset; this.precipitation = precipitation;
            this.gameMinutes = gameMinutes; this.pace = pace; this.mapHalfM = mapHalfM;
            region = StormTornadoes.RegionOf(preset.key);
            var r = new StormRng(seed, 500);
            // (2.0's first draw picked the storm count; it is still taken so each seed keeps 2.0's tiers)
            r.U();
            double uTier = r.U(), uFive = r.U(), uHead = r.U();
            const int n = StormCount;
            topEf = uTier < 0.2 ? (uFive < 0.15 ? 5 : 4) : uTier < 0.4 ? 3 : 2;

            // the day (its own stream): the preset's motion turned and sped a little, the lanes' gap and the pair's shift
            var day = new StormRng(seed, 510);
            double gapM = day.Range(MinSeparationM, LaneGapMaxM), shiftM = day.Range(-LaneShiftM, LaneShiftM);
            double dayTurn = day.Range(-DayTurnDeg, DayTurnDeg), daySpeed = 1 + day.Range(-DaySpeedSpread, DaySpeedSpread);
            // the day's mean motion over the map (the same environment everywhere: a preset); every storm's own is a little off it
            dayMotion = StormGame.Deviate(StormDesign.Plan(preset, 1), dayTurn, daySpeed).motion;
            double mps = Math.Sqrt(dayMotion.x * dayMotion.x + dayMotion.y * dayMotion.y);
            double ux = mps > 0.5 ? dayMotion.x / mps : 1, uz = mps > 0.5 ? dayMotion.y / mps : 0;
            double ax = -uz, az = ux;   // across the motion, to its left

            // 2.0's draws in 2.0's order: the jitter about each lane, which storm takes which side, the starts
            var jitter = Enumerable.Range(0, n).Select(i => (r.U() - 0.5) * 0.06 * GameSquareM).ToList();
            var side = Enumerable.Range(0, n).Select(i => i == 0 ? -0.5 : 0.5).ToList();
            for (int i = n - 1; i > 0; i--) { int j = (int)(r.U() * (i + 1)); (side[i], side[j]) = (side[j], side[i]); }
            var starts = new List<double> { 0 };
            for (int i = 1; i < n; i++) starts.Add(r.Range(2, LatestStartMin));
            starts.Sort();
            List<double> Lanes() => Enumerable.Range(0, n).Select(i => side[i] * gapM + shiftM + jitter[i]).ToList();
            double Chord(double laneM) { double px = ax * laneM, pz = az * laneM; return Edge(px, pz, -ux, -uz, mapHalfM) + Edge(px, pz, ux, uz, mapHalfM); }
            var lanes = Lanes();
            // every lane crosses enough of the map: the pair's shift gives way first, then its gap (never under the minimum)
            for (int guard = 0; guard < 60 && lanes.Any(l => Chord(l) < MinChordM); guard++)
            {
                if (Math.Abs(shiftM) > 100) shiftM *= 0.5; else if (gapM > MinSeparationM + 1) gapM = Math.Max(MinSeparationM, gapM - MileM); else break;
                lanes = Lanes();
            }

            // north-south rank of each lane: where its track crosses the map's centre line (ties: by lane)
            var byNorth = Enumerable.Range(0, n).OrderBy(i => az * lanes[i]).ThenBy(i => lanes[i]).ToList();
            double North(int i) => n > 1 ? byNorth.IndexOf(i) / (n - 1.0) : 0.5;
            headline = -1;
            if (topEf >= 3)
            {
                double total = Enumerable.Range(0, n).Sum(i => HeadlineWeight(North(i))), acc = 0;
                for (int i = 0; i < n && headline < 0; i++) { acc += HeadlineWeight(North(i)) / total; if (uHead < acc) headline = i; }
                if (headline < 0) headline = byNorth[n - 1];
            }

            // each storm's own motion and initiation (its own stream)
            var own = new (double turn, double speed, bool onMap, double inM, double enterBefore)[n];
            for (int i = 0; i < n; i++)
            {
                var rs = new StormRng(seed, 800 + i);
                own[i] = (dayTurn + rs.Range(-StormTurnDeg, StormTurnDeg), daySpeed * (1 + rs.Range(-StormSpeedSpread, StormSpeedSpread)),
                          rs.U() < OnMapShare, rs.Range(OnMapMinM, OnMapMaxM), enterBeforeMature + rs.Range(0, EnterSpreadMin));
            }

            ScenarioStorm Build(int i)
            {
                var st = new ScenarioStorm { index = i, startMin = starts[i], laneM = lanes[i], northness = North(i) };
                // precipitation by lane unless the scenario sets one (its own stream, as in 2.0)
                st.precip = precipitation == ScenarioPrecip.HP ? Precip.HP : precipitation == ScenarioPrecip.Classic ? Precip.Classic
                    : LaneMode(st.northness, new StormRng(seed, 700 + i).U());
                st.overrides = new PlanOverrides { precip = st.precip, forceTornadic = true, turnDeg = own[i].turn, speedScale = own[i].speed };
                int baseSeed = 1 + (int)(new StormRng(seed, 600 + i).Next() % 999983UL);
                // the storm must put down a tornado during the game; try the next seed if not
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    st.seed = baseSeed + 7919 * attempt;
                    st.plan = StormGame.Deviate(StormGame.Adapt(StormDesign.Plan(preset, st.seed, st.overrides)), st.overrides);
                    // where it crosses the map decides when it dies (StormLife): only tornadoes over before then count
                    Place(st, ax, az, own[i].enterBefore, own[i].onMap, own[i].inM);
                    st.plan = StormLife.Trim(st.plan, StormLife.DyingFrom(st.startX, st.startZ, st.plan.motion, 1.0 / pace, mapHalfM));
                    st.tornadoes = StormTornadoes.Build(st.plan, region);
                    if (st.plan.kind != StormKind.Supercell || st.InGame(gameMinutes, pace).Any()) break;
                }
                // ratings: every tornado of the game capped at the game's top rating; the headline storm's strongest made it
                var inGame = st.InGame(gameMinutes, pace).ToList();
                foreach (var t in inGame) if (t.ef > topEf) StormTornadoes.Rate(t, topEf, st.plan);
                if (i == headline && inGame.Count > 0)
                {
                    var best = inGame.OrderByDescending(t => t.ef).ThenByDescending(t => t.widthM).First();
                    StormTornadoes.Rate(best, topEf, st.plan);
                }
                return st;
            }

            // the storms; while their updrafts come closer than the minimum (tracks that converge), the lanes are pushed
            // apart about their middle and the storms laid out again (the same draws: only the lanes move)
            for (int pass = 0; ; pass++)
            {
                storms.Clear();
                for (int i = 0; i < n; i++) storms.Add(Build(i));
                double close = ClosestApproachM();
                if (close >= MinSeparationM - 1 || pass >= 8) break;
                double push = (MinSeparationM - close) * 0.55 + 50;
                int lo = lanes[0] < lanes[1] ? 0 : 1;
                lanes[lo] -= push; lanes[1 - lo] += push;
            }
        }

        /// <summary>When a storm is fully mature, storm minutes: a supercell's first wall cloud, a pulse storm's mature stage.</summary>
        public static double Maturity(StormPlan P) => P.kind == StormKind.Supercell ? (P.cycles?[0].start ?? 30) : P.kind == StormKind.Pulse ? 13 : 20;

        /// <summary>How far from (px, pz) along (dx, dz) to the edge of the square of half-width h, m (negative: the point is
        /// already past that edge; 0 with no motion).</summary>
        static double Edge(double px, double pz, double dx, double dz, double h)
        {
            double t = double.PositiveInfinity;
            if (dx > 1e-6) t = Math.Min(t, (h - px) / dx); else if (dx < -1e-6) t = Math.Min(t, (-h - px) / dx);
            if (dz > 1e-6) t = Math.Min(t, (h - pz) / dz); else if (dz < -1e-6) t = Math.Min(t, (-h - pz) / dz);
            return double.IsInfinity(t) ? 0 : t;
        }

        /// <summary>The storm's track: through its lane's point on the map's centre line (across the day's mean motion, ax/az)
        /// along its own motion. Off the map, it starts as far back from the upwind edge as it travels in the enterBefore
        /// life-cycle minutes before it is mature (at least 1.5 km); over the map (onMap), inM in from that edge, never past
        /// a third of its crossing.</summary>
        void Place(ScenarioStorm st, double ax, double az, double enterBefore, bool onMap, double inM)
        {
            double h = mapHalfM;
            var m = st.plan.motion;
            double mps = Math.Sqrt(m.x * m.x + m.y * m.y);
            double ux = mps > 0.5 ? m.x / mps : 1, uz = mps > 0.5 ? m.y / mps : 0;
            double mature = Maturity(st.plan), entryLife = Math.Max(0, mature - enterBefore);
            st.matureMin = st.startMin + mature / pace;
            // the lane's point on the centre line, then back along the storm's motion to the map's edge
            double px = ax * st.laneM, pz = az * st.laneM, back = Edge(px, pz, -ux, -uz, h), fore = Edge(px, pz, ux, uz, h);
            st.entryX = px - ux * back; st.entryZ = pz - uz * back;
            st.onMap = onMap;
            if (onMap)
            {
                double d = Math.Min(inM, Math.Max(0, back + fore) / 3);
                st.startX = st.entryX + ux * d; st.startZ = st.entryZ + uz * d;
                st.entryMin = st.startMin;
            }
            else
            {
                double dist = Math.Max(mps * 60 * entryLife / pace, 1500);
                st.startX = st.entryX - ux * dist; st.startZ = st.entryZ - uz * dist;
                st.entryMin = st.startMin + (mps > 0.5 ? dist / (mps * 60) : 0);   // the ground is crossed at the real speed
            }
            st.bearing = (Math.Atan2(st.startX, st.startZ) * 180 / Math.PI + 360) % 360;
        }

        /// <summary>How likely a storm is to carry the game's strongest tornado, by lane (0 southernmost .. 1 northernmost):
        /// 0.5 + 3.5 q². With two storms the northern one gets it in 89 % of games.</summary>
        public static double HeadlineWeight(double northness) => 0.5 + 3.5 * northness * northness;

        /// <summary>A storm's precipitation mode from its lane and a uniform draw u: HP odds 5 % southernmost, 15 % middle, 70 %
        /// northernmost (linear between); the rest classic. LP is out of the game, as in 2.0.</summary>
        public static Precip LaneMode(double northness, double u)
        {
            double q = C.Clamp(northness, 0, 1), f = q < 0.5 ? q / 0.5 : (q - 0.5) / 0.5;
            double hp = q < 0.5 ? C.Lerp(0.05, 0.15, f) : C.Lerp(0.15, 0.70, f);
            return u < hp ? Precip.HP : Precip.Classic;
        }

        /// <summary>How close the storms' updrafts come while both are alive during the game, m (sampled every 6 s of game
        /// time); infinity when they never share the sky.</summary>
        public double ClosestApproachM()
        {
            double best = double.PositiveInfinity;
            for (double g = 0; g <= gameMinutes + 1e-9; g += 0.1)
                for (int i = 0; i < storms.Count; i++)
                    for (int j = i + 1; j < storms.Count; j++)
                    {
                        if (!storms[i].AliveAt(g, pace) || !storms[j].AliveAt(g, pace)) continue;
                        var a = storms[i].UpdraftAt(g, pace); var b = storms[j].UpdraftAt(g, pace);
                        best = Math.Min(best, Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z)));
                    }
            return best;
        }

        /// <summary>the strongest tornado of the game (forming during it)</summary>
        public int MaxEf => storms.SelectMany(s => s.InGame(gameMinutes, pace)).Select(t => t.ef).DefaultIfEmpty(-1).Max();
        public int TornadoCount => storms.Sum(s => s.InGame(gameMinutes, pace).Count());
        public bool EveryStormTornadic => storms.All(s => s.plan.kind == StormKind.Supercell && s.InGame(gameMinutes, pace).Any());

        public override string ToString() =>
            $"{storms.Count} storms, {TornadoCount} tornadoes in the game, strongest EF{MaxEf} (built for EF{topEf} at most)";
    }
}

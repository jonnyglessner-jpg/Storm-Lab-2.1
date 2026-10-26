// A game scenario (StormLab 2.0's StormScenario, on the web build's storms): the storms of one hour-long game, laid out
// from one seed. Two supercells start a few minutes apart in lanes about 26 km apart across the storm motion (never under
// 15 miles: MinSeparationM). Each starts
// as towering cumulus off the map to the west, south-west or south and comes onto the map just before it is fully mature
// (its first wall cloud), then crosses it. Every storm puts down at least one tornado during the game. How strong a game
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
        /// <summary>where its updraft is at its start and where it comes onto the map, world x/z, m; its start's compass bearing
        /// from the map's centre, degrees; game minutes when it comes on and when it is fully mature</summary>
        public double startX, startZ, entryX, entryZ, bearing, entryMin, matureMin;
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
        /// <summary>2.0's lanes: 0.27 of its 30-mile game square either side of the centre line (±0.03 jitter), 23-29 km apart</summary>
        public const double GameSquareM = 30 * 1609.344, LaneOffset = 0.27;
        /// <summary>the storms are never closer than this across the motion, m (15 miles; 2.0's jitter alone could bring its
        /// lanes to 14.4)</summary>
        public const double MinSeparationM = 15 * 1609.344;

        public readonly int seed;
        public readonly StormPreset preset;
        public readonly ClimoRegion region;
        public readonly ScenarioPrecip precipitation;
        public readonly double gameMinutes, pace, mapHalfM;
        /// <summary>the strongest rating the game is built to reach (2: no tornado above EF2); the storm that carries it (-1: none)</summary>
        public readonly int topEf, headline;
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

            // storm motion over the map: the same environment everywhere (a preset), so every storm moves alike
            var motion = StormDesign.Plan(preset, 1).motion;
            double mps = Math.Sqrt(motion.x * motion.x + motion.y * motion.y);
            double ux = mps > 0.5 ? motion.x / mps : 1, uz = mps > 0.5 ? motion.y / mps : 0;
            double ax = -uz, az = ux;   // across the motion, to its left

            var lanes = Enumerable.Range(0, n).Select(i => (i == 0 ? -LaneOffset : LaneOffset) * GameSquareM + (r.U() - 0.5) * 0.06 * GameSquareM).ToList();
            // lanes closer than the minimum are pushed apart about their middle (the draws stay as they were)
            if (lanes[1] - lanes[0] < MinSeparationM)
            {
                double mid = (lanes[0] + lanes[1]) / 2;
                lanes[0] = mid - MinSeparationM / 2; lanes[1] = mid + MinSeparationM / 2;
            }
            for (int i = n - 1; i > 0; i--) { int j = (int)(r.U() * (i + 1)); (lanes[i], lanes[j]) = (lanes[j], lanes[i]); }
            var starts = new List<double> { 0 };
            for (int i = 1; i < n; i++) starts.Add(r.Range(2, LatestStartMin));
            starts.Sort();

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

            for (int i = 0; i < n; i++)
            {
                var st = new ScenarioStorm { index = i, startMin = starts[i], laneM = lanes[i], northness = North(i) };
                // precipitation by lane unless the scenario sets one (its own stream, as in 2.0)
                st.precip = precipitation == ScenarioPrecip.HP ? Precip.HP : precipitation == ScenarioPrecip.Classic ? Precip.Classic
                    : LaneMode(st.northness, new StormRng(seed, 700 + i).U());
                st.overrides = new PlanOverrides { precip = st.precip, forceTornadic = true };
                int baseSeed = 1 + (int)(new StormRng(seed, 600 + i).Next() % 999983UL);
                // the storm must put down a tornado during the game; try the next seed if not
                double window = (gameMinutes - st.startMin) * pace;
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    st.seed = baseSeed + 7919 * attempt;
                    st.plan = StormGame.Adapt(StormDesign.Plan(preset, st.seed, st.overrides));
                    // where it crosses the map decides when it dies (StormLife): only tornadoes over before then count
                    Place(st, ux, uz, ax, az, mps, enterBeforeMature);
                    st.plan = StormLife.Trim(st.plan, StormLife.DyingFrom(st.startX, st.startZ, st.plan.motion, 1.0 / pace, mapHalfM));
                    st.tornadoes = StormTornadoes.Build(st.plan, region);
                    if (st.plan.kind != StormKind.Supercell || st.InGame(gameMinutes, pace).Any()) break;
                }
                // ratings: every tornado of the game capped at the game's top rating; the headline storm's strongest made it
                var inGame = st.InGame(gameMinutes, pace).ToList();
                foreach (var t in inGame) if (t.ef > topEf) StormTornadoes.Rate(t, topEf, st.plan.vTor);
                if (i == headline && inGame.Count > 0)
                {
                    var best = inGame.OrderByDescending(t => t.ef).ThenByDescending(t => t.widthM).First();
                    StormTornadoes.Rate(best, topEf, st.plan.vTor);
                }
                storms.Add(st);
            }
        }

        /// <summary>When a storm is fully mature, storm minutes: a supercell's first wall cloud, a pulse storm's mature stage.</summary>
        public static double Maturity(StormPlan P) => P.kind == StormKind.Supercell ? (P.cycles?[0].start ?? 30) : P.kind == StormKind.Pulse ? 13 : 20;

        /// <summary>The storm's track: its lane line, onto the map over the upwind edge enterBeforeMature (life-cycle minutes)
        /// before it is mature, starting off the map as far back as it travels by then (at least 1.5 km).</summary>
        void Place(ScenarioStorm st, double ux, double uz, double ax, double az, double mps, double enterBeforeMature)
        {
            double h = mapHalfM;
            double mature = Maturity(st.plan), entryLife = Math.Max(0, mature - enterBeforeMature);
            st.matureMin = st.startMin + mature / pace; st.entryMin = st.startMin + entryLife / pace;
            double dist = Math.Max(mps * 60 * entryLife / pace, 1500);
            // the lane's point on the centre line, then back along the motion to the map's edge
            double px = ax * st.laneM, pz = az * st.laneM, back = double.PositiveInfinity;
            if (ux > 1e-6) back = Math.Min(back, (px + h) / ux); else if (ux < -1e-6) back = Math.Min(back, (px - h) / ux);
            if (uz > 1e-6) back = Math.Min(back, (pz + h) / uz); else if (uz < -1e-6) back = Math.Min(back, (pz - h) / uz);
            if (double.IsInfinity(back)) back = 0;
            st.entryX = px - ux * back; st.entryZ = pz - uz * back;
            st.startX = st.entryX - ux * dist; st.startZ = st.entryZ - uz * dist;
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

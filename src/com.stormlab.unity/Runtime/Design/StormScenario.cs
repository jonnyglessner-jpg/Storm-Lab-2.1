// A game scenario (StormLab 2.0's StormScenario, on the web build's storms): the storms of one hour-long game, laid out
// from one seed. The seed's first decision is the day's event type (StormEvent.cs; user, 2026-11-09): garden variety,
// isolated storms, local outbreak, regional outbreak, legendary event or generational outbreak. The event sets how many
// storms the day has (one to four), whether each puts down a tornado during the game, and how strong the day gets: its
// top rating (no tornado of the day is stronger; from EF3 up the headline storm's strongest is made exactly that), and
// in the outbreaks a second storm's and every storm's strongest lifted to a rating too. A legendary event is laid out
// from its own definition instead (BuildLegend, StormLegends.cs).
//
// Layout: the storms run in lanes across the day's mean storm motion. Two storms take two lanes, their tracks crossing
// the map's centre line 25-31 miles apart (never under 25: MinSeparationM, user 2026-11-02), the pair shifted up to 6
// miles either way; one storm takes a lane of its own within 12 miles of the centre. A third and a fourth storm are
// lead storms: each shares a lane with one of the first two and initiates over the map 25 miles or more downstream of
// it (towering cumulus deep in the map while its trailing neighbour is still coming onto it), so a full outbreak is two
// lanes of two. The day's motion is the preset's turned up to 15 degrees and sped up to 10 % either way, and each
// storm's is a few degrees and a few per cent off the day's (StormGame.Deviate turns the storm with it), so the tracks
// fan a little; while two storms are alive they never come closer than the minimum (lanes are pushed apart and lead
// storms moved downstream until they do not; a lead storm that cannot be fitted, or has no room left to put down its
// tornado, is left out: storms.Count is then under eventDay.storms). About a third of the other storms initiate over
// the map itself (towering cumulus up to 12 km in from the upwind edge); the rest start off the map to the west,
// south-west or south and come onto it 2-10 life-cycle minutes before they are fully mature (the first wall cloud),
// then cross it. The strongest tornado belongs to one storm, most likely the northernmost (HeadlineWeight); the others
// are capped at that rating. Precipitation mode by lane: the northern storm most likely goes HP, the southern one is
// almost always classic (LaneMode), unless the scenario sets one mode for every storm.
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
        /// <summary>which of the day's planned storms it is (its random streams): its index unless a storm before it was left out</summary>
        public int slot;
        /// <summary>the web build's seed for this storm (StormDesign.Plan)</summary>
        public int seed;
        /// <summary>game minutes when it initiates (towering cumulus)</summary>
        public double startMin;
        /// <summary>its lane: distance from the map's centre line across the storm motion, m; 0 southernmost .. 1 northernmost</summary>
        public double laneM, northness;
        /// <summary>the lane it runs in: 1 the northern, -1 the southern, 0 a lone storm's</summary>
        public int side;
        /// <summary>a lead storm: it initiates over the map, downstream of the storm it shares its lane with</summary>
        public bool lead;
        /// <summary>the day's event has it put down a tornado during the game</summary>
        public bool tornadic = true;
        /// <summary>what the forecast and the panels call it: "northern", "southern trailing", "lone", a legendary storm's name</summary>
        public string label = "";
        public Precip precip;
        public PlanOverrides overrides;
        public StormPlan plan;
        /// <summary>each circulation's tornado (rated for the game), as StormRenderer.Tornadoes</summary>
        public TornadoCharacter[] tornadoes;
        /// <summary>where its updraft is at its start and where its track crosses the map's upwind edge, world x/z, m; its
        /// start's compass bearing from the map's centre, degrees; game minutes when it is on the map (NaN: a legendary
        /// storm written on a track that misses the map) and when it is fully mature. onMap: it initiates over the map (its
        /// start is inside the edge crossing, entryMin is its start)</summary>
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
        public const double MileM = 1609.344;
        /// <summary>2.0's 30-mile game square: its lane jitter (±0.03 of it) is still drawn as 2.0 drew it</summary>
        public const double GameSquareM = 30 * MileM;
        /// <summary>the storms are never closer than this, m (25 miles, user 2026-11-02): their tracks cross the map's centre
        /// line at least this far apart across the day's mean motion (up to LaneGapMaxM, the pair shifted up to LaneShiftM
        /// either way; a lone storm's lane up to SoloShiftM off the centre), and while two are alive their updrafts never
        /// come closer (ClosestApproachM; the lanes are pushed apart, and a lead storm moved downstream of the storm it
        /// shares its lane with, until they do not)</summary>
        public const double MinSeparationM = 25 * MileM, LaneGapMaxM = 31 * MileM, LaneShiftM = 6 * MileM, SoloShiftM = 12 * MileM;
        /// <summary>every lane crosses at least this much of the map, m: the pair's shift gives way first, then its gap. A
        /// lane with two storms (a lead storm 25 miles downstream of a trailing one) needs PairChordM</summary>
        public const double MinChordM = 40000, PairChordM = 64000;
        /// <summary>the day's storm motion: the preset's turned up to this, degrees, and sped up to this share either way
        /// (stream 510); each storm's is off the day's by up to these (stream 800 + i)</summary>
        public const double DayTurnDeg = 15, DaySpeedSpread = 0.10, StormTurnDeg = 8, StormSpeedSpread = 0.08;
        /// <summary>where a storm initiates: this share of them over the map, OnMapMinM-OnMapMaxM in from the upwind edge
        /// (never past a third of their crossing); the rest off the map, coming onto it the scenario's enterBeforeMature plus
        /// up to EnterSpreadMin life-cycle minutes before their first wall cloud</summary>
        public const double OnMapShare = 0.35, OnMapMinM = 500, OnMapMaxM = 12000, EnterSpreadMin = 8;
        /// <summary>a lead storm (the third and fourth of an outbreak) initiates over the map this far in from the upwind
        /// edge to begin with, m (plus up to LeadSpreadM; it is moved further in until it is the minimum separation ahead of
        /// the storm it shares its lane with), and never so far in that it has under LeadRunMin game minutes of map left
        /// at its own speed (the time it takes to mature and put a tornado down). It goes up in the game's first
        /// LeadStartMin minutes and the storm behind it PairWaitMin-PairWaitMax minutes after it (the head start is room it
        /// does not have to find on the map), and it moves as that storm does, so the pair keep their distance</summary>
        public const double LeadInM = 20000, LeadSpreadM = 5000, LeadRunMin = 20, LeadStartMin = 2, PairWaitMin = 5, PairWaitMax = 9;

        public readonly int seed;
        public readonly StormPreset preset;
        public readonly ClimoRegion region;
        public readonly ScenarioPrecip precipitation;
        public readonly double gameMinutes, pace, mapHalfM;
        /// <summary>the day's event (its type, how many storms it planned, the real event a legendary day recreates)</summary>
        public readonly StormEventDay eventDay;
        public StormEventType EventType => eventDay.type;
        /// <summary>the hour of day the first storm initiates, when the event sets one (a legendary event); NaN: the seed's</summary>
        public readonly double startHour = double.NaN;
        /// <summary>the strongest rating the game is built to reach (2: no tornado above EF2); the storm that carries it (-1: none)</summary>
        public int topEf { get; private set; }
        public int headline { get; private set; } = -1;
        /// <summary>the day's mean storm motion, m/s (the preset's turned and sped a little): the lanes lie across it</summary>
        public Vec2 dayMotion { get; private set; }
        public readonly List<ScenarioStorm> storms = new List<ScenarioStorm>();
        /// <summary>the planned storms the layout could not fit (lead storms only), and why, for the panels and the checks</summary>
        public readonly List<string> leftOut = new List<string>();

        /// <summary>eventType: the day's event whatever the seed rolls (null: the seed's own). legend: this legendary event.
        /// legendsSeen: the legendary events a career has had (StormEvents.Day).</summary>
        public StormScenario(StormPreset preset, int seed, double mapHalfM, ScenarioPrecip precipitation = ScenarioPrecip.Auto,
            double gameMinutes = DefaultGameMinutes, double pace = DefaultPace, double enterBeforeMature = 2,
            StormEventType? eventType = null, LegendaryEvent legend = null, IEnumerable<string> legendsSeen = null)
        {
            this.seed = seed; this.precipitation = precipitation;
            bool web = preset.key.StartsWith(StormPreset.WebPrefix, StringComparison.Ordinal);
            // the day's event first (user, 2026-11-09): everything below is laid out for it
            eventDay = StormEvents.Day(seed, StormPreset.BaseKey(preset.key), eventType, legend, legendsSeen);
            legend = eventDay.legend;
            if (legend != null) startHour = legend.startHour;
            // (user, 2026-11-07) the day's own environment about the setup's (StormPreset.Day: shear, hodograph, CAPE, LCL),
            // unless the key already names a day or a website preset; a legendary event may bring its own sounding
            this.preset = legend?.environment != null ? legend.environment
                : preset.key.IndexOf(StormPreset.DaySep) > 0 || preset.key.IndexOf(LegendaryEvents.EnvSep) > 0 || web ? preset : StormPreset.Day(preset, seed);
            preset = this.preset;
            this.gameMinutes = gameMinutes; this.pace = pace; this.mapHalfM = mapHalfM;
            region = StormTornadoes.RegionOf(preset.key);
            topEf = eventDay.topEf;
            if (legend != null) { BuildLegend(legend); Label(); return; }

            var spec = eventDay.spec;
            int planned = eventDay.storms, first = Math.Min(planned, 2);
            var r = new StormRng(seed, 500);
            // (2.0's first draws picked the storm count and the tier, which the event decides now; they are still taken so
            // each seed keeps 2.0's lanes and starts)
            r.U(); r.U(); r.U();
            double uHead = r.U();

            // the day (its own stream): the preset's motion turned and sped a little, the lanes' gap and the pair's shift
            var day = new StormRng(seed, 510);
            double gapM = day.Range(MinSeparationM, LaneGapMaxM), shiftM = day.Range(-LaneShiftM, LaneShiftM);
            double dayTurn = day.Range(-DayTurnDeg, DayTurnDeg), daySpeed = 1 + day.Range(-DaySpeedSpread, DaySpeedSpread);
            if (planned == 1) shiftM *= SoloShiftM / LaneShiftM;
            // the day's mean motion over the map (the same environment everywhere: a preset); every storm's own is a little off it
            dayMotion = StormGame.Deviate(StormDesign.Plan(preset, 1), dayTurn, daySpeed).motion;
            double mps = Math.Sqrt(dayMotion.x * dayMotion.x + dayMotion.y * dayMotion.y);
            double ux = mps > 0.5 ? dayMotion.x / mps : 1, uz = mps > 0.5 ? dayMotion.y / mps : 0;
            double ax = -uz, az = ux;   // across the motion, to its left

            // the first two storms, 2.0's draws in 2.0's order: the jitter about each lane, which storm takes which side,
            // the starts. A lone storm has the middle to itself.
            var jitter = new double[planned]; var side = new double[planned]; var starts = new double[planned];
            var lead = new bool[planned]; var leadIn = new double[planned];
            for (int i = 0; i < first; i++) jitter[i] = (r.U() - 0.5) * 0.06 * GameSquareM;
            for (int i = 0; i < first; i++) side[i] = first == 1 ? 0 : i == 0 ? -0.5 : 0.5;
            for (int i = first - 1; i > 0; i--) { int j = (int)(r.U() * (i + 1)); (side[i], side[j]) = (side[j], side[i]); }
            var firstStarts = new List<double> { 0 };
            for (int i = 1; i < first; i++) firstStarts.Add(r.Range(2, LatestStartMin));
            firstStarts.Sort();
            for (int i = 0; i < first; i++) starts[i] = firstStarts[i];
            // the lead storms of an outbreak (their own stream): the third takes one lane, the fourth the other
            var extra = new StormRng(seed, 550);
            bool thirdNorth = extra.U() < 0.5;
            for (int i = 2; i < planned; i++)
            {
                side[i] = (i == 2) == thirdNorth ? 0.5 : -0.5;
                jitter[i] = (extra.U() - 0.5) * 0.06 * GameSquareM;
                starts[i] = extra.Range(0, LeadStartMin);
                lead[i] = true; leadIn[i] = LeadInM + extra.Range(0, LeadSpreadM);
                // the storm behind it in its lane goes up after it
                double wait = extra.Range(PairWaitMin, PairWaitMax);
                for (int k = 0; k < first; k++) if (side[k] == side[i]) starts[k] = Math.Max(starts[k], starts[i] + wait);
            }
            var leadIn0 = (double[])leadIn.Clone();
            double pushM = 0;   // the lanes pushed apart, each by this
            double Lane(int i) => side[i] * gapM + shiftM + jitter[i] + Math.Sign(side[i]) * pushM;
            double Chord(double laneM) { double px = ax * laneM, pz = az * laneM; return Edge(px, pz, -ux, -uz, mapHalfM) + Edge(px, pz, ux, uz, mapHalfM); }
            // every lane crosses enough of the map (more of it with two storms in it): the pair's shift gives way first, then
            // its gap (never under the minimum)
            bool Short() => Enumerable.Range(0, planned).Any(i => Chord(Lane(i)) < (Enumerable.Range(0, planned).Any(k => lead[k] && side[k] == side[i]) ? PairChordM : MinChordM));
            for (int guard = 0; guard < 60 && Short(); guard++)
            {
                if (Math.Abs(shiftM) > 100) shiftM *= 0.5; else if (gapM > MinSeparationM + 1) gapM = Math.Max(MinSeparationM, gapM - MileM); else break;
            }

            // each storm's own motion and initiation (its own stream)
            var own = new (double turn, double speed, bool onMap, double inM, double enterBefore)[planned];
            for (int i = 0; i < planned; i++)
            {
                var rs = new StormRng(seed, 800 + i);
                own[i] = (dayTurn + rs.Range(-StormTurnDeg, StormTurnDeg), daySpeed * (1 + rs.Range(-StormSpeedSpread, StormSpeedSpread)),
                          rs.U() < OnMapShare, rs.Range(OnMapMinM, OnMapMaxM), enterBeforeMature + rs.Range(0, EnterSpreadMin));
            }
            // (a lead storm moves as the storm behind it does)
            for (int i = first; i < planned; i++)
                for (int k = 0; k < first; k++) if (side[k] == side[i]) { own[i].turn = own[k].turn; own[i].speed = own[k].speed; }

            // the planned storms still in the layout (a lead storm that cannot be fitted is left out, below)
            var ids = Enumerable.Range(0, planned).ToList();
            for (; ; )
            {
                int n = ids.Count;
                // north-south rank of each lane: where its track crosses the map's centre line (ties: by lane)
                var byNorth = ids.OrderBy(i => az * Lane(i)).ThenBy(i => Lane(i)).ToList();
                double North(int i) => n > 1 ? byNorth.IndexOf(i) / (n - 1.0) : 0.5;
                double northSide = az >= 0 ? 0.5 : -0.5;
                // the headline storm carries the day's top rating (from EF3 up), a second storm the event's second rating:
                // by lane, among the storms that put down a tornado
                int head = -1, second = -1;
                var tor = ids.Where(i => eventDay.tornadic[i]).ToList();
                if (topEf >= 3 && tor.Count > 0)
                {
                    double total = tor.Sum(i => HeadlineWeight(North(i))), acc = 0;
                    foreach (int i in tor) { acc += HeadlineWeight(North(i)) / total; if (uHead < acc) { head = i; break; } }
                    if (head < 0) head = tor.OrderBy(i => North(i)).Last();
                }
                if (spec.secondEf >= 0) second = tor.Where(i => i != head).OrderByDescending(i => HeadlineWeight(North(i))).ThenBy(i => i).DefaultIfEmpty(-1).First();

                ScenarioStorm Build(int id, int index)
                {
                    var st = new ScenarioStorm
                    {
                        index = index, slot = id, startMin = starts[id], laneM = Lane(id), northness = North(id), lead = lead[id],
                        side = side[id] == 0 ? 0 : side[id] == northSide ? 1 : -1, tornadic = eventDay.tornadic[id],
                    };
                    // precipitation by lane unless the scenario sets one (its own stream, as in 2.0)
                    st.precip = precipitation == ScenarioPrecip.HP ? Precip.HP : precipitation == ScenarioPrecip.Classic ? Precip.Classic
                        : LaneMode(st.northness, new StormRng(seed, 700 + id).U());
                    st.overrides = new PlanOverrides { precip = st.precip, forceTornadic = st.tornadic, noTornado = !st.tornadic, turnDeg = own[id].turn, speedScale = own[id].speed };
                    int baseSeed = 1 + (int)(new StormRng(seed, 600 + id).Next() % 999983UL);
                    // a storm that shares its lane with a lead storm starts off the map (the lead needs the room downstream)
                    bool onMap = lead[id] || own[id].onMap && !ids.Any(k => lead[k] && side[k] == side[id]);
                    // a tornadic storm must put down a tornado during the game; try the next seed if not
                    for (int attempt = 0; attempt < 12; attempt++)
                    {
                        st.seed = baseSeed + 7919 * attempt;
                        st.plan = StormGame.Deviate(StormGame.Adapt(StormGame.Design(preset, st.seed, st.overrides)), st.overrides);
                        // where it crosses the map decides when it dies (StormLife): only tornadoes over before then count
                        Place(st, ax, az, own[id].enterBefore, onMap, lead[id] ? leadIn[id] : own[id].inM);
                        st.plan = StormLife.Trim(st.plan, StormLife.DyingFrom(st.startX, st.startZ, st.plan.motion, 1.0 / pace, mapHalfM));
                        // (a garden-variety storm: its first tornado of the game only; the cycles after it go without)
                        if (spec.maxTornadoes > 0 && st.plan.cycles != null)
                        {
                            int kept = 0;
                            foreach (var c in st.plan.cycles)
                                if (c.tornadic && (kept >= spec.maxTornadoes || c.times[2] > (gameMinutes - st.startMin) * pace - 1)) c.tornadic = false;
                                else if (c.tornadic) kept++;
                        }
                        st.tornadoes = StormTornadoes.Build(st.plan, region);
                        if (!st.tornadic || st.plan.kind != StormKind.Supercell || st.InGame(gameMinutes, pace).Any()) break;
                    }
                    // ratings: every tornado of the game capped at the day's top rating; the headline storm's strongest made
                    // it, a second storm's and every storm's strongest lifted to the event's (never above the top)
                    var inGame = st.InGame(gameMinutes, pace).ToList();
                    foreach (var t in inGame) if (t.ef > topEf) StormTornadoes.Rate(t, topEf, st.plan);
                    if (inGame.Count > 0)
                    {
                        var best = inGame.OrderByDescending(t => t.ef).ThenByDescending(t => t.widthM).First();
                        int lift = id == head ? topEf : Math.Min(topEf, id == second ? Math.Max(spec.secondEf, spec.floorEf) : spec.floorEf);
                        if (id == head || best.ef < lift) StormTornadoes.Rate(best, lift, st.plan);
                    }
                    return st;
                }

                // the storms; while two updrafts come closer than the minimum, a lead storm is moved downstream of the storm
                // in its lane, or the lanes are pushed apart about their middle (tracks that converge), and the storms laid
                // out again (the same draws: only the lanes and the lead storms' starts move)
                for (int pass = 0; ; pass++)
                {
                    storms.Clear();
                    for (int k = 0; k < n; k++) storms.Add(Build(ids[k], k));
                    if (pass >= 10) break;
                    double lanePush = 0; bool moved = false;
                    for (int a = 0; a < n; a++)
                        for (int b = a + 1; b < n; b++)
                        {
                            double close = ClosestApproachM(a, b);
                            if (close >= MinSeparationM - 1) continue;
                            int ia = ids[a], ib = ids[b];
                            if (side[ia] == side[ib] && (lead[ia] || lead[ib])) { leadIn[lead[ib] ? ib : ia] += (MinSeparationM - close) * 1.1 + 100; moved = true; }
                            else lanePush = Math.Max(lanePush, (MinSeparationM - close) * 0.55 + 50);
                        }
                    if (lanePush <= 0 && !moved) break;
                    pushM += lanePush;
                }
                // a lead storm still too close to another, or with no tornado for a day that has every storm put one
                // down (too little of the map left to it), is left out, and the rest laid out without it
                int drop = -1; double worst = MinSeparationM - 1;
                for (int a = 0; a < n; a++)
                    for (int b = a + 1; b < n; b++)
                    {
                        double close = ClosestApproachM(a, b);
                        if (close >= worst || !(lead[ids[a]] || lead[ids[b]])) continue;
                        worst = close; drop = lead[ids[b]] ? ids[b] : ids[a];
                    }
                if (drop >= 0) leftOut.Add($"lead storm {drop + 1}: no room {MinSeparationM / MileM:0} miles from the others (closest {worst / MileM:0.0} mi)");
                if (drop < 0)
                    for (int k = n - 1; k >= 0 && drop < 0; k--)
                        if (lead[ids[k]] && storms[k].tornadic && !storms[k].InGame(gameMinutes, pace).Any()) { drop = ids[k]; leftOut.Add($"lead storm {drop + 1}: too little of the map left for its tornado"); }
                if (drop < 0) { headline = head < 0 ? -1 : ids.IndexOf(head); break; }
                ids.Remove(drop);
                pushM = 0; Array.Copy(leadIn0, leadIn, planned);
            }
            Label();
        }

        /// <summary>
        /// A legendary event's day, laid out as it is written (StormLegends.cs): each storm where and when its definition
        /// puts it, with the motion written for it (turned and scaled off the environment's own, as every game storm's is),
        /// its written tornadoes made the ratings and shapes written, its other tornadoes capped. Nothing is drawn into
        /// lanes or pushed apart: LegendaryEvents.Problems lists what the definition asks that the layout could not give.
        /// </summary>
        void BuildLegend(LegendaryEvent L)
        {
            var env = StormDesign.Plan(preset, 1).motion;
            dayMotion = env;
            double s0 = Math.Sqrt(env.x * env.x + env.y * env.y), b0 = Math.Atan2(env.x, env.y) * 180 / Math.PI;
            double ux = s0 > 0.5 ? env.x / s0 : 1, uz = s0 > 0.5 ? env.y / s0 : 0, ax = -uz, az = ux;
            int n = eventDay.storms;
            var north = Enumerable.Range(0, n).OrderBy(i => L.storms[i].northMi).ThenBy(i => i).ToList();
            for (int i = 0; i < n; i++)
            {
                var ls = L.storms[i];
                var st = new ScenarioStorm
                {
                    index = i, slot = i, startMin = Math.Max(0, ls.startMin), precip = ls.precip, tornadic = ls.tornadic, label = ls.name ?? "",
                    startX = ls.eastMi * MileM, startZ = ls.northMi * MileM, northness = n > 1 ? north.IndexOf(i) / (n - 1.0) : 0.5,
                };
                st.side = n == 1 ? 0 : st.northness > 0.5 ? 1 : -1;
                st.laneM = ax * st.startX + az * st.startZ;
                st.overrides = new PlanOverrides
                {
                    precip = ls.precip, forceTornadic = ls.tornadic, noTornado = !ls.tornadic,
                    turnDeg = double.IsNaN(ls.towardDeg) || s0 < 0.5 ? 0 : Math.IEEERemainder(ls.towardDeg - b0, 360),
                    speedScale = double.IsNaN(ls.mph) || s0 < 0.5 ? 1 : ls.mph * 0.44704 / s0,
                };
                var written = ls.tornadic ? ls.tornadoes ?? new LegendaryTornado[0] : new LegendaryTornado[0];
                // its own seed as written, or the first of the day's that puts its written tornadoes down during the game
                int baseSeed = ls.seed > 0 ? ls.seed : 1 + (int)(new StormRng(seed, 600 + i).Next() % 999983UL);
                for (int attempt = 0; attempt < (ls.seed > 0 ? 1 : 12); attempt++)
                {
                    st.seed = baseSeed + 7919 * attempt;
                    st.plan = StormGame.Deviate(StormGame.Adapt(StormGame.Design(preset, st.seed, st.overrides)), st.overrides);
                    PlaceAt(st);
                    st.plan = StormLife.Trim(st.plan, StormLife.DyingFrom(st.startX, st.startZ, st.plan.motion, 1.0 / pace, mapHalfM));
                    st.tornadoes = StormTornadoes.Build(st.plan, region);
                    var now = st.InGame(gameMinutes, pace).ToList();
                    if (!ls.tornadic || st.plan.kind != StormKind.Supercell) break;
                    if (now.Count > 0 && written.All(w => w.cycle < 0 || w.cycle < st.tornadoes.Length && now.Contains(st.tornadoes[w.cycle]))) break;
                }
                // the written tornadoes: on their cycles first, then the strongest left; the rest capped
                var done = new HashSet<TornadoCharacter>();
                foreach (var w in written.OrderBy(w => w.cycle < 0 ? 1 : 0))
                {
                    var t = w.cycle >= 0 ? (st.tornadoes != null && w.cycle < st.tornadoes.Length ? st.tornadoes[w.cycle] : null)
                        : st.InGame(gameMinutes, pace).Where(x => !done.Contains(x)).OrderByDescending(x => x.ef).ThenByDescending(x => x.widthM).FirstOrDefault();
                    if (t == null) continue;
                    if (w.shape != null)
                    {
                        // the shape and width as written (StormTornadoes.Forced: that shape alone, outside the game's shape limits)
                        var f = StormTornadoes.Forced(st.plan, region, t.cycle, w.shape.Value, w.widthYd > 0 ? w.widthYd * StormTornadoes.Yd : t.widthM, w.ef);
                        if (f != null)
                        {
                            st.tornadoes[t.cycle] = t = f;
                            var c = st.plan.cycles[t.cycle];
                            if (!double.IsNaN(c.bigness)) c.bigness = StormGame.Bigness(t.topKm);
                        }
                    }
                    else StormTornadoes.Rate(t, w.ef, st.plan);
                    done.Add(t);
                }
                foreach (var t in st.InGame(gameMinutes, pace)) if (!done.Contains(t) && t.ef > ls.otherEf) StormTornadoes.Rate(t, ls.otherEf, st.plan);
                storms.Add(st);
            }
            topEf = Math.Max(topEf, MaxEf);
            headline = storms.Count == 0 || MaxEf < 0 ? -1 : storms.OrderByDescending(s => s.InGame(gameMinutes, pace).Select(t => t.ef).DefaultIfEmpty(-1).Max()).First().index;
        }

        /// <summary>What each storm is called: by its lane, and where two share one, lead or trailing; a lone storm; a
        /// legendary storm keeps the name written for it.</summary>
        void Label()
        {
            foreach (var st in storms)
            {
                if (!string.IsNullOrEmpty(st.label)) continue;
                if (storms.Count == 1) { st.label = "lone"; continue; }
                bool pair = storms.Any(o => o != st && o.side == st.side);
                bool ahead = st.lead || pair && storms.Where(o => o != st && o.side == st.side).All(o => Along(st) > Along(o));
                st.label = (st.side > 0 ? "northern" : "southern") + (pair ? ahead ? " lead" : " trailing" : "");
            }
            double Along(ScenarioStorm s) => s.startX * dayMotion.x + s.startZ * dayMotion.y;
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

        /// <summary>How far from (px, pz), outside the square of half-width h, along (dx, dz) to where the line comes into
        /// it, m; NaN when it never does.</summary>
        static double Enter(double px, double pz, double dx, double dz, double h)
        {
            double t0 = 0, t1 = double.PositiveInfinity;
            foreach (var (p, d) in new[] { (px, dx), (pz, dz) })
            {
                if (Math.Abs(d) < 1e-9) { if (Math.Abs(p) > h) return double.NaN; continue; }
                double a = (-h - p) / d, b = (h - p) / d;
                if (a > b) (a, b) = (b, a);
                t0 = Math.Max(t0, a); t1 = Math.Min(t1, b);
            }
            return t0 <= t1 ? t0 : double.NaN;
        }

        /// <summary>The storm's track: through its lane's point on the map's centre line (across the day's mean motion, ax/az)
        /// along its own motion. Off the map, it starts as far back from the upwind edge as it travels in the enterBefore
        /// life-cycle minutes before it is mature (at least 1.5 km); over the map (onMap), inM in from that edge, never past
        /// a third of its crossing; a lead storm as far in as inM asks, short of its last LeadRunMin game minutes of map.</summary>
        void Place(ScenarioStorm st, double ax, double az, double enterBefore, bool onMap, double inM)
        {
            double h = mapHalfM;
            var m = st.plan.motion;
            double mps = Math.Sqrt(m.x * m.x + m.y * m.y);
            double ux = mps > 0.5 ? m.x / mps : 1, uz = mps > 0.5 ? m.y / mps : 0;
            // (the game's structure, StormStructure, hangs the wall cloud and tornado up to about 2 km behind the updraft: a
            // storm off the map crosses the edge with that much more to spare, or its first touchdown can be off the map)
            double lead = mps > 0.5 ? 2500.0 / (mps * 60) * pace : 0;
            double mature = Maturity(st.plan), entryLife = Math.Max(0, mature - enterBefore - lead);
            st.matureMin = st.startMin + mature / pace;
            // the lane's point on the centre line, then back along the storm's motion to the map's edge
            double px = ax * st.laneM, pz = az * st.laneM, back = Edge(px, pz, -ux, -uz, h), fore = Edge(px, pz, ux, uz, h);
            st.entryX = px - ux * back; st.entryZ = pz - uz * back;
            st.onMap = onMap;
            if (onMap)
            {
                double chord = Math.Max(0, back + fore);
                double d = st.lead ? Math.Min(inM, Math.Max(0, chord - LeadRunMin * 60 * mps)) : Math.Min(inM, chord / 3);
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

        /// <summary>A storm whose start is given (a legendary event's): where and when its track comes onto the map along
        /// its own motion (over the map already: there and then; never: entryMin NaN), and when it is mature.</summary>
        void PlaceAt(ScenarioStorm st)
        {
            var m = st.plan.motion;
            double mps = Math.Sqrt(m.x * m.x + m.y * m.y);
            double ux = mps > 0.5 ? m.x / mps : 1, uz = mps > 0.5 ? m.y / mps : 0;
            st.matureMin = st.startMin + Maturity(st.plan) / pace;
            st.onMap = Math.Max(Math.Abs(st.startX), Math.Abs(st.startZ)) <= mapHalfM;
            st.entryX = st.startX; st.entryZ = st.startZ; st.entryMin = st.startMin;
            if (!st.onMap)
            {
                double t = mps > 0.5 ? Enter(st.startX, st.startZ, ux, uz, mapHalfM) : double.NaN;
                if (double.IsNaN(t)) st.entryMin = double.NaN;
                else { st.entryX += ux * t; st.entryZ += uz * t; st.entryMin = st.startMin + t / (mps * 60); }
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

        /// <summary>How close two storms' updrafts come while both are alive during the game, m (sampled every 6 s of game
        /// time); infinity when they never share the sky.</summary>
        public double ClosestApproachM(int i, int j)
        {
            double best = double.PositiveInfinity;
            for (double g = 0; g <= gameMinutes + 1e-9; g += 0.1)
            {
                if (!storms[i].AliveAt(g, pace) || !storms[j].AliveAt(g, pace)) continue;
                var a = storms[i].UpdraftAt(g, pace); var b = storms[j].UpdraftAt(g, pace);
                best = Math.Min(best, Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z)));
            }
            return best;
        }

        /// <summary>How close any two of the storms come (infinity with one storm).</summary>
        public double ClosestApproachM()
        {
            double best = double.PositiveInfinity;
            for (int i = 0; i < storms.Count; i++)
                for (int j = i + 1; j < storms.Count; j++) best = Math.Min(best, ClosestApproachM(i, j));
            return best;
        }

        /// <summary>the strongest tornado of the game (forming during it)</summary>
        public int MaxEf => storms.SelectMany(s => s.InGame(gameMinutes, pace)).Select(t => t.ef).DefaultIfEmpty(-1).Max();
        public int TornadoCount => storms.Sum(s => s.InGame(gameMinutes, pace).Count());
        public bool EveryStormTornadic => storms.All(s => s.plan.kind == StormKind.Supercell && s.InGame(gameMinutes, pace).Any());
        /// <summary>every storm the event has put down a tornado does, and no other storm does</summary>
        public bool TornadoesAsPlanned => storms.All(s => s.tornadic == s.InGame(gameMinutes, pace).Any());

        public override string ToString() =>
            $"{eventDay}: {storms.Count} storm{(storms.Count == 1 ? "" : "s")}{(storms.Count < eventDay.storms ? $" of {eventDay.storms} planned" : "")}, {TornadoCount} tornado{(TornadoCount == 1 ? "" : "es")} in the game, strongest {(MaxEf < 0 ? "none" : "EF" + MaxEf)} (built for EF{topEf} at most)";
    }
}

// Legendary events (user, 2026-11-09): recreations of real, infamous days (Moore 2013, Pilger 2014, Mayfield 2021),
// the fifth of the day's event types (StormEvent.cs). This is the frame they are written into; none is written yet
// (LegendaryEvents.All is empty), and until one is a day that rolls a legendary event is a regional outbreak.
//
// A legendary event is data: the setup (and so the maps) it belongs to, its environment (a sounding of its own, or the
// setup's on the day), the hour its first storm goes up, and its storms, each placed by hand: where and when it
// initiates, its motion, its precipitation mode, and the tornadoes it must put down (which cycle, how strong, what
// shape, how wide). StormScenario lays the day out from it as written (StormScenario.BuildLegend): no lanes are drawn
// and nothing is pushed apart, so the author keeps the storms 25 miles from each other (Problems says when they are
// not, with everything else that cannot be honoured).
//
// To add one: write it as a LegendaryEvent and Register it (a static list here, or a data file read at start-up),
// then run `validation/parity/csharp -- --events`, which lays every registered event out and prints its Problems.
// Not part of the web build's design.
using System;
using System.Collections.Generic;
using System.Linq;

namespace StormLab
{
    /// <summary>A tornado a legendary storm must put down.</summary>
    public sealed class LegendaryTornado
    {
        /// <summary>the storm's cycle that carries it (0 the first); -1: the storm's strongest tornado of the game</summary>
        public int cycle = -1;
        public int ef;
        /// <summary>its shape and damage-path width, yards; null / 0: the storm's own tornado at this rating
        /// (StormTornadoes.Rate, which changes a shape that cannot carry the rating)</summary>
        public FunnelShape? shape;
        public double widthYd;
        /// <summary>what it is known as ("the Moore tornado"), for the forecast and the record</summary>
        public string name;
    }

    /// <summary>One storm of a legendary event, placed by hand.</summary>
    public sealed class LegendaryStorm
    {
        public string name;
        public Precip precip = Precip.Classic;
        /// <summary>game minutes when it initiates (towering cumulus)</summary>
        public double startMin;
        /// <summary>where it initiates: miles east and north of the map's centre (off the map: it comes onto it along its motion)</summary>
        public double eastMi, northMi;
        /// <summary>its motion: the compass bearing it moves toward, degrees, and its speed, mph (NaN: the environment's own)</summary>
        public double towardDeg = double.NaN, mph = double.NaN;
        /// <summary>the storm's own seed (its cycles and look); 0: drawn from the day's seed, the first that puts down a
        /// tornado during the game</summary>
        public int seed;
        public bool tornadic = true;
        public LegendaryTornado[] tornadoes;
        /// <summary>the cap on its other tornadoes during the game (the ones not written above)</summary>
        public int otherEf = 1;
    }

    public sealed class LegendaryEvent
    {
        /// <summary>"moore-2013"</summary>
        public string key;
        /// <summary>"Moore, Oklahoma", "20 May 2013", and what the forecast says of the day</summary>
        public string name, date, blurb;
        /// <summary>the game setup it belongs to (StormPreset.All's key: its maps, soils and region)</summary>
        public string setup = "plains";
        /// <summary>its own sounding; null: the setup's environment on the day (StormPreset.Day). Its key is set on Register.</summary>
        public StormPreset environment;
        /// <summary>the hour of day its first storm initiates (24 h clock); NaN: the seed's</summary>
        public double startHour = double.NaN;
        public LegendaryStorm[] storms;

        /// <summary>the strongest tornado written into it (-1: none)</summary>
        public int TopEf => storms == null ? -1 : storms.Where(s => s.tornadic && s.tornadoes != null).SelectMany(s => s.tornadoes).Select(t => t.ef).DefaultIfEmpty(-1).Max();
    }

    public static class LegendaryEvents
    {
        /// <summary>a legendary event's own environment is the preset "<setup>@<key>" (StormPreset.Find)</summary>
        public const char EnvSep = '@';

        static readonly List<LegendaryEvent> all = new List<LegendaryEvent>();
        /// <summary>every legendary event written (none yet)</summary>
        public static IReadOnlyList<LegendaryEvent> All => all;

        /// <summary>Add an event (one of the same key is replaced).</summary>
        public static void Register(LegendaryEvent e)
        {
            if (e == null || string.IsNullOrEmpty(e.key)) throw new ArgumentException("a legendary event needs a key");
            Unregister(e.key);
            if (e.environment != null) { e.environment.key = EnvKey(e); if (string.IsNullOrEmpty(e.environment.name)) e.environment.name = e.name; }
            all.Add(e);
        }
        public static bool Unregister(string key) => all.RemoveAll(e => e.key == key) > 0;

        public static LegendaryEvent Find(string key) => all.Find(e => e.key == key);
        /// <summary>the events of a setup, in the order they were registered</summary>
        public static List<LegendaryEvent> For(string setup) => all.Where(e => e.setup == setup && e.storms != null && e.storms.Length > 0).ToList();

        public static string EnvKey(LegendaryEvent e) => e.setup + EnvSep + e.key;
        /// <summary>the event whose environment a preset key names ("plains@moore-2013"); null when it names none</summary>
        public static LegendaryEvent OfEnvKey(string presetKey)
        {
            int i = presetKey?.IndexOf(EnvSep) ?? -1;
            return i > 0 ? Find(presetKey.Substring(i + 1)) : null;
        }

        /// <summary>The setup's event a uniform draw u picks: one the career has not had (seen) while any is left, then
        /// any of them again; null when none is written for the setup.</summary>
        public static LegendaryEvent Pick(string setup, double u, IEnumerable<string> seen = null)
        {
            var mine = For(setup);
            if (mine.Count == 0) return null;
            var had = seen == null ? null : new HashSet<string>(seen);
            var fresh = had == null ? mine : mine.Where(e => !had.Contains(e.key)).ToList();
            var from = fresh.Count > 0 ? fresh : mine;
            return from[Math.Min((int)(u * from.Count), from.Count - 1)];
        }

        /// <summary>What a scenario laid out from an event could not honour (empty: all of it): storms that never reach the
        /// map or come within 25 miles of each other, a written tornado that does not form during the game or is not the
        /// rating written. sc: the event's scenario (StormScenario with legend: e).</summary>
        public static List<string> Problems(LegendaryEvent e, StormScenario sc)
        {
            var p = new List<string>();
            if (e.storms == null || e.storms.Length == 0) { p.Add("no storms"); return p; }
            if (e.storms.Length > StormEvents.MaxStorms) p.Add($"{e.storms.Length} storms written, {StormEvents.MaxStorms} drawn at most");
            if (StormPreset.Find(e.setup) == null) p.Add($"no game setup \"{e.setup}\"");
            for (int i = 0; i < sc.storms.Count; i++)
            {
                var st = sc.storms[i]; var ls = e.storms[i];
                string who = string.IsNullOrEmpty(ls.name) ? $"storm {i + 1}" : ls.name;
                if (double.IsNaN(st.entryMin)) p.Add($"{who}: its track never crosses the map");
                else if (st.entryMin > sc.gameMinutes) p.Add($"{who}: on the map only at {st.entryMin:0} min, after the game");
                if (st.plan.kind != StormKind.Supercell) p.Add($"{who}: the environment gives a {st.plan.kind}, not a supercell");
                var inGame = st.InGame(sc.gameMinutes, sc.pace).ToList();
                if (ls.tornadic && inGame.Count == 0) p.Add($"{who}: no tornado during the game");
                foreach (var lt in ls.tornadoes ?? new LegendaryTornado[0])
                {
                    string what = string.IsNullOrEmpty(lt.name) ? $"its EF{lt.ef}" : lt.name;
                    if (lt.cycle >= 0 && (st.tornadoes == null || lt.cycle >= st.tornadoes.Length || st.tornadoes[lt.cycle] == null)) p.Add($"{who}: {what} is written on cycle {lt.cycle}, which has no tornado (seed {st.seed})");
                    else if (lt.cycle >= 0 && !inGame.Contains(st.tornadoes[lt.cycle])) p.Add($"{who}: {what} on cycle {lt.cycle} forms after the game");
                    else if (!inGame.Any(t => t.ef == lt.ef && (lt.shape == null || t.shape == lt.shape))) p.Add($"{who}: {what} was not made (no EF{lt.ef}{(lt.shape != null ? " " + lt.shape : "")} during the game)");
                }
            }
            double close = sc.ClosestApproachM();
            if (close < StormScenario.MinSeparationM - 1) p.Add($"storms come within {close / StormScenario.MileM:0.0} miles of each other (the game keeps them {StormScenario.MinSeparationM / StormScenario.MileM:0} apart)");
            return p;
        }
    }
}

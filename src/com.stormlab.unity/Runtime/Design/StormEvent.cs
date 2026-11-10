// The day's event type (user, 2026-11-09): what kind of day a seed is, decided first (during the forecast), before any
// storm is laid out. From the most common to the rarest: garden variety, isolated storms, local outbreak, regional
// outbreak, legendary event, generational outbreak. The type decides how many storms the day has, whether each puts down
// a tornado during the game, and how strong the day's tornadoes get; StormScenario lays the storms out from it. A
// legendary event is a recreation of a real, infamous day (StormLegends.cs): none is written yet, and until one is, a
// day that rolls it is a regional outbreak (its neighbour in the list).
//
// One table (StormEvents.All) holds every number, so the mix is tuned in one place:
//   share       how often a seed rolls the type (the list's order is most to least common)
//   storms      how many storms the day has (drawn evenly between the two)
//   tornadoes   the chance each storm puts down a tornado during the game (1: every storm, as the game was before)
//   topEf       the odds of the day's top rating, EF0..EF5: no tornado of the day is stronger, and from EF3 up the
//               headline storm's strongest tornado is made exactly that (StormScenario)
//   secondEf    the rating a second storm's strongest tornado is lifted to (-1: none)
//   floorEf     the rating every tornadic storm's strongest tornado is lifted to (-1: none)
//   maxTornadoes  the most tornadoes one storm puts down during the game (0: as many as its cycles give)
// Draws: stream 530 (the type, the storm count, the top rating, which legend), 540 + i (whether storm i is tornadic).
// Not part of the web build's design.
using System;
using System.Collections.Generic;

namespace StormLab
{
    /// <summary>the day's event, from the most common to the rarest (the order is the user's, 2026-11-09)</summary>
    public enum StormEventType { GardenVariety, IsolatedStorms, LocalOutbreak, RegionalOutbreak, LegendaryEvent, GenerationalOutbreak }

    /// <summary>what a scenario is asked for: the seed's own event, or one of the types whatever the seed rolls (the test
    /// map's panel, the checks)</summary>
    public enum StormEventChoice { Auto, GardenVariety, IsolatedStorms, LocalOutbreak, RegionalOutbreak, LegendaryEvent, GenerationalOutbreak }

    public sealed class StormEventSpec
    {
        public StormEventType type;
        public string key, name, risk, blurb;
        /// <summary>the share of seeds that roll this type (the table's shares add up to 1)</summary>
        public double share;
        public int minStorms, maxStorms;
        /// <summary>the chance each storm puts down a tornado during the game</summary>
        public double tornadoes;
        /// <summary>the odds of the day's top rating, EF0..EF5</summary>
        public double[] topEf;
        /// <summary>a second storm's strongest tornado, and every tornadic storm's, lifted to these ratings (-1: none); never
        /// above the day's top rating</summary>
        public int secondEf = -1, floorEf = -1;
        /// <summary>the most tornadoes a storm puts down during the game (0: as many as its cycles give)</summary>
        public int maxTornadoes;
    }

    /// <summary>One day's event as drawn from its seed.</summary>
    public sealed class StormEventDay
    {
        public int seed;
        /// <summary>what the seed rolled, and what the day is: they differ when a type was asked for, or when a legendary
        /// event was rolled with none written for the setup (fellBack)</summary>
        public StormEventType rolled, type;
        public bool forced, fellBack;
        public StormEventSpec spec;
        /// <summary>the real event recreated (a legendary event only)</summary>
        public LegendaryEvent legend;
        /// <summary>how many storms the day is planned with (the layout may fit fewer: StormScenario.storms)</summary>
        public int storms;
        /// <summary>the strongest rating the day is built to reach</summary>
        public int topEf;
        /// <summary>whether each planned storm puts down a tornado during the game</summary>
        public bool[] tornadic;

        public string Name => legend != null ? legend.name : spec.name;
        public override string ToString() => $"{Name}{(legend != null ? " (legendary event)" : "")}{(fellBack ? " (rolled a legendary event: none written)" : forced && rolled != type ? $" (asked for; the seed rolls {StormEvents.Spec(rolled).name.ToLowerInvariant()})" : "")}";
    }

    public static class StormEvents
    {
        public const int Stream = 530, TornadicStream = 540;
        /// <summary>the most storms a day can have (StormRenderer composites up to four)</summary>
        public const int MaxStorms = 4;

        /// <summary>every event type, in the enum's order: from the most common to the rarest</summary>
        public static readonly StormEventSpec[] All =
        {
            new StormEventSpec { type = StormEventType.GardenVariety, key = "garden", name = "Garden variety", risk = "Marginal risk", share = 0.40,
                minStorms = 1, maxStorms = 2, tornadoes = 0.5, topEf = new double[] { 0.5, 0.5, 0, 0, 0, 0 }, maxTornadoes = 1,
                blurb = "An ordinary storm day: a storm or two, good structure, and at most a brief, weak tornado." },
            new StormEventSpec { type = StormEventType.IsolatedStorms, key = "isolated", name = "Isolated storms", risk = "Slight risk", share = 0.27,
                minStorms = 1, maxStorms = 2, tornadoes = 1, topEf = new double[] { 0, 0.2, 0.55, 0.25, 0, 0 },
                blurb = "One or two discrete supercells with room to themselves. Each should put down a tornado; a strong one is possible." },
            new StormEventSpec { type = StormEventType.LocalOutbreak, key = "local", name = "Local outbreak", risk = "Enhanced risk", share = 0.17,
                minStorms = 2, maxStorms = 3, tornadoes = 1, topEf = new double[] { 0, 0, 0.15, 0.5, 0.35, 0 }, floorEf = 1,
                blurb = "Several tornadic supercells over the area. Strong tornadoes are likely, and a violent one is possible." },
            new StormEventSpec { type = StormEventType.RegionalOutbreak, key = "regional", name = "Regional outbreak", risk = "Moderate risk", share = 0.10,
                minStorms = 3, maxStorms = 4, tornadoes = 1, topEf = new double[] { 0, 0, 0, 0.2, 0.6, 0.2 }, secondEf = 3, floorEf = 2,
                blurb = "Storms all over the map, most of them producing. Expect more than one strong tornado and plan for a violent one." },
            new StormEventSpec { type = StormEventType.LegendaryEvent, key = "legendary", name = "Legendary event", risk = "High risk", share = 0.04,
                minStorms = 1, maxStorms = MaxStorms, tornadoes = 1, topEf = new double[] { 0, 0, 0, 0, 0.5, 0.5 },
                blurb = "A day that went into the books: a real, infamous event, as it happened." },
            new StormEventSpec { type = StormEventType.GenerationalOutbreak, key = "generational", name = "Generational outbreak", risk = "High risk (particularly dangerous situation)", share = 0.02,
                minStorms = 4, maxStorms = 4, tornadoes = 1, topEf = new double[] { 0, 0, 0, 0, 0, 1 }, secondEf = 4, floorEf = 3,
                blurb = "The kind of day that comes once in a generation: every storm a long-lived tornado producer, several of them violent." },
        };

        public static StormEventSpec Spec(StormEventType t) => All[(int)t];
        public static StormEventSpec Find(string key) => Array.Find(All, s => s.key == key);
        public static StormEventType? Forced(StormEventChoice c) => c == StormEventChoice.Auto ? (StormEventType?)null : (StormEventType)((int)c - 1);
        public static StormEventChoice Choice(StormEventType t) => (StormEventChoice)((int)t + 1);

        /// <summary>the item a uniform draw u picks from a list of weights (they need not add up to 1)</summary>
        static int Pick(double u, IReadOnlyList<double> weights)
        {
            double total = 0; foreach (double w in weights) total += w;
            double acc = 0; int last = 0;
            for (int i = 0; i < weights.Count; i++)
            {
                if (weights[i] <= 0) continue;
                last = i; acc += weights[i] / total;
                if (u < acc) return i;
            }
            return last;
        }

        /// <summary>What a seed rolls, whatever is written or asked for.</summary>
        public static StormEventType Roll(int seed)
        {
            var shares = new double[All.Length];
            for (int i = 0; i < All.Length; i++) shares[i] = All[i].share;
            return (StormEventType)Pick(new StormRng(seed, Stream).U(), shares);
        }

        /// <summary>
        /// The day's event from its seed. setup: the game setup the day is played on (StormPreset.BaseKey: a legendary event
        /// belongs to one). forced: this type whatever the seed rolls. legend: this real event (the day is then a legendary
        /// event). legendsSeen: the keys of the legendary events a career has already had, which are not picked again while
        /// another is left. A legendary event with none written for the setup is a regional outbreak (fellBack).
        /// </summary>
        public static StormEventDay Day(int seed, string setup, StormEventType? forced = null, LegendaryEvent legend = null, IEnumerable<string> legendsSeen = null)
        {
            // (every draw is taken whatever the type, so a type asked for keeps the seed's other draws)
            var r = new StormRng(seed, Stream);
            r.U();
            double uCount = r.U(), uTop = r.U(), uLegend = r.U();
            var d = new StormEventDay { seed = seed, rolled = Roll(seed) };
            d.type = legend != null ? StormEventType.LegendaryEvent : forced ?? d.rolled;
            d.forced = legend != null || forced != null;
            if (d.type == StormEventType.LegendaryEvent)
            {
                d.legend = legend ?? LegendaryEvents.Pick(setup, uLegend, legendsSeen);
                if (d.legend == null) { d.type = StormEventType.RegionalOutbreak; d.fellBack = true; }
            }
            d.spec = Spec(d.type);
            if (d.legend != null)
            {
                d.storms = Math.Min(d.legend.storms?.Length ?? 0, MaxStorms);
                d.topEf = d.legend.TopEf;
                d.tornadic = new bool[d.storms];
                for (int i = 0; i < d.storms; i++) d.tornadic[i] = d.legend.storms[i].tornadic;
                return d;
            }
            d.storms = Math.Min(d.spec.minStorms + (int)(uCount * (d.spec.maxStorms - d.spec.minStorms + 1)), d.spec.maxStorms);
            d.topEf = Pick(uTop, d.spec.topEf);
            d.tornadic = new bool[d.storms];
            for (int i = 0; i < d.storms; i++) d.tornadic[i] = d.spec.tornadoes >= 1 || new StormRng(seed, TornadicStream + i).U() < d.spec.tornadoes;
            return d;
        }
    }
}

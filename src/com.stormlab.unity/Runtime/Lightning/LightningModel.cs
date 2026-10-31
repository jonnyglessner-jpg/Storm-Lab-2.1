// Procedural presentation model, not an electrification solver. No storm-genome RNG draws.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace StormLab.Lightning
{
    /// <summary>Crawler: a long, branching discharge that runs out under the anvil (anvil crawler, "spider" lightning) or out of
    /// the storm's side into clear air, and never reaches the ground. Only storm-driven activity produces it.</summary>
    public enum FlashKind { InCloud, NegativeGround, PositiveGround, Crawler }

    [Serializable]
    public sealed class LightningSettings
    {
        public int seed = 1701;
        public float flashesPerMinute = 12;
        public float groundFraction = .25f;
        // Conditional on a ground flash, deliberately configurable rather than tied to severity.
        public float positiveGroundFraction = .08f;
        public float cellRadiusM = 6000;
        public float lowerChargeHeightM = 5500;
        public float upperChargeHeightM = 9500;
        // Ceiling for a storm-driven rate (LightningSystem.ActivityAtTime): candidates are drawn at this rate and thinned.
        // Higher storm rates are clipped to it. It bounds the per-flash cloud-anchor readbacks as well. 200 leaves room for
        // a strong supercell's lightning jump (150-200/min) without flattening it.
        public float maxFlashesPerMinute = 200;
        // Share of ground flashes that reach out of the storm before they strike: out through the cloud, out of the tower's
        // side or the anvil's underside, and down to the ground well out from under where they began, with the biggest
        // branches and now and then a second ground contact. The rest drop through the base.
        public float reachingGroundFraction = .6f;

        public void Validate()
        {
            if (!Finite(maxFlashesPerMinute) || maxFlashesPerMinute <= 0 || maxFlashesPerMinute > 600 ||
                !Finite(flashesPerMinute) || flashesPerMinute < 0 || flashesPerMinute > 600 ||
                !Finite(groundFraction) || groundFraction < 0 || groundFraction > 1 ||
                !Finite(positiveGroundFraction) || positiveGroundFraction < 0 || positiveGroundFraction > 1 ||
                !Finite(reachingGroundFraction) || reachingGroundFraction < 0 || reachingGroundFraction > 1 ||
                !Finite(cellRadiusM) || cellRadiusM <= 0 ||
                !Finite(lowerChargeHeightM) || lowerChargeHeightM < 1000 ||
                !Finite(upperChargeHeightM) || upperChargeHeightM <= lowerChargeHeightM)
                throw new ArgumentOutOfRangeException(nameof(LightningSettings), "Invalid lightning rate, fraction or charge geometry.");
        }
        internal static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }

    public readonly struct FlashEvent
    {
        public readonly long id;
        public readonly double time;
        public readonly uint seed;
        public readonly FlashKind kind;
        /// <summary>0..1, how far from the updraft the flash reaches (LightningActivity.reach when it was drawn)</summary>
        public readonly float reach;
        public FlashEvent(long id, double time, uint seed, FlashKind kind, float reach = 0)
        { this.id = id; this.time = time; this.seed = seed; this.kind = kind; this.reach = reach; }
    }

    /// <summary>A storm's lightning at one moment: total flash rate over the storm and the flash-type split.
    /// Supplied by a storm adapter; a presentation estimate, not an electrification solver.</summary>
    public readonly struct LightningActivity
    {
        public readonly float flashesPerMinute, groundFraction, positiveGroundFraction;
        /// <summary>0..1: how far flashes reach out from the updraft and how many side branches they carry (heightened
        /// moments: a lightning jump). Crawler fraction: share of the in-cloud flashes that are crawlers.</summary>
        public readonly float reach, crawlerFraction;
        public LightningActivity(float flashesPerMinute, float groundFraction, float positiveGroundFraction, float reach = 0, float crawlerFraction = 0)
        {
            this.flashesPerMinute = flashesPerMinute; this.groundFraction = groundFraction;
            this.positiveGroundFraction = positiveGroundFraction; this.reach = reach; this.crawlerFraction = crawlerFraction;
        }
        public static readonly LightningActivity None = new LightningActivity(0, 0, 0);
        public override string ToString() =>
            $"{flashesPerMinute:0.#} flashes/min, {groundFraction * 100:0}% to ground, {positiveGroundFraction * 100:0}% of those positive" +
            (reach > .005f ? $", reach {reach:0.00}" : "") + (crawlerFraction > .005f ? $", {crawlerFraction * 100:0}% crawlers" : "");
    }

    // Ten-second bins have independent streams: seeking costs only the requested window,
    // and frame partitioning never changes the flashes. Settings must stay fixed for a replay.
    public static class LightningSchedule
    {
        // Salt for the storm-driven acceptance/type stream, kept apart from the candidate stream.
        const uint ActivitySalt = 0x5bd1e995u;

        public static void Query(LightningSettings settings, double after, double through, List<FlashEvent> output)
            => Query(settings, after, through, output, null);

        /// <summary>With an activity function, candidates arrive at maxFlashesPerMinute and each is kept with probability
        /// rate(t) / maxFlashesPerMinute, its type drawn from the fractions at t. The function must depend only on t:
        /// then seeking, replay and frame partitioning still return the same flashes. Without one, the fixed settings apply
        /// exactly as before, so existing seeds keep their histories.</summary>
        public static void Query(LightningSettings settings, double after, double through, List<FlashEvent> output,
            Func<double, LightningActivity> activity)
        {
            settings.Validate();
            if (double.IsNaN(after) || double.IsInfinity(after) || double.IsNaN(through) ||
                double.IsInfinity(through) || through < after || through - after > 3600 || through > 1e9)
                throw new ArgumentOutOfRangeException(nameof(through), "Query a finite window of at most one hour, up to 1e9 seconds.");
            output.Clear();
            float rate = activity == null ? settings.flashesPerMinute : settings.maxFlashesPerMinute;
            if (through < 0 || rate == 0) return;
            long first = Math.Max(0, (long)Math.Floor(after / 10));
            long last = (long)Math.Floor(through / 10);
            for (long bin = first; bin <= last; bin++)
            {
                uint binSeed = LightningRandom.Hash((uint)settings.seed, (uint)bin);
                var rng = new LightningRandom(binSeed);
                double t = bin * 10;
                int ordinal = 0;
                while (true)
                {
                    t += -Math.Log(rng.Unit()) * 60 / rate;
                    if (t >= (bin + 1) * 10) break;
                    uint flashSeed = rng.Next();
                    bool inWindow = t > after && t <= through;
                    FlashKind kind; float reach = 0;
                    if (activity == null)
                        kind = rng.Unit() >= settings.groundFraction ? FlashKind.InCloud :
                            (rng.Unit() < settings.positiveGroundFraction ? FlashKind.PositiveGround : FlashKind.NegativeGround);
                    // The storm is only asked about candidates in the window; skipping the rest draws nothing from rng.
                    else if (!inWindow || !Accept(activity(t), settings, binSeed, ordinal, out kind, out reach))
                    { ordinal++; continue; }
                    if (inWindow) output.Add(new FlashEvent(bin * 65536 + ordinal, t, flashSeed, kind, reach));
                    ordinal++;
                }
            }
        }

        static bool Accept(in LightningActivity a, LightningSettings settings, uint binSeed, int ordinal, out FlashKind kind, out float reach)
        {
            kind = FlashKind.InCloud; reach = 0;
            if (!LightningSettings.Finite(a.flashesPerMinute) || !LightningSettings.Finite(a.groundFraction) ||
                !LightningSettings.Finite(a.positiveGroundFraction) || !LightningSettings.Finite(a.reach) ||
                !LightningSettings.Finite(a.crawlerFraction))
                throw new ArgumentOutOfRangeException(nameof(a), "Storm lightning activity must be finite.");
            // Each candidate has its own stream, so acceptance never shifts the candidate times or geometry seeds.
            var pick = new LightningRandom(LightningRandom.Hash(binSeed ^ ActivitySalt, (uint)ordinal));
            float rate = Math.Min(Math.Max(a.flashesPerMinute, 0), settings.maxFlashesPerMinute);
            if (pick.Unit() * settings.maxFlashesPerMinute >= rate) return false;
            float ground = Math.Min(Math.Max(a.groundFraction, 0), 1);
            float positive = Math.Min(Math.Max(a.positiveGroundFraction, 0), 1);
            double u = pick.Unit(), v = pick.Unit();
            kind = u >= ground ? FlashKind.InCloud : v < positive ? FlashKind.PositiveGround : FlashKind.NegativeGround;
            // Drawn after the type draws, so adding crawlers never changes which flashes are cloud or ground.
            double c = pick.Unit();
            if (kind == FlashKind.InCloud && c < Math.Min(Math.Max(a.crawlerFraction, 0), 1)) kind = FlashKind.Crawler;
            reach = Math.Min(Math.Max(a.reach, 0), 1);
            return true;
        }
    }

    internal struct LightningRandom
    {
        uint state;
        public LightningRandom(uint seed) { state = seed == 0 ? 1u : seed; }
        public uint Next() { uint x = state; x ^= x << 13; x ^= x >> 17; x ^= x << 5; return state = x; }
        public double Unit() => ((Next() >> 8) + .5) / 16777216.0;
        public float Range(float a, float b) => a + (b - a) * (float)Unit();
        public static uint Hash(uint a, uint b)
        {
            uint x = unchecked(a ^ (b * 0x9e3779b9u));
            x ^= x >> 16; x = unchecked(x * 0x7feb352du); x ^= x >> 15;
            x = unchecked(x * 0x846ca68bu); return x ^ (x >> 16);
        }
    }

    public sealed class LightningChannel
    {
        public readonly Vector3[] points;
        public readonly float brightness;
        public readonly bool main;
        /// <summary>When this channel's leader sets out (seconds after the flash begins) and how long it takes to reach its
        /// tip. A ground bolt's channels all run with its stepped leader (0, LeaderSeconds); a crawler's branches set out
        /// when the trunk's leader passes their root, so the flash spreads over the anvil instead of appearing whole.</summary>
        public readonly float startAge, growSeconds;
        public LightningChannel(Vector3[] points, float brightness, bool main)
            : this(points, brightness, main, 0, LightningFlash.LeaderSeconds) { }
        public LightningChannel(Vector3[] points, float brightness, bool main, float startAge, float growSeconds)
        {
            this.points = points; this.brightness = brightness; this.main = main;
            this.startAge = startAge; this.growSeconds = Math.Max(.001f, growSeconds);
        }
        /// <summary>0..1: how far this channel's leader has run at a flash age.</summary>
        public float Progress(double age) => (float)Math.Max(0, Math.Min(1, (age - startAge) / growSeconds));
        public float Length()
        {
            float l = 0;
            for (int i = 1; i < points.Length; i++) l += Vector3.Distance(points[i], points[i - 1]);
            return l;
        }
    }

    /// <summary>Cloud-validated world-space anchors supplied by an optional renderer adapter.</summary>
    public readonly struct LightningAnchors
    {
        public readonly Vector3 origin, inCloudTarget;
        public LightningAnchors(Vector3 origin, Vector3 inCloudTarget)
        { this.origin = origin; this.inCloudTarget = inCloudTarget; }
    }

    public sealed class LightningFlash
    {
        public readonly FlashEvent Event;
        public readonly LightningChannel[] Channels;
        public readonly float[] StrokeTimes;
        public readonly Vector3 LightPosition;
        public const float LeaderSeconds = .035f;
        /// <summary>How fast a crawler's leader is seen to run out over the anvil, m/s: a 15 km trunk in a little over a second,
        /// as the long anvil crawlers are seen to crawl. Its channels set out one after another as it passes their roots.</summary>
        public const float CrawlerSpeedMps = 12000;
        /// <summary>Seconds until the whole flash has formed: a ground bolt's stepped leader, or the time a crawler's last
        /// twig has run its length.</summary>
        public float LeadSeconds { get; }
        /// <summary>Seconds the flash is shown: past its last pulse, and past the end of a crawler's crawl.</summary>
        public float Duration => Math.Max(StrokeTimes[StrokeTimes.Length - 1], LeadSeconds + .1f) + .25f;

        LightningFlash(FlashEvent e, LightningChannel[] channels, float[] strokes, Vector3 lightPosition)
        {
            Event = e; Channels = channels; StrokeTimes = strokes; LightPosition = lightPosition;
            float lead = 0;
            foreach (var c in channels) lead = Math.Max(lead, c.startAge + c.growSeconds);
            LeadSeconds = lead;
        }

        /// <summary>Coordinates are world metres, y up. Ground is a flat plane; a terrain adapter may supply the endpoint height.</summary>
        public static LightningFlash Create(FlashEvent e, LightningSettings settings, Vector3 centre, float groundY,
            Func<Vector3, float> terrainHeight = null, LightningAnchors? anchors = null)
        {
            settings.Validate();
            if (e.kind == FlashKind.Crawler) return Crawler(e, settings, centre, groundY, terrainHeight, anchors);
            var rng = new LightningRandom(e.seed);
            float Ground(Vector3 p) => terrainHeight == null ? groundY : terrainHeight(p);
            // Reach (heightened activity) carries flashes further from the updraft and gives them more and longer branches.
            float reach = Math.Min(Math.Max(e.reach, 0), 1);
            bool ground = e.kind != FlashKind.InCloud;
            Vector3 offset = Disk(ref rng, settings.cellRadiusM * .65f);
            Vector3 start = centre + offset + Vector3.UnitY *
                (e.kind == FlashKind.PositiveGround ? settings.upperChargeHeightM : settings.lowerChargeHeightM);
            // A reaching bolt runs out of the storm before it strikes (reachingGroundFraction): out through the cloud, out of
            // the tower's side or the anvil's underside, and down to the ground well out from under where it began, with the
            // biggest branches. The rest drop through the base.
            bool reaching = ground && rng.Unit() < settings.reachingGroundFraction;
            Vector3 end;
            if (e.kind == FlashKind.InCloud)
                end = centre + Disk(ref rng, settings.cellRadiusM) + Vector3.UnitY * settings.upperChargeHeightM;
            else
            {
                end = centre + offset + Disk(ref rng, settings.cellRadiusM * (e.kind == FlashKind.PositiveGround ? 1.7f : .65f) * (1 + 1.5f * reach));
            }
            if (anchors.HasValue)
            {
                var placement = anchors.Value;
                if (!Finite(placement.origin) || !Finite(placement.inCloudTarget) || placement.origin.Y <= groundY)
                    throw new ArgumentOutOfRangeException(nameof(anchors));
                if (e.kind == FlashKind.InCloud) end = placement.inCloudTarget;
                else { end.X += placement.origin.X - start.X; end.Z += placement.origin.Z - start.Z; }
                start = placement.origin;
                if (Vector3.DistanceSquared(start, end) < 1) throw new ArgumentOutOfRangeException(nameof(anchors));
            }
            if (reaching)
            {
                // outward from the updraft through the origin: the way out of the storm's side, or on out under the anvil
                Vector3 outward = new Vector3(start.X - centre.X, 0, start.Z - centre.Z);
                if (outward.LengthSquared() < 250000) outward += Disk(ref rng, 1) * 1000;
                outward = Vector3.Normalize(outward);
                float lateral = (e.kind == FlashKind.PositiveGround ? rng.Range(2500, 7000) : rng.Range(3500, 8500)) * (1 + .6f * reach);
                end = start + outward * lateral + Disk(ref rng, 900);
            }
            if (ground)
            {
                end.Y = Ground(end);
                if (!LightningSettings.Finite(end.Y)) throw new ArgumentOutOfRangeException(nameof(terrainHeight));
            }
            var channels = new List<LightningChannel>();
            Vector3[] main = Path(start, end, ref rng, 128, reaching ? .12f : .14f);
            if (reaching)
            {
                // Out at height first, then the slant down: the channel runs most of the way out through the cloud, bends
                // where it leaves it (the knee) and drops to the ground, as the big bolts out of a storm's side do. The
                // straight path is bent through the knee (a tent over its length), keeping its roughness.
                float along = rng.Range(.35f, .5f);
                Vector3 knee = start + (end - start) * new Vector3(1, 0, 1) * rng.Range(.45f, .65f);
                knee.Y = start.Y - (start.Y - end.Y) * rng.Range(.25f, .4f);
                Vector3 bend = knee - (start + (end - start) * along);
                for (int i = 1; i < main.Length - 1; i++)
                {
                    float f = i / 128f;
                    main[i] += bend * (f < along ? f / along : (1 - f) / (1 - along));
                }
            }
            for (int i = 1; i < main.Length - 1; i++) main[i].Y = Math.Max(main[i].Y, Ground(main[i]) + 5);
            channels.Add(new LightningChannel(main, 1, true));
            // Branches inherit a local direction; fine and broad bends are correlated at several scales. A ground bolt's point
            // down and out (the stepped leader's failed tries): a handful of them, most short stubs, one in three a long one
            // that may fork once more. Calmer than the first version's (user, 2026-10-31: the branches needed calming): a
            // real bolt's branches follow its trunk down, and the trunk stays the one thing the eye reads.
            float big = reaching ? 1.35f : 1;
            int branches = (e.kind == FlashKind.PositiveGround ? 3 : 6) + (int)(3 * reach) + (reaching ? 1 : 0);
            for (int b = 0; b < branches; b++)
            {
                int at = reaching ? (int)rng.Range(24, 112) : (int)rng.Range(12, 108);
                Vector3 direction = Vector3.Normalize(main[Math.Min(at + 5, 128)] - main[at]);
                Vector3 side = Disk(ref rng, 1);
                bool longOne = rng.Unit() < .3;
                float length = (longOne ? rng.Range(800, 1600) : rng.Range(250, 800)) * big * (1 + .5f * reach);
                Vector3 tip = main[at] + Vector3.Normalize(direction * .9f + side * .7f - Vector3.UnitY * (ground ? rng.Range(.1f, .6f) : 0)) * length;
                // Side leaders are unconnected: they must not accidentally become extra ground contacts.
                tip.Y = Math.Max(groundY + 80, tip.Y);
                var branch = Path(main[at], tip, ref rng, 32, .1f);
                for (int i = 1; i < branch.Length; i++) branch[i].Y = Math.Max(branch[i].Y, Ground(branch[i]) + 20);
                float bright = rng.Range(.1f, .3f) * (1 + .3f * reach);
                channels.Add(new LightningChannel(branch, bright, false));
                if (longOne && length > 1100)
                {
                    int from = (int)rng.Range(10, 24);
                    Vector3 d2 = Vector3.Normalize(branch[Math.Min(from + 4, 32)] - branch[from]);
                    Vector3 tip2 = branch[from] + Vector3.Normalize(d2 * .8f + Disk(ref rng, 1) * .7f - Vector3.UnitY * (ground ? rng.Range(.1f, .5f) : 0)) * length * rng.Range(.3f, .5f);
                    tip2.Y = Math.Max(groundY + 80, tip2.Y);
                    var twig = Path(branch[from], tip2, ref rng, 16, .1f);
                    for (int i = 1; i < twig.Length; i++) twig[i].Y = Math.Max(twig[i].Y, Ground(twig[i]) + 20);
                    channels.Add(new LightningChannel(twig, bright * rng.Range(.5f, .8f), false));
                }
            }
            if (ground && rng.Unit() < (reaching ? .3 : .12))
            {
                // A forked strike: one branch makes it to the ground too, a few hundred metres to a mile from the first
                // contact, and carries the return strokes with the trunk (so it is a main channel, dimmer).
                int at = (int)rng.Range(reaching ? 72 : 60, 108);
                float angle = rng.Range(0, (float)Math.PI * 2), r = rng.Range(400, 1800) * big;
                Vector3 foot = end + new Vector3((float)Math.Cos(angle) * r, 0, (float)Math.Sin(angle) * r);
                foot.Y = Ground(foot);
                if (!LightningSettings.Finite(foot.Y)) throw new ArgumentOutOfRangeException(nameof(terrainHeight));
                var fork = Path(main[at], foot, ref rng, 64, .13f);
                for (int i = 1; i < fork.Length - 1; i++) fork[i].Y = Math.Max(fork[i].Y, Ground(fork[i]) + 5);
                channels.Add(new LightningChannel(fork, rng.Range(.45f, .75f), true));
            }
            int count = e.kind == FlashKind.NegativeGround ? (int)rng.Range(1, 6) :
                e.kind == FlashKind.InCloud ? (int)rng.Range(2, 5) : 1;
            var strokes = new float[count];
            strokes[0] = LeaderSeconds;
            for (int i = 1; i < count; i++) strokes[i] = strokes[i - 1] + rng.Range(.035f, .095f);
            return new LightningFlash(e, channels.ToArray(), strokes, start);
        }

        /// <summary>
        /// Crawler geometry. Most run nearly horizontally from the upper updraft out under the anvil, throwing long side
        /// branches that spread sideways like a spider's legs, forking as they go: some hang below the anvil's underside in
        /// the clear, the rest run through it and light it from inside. The others leave the storm's side and end in clear
        /// air. Neither reaches the ground. With cloud anchors the origin is in the upper cloud and the far end in the anvil.
        /// </summary>
        static LightningFlash Crawler(FlashEvent e, LightningSettings settings, Vector3 centre, float groundY,
            Func<Vector3, float> terrainHeight, LightningAnchors? anchors)
        {
            var rng = new LightningRandom(e.seed);
            float reach = Math.Min(Math.Max(e.reach, 0), 1);
            float Ground(Vector3 p) => terrainHeight == null ? groundY : terrainHeight(p);
            Vector3 start = centre + Disk(ref rng, settings.cellRadiusM * .4f) + Vector3.UnitY * settings.upperChargeHeightM;
            Vector3 far = centre + Disk(ref rng, settings.cellRadiusM * (2.2f + 1.5f * reach)) + Vector3.UnitY * (settings.upperChargeHeightM - 1200);
            if (anchors.HasValue)
            {
                var placement = anchors.Value;
                if (!Finite(placement.origin) || !Finite(placement.inCloudTarget) || placement.origin.Y <= groundY)
                    throw new ArgumentOutOfRangeException(nameof(anchors));
                start = placement.origin; far = placement.inCloudTarget;
                if (Vector3.DistanceSquared(start, far) < 1) throw new ArgumentOutOfRangeException(nameof(anchors));
            }
            bool toAir = rng.Unit() < .35;
            Vector3 end = far;
            if (toAir)
            {
                // Out of the storm's side: away from the updraft and down, ending in clear air well above the ground.
                Vector3 outward = new Vector3(far.X - centre.X, 0, far.Z - centre.Z);
                if (outward.LengthSquared() < 1) outward = Disk(ref rng, 1);
                outward = Vector3.Normalize(outward);
                end = start + outward * rng.Range(6000, 10000) * (.85f + .4f * reach);
                end.Y = start.Y - rng.Range(2500, 5000);
            }
            // An anvil crawler's far end is in the anvil's lowest layer (the anchor); the trunk slopes on down out of it, so
            // its outer half runs just under the anvil's base in the clear, where it and its hanging legs are seen whole.
            if (!toAir) end.Y -= rng.Range(400, 1000);
            float Floor(Vector3 p) => Ground(p) + (toAir ? 900 : 2500);
            end.Y = Math.Max(end.Y, Floor(end));
            var channels = new List<LightningChannel>();
            Vector3[] main = Path(start, end, ref rng, 128, toAir ? .12f : .07f);
            if (!toAir)
            {
                // Out of the tower top and down to the far end's height within the first third, then level: the trunk
                // leaves the anvil's thick root early and runs the rest of the way just under the anvil's base.
                for (int i = 1; i < main.Length - 1; i++)
                {
                    float t = i / 128f, u = Math.Min(1, t / .3f);
                    main[i].Y += (start.Y + (end.Y - start.Y) * u * u * (3 - 2 * u)) - (start.Y + (end.Y - start.Y) * t);
                }
            }
            for (int i = 1; i < main.Length; i++) main[i].Y = Math.Max(main[i].Y, Floor(main[i]));
            // The crawl: the trunk's leader runs out at CrawlerSpeedMps (a long one held to a couple of seconds), and every
            // channel off it sets out when the leader passes its root and runs its own length at the same speed, so the
            // flash is seen to flow out over the anvil and fork as it goes, never to appear whole (user, 2026-10-31).
            float trunkLength = PathLength(main);
            float trunkSeconds = Math.Min(2.2f, Math.Max(.5f, trunkLength / CrawlerSpeedMps));
            float speed = trunkLength / trunkSeconds;
            var trunk = new LightningChannel(main, 1, true, 0, trunkSeconds);
            channels.Add(trunk);
            LightningChannel Sprout(LightningChannel parent, int at, Vector3 tip, int segments, float roughness, float bright)
            {
                var pts = Path(parent.points[at], tip, ref rng, segments, roughness);
                for (int i = 1; i < pts.Length; i++) pts[i].Y = Math.Max(pts[i].Y, Floor(pts[i]));
                float start = parent.startAge + parent.growSeconds * at / (float)(parent.points.Length - 1);
                return new LightningChannel(pts, bright, false, start, Math.Max(.06f, PathLength(pts) / speed));
            }
            // The trunk forks: one or two secondary trunks leave it a quarter to two thirds of the way out, diverging 20 to
            // 50 degrees, and run on nearly as far, so the flash fans out over the anvil instead of following one line.
            var trunks = new List<LightningChannel> { trunk };
            int trunkForks = toAir ? (rng.Unit() < .5 ? 1 : 0) : (int)rng.Range(1, 3);
            for (int f = 0; f < trunkForks; f++)
            {
                int at = (int)rng.Range(30, 85);
                Vector3 d = main[Math.Min(at + 8, 128)] - main[at]; d.Y = 0;
                d = d.LengthSquared() > 1 ? Vector3.Normalize(d) : Vector3.UnitX;
                float angle = rng.Range(.35f, .9f) * (rng.Unit() < .5 ? -1 : 1);
                float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
                Vector3 dir = new Vector3(d.X * cos - d.Z * sin, 0, d.X * sin + d.Z * cos);
                float len = Vector3.Distance(main[at], main[128]) * rng.Range(.6f, 1.1f);
                Vector3 tip = main[at] + dir * len;
                tip.Y = main[at].Y + (toAir ? -rng.Range(300, 1500) : rng.Range(-600, 200));
                tip.Y = Math.Max(tip.Y, Floor(tip));
                var fork = Sprout(trunk, at, tip, 64, toAir ? .1f : .07f, rng.Range(.6f, .85f));
                channels.Add(fork); trunks.Add(fork);
            }
            // Legs along every trunk, spreading sideways like a spider's legs, each forking again like a river delta seen
            // from above. Under the anvil a good half hang below its underside, seen whole from the ground.
            foreach (var t in trunks)
            {
                int segments = t.points.Length - 1;
                int legs = Math.Max(2, Math.Min(12, (int)(t.Length() / (toAir ? 1800 : 1500) * (1 + .4f * reach))));
                for (int b = 0; b < legs; b++)
                {
                    int at = (int)rng.Range(segments * .06f, segments * .94f);
                    Vector3 direction = t.points[Math.Min(at + Math.Max(1, segments / 25), segments)] - t.points[at]; direction.Y = 0;
                    direction = direction.LengthSquared() > 1 ? Vector3.Normalize(direction) : Vector3.UnitX;
                    Vector3 side = Disk(ref rng, 1);
                    float length = (toAir ? rng.Range(600, 2200) : rng.Range(1200, 4500)) * (.8f + .6f * reach);
                    Vector3 tip = t.points[at] + Vector3.Normalize(direction * .45f + side) * length;
                    bool under = !toAir && rng.Unit() < .55;
                    tip.Y = t.points[at].Y + (toAir ? -rng.Range(200, 1400) : under ? -rng.Range(500, 1600) : rng.Range(-100, 1200));
                    tip.Y = Math.Max(tip.Y, Floor(tip));
                    float bright = rng.Range(.3f, .65f) * (t == trunk ? 1 : .85f);
                    var leg = Sprout(t, at, tip, 32, .1f, bright);
                    channels.Add(leg);
                    int forks = (int)rng.Range(0, 3);
                    for (int f = 0; f < forks; f++)
                    {
                        int from = (int)rng.Range(8, 26);
                        Vector3 d2 = leg.points[Math.Min(from + 4, 32)] - leg.points[from]; d2.Y = 0;
                        d2 = d2.LengthSquared() > 1 ? Vector3.Normalize(d2) : direction;
                        Vector3 tip2 = leg.points[from] + Vector3.Normalize(d2 * .4f + Disk(ref rng, 1)) * length * rng.Range(.25f, .6f);
                        tip2.Y = leg.points[from].Y + (under || toAir ? -rng.Range(0, 600) : rng.Range(-200, 500));
                        tip2.Y = Math.Max(tip2.Y, Floor(tip2));
                        channels.Add(Sprout(leg, from, tip2, 16, .1f, bright * rng.Range(.55f, .85f)));
                    }
                }
            }
            // A crawler flickers: pulses (recoil leaders brightening the channels already formed) spread from a third of the
            // way through the crawl to its end, where the whole network lights once more; between them the creeping leader
            // itself glows (Intensity).
            float crawl = 0;
            foreach (var c in channels) crawl = Math.Max(crawl, c.startAge + c.growSeconds);
            int count = (int)rng.Range(3, 7);
            var strokes = new float[count];
            float first = trunkSeconds * rng.Range(.3f, .5f), last = Math.Max(first + .1f, crawl - .02f);
            for (int i = 0; i < count; i++)
            {
                float f = count == 1 ? 1 : i / (float)(count - 1);
                strokes[i] = first + (last - first) * f + (i > 0 && i < count - 1 ? rng.Range(-.04f, .04f) : 0);
                if (i > 0) strokes[i] = Math.Max(strokes[i], strokes[i - 1] + .06f);
            }
            return new LightningFlash(e, channels.ToArray(), strokes, main[main.Length / 2]);
        }

        static bool Finite(Vector3 p) => LightningSettings.Finite(p.X) && LightningSettings.Finite(p.Y) && LightningSettings.Finite(p.Z);

        static float PathLength(Vector3[] p)
        {
            float l = 0;
            for (int i = 1; i < p.Length; i++) l += Vector3.Distance(p[i], p[i - 1]);
            return l;
        }

        static Vector3 Disk(ref LightningRandom rng, float radius)
        {
            float angle = rng.Range(0, (float)Math.PI * 2), r = radius * (float)Math.Sqrt(rng.Unit());
            return new Vector3((float)Math.Cos(angle) * r, 0, (float)Math.Sin(angle) * r);
        }

        static Vector3[] Path(Vector3 a, Vector3 b, ref LightningRandom rng, int segments, float roughness)
        {
            var p = new Vector3[segments + 1]; p[0] = a; p[segments] = b;
            Vector3 axis = Vector3.Normalize(b - a);
            Vector3 u = Vector3.Normalize(Vector3.Cross(axis, Math.Abs(axis.Y) > .9f ? Vector3.UnitX : Vector3.UnitY));
            Vector3 v = Vector3.Cross(axis, u);
            float amplitude = Vector3.Distance(a, b) * roughness;
            for (int stride = segments; stride > 1; stride /= 2)
            {
                for (int i = 0; i < segments; i += stride)
                    p[i + stride / 2] = (p[i] + p[i + stride]) * .5f +
                        (u * rng.Range(-1, 1) + v * rng.Range(-1, 1)) * amplitude;
                amplitude *= .57f;
            }
            return p;
        }

        /// <summary>Peak radiance over an exposure interval, so a short stroke survives a dropped render frame.</summary>
        public float Intensity(double ageFrom, double ageThrough)
        {
            if (ageThrough < 0 || ageFrom > Duration || ageThrough < ageFrom) return 0;
            float peak = 0;
            bool crawler = Event.kind == FlashKind.Crawler;
            for (int i = 0; i < StrokeTimes.Length; i++)
            {
                double age = Math.Max(ageFrom, StrokeTimes[i]) - StrokeTimes[i];
                if (ageThrough < StrokeTimes[i]) continue;
                // a crawler's pulses are slower and softer than a return stroke: its channels brighten and fade over tenths
                double decay = crawler ? .05 : i == 0 ? .024 : .016;
                float weight = crawler ? (i == 0 ? .8f : .6f) : (i == 0 ? 1 : .72f);
                peak = Math.Max(peak, (float)Math.Exp(-age / decay) * weight);
            }
            // the creeping leader's own glow while a crawler is still running out over the anvil: well seen, as the crawl
            // is what the eye follows, below the pulses that go over it
            if (crawler && ageFrom < LeadSeconds + .1) peak = Math.Max(peak, .55f);
            if (Event.kind == FlashKind.PositiveGround && ageThrough >= LeaderSeconds)
                peak = Math.Max(peak, .12f * (float)Math.Exp(-Math.Max(0, ageFrom - LeaderSeconds) / .12));
            return peak;
        }

        /// <summary>0..1: how far the whole flash has formed (its channels' own Progress differs for a crawler).</summary>
        public float LeaderProgress(double age) => (float)Math.Max(0, Math.Min(1, age / LeadSeconds));
    }

    public static class LightningAcoustics
    {
        public static float SoundSpeed(float temperatureC) => 331.3f + .606f * temperatureC;
        public static float ArrivalSeconds(Vector3 channelPoint, Vector3 observer, float temperatureC = 20)
        {
            float speed = SoundSpeed(temperatureC);
            if (!LightningSettings.Finite(speed) || speed <= 0) throw new ArgumentOutOfRangeException(nameof(temperatureC));
            return Vector3.Distance(channelPoint, observer) / speed;
        }
    }
}

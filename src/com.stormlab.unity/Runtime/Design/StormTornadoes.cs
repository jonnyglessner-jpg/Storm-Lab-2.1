// StormLab 2.0's tornadoes on the web build's cycles. The web build decides when each cycle's tornado forms, ropes out
// and lifts, how wide it is, and its shape among five (StormDesign, StormCycles). This adds what made 2.0's tornadoes look
// like themselves (2.0's StormGenome.ApplyShape and Individualise, DesignedStorm.TornadoOf, kept as close to them as the
// web build's cycles allow): a rating and wind from its width, the four shapes the web build lacks (drillbit, elephant
// trunk, dust whirl, sidewinder), per-tornado proportions and lean, the shapes it passes through in its life, its skin
// (ragged, laminar or ghost), brightness, the soil it lofts, suction vortices for big ones and a satellite tornado. The
// draws come from 2.0's generator (SplitMix64) on their own streams, so the web build's storm is unchanged.
//
// Each frame, Apply writes the extra slots 2.0's tornado drawing reads (StormMarch.compute: S_tor6 .. S_tor11) and
// adjusts the tornado slots the web build filled (shape, proportions, lean, suction vortices). Only the detailed
// tornado (StormRenderer.tornado = Detailed) uses them; the web build's tornado reads the block as StormDesign left it.
using System;

namespace StormLab
{
    public enum ClimoRegion { Plains, Midwest, Dixie }

    /// <summary>2.0's tornado shapes (StormGenome.TornadoShape, same order: the shader reads the index).</summary>
    public enum FunnelShape { Rope, Cone, Stovepipe, Wedge, MultiVortex, Drillbit, ElephantTrunk, DustWhirl, Sidewinder }

    /// <summary>The funnel's surface: torn and ragged (most), smooth and laminar with fine striations, or thin and translucent.</summary>
    public enum FunnelSkin { Ragged, Laminar, Ghost }

    /// <summary>Small, platform-stable random generator (SplitMix64), as 2.0's StormRng.</summary>
    public struct StormRng
    {
        ulong s;
        public StormRng(int seed, int stream = 0) { s = Mix(Mix(((ulong)(uint)seed << 32) | (uint)stream) ^ 0xD1B54A32D192ED03UL); }
        static ulong Mix(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
        public ulong Next()
        {
            ulong z = s += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL; z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
        /// <summary>uniform in [0, 1)</summary>
        public double U() => (Next() >> 11) * (1.0 / 9007199254740992.0);
        public double Range(double a, double b) => a + (b - a) * U();
        /// <summary>standard normal (Box-Muller)</summary>
        public double N() { double u1 = Math.Max(U(), 1e-12), u2 = U(); return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2); }
    }

    /// <summary>One stage of a tornado's life: the shape it takes from startFrac of its life on, and its visible funnel then.</summary>
    public struct FunnelPhase
    {
        public FunnelShape shape;
        public double startFrac;
        /// <summary>visible radius at the ground and the top, km; profile (0 slender .. 1 wide all the way down); sinuosity</summary>
        public double groundKm, topKm, profile, sinuosity;
        /// <summary>condensation reach relative to the web build's</summary>
        public double cond;
        /// <summary>sidewinder: how far the lower end is dragged sideways along the ground, km</summary>
        public double sideKm;
        /// <summary>the vortex's swirl ratio in this phase (StormTornadoes.SwirlOf)</summary>
        public double swirl;
    }

    /// <summary>One cycle's tornado as 2.0 would have drawn it.</summary>
    public sealed class TornadoCharacter
    {
        public int cycle;
        /// <summary>the web build's shape for this cycle, and the main shape here (it may be one of 2.0's extra four)</summary>
        public TornadoShape webShape; public FunnelShape shape;
        /// <summary>storm minutes: touchdown, the start of the rope-out, and gone (the web build's milestones)</summary>
        public double formMin, ropeMin, goneMin;
        public int ef;
        /// <summary>peak 3-s gust, m/s; radius of maximum wind, km; damage-path width, m</summary>
        public double vmax, rcKm, widthM;
        /// <summary>the web build's ground and top radius, km, and this tornado's (its proportions applied)</summary>
        public double webGroundKm, webTopKm, groundKm, topKm;
        public double profile, sinuosity, condReach, dust, rainWrap, ropeFrac;
        public int subVortices; public double multiFrac; public bool collar;
        /// <summary>the vortex's swirl ratio in its main shape (StormTornadoes.SwirlOf): what decides its form</summary>
        public double swirl;
        public FunnelPhase[] phases;
        /// <summary>lean of the ground end from the top, km, toward leanAngle (radians from the storm's motion, + left)</summary>
        public double leanKm, leanAngle, sideAngle;
        public FunnelSkin skin; public double albedo, roughness, striation, ghost;
        public double soilR, soilG, soilB, dirt; public string soilName;
        public bool satellite; public double satFrac, satLifeS, satOrbitKm, satRadKm, satVmax, satAngle;
        /// <summary>a cone's or stovepipe's formation: the share of its life it takes to widen from a thin vortex, how much
        /// sooner (+) or later (-) its top widens than its foot, how thin it starts (a share of its foot's full width), and
        /// how much it snakes while thin</summary>
        public double formFrac, formTopLead, formThin, formSinu;
        public const double MorphFrac = 0.1;
        public double LifeMin => goneMin - formMin;
        /// <summary>wind at radius r km, m/s (Rankine vortex, peak at rc)</summary>
        public double WindAt(double rKm) => rKm <= rcKm ? vmax * rKm / Math.Max(rcKm, 1e-3) : vmax * rcKm / rKm;
    }

    public static class StormTornadoes
    {
        /// <summary>first of the extra slots (S_tor6), and how many</summary>
        public const int Slot0 = StormDesign.NSLOT, Slots = 6;
        /// <summary>the whole parameter block with the extra slots</summary>
        public const int NSLOT = StormDesign.NSLOT + Slots;

        public static readonly string[] ShapeName = { "Rope tornado", "Cone tornado", "Stovepipe tornado", "Wedge tornado", "Multiple-vortex tornado", "Drillbit tornado", "Elephant-trunk tornado", "Dust whirl under the wall cloud", "Sidewinder tornado" };

        static double Ramp(double t, double a, double b) { double x = C.Clamp((t - a) / (b - a), 0, 1); return x * x * (3 - 2 * x); }

        /// <summary>the climate region for a preset's soils and skins (dixie: Dixie, ohio: Midwest, else the Plains)</summary>
        public static ClimoRegion RegionOf(string preset)
        {
            var k = preset != null && preset.StartsWith(StormPreset.WebPrefix, StringComparison.Ordinal) ? preset.Substring(StormPreset.WebPrefix.Length) : preset;
            return k == "dixie" ? ClimoRegion.Dixie : k == "ohio" ? ClimoRegion.Midwest : ClimoRegion.Plains;
        }

        static FunnelShape FromWeb(TornadoShape s) => s switch
        {
            TornadoShape.Rope => FunnelShape.Rope, TornadoShape.Cone => FunnelShape.Cone, TornadoShape.Stovepipe => FunnelShape.Stovepipe,
            TornadoShape.Wedge => FunnelShape.Wedge, _ => FunnelShape.MultiVortex
        };
        /// <summary>visible ground radius as a fraction of the damage path's half-width, by shape (2.0's BaseGeometry)</summary>
        static double ShapeWidth(FunnelShape s) => s switch
        {
            FunnelShape.Wedge => 0.8, FunnelShape.MultiVortex => 0.75, FunnelShape.Stovepipe => 0.6, FunnelShape.ElephantTrunk => 0.5,
            FunnelShape.DustWhirl => 0.5, FunnelShape.Cone => 0.45, FunnelShape.Drillbit => 0.4, FunnelShape.Rope => 0.35, _ => 0.3
        };

        /// <summary>2.0's BaseGeometry: a shape's visible funnel for a damage path W m wide; u picks the bend within the shape's range.</summary>
        static void BaseGeometry(FunnelShape shape, double W, double u, out double g, out double top, out double profile, out double sinu)
        {
            double half = 0.5 * W / 1000;
            switch (shape)
            {
                case FunnelShape.Rope:          g = 0.35 * half; top = 2.8 * g + 0.05;  profile = 0.08; sinu = C.Lerp(0.5, 0.8, u); break;
                case FunnelShape.Drillbit:      g = 0.4 * half;  top = 4.0 * g + 0.08;  profile = 0.22; sinu = C.Lerp(0.05, 0.25, u); break;
                case FunnelShape.ElephantTrunk: g = 0.5 * half;  top = 2.6 * g + 0.08;  profile = 0.18; sinu = C.Lerp(0.7, 1.0, u); break;
                case FunnelShape.Cone:          g = 0.45 * half; top = 3.4 * g + 0.1;   profile = 0.6;  sinu = C.Lerp(0.05, 0.2, u); break;
                case FunnelShape.Stovepipe:     g = 0.6 * half;  top = 1.45 * g + 0.06; profile = 0.85; sinu = C.Lerp(0.0, 0.1, u); break;
                // a wedge is a broad funnel, not a block: its sides flare out into the base, about twice as wide at the top
                // as at the ground (2.0: a widened drillbit looks far more like a real wedge than a near-cylinder). Profile
                // 0.85 makes its sides nearly straight, slanting evenly out to the top (user, 2026-10-26: 0.55 kept it
                // narrow two thirds of the way up, then flared it suddenly)
                case FunnelShape.Wedge:         g = 0.8 * half;  top = 1.9 * g + 0.15;  profile = 0.85; sinu = 0.03; break;
                case FunnelShape.MultiVortex:   g = 0.75 * half; top = 1.35 * g + 0.1;  profile = 0.9;  sinu = 0.05; break;
                case FunnelShape.Sidewinder:    g = 0.3 * half;  top = 3.0 * g + 0.05;  profile = 0.1;  sinu = C.Lerp(0.6, 0.9, u); break;
                default:                        g = 0.5 * half;  top = 3.0 * g + 0.08;  profile = 0.4;  sinu = C.Lerp(0.1, 0.35, u); break;   // dust whirl
            }
            g = C.Clamp(g, 0.012, 0.95);
            top = C.Clamp(top, g, 1.4);
        }

        /// <summary>
        /// One tornado character per circulation of the plan: its seeded cycles, or the teaching sequence's two. Null for storms
        /// without tornadoes. lclM: the sounding's LCL, m; hpness: the web build's -1 (LP) .. 1 (HP).
        /// </summary>
        public static TornadoCharacter[] Build(StormPlan P, ClimoRegion region)
        {
            if (P.kind != StormKind.Supercell) return null;
            int seed = P.seed ?? 1701;
            TornadoCharacter[] o;
            if (P.cycles != null)
            {
                o = new TornadoCharacter[P.cycles.Length];
                for (int k = 0; k < o.Length; k++)
                {
                    var c = P.cycles[k];
                    if (!c.tornadic) continue;
                    // milestones: start, +7, form, mature, (multi-vortex mid), rope, thin, gone, fade, end
                    int n = c.times.Length;
                    o[k] = Make(P, region, seed, k, c.shape, c.times[2], c.times[n - 5], c.times[n - 3], c.radius, c.radius * c.topRatio, c.dust);
                }
            }
            else
            {
                // the teaching sequence (StormDesign): a tornado 18-31.5 min into the cycle at 30 min, then a violent one that
                // starts as a multiple-vortex tornado 15 min into the cycle at 68 min and lifts 45 min in
                o = new TornadoCharacter[2];
                if (P.tornadic)
                {
                    var s0 = P.torType > 0.7 ? TornadoShape.Wedge : P.torType > 0.3 ? TornadoShape.Cone : TornadoShape.Rope;
                    double top0 = P.torR * (P.torType > 0.7 ? 1.4 : 2.6) + 0.12;
                    o[0] = Make(P, region, seed, 0, s0, 48, 54, 61.5, P.torR, top0, 1);
                    o[1] = Make(P, region, seed, 1, TornadoShape.Multivortex, 83, 100, 113, 0.8, 0.8 * 1.2 + 0.12, 1);
                }
            }
            return o;
        }

        static TornadoCharacter Make(StormPlan P, ClimoRegion region, int seed, int k, TornadoShape web, double formMin, double ropeMin, double goneMin, double groundKm, double topKm, double dust)
        {
            var t = new TornadoCharacter { cycle = k, webShape = web, formMin = formMin, ropeMin = ropeMin, goneMin = goneMin, webGroundKm = groundKm, webTopKm = topKm };
            var r = new StormRng(seed, 900 + k);
            double dry = C.Clamp((P.lclM - 1300) / 900, 0, 1);
            // the damage path the web build's funnel implies, and the wind of a tornado that wide: the storm's median tornado
            // (StormDiag.vMed) grown with width; the rating follows the EF scale's 3-s gusts
            var main = FromWeb(web);
            t.widthM = 2 * groundKm / ShapeWidth(main) * 1000;
            t.vmax = WindOf(P.vTor, t.widthM);
            t.ef = EfOf(t.vmax);
            t.rcKm = C.Clamp(0.5 * t.widthM / 1000 * 29.0 / t.vmax, 0.012, 0.6);
            // condensation (2.0's PickShape): high, dry bases leave a gap filled by a dust whirl
            double cond = C.Clamp(1.3 - (P.lclM - 700) / 1300, 0.2, 1) + 0.25 * C.Clamp((t.vmax - 45) / 50, 0, 1);
            t.condReach = C.Clamp(cond * r.Range(0.85, 1.1), 0.15, 1);
            // the shapes the web build lacks, drawn among its slender ones as 2.0 weighed them: elephant trunks and
            // sidewinders among ropes, drillbits among cones, and dust whirls where the condensation stays high
            double u = r.U();
            if (main == FunnelShape.Rope) main = u < 0.3 ? FunnelShape.ElephantTrunk : u < 0.4 ? FunnelShape.Sidewinder : main;
            else if (main == FunnelShape.Cone && u < 0.25) main = FunnelShape.Drillbit;
            if ((main == FunnelShape.Rope || main == FunnelShape.Cone || main == FunnelShape.Drillbit) && t.condReach < 0.45 && r.U() < 0.7) main = FunnelShape.DustWhirl;
            t.shape = main;
            // the main phase keeps the web build's size; profile and bend from 2.0's shape
            BaseGeometry(main, t.widthM, main == FunnelShape.Wedge || main == FunnelShape.MultiVortex ? 0.5 : r.U(), out double g2, out double top2, out t.profile, out t.sinuosity);
            t.groundKm = groundKm;
            // a shape the web build has keeps its top; one of 2.0's extra shapes takes 2.0's flare at the web build's width
            t.topKm = FromWeb(web) == main ? topKm : C.Clamp(top2 / g2 * groundKm, groundKm, 1.4);
            if (main == FunnelShape.DustWhirl) t.condReach = Math.Min(t.condReach, 0.3);
            // suction vortices (2.0's ApplyShape): the multiple-vortex shape keeps them most of its life; big, strong wedges
            // and stovepipes start as multiple vortices before a central vortex takes over
            if (main == FunnelShape.MultiVortex) { t.subVortices = 3 + (int)(r.U() * 4); t.multiFrac = r.Range(0.6, 0.85); }
            else if ((main == FunnelShape.Wedge || main == FunnelShape.Stovepipe) && t.ef >= 2 && r.U() < 0.55) { t.subVortices = 3 + (int)(r.U() * 3); t.multiFrac = r.Range(0.15, 0.3); }
            // a stovepipe is one smooth, laminar vortex all its life (user, 2026-10-26): it keeps the draws, not the vortices
            if (main == FunnelShape.Stovepipe) { t.subVortices = 0; t.multiFrac = 0; }
            t.collar = main == FunnelShape.Wedge && t.LifeMin > 10 && r.U() < 0.6;
            t.ropeFrac = C.Clamp((goneMin - ropeMin) / Math.Max(t.LifeMin, 0.1), 0.1, 0.6);
            t.dust = dust;
            t.rainWrap = C.Clamp((P.hpness - 0.2) / 0.6, 0, 1) * r.Range(0.6, 1.0);
            // what makes this tornado itself: 2.0's own stream for it; held within its shape's limits
            var ri = new StormRng(seed, 300 + k);
            Individualise(t, P, region, ref ri, true);
            DrawFormation(t, new StormRng(seed, 1100 + k));
            return t;
        }

        static readonly double[] EfGust = { 33, 44, 55, 67, 82, 95 };   // the middle of each EF rating's 3-s gusts, m/s
        static readonly double[] EfLo = { 29, 38.5, 49.6, 60.8, 74.2, 89.9, 135.5 };   // where each rating's 3-s gusts begin, m/s (and past the top)
        /// <summary>the rating of a peak 3-s gust, m/s</summary>
        static int EfOf(double vmax) => vmax < 38.5 ? 0 : vmax < 49.6 ? 1 : vmax < 60.8 ? 2 : vmax < 74.2 ? 3 : vmax < 89.9 ? 4 : 5;
        /// <summary>the wind of a tornado with a damage path widthM wide: the storm's median tornado (StormPlan.vTor) grown with width</summary>
        static double WindOf(double vTor, double widthM) => C.Clamp(Math.Max(vTor, 30) * Math.Pow(Math.Max(widthM, 40) / 250, 0.3), 29, 135);

        // Sizes and ratings by shape (user, 2026-10-29): what each shape can be, as its visible width at the ground and the
        // EF ratings it can carry. A wedge is 750 yd to 2 mi wide and EF3 or more (EF5 only from 1000 yd); cones are at
        // most 500 yd and EF4, stovepipes 250 yd and EF4, ropes 100 yd and EF2, elephant trunks 200 yd and EF2, drillbits
        // 300 yd (the one other shape that reaches EF5, at up to 750 yd then). A sidewinder is a rope laid over, a dust
        // whirl a weak tornado without a funnel to the ground: both as ropes in rating, the dust whirl as wide as a drillbit.
        public const double Yd = 0.9144;
        /// <summary>A wedge's visible width at the ground, m: 750 yd to two miles (user, 2026-10-29; was half a mile from
        /// 2026-10-25), and the least an EF5 wedge is.</summary>
        public const double WedgeMinM = 750 * Yd, WedgeMaxM = 2 * 1609.344, WedgeEf5MinM = 1000 * Yd;
        public const double ConeMaxM = 500 * Yd, StovepipeMaxM = 250 * Yd, RopeMaxM = 100 * Yd, TrunkMaxM = 200 * Yd;
        public const double DrillbitMaxM = 300 * Yd, DrillbitEf5MaxM = 750 * Yd;
        /// <summary>the least a shape's visible ground width may be, m (only a wedge has a floor above the funnel's own)</summary>
        public static double MinWidthM(FunnelShape s) => s == FunnelShape.Wedge ? WedgeMinM : 24;
        /// <summary>the most a shape's visible ground width may be, m, at rating ef (a drillbit grows only as an EF5)</summary>
        public static double MaxWidthM(FunnelShape s, int ef = 0) => s switch
        {
            FunnelShape.Wedge => WedgeMaxM, FunnelShape.Cone => ConeMaxM, FunnelShape.Stovepipe => StovepipeMaxM,
            FunnelShape.Rope => RopeMaxM, FunnelShape.Sidewinder => RopeMaxM, FunnelShape.ElephantTrunk => TrunkMaxM,
            FunnelShape.Drillbit => ef >= 5 ? DrillbitEf5MaxM : DrillbitMaxM, FunnelShape.DustWhirl => DrillbitMaxM, _ => 1900
        };
        /// <summary>the ratings a shape can carry (the multiple-vortex shape, "web:" presets only, any)</summary>
        public static int MinEf(FunnelShape s) => s == FunnelShape.Wedge ? 3 : 0;
        public static int MaxEf(FunnelShape s) => s switch
        {
            FunnelShape.Rope => 2, FunnelShape.Sidewinder => 2, FunnelShape.ElephantTrunk => 2, FunnelShape.DustWhirl => 2,
            FunnelShape.Cone => 4, FunnelShape.Stovepipe => 4, _ => 5
        };
        // The vortex behind the funnel (user, 2026-10-29: fluid dynamics for the funnel's form, without a solver). What a
        // tornado vortex looks like is set by its swirl ratio S, circulation over inflow (Ward 1972; Church, Snow, Baker &
        // Agee 1979; Rotunno 1979; Lewellen 1993; Davies-Jones 2015): below about 0.3 a single-cell vortex, one smooth
        // intense core from the ground to the cloud (a rope, drillbit or elephant trunk); from there a vortex breakdown
        // sits in the core, a narrow supercritical jet below it and a wide turbulent subcritical core above, the
        // breakdown descending as S grows (a stovepipe with its breakdown up at the cloud, a cone with it part way down);
        // at S about 1 the breakdown reaches the ground and the vortex is two-celled, a wide core with a downdraft on its
        // axis (a wedge); past about 1.5-2 the core splits into 2-6 secondary vortices. The core's radius grows with S
        // (Church et al.: roughly linearly up to S 1), so within a shape a wider funnel is a higher-swirl one. The visible
        // funnel is the condensation surface of that vortex's pressure field (StormMarch tornadoForm): a Rankine core 2.5x
        // wider above the breakdown (Church & Snow 1979's jump), the axis deficit falling with the core's widening, and
        // the surface where the deficit matches what the air needs to condense at that height, which falls to nothing at
        // the cloud base: so a funnel flares into the base, a weak one narrows to a needle at the ground and a violent one
        // is wide to the ground. The scale is the shape's (its ground width, the limits above); the vortex gives the form.
        /// <summary>swirl ratios: where a breakdown first appears in the core, where it reaches the ground (two cells),
        /// where the core splits into secondary vortices</summary>
        public const double SwirlBreakdown = 0.3, SwirlTwoCell = 1.0, SwirlMulti = 1.6;
        /// <summary>the share of the hydrostatic pressure difference to the wall-cloud base the funnel needs to condense
        /// (near-saturated inflow under a wall cloud condenses well short of its dry LCL deficit; set so an EF2's funnel
        /// reaches the ground under a 700 m base) and the core's widening above its breakdown</summary>
        public const double FunnelKappa = 0.35, CoreWidening = 2.5;
        /// <summary>a shape's swirl ratios, least to most (its widest funnel the most)</summary>
        static (double lo, double hi) SwirlRange(FunnelShape s) => s switch
        {
            FunnelShape.Rope => (0.1, 0.25), FunnelShape.Sidewinder => (0.1, 0.25), FunnelShape.DustWhirl => (0.1, 0.3),
            FunnelShape.ElephantTrunk => (0.15, 0.3), FunnelShape.Drillbit => (0.2, 0.35), FunnelShape.Stovepipe => (0.3, 0.45),
            FunnelShape.Cone => (0.5, 0.8), FunnelShape.Wedge => (1.0, 1.6), _ => (2.0, 3.0)
        };
        /// <summary>The swirl ratio of a funnel of shape s with ground radius groundKm: within the shape's range by where
        /// its width falls in the shape's (a quarter of its widest to its widest, a wedge WedgeMinM to WedgeMaxM), in log.</summary>
        public static double SwirlOf(FunnelShape s, double groundKm)
        {
            var (lo, hi) = SwirlRange(s);
            double Dhi = MaxWidthM(s, 5), Dlo = s == FunnelShape.Wedge ? WedgeMinM : Dhi / 4;
            double u = C.Clamp(Math.Log(Math.Max(2000 * groundKm, 1) / Dlo) / Math.Log(Dhi / Dlo), 0, 1);
            return lo + (hi - lo) * u;
        }
        /// <summary>the vortex breakdown's height as a share of the funnel's (ztop): at the top at SwirlBreakdown, at the
        /// ground from SwirlTwoCell; past 1 (up to 1.15) there is none in the funnel</summary>
        public static double BreakdownOf(double swirl) => C.Clamp((SwirlTwoCell - swirl) / (SwirlTwoCell - SwirlBreakdown), 0, 1.15);
        /// <summary>the secondary vortices a swirl ratio supports (2 as the core first splits, 6 at the most)</summary>
        public static int SubVorticesOf(double swirl) => (int)C.Clamp(Math.Round(1 + 2.5 * (swirl - 0.6)), 2, 6);
        /// <summary>the pressure deficit the funnel needs at the ground as a share of the vortex's axis deficit (S_tor7.x):
        /// FunnelKappa of the hydrostatic difference up to ztop over the Rankine deficit rho vmax^2; at most 0.75, so the
        /// form is defined to the ground (where the web build's condensation reach draws the funnel below its physical
        /// bottom, its form continues as at that bottom)</summary>
        public static double QGround(double vmax, double ztopKm) => C.Clamp(FunnelKappa * 9.81 * ztopKm * 1000 / Math.Max(vmax * vmax, 1), 0.02, 0.75);
        static double FormG(double q) { q = C.Clamp(q, 0.02, 0.95); return q >= 0.5 ? Math.Sqrt(2 * (1 - q)) : 1 / Math.Sqrt(2 * q); }
        /// <summary>The funnel's form (StormMarch tornadoForm, the same sums): its radius at height h (0 the ground, 1
        /// ztop) over its radius at the ground; zB the breakdown height (BreakdownOf), qg QGround, zeta ztop over the base.</summary>
        public static double Form(double h, double zB, double qg, double zeta)
        {
            double x = C.Clamp((h - (zB - 0.08)) / 0.16, 0, 1), B = x * x * (3 - 2 * x);
            double rc = 1 + (CoreWidening - 1) * B;
            return rc * FormG(qg * (1 - zeta * h) * rc) / FormG(qg);
        }
        /// <summary>a stovepipe's top is at least this much wider than its foot; a cone's is this much wider than the web build's</summary>
        public const double StovepipeTopMin = 1.3, ConeTopWiden = 1.15;

        /// <summary>
        /// A tornado within its shape's limits: its visible width at the ground held to MinWidthM..MaxWidthM (its top with it, a
        /// wedge keeping its flare), its damage path, wind and rating from that width as in Make, then the rating held to what
        /// the shape can carry (HoldRating). Its phases are sized afterwards, from the held width.
        /// </summary>
        static void Constrain(TornadoCharacter t, StormPlan P)
        {
            double D = 2000 * t.groundKm, Dc = C.Clamp(D, MinWidthM(t.shape), MaxWidthM(t.shape));
            if (t.shape == FunnelShape.Wedge) SetWedgeWidth(t, Dc);
            else if (Dc != D) { double k = Dc / D; t.groundKm *= k; t.topKm = Math.Max(t.topKm * k, t.groundKm); }
            t.widthM = 2 * t.groundKm / ShapeWidth(t.shape) * 1000;
            t.vmax = WindOf(P.vTor, t.widthM); t.ef = EfOf(t.vmax);
            HoldRating(t);
        }

        /// <summary>The rating held to its shape's range (MinEf..MaxEf; an EF5 wedge is at least WedgeEf5MinM wide, a narrower
        /// one is an EF4), and the wind to that rating's gusts; the radius of maximum wind follows.</summary>
        static void HoldRating(TornadoCharacter t)
        {
            int lo = MinEf(t.shape), hi = MaxEf(t.shape);
            if (t.shape == FunnelShape.Wedge && 2000 * t.groundKm < WedgeEf5MinM - 1e-6) hi = Math.Min(hi, 4);
            if (t.ef > hi) { t.ef = hi; t.vmax = Math.Min(t.vmax, EfLo[hi + 1] - 0.5); }
            else if (t.ef < lo) { t.ef = lo; t.vmax = Math.Max(t.vmax, EfLo[lo] + 0.5); }
            t.rcKm = C.Clamp(0.5 * t.widthM / 1000 * 29.0 / t.vmax, 0.012, 0.6);
        }

        /// <summary>Every phase within its own shape's width at rating ef (a wedge's cone start no wider than a cone may be);
        /// the phases in the tornado's own shape take its size.</summary>
        static void ClampPhases(TornadoCharacter t, int ef)
        {
            if (t.phases == null) return;
            for (int i = 0; i < t.phases.Length; i++)
            {
                var p = t.phases[i];
                if (p.shape == t.shape) { p.groundKm = t.groundKm; p.topKm = t.topKm; }
                else
                {
                    double D = 2000 * p.groundKm, Dc = Math.Min(D, MaxWidthM(p.shape, ef));
                    if (Dc < D) { double k = Dc / D; p.groundKm *= k; p.topKm = Math.Max(p.topKm * k, p.groundKm); }
                }
                p.swirl = SwirlOf(p.shape, p.groundKm);
                t.phases[i] = p;
            }
        }

        /// <summary>
        /// A tornado changed to shape s, for a rating its own shape cannot carry (Rate): 2.0's geometry for s at its damage
        /// path, as one phase, with that shape's vortices, collar, skin and brightness; its lean follows the new profile (wedges
        /// stand nearly upright); its soil, formation, satellite and the web build's milestones stay its own.
        /// </summary>
        static void Reshape(TornadoCharacter t, StormPlan P, FunnelShape s)
        {
            var r = new StormRng(P.seed ?? 1701, 1200 + t.cycle);
            bool wasBig = t.shape == FunnelShape.Wedge || t.shape == FunnelShape.MultiVortex, big = s == FunnelShape.Wedge || s == FunnelShape.MultiVortex;
            double oldProfile = t.profile;
            t.shape = s;
            BaseGeometry(s, t.widthM, big ? 0.5 : r.U(), out t.groundKm, out t.topKm, out t.profile, out t.sinuosity);
            if (s == FunnelShape.Stovepipe) t.topKm = Math.Max(t.topKm, t.groundKm * StovepipeTopMin);
            t.swirl = SwirlOf(s, t.groundKm);
            t.phases = new[] { new FunnelPhase { shape = s, startFrac = 0, groundKm = t.groundKm, topKm = t.topKm, profile = t.profile, sinuosity = t.sinuosity, cond = 1, swirl = t.swirl } };
            // vortices and collar as Make: a wedge may start as multiple vortices (as many as its swirl supports); a
            // stovepipe never has them
            t.subVortices = 0; t.multiFrac = 0;
            if (s == FunnelShape.Wedge && r.U() < 0.55) { r.U(); t.subVortices = SubVorticesOf(t.swirl); t.multiFrac = r.Range(0.15, 0.3); }
            t.collar = s == FunnelShape.Wedge && t.LifeMin > 10 && r.U() < 0.6;
            // a wedge is loaded with rain and debris: its funnel reaches the ground
            if (big) t.condReach = Math.Max(t.condReach, 0.7);
            t.leanKm *= (1 - 0.7 * t.profile) / Math.Max(1 - 0.7 * oldProfile, 1e-3);
            // skin as Individualise: a stovepipe is laminar, a wedge never a ghost and smooth in outline
            if (s == FunnelShape.Stovepipe && t.skin != FunnelSkin.Laminar) { t.skin = FunnelSkin.Laminar; t.roughness = r.Range(0.18, 0.35); t.striation = r.Range(0.6, 1.0); t.ghost = 0; }
            if (big && t.skin == FunnelSkin.Ghost) { t.skin = FunnelSkin.Ragged; t.roughness = r.Range(0.8, 1.5); t.striation = r.Range(0.0, 0.35); t.ghost = 0; }
            if (s == FunnelShape.Wedge && t.skin == FunnelSkin.Ragged && t.roughness > 0.75) t.roughness = 0.45 + (t.roughness - 0.8) / 0.7 * 0.3;
            if (big != wasBig) t.albedo = C.Clamp(t.albedo * (big ? 0.72 : 1 / 0.72), 0.5, 1.2);
        }
        /// <summary>A wedge's top over its foot (user, 2026-10-26: a wider top, its sides more slanted; was 1.1-1.6), and
        /// how much wider than its foot its top may be at most, km.</summary>
        public const double WedgeFlareMin = 1.5, WedgeFlareMax = 2.1, WedgeTopReachKm = 1.3;
        /// <summary>A stovepipe's visible width over the web build's (user, 2026-10-26: a bit thinner on average).</summary>
        public const double StovepipeSlim = 0.75;

        /// <summary>A wedge's size from the web build's cycle (bigger cycles, wider wedges, between WedgeMinM and WedgeMaxM)
        /// and this tornado's own proportion gS; its damage path, wind and rating follow from that width, as in Make.</summary>
        static void SizeWedge(TornadoCharacter t, StormPlan P, double gS)
        {
            // where this cycle falls among the web build's wedges: its radius is the setup's tornado radius (torR) times a draw
            // of 1.5-3.2 (cycles.ts), so every setup spans the whole range and a bigger cycle is a wider wedge
            double q = C.Clamp((t.webGroundKm / Math.Max(P.torR, 1e-3) - 1.5) / 1.7, 0, 1);
            double D = C.Clamp(WedgeMinM * Math.Pow(WedgeMaxM / WedgeMinM, q) * gS, WedgeMinM, WedgeMaxM);
            // its flare: where the web build's draw fell in 1.1-1.6, carried to WedgeFlareMin-Max
            double u = C.Clamp((t.topKm / Math.Max(t.groundKm, 1e-3) - 1.1) / 0.5, 0, 1);
            t.topKm = t.groundKm * C.Lerp(WedgeFlareMin, WedgeFlareMax, u);
            SetWedgeWidth(t, D);
            t.vmax = WindOf(P.vTor, t.widthM);
            t.ef = EfOf(t.vmax);
            t.rcKm = C.Clamp(0.5 * t.widthM / 1000 * 29.0 / t.vmax, 0.012, 0.6);
        }

        /// <summary>A wedge D m wide at the ground, keeping its flare (top over ground, WedgeFlareMin-Max, its top at most
        /// WedgeTopReachKm wider than its foot) and its wedge phase.</summary>
        static void SetWedgeWidth(TornadoCharacter t, double D)
        {
            double flare = C.Clamp(t.topKm / Math.Max(t.groundKm, 1e-3), WedgeFlareMin, WedgeFlareMax);
            t.groundKm = D / 2000; t.topKm = Math.Min(t.groundKm * flare, t.groundKm + WedgeTopReachKm);
            t.widthM = 2 * t.groundKm / ShapeWidth(FunnelShape.Wedge) * 1000;
            if (t.phases == null) return;
            for (int i = 0; i < t.phases.Length; i++)
            {
                if (t.phases[i].shape != FunnelShape.Wedge) continue;
                var p = t.phases[i]; p.groundKm = t.groundKm; p.topKm = t.topKm; t.phases[i] = p;
            }
        }

        /// <summary>
        /// A tornado of a given shape, damage-path width and rating on circulation k of the plan (tools and comparison renders,
        /// as 2.0's StormGenome.Reshape): its size and look from 2.0's rules, shown as that shape alone, without a change of
        /// shape, a satellite or a lean. Assign it to StormRenderer.Tornadoes[k].
        /// </summary>
        public static TornadoCharacter Forced(StormPlan P, ClimoRegion region, int k, FunnelShape shape, double widthM, int ef)
        {
            var web = Build(P, region);
            if (web == null || k >= web.Length || web[k] == null) return null;
            var w = web[k];
            var t = new TornadoCharacter
            {
                cycle = k, webShape = w.webShape, formMin = w.formMin, ropeMin = w.ropeMin, goneMin = w.goneMin,
                webGroundKm = w.webGroundKm, webTopKm = w.webTopKm, shape = shape, widthM = widthM, ef = ef, dust = w.dust, ropeFrac = w.ropeFrac
            };
            var r = new StormRng(7, 900);
            t.vmax = EfGust[Math.Max(0, Math.Min(ef, 5))];
            t.rcKm = C.Clamp(0.5 * widthM / 1000 * 29.0 / t.vmax, 0.012, 0.6);
            double cond = C.Clamp(1.3 - (P.lclM - 700) / 1300, 0.2, 1) + 0.25 * C.Clamp((t.vmax - 45) / 50, 0, 1);
            t.condReach = C.Clamp(cond, 0.15, 1);
            BaseGeometry(shape, widthM, shape == FunnelShape.Wedge || shape == FunnelShape.MultiVortex ? 0.5 : r.U(), out t.groundKm, out t.topKm, out t.profile, out t.sinuosity);
            if (shape == FunnelShape.DustWhirl) t.condReach = Math.Min(t.condReach, 0.3);
            if (shape == FunnelShape.MultiVortex) { t.subVortices = 3 + (int)(r.U() * 4); t.multiFrac = r.Range(0.6, 0.85); }
            else if ((shape == FunnelShape.Wedge || shape == FunnelShape.Stovepipe) && ef >= 2 && r.U() < 0.55) { t.subVortices = 3 + (int)(r.U() * 3); t.multiFrac = r.Range(0.15, 0.3); }
            if (shape == FunnelShape.Stovepipe) { t.subVortices = 0; t.multiFrac = 0; }
            t.collar = shape == FunnelShape.Wedge && t.LifeMin > 10 && r.U() < 0.6;
            t.rainWrap = 0;
            Individualise(t, P, region, ref r, false);   // (a tool's tornado is what it was asked for, outside the shape limits)
            t.phases = new[] { new FunnelPhase { shape = shape, groundKm = t.groundKm, topKm = t.topKm, profile = t.profile, sinuosity = t.sinuosity, cond = 1, sideKm = shape == FunnelShape.Sidewinder ? 0.6 : 0, swirl = t.swirl } };
            t.satellite = false; t.leanKm = 0;
            DrawFormation(t, new StormRng(7, 1100));
            return t;
        }

        /// <summary>
        /// Give a tornado a rating (StormScenario's tiers and caps): its peak wind is that rating's, and its damage path and
        /// funnel grow or shrink to the width that wind goes with (the inverse of Make's wind from width), so a game's
        /// strongest tornado looks the part. A shape that cannot carry the rating changes first (user, 2026-10-29): a wedge
        /// held below EF3 becomes a stovepipe (EF2) or a cone; a rope, elephant trunk, sidewinder or dust whirl raised to
        /// EF3-4 becomes a cone; anything but a wedge or drillbit raised to EF5 becomes a wedge on a low base or a drillbit
        /// on a high one (StormGame's rule). Then the width is held within the shape's limits at that rating (an EF5 wedge at
        /// least WedgeEf5MinM wide, an EF5 drillbit up to DrillbitEf5MaxM). P: the storm's plan (its median tornado wind vTor).
        /// </summary>
        public static void Rate(TornadoCharacter t, int ef, StormPlan P)
        {
            if (t == null) return;
            ef = Math.Max(0, Math.Min(ef, 5));
            if (ef < MinEf(t.shape)) Reshape(t, P, ef >= 2 ? FunnelShape.Stovepipe : FunnelShape.Cone);
            else if (ef > MaxEf(t.shape)) Reshape(t, P, ef >= 5 ? (P.zb > 1.9 ? FunnelShape.Drillbit : FunnelShape.Wedge) : FunnelShape.Cone);
            double widthM = C.Clamp(250 * Math.Pow(EfGust[ef] / Math.Max(P.vTor, 30), 1 / 0.3), 40, 2600);
            double k = widthM / Math.Max(t.widthM, 1);
            t.ef = ef; t.vmax = EfGust[ef];
            t.groundKm = C.Clamp(t.groundKm * k, 0.012, 0.95); t.topKm = C.Clamp(t.topKm * k, t.groundKm, 1.4);
            if (t.phases != null)
                for (int i = 0; i < t.phases.Length; i++)
                {
                    var p = t.phases[i];
                    p.groundKm = C.Clamp(p.groundKm * k, 0.012, 0.95); p.topKm = C.Clamp(p.topKm * k, p.groundKm, 1.4);
                    t.phases[i] = p;
                }
            // within the shape's width at this rating (a wedge keeps its flare, and is at least WedgeEf5MinM wide as an EF5)
            double D = 2000 * t.groundKm, Dc = C.Clamp(D, t.shape == FunnelShape.Wedge && ef >= 5 ? WedgeEf5MinM : MinWidthM(t.shape), MaxWidthM(t.shape, ef));
            if (t.shape == FunnelShape.Wedge) SetWedgeWidth(t, Dc);
            else if (Dc != D) { double kc = Dc / D; t.groundKm *= kc; t.topKm = Math.Max(t.topKm * kc, t.groundKm); }
            t.widthM = 2 * t.groundKm / ShapeWidth(t.shape) * 1000;
            t.rcKm = C.Clamp(0.5 * t.widthM / 1000 * 29.0 / t.vmax, 0.012, 0.6);
            t.swirl = SwirlOf(t.shape, t.groundKm);
            if (t.subVortices > 0 && t.shape == FunnelShape.Wedge) t.subVortices = SubVorticesOf(t.swirl);
            ClampPhases(t, ef);
        }

        /// <summary>
        /// How a cone or stovepipe forms (user, 2026-10-26): it comes down as a thin vortex, as a wedge's central vortex does,
        /// and widens into its shape, each its own way: quickly or slowly (12-30 % of its life), top first or foot first,
        /// a fifth to nearly half its foot's width at first, snaking more or less while thin. Its own stream, so the other draws are unchanged.
        /// </summary>
        static void DrawFormation(TornadoCharacter t, StormRng r)
        {
            t.formFrac = r.Range(0.12, 0.3); t.formTopLead = r.Range(-0.3, 0.5);
            t.formThin = r.Range(0.2, 0.45); t.formSinu = r.Range(0.15, 0.45);
        }

        /// <summary>a big tornado's central vortex at touchdown, as a share of its full width</summary>
        const double CoreStart = 0.15;
        /// <summary>a wedge's narrowing before its rope-out: how far, and over how many storm minutes</summary>
        public const double WedgeShrink = 0.6, WedgeShrinkMin = 2;
        /// <summary>the share of its life at which a tornado's rope-out begins (the web build's rope milestone)</summary>
        public static double RopeStart(TornadoCharacter t) => 1 - t.ropeFrac;
        /// <summary>the share of its life a wedge takes to narrow before its rope-out (WedgeShrinkMin)</summary>
        public static double ShrinkSpan(TornadoCharacter t) => C.Clamp(WedgeShrinkMin / Math.Max(t.LifeMin, 0.1), 0.04, 0.2);
        /// <summary>a wedge's rope at the end: its central vortex's radius as a share of the foot's (25-60 m)</summary>
        const double RopeCore = 0.05;
        /// <summary>share of its life a forming funnel takes to come down, before it widens</summary>
        const double FormDown = 0.06;
        static bool FormsThin(FunnelShape s) => s == FunnelShape.Cone || s == FunnelShape.Stovepipe;

        static FunnelPhase PhaseOf(TornadoCharacter t, FunnelShape shape, double startFrac, double gS, double tS, double pJ, double u)
        {
            BaseGeometry(shape, t.widthM, u, out double g, out double top, out double prof, out double sinu);
            var p = new FunnelPhase { shape = shape, startFrac = startFrac, cond = 1, sinuosity = sinu };
            p.groundKm = C.Clamp(g * gS, 0.012, 0.95);
            p.topKm = C.Clamp(top * tS, p.groundKm, 1.4);
            p.profile = C.Clamp(prof + pJ, 0.03, 1);
            p.swirl = SwirlOf(shape, p.groundKm);
            return p;
        }

        static bool Slender(FunnelShape s) => s == FunnelShape.Rope || s == FunnelShape.ElephantTrunk || s == FunnelShape.Sidewinder;

        /// <summary>
        /// 2.0's StormGenome.Individualise: the draws that make a tornado look like itself rather than its shape's average, its
        /// proportions and lean, the shapes it passes through, its skin and brightness, the soil it lofts, and a satellite.
        /// constrain: hold it within its shape's limits (Constrain, ClampPhases) once its proportions are drawn; the draws are
        /// the same either way.
        /// </summary>
        static void Individualise(TornadoCharacter t, StormPlan P, ClimoRegion region, ref StormRng r, bool constrain)
        {
            double zb = C.Clamp(P.lclM / 1000, 0.4, 2.8);
            double dry = C.Clamp((P.lclM - 1300) / 900, 0, 1), hp = P.hpness, wetHp = C.Clamp(hp, 0, 1);
            // proportions: the same shape comes fatter or thinner, more or less flared (about +-20 %)
            double gS = Math.Exp(0.18 * r.N()), tS = Math.Exp(0.15 * r.N()), pJ = 0.08 * r.N();
            t.groundKm = C.Clamp(t.groundKm * gS, 0.012, 0.95);
            t.topKm = C.Clamp(t.topKm * tS, t.groundKm, 1.4);
            t.profile = C.Clamp(t.profile + pJ, 0.03, 1);
            // a wedge: half a mile to two miles wide (before its phases, which are sized from its width)
            if (t.shape == FunnelShape.Wedge) SizeWedge(t, P, gS);
            // a stovepipe: a quarter thinner than the web build's (user, 2026-10-26), its top wider than its foot (user,
            // 2026-10-29: wider at the top, curving out into the wall cloud; the curve is TopFlare, drawn in the march)
            if (t.shape == FunnelShape.Stovepipe) { t.groundKm = Math.Max(t.groundKm * StovepipeSlim, 0.012); t.topKm = Math.Max(t.topKm * StovepipeSlim, t.groundKm * StovepipeTopMin); }
            // a cone: a bit wider at the top than the web build's (user, 2026-10-29)
            if (t.shape == FunnelShape.Cone) t.topKm = Math.Min(t.topKm * ConeTopWiden, 1.4);
            // within its shape's width and ratings (user, 2026-10-29), before its phases are sized from its width
            if (constrain) Constrain(t, P);
            // the vortex: its swirl ratio from its shape and width; a wedge's secondary vortices as many as it supports
            t.swirl = SwirlOf(t.shape, t.groundKm);
            if (t.subVortices > 0 && t.shape == FunnelShape.Wedge) t.subVortices = SubVorticesOf(t.swirl);

            // its life: many tornadoes change shape. Strong ones often start as a cone and grow into a stovepipe or wedge; a
            // wedge can narrow again before it ropes out; drillbits fill out into cones; elephant trunks thin into ropes, and
            // a rope can start to sidewind; a high-based dust whirl's funnel can finally come down.
            FunnelShape? pre = null, post = null;
            double u1 = r.U(), u2 = r.U(), u3 = r.U(), u4 = r.U();
            switch (t.shape)
            {
                case FunnelShape.Wedge:         if (u1 < 0.55) pre = u2 < 0.5 ? FunnelShape.Cone : FunnelShape.Stovepipe; if (!t.collar && u3 < 0.35) post = FunnelShape.Stovepipe; break;
                case FunnelShape.MultiVortex:   if (u1 < 0.4) pre = FunnelShape.Cone; break;
                case FunnelShape.Stovepipe:     if (u1 < 0.35) pre = FunnelShape.Cone; if (u3 < 0.3) post = u4 < 0.5 ? FunnelShape.Cone : FunnelShape.ElephantTrunk; break;
                case FunnelShape.Cone:          if (u1 < 0.25) pre = FunnelShape.Drillbit; if (t.ef >= 2 && u3 < 0.3) post = FunnelShape.Stovepipe; else if (u3 < 0.25) post = FunnelShape.ElephantTrunk; break;
                case FunnelShape.Drillbit:      if (t.ef >= 1 && u1 < 0.3) post = FunnelShape.Cone; break;
                case FunnelShape.ElephantTrunk: if (u1 < 0.35) post = FunnelShape.Rope; break;
                case FunnelShape.Rope:          if (u1 < 0.25) pre = FunnelShape.ElephantTrunk; if (u3 < 0.3) post = FunnelShape.Sidewinder; break;
                case FunnelShape.DustWhirl:     if (u1 < 0.3) post = FunnelShape.Drillbit; break;
                case FunnelShape.Sidewinder:    pre = u1 < 0.6 ? FunnelShape.Rope : FunnelShape.ElephantTrunk; break;
            }
            double s1 = r.Range(0.12, 0.3), s2 = Math.Min(r.Range(0.55, 0.75), 1 - t.ropeFrac - TornadoCharacter.MorphFrac);
            if (s2 < s1 + 2 * TornadoCharacter.MorphFrac) post = null;
            double side = C.Clamp(r.Range(0.35, 0.75) * zb, 0.2, 1.0);
            var ph = new System.Collections.Generic.List<FunnelPhase>(3);
            if (pre.HasValue) ph.Add(PhaseOf(t, pre.Value, 0, gS, tS, pJ, r.U()));
            ph.Add(new FunnelPhase { shape = t.shape, startFrac = pre.HasValue ? s1 : 0, groundKm = t.groundKm, topKm = t.topKm, profile = t.profile, sinuosity = t.sinuosity, cond = 1, swirl = t.swirl });
            if (post.HasValue)
            {
                var p = PhaseOf(t, post.Value, s2, gS, tS, pJ, r.U());
                // the dust whirl's condensation lowers most of the way to the ground
                if (t.shape == FunnelShape.DustWhirl) p.cond = Math.Min(0.8 / Math.Max(t.condReach, 0.1), 3);
                ph.Add(p);
            }
            for (int i = 0; i < ph.Count; i++) { var p = ph[i]; if (p.shape == FunnelShape.Sidewinder) { p.sideKm = side; ph[i] = p; } }
            t.phases = ph.ToArray();
            if (constrain) ClampPhases(t, t.ef);
            t.sideAngle = Math.PI + r.Range(-1.1, 1.1);

            // lean: most trail their ground end behind the storm, a few lead it; wedges stand nearly upright
            double ul = r.U();
            t.leanKm = zb * r.Range(0.0, 0.3) * (1 - 0.7 * t.profile);
            t.leanAngle = ul < 0.7 ? Math.PI + r.Range(-1.2, 1.2) : r.Range(-1.0, 1.0);

            // skin: most funnels are torn and ragged; some cones, stovepipes and drillbits are smooth and laminar with fine
            // striations (less so in rain-laden HP air); weak funnels under a high, dry base are often thin and translucent
            bool mid = t.shape == FunnelShape.Cone || t.shape == FunnelShape.Stovepipe || t.shape == FunnelShape.Drillbit;
            bool big = t.shape == FunnelShape.Wedge || t.shape == FunnelShape.MultiVortex;
            double pLam = (mid ? 0.2 : Slender(t.shape) ? 0.12 : 0.04) * (1 - 0.6 * wetHp);
            double pGhost = (t.ef <= 1 ? 0.18 : 0.05) * (0.6 + 0.8 * dry) * (big ? 0.2 : 1);
            double uSkin = r.U();
            t.skin = uSkin < pLam ? FunnelSkin.Laminar : uSkin < pLam + pGhost ? FunnelSkin.Ghost : FunnelSkin.Ragged;
            switch (t.skin)
            {
                case FunnelSkin.Laminar: t.roughness = r.Range(0.18, 0.35); t.striation = r.Range(0.6, 1.0); t.ghost = 0; break;
                case FunnelSkin.Ghost:   t.roughness = r.Range(0.9, 1.3);   t.striation = 0.1;               t.ghost = r.Range(0.55, 0.85); break;
                default:                 t.roughness = r.Range(0.8, 1.5);   t.striation = r.Range(0.0, 0.35); t.ghost = 0; break;
            }
            // a wedge's outline is smooth at its size: the same draw, mapped to a calmer range
            if (t.shape == FunnelShape.Wedge && t.skin == FunnelSkin.Ragged) t.roughness = 0.45 + (t.roughness - 0.8) / 0.7 * 0.3;
            // a stovepipe is always laminar (user, 2026-10-26); one drawn otherwise takes its laminar look from its own stream,
            // so every draw after this one is as before
            if (t.shape == FunnelShape.Stovepipe && t.skin != FunnelSkin.Laminar)
            {
                var rl = new StormRng(P.seed ?? 1701, 1000 + t.cycle);
                t.skin = FunnelSkin.Laminar; t.roughness = rl.Range(0.18, 0.35); t.striation = rl.Range(0.6, 1.0); t.ghost = 0;
            }
            // brightness: condensation under a high, dry base is bright white in the sun; rain-laden HP funnels are grey;
            // wedges and multiple-vortex tornadoes are dark masses, loaded with rain and debris
            t.albedo = C.Clamp((0.9 + 0.15 * dry - 0.22 * wetHp + 0.08 * C.Clamp(-hp, 0, 1)) * (big ? 0.72 : 1) * Math.Exp(0.1 * r.N()), 0.5, 1.2);

            // the soil it lofts: Plains red beds or tan loam, pale dust on the dry High Plains, Dixie red clay or dark loam,
            // dark Midwest prairie soil. Wet ground under an HP storm lofts less, and darker.
            double us = r.U();
            (string n, double R, double G, double B) soil;
            if (dry > 0.4) soil = us < 0.25 ? ("red", 0.64, 0.38, 0.26) : ("pale tan", 0.68, 0.6, 0.47);
            else switch (region)
            {
                case ClimoRegion.Dixie:   soil = us < 0.55 ? ("red clay", 0.56, 0.31, 0.21) : ("dark", 0.4, 0.33, 0.27); break;
                case ClimoRegion.Midwest: soil = us < 0.8 ? ("dark", 0.36, 0.3, 0.25) : ("tan", 0.56, 0.47, 0.36); break;
                default:                  soil = us < 0.4 ? ("red", 0.62, 0.35, 0.23) : ("tan", 0.56, 0.47, 0.36); break;
            }
            double wet = C.Clamp((hp - 0.2) / 0.6, 0, 1) * r.Range(0.5, 1.0);
            t.soilName = soil.n; t.soilR = soil.R * (1 - 0.3 * wet); t.soilG = soil.G * (1 - 0.3 * wet); t.soilB = soil.B * (1 - 0.3 * wet);
            t.dust *= 1 - 0.4 * wet;
            // a strong tornado's funnel is stained low down by what it lofts
            t.dirt = C.Clamp((0.15 + 0.12 * t.ef) * t.dust * r.Range(0.5, 1.3), 0, 0.85);

            // satellite: a smaller tornado orbiting a strong, wide one inside the same broad circulation, for a few minutes
            double pSat = t.ef >= 2 && t.groundKm > 0.1 ? 0.18 + 0.08 * (t.ef - 2) : 0;
            t.satellite = r.U() < pSat && t.LifeMin * 60 > 360;
            if (t.satellite)
            {
                t.satFrac = r.Range(0.25, 0.6);
                t.satLifeS = Math.Min(r.Range(60, 240), 0.3 * t.LifeMin * 60);
                t.satOrbitKm = Math.Max(t.groundKm * r.Range(1.8, 3.5), 0.25) + r.Range(0.2, 0.8);
                t.satRadKm = r.Range(0.012, 0.035);
                t.satVmax = r.Range(32, 55);
                t.satAngle = r.Range(0, 2 * Math.PI);
            }
        }

        /// <summary>
        /// The tornado now, 2.0's way, written into the frame: the extra slots, and the shape, proportions, lean, suction
        /// vortices and rating in the web build's tornado slots. Leaves the frame alone while no tornado is on.
        /// </summary>
        public static void Apply(StormPlan P, TornadoCharacter[] chars, double tMin, StormFrame F)
        {
            var a = F.param;
            Array.Clear(a, Slot0 * 4, Slots * 4);
            if (chars == null) return;
            float amount = a[13 * 4 + 2];
            if (amount <= 0) return;
            int k = P.cycles != null ? F.cycleIndex : tMin < 30 ? -1 : Math.Min((int)Math.Floor((tMin - 30) / 38), 1);
            if (k < 0 || k >= chars.Length || chars[k] == null) return;
            var tg = chars[k];
            void Set(int slot, double x, double y, double z, double w) { a[slot * 4] = (float)x; a[slot * 4 + 1] = (float)y; a[slot * 4 + 2] = (float)z; a[slot * 4 + 3] = (float)w; }
            double fx = P.fwd.x, fy = P.fwd.y, lx = P.lft.x, ly = P.lft.y;
            double lifeFrac = C.Clamp((tMin - tg.formMin) / Math.Max(tg.LifeMin, 0.1), 0, 1);

            // its life story: the phases blended in turn, each over MorphFrac of the life (2.0's TornadoOf)
            double pg = tg.groundKm, pt = tg.topKm, pc = 1, psd = 0, profile = tg.profile, sinu = tg.sinuosity, swirl = tg.swirl;
            var phaseShape = tg.shape;
            var ph = tg.phases;
            if (ph != null && ph.Length > 0)
            {
                pg = ph[0].groundKm; pt = ph[0].topKm; profile = ph[0].profile; sinu = ph[0].sinuosity; pc = ph[0].cond; psd = ph[0].sideKm;
                phaseShape = ph[0].shape; swirl = ph[0].swirl;
                for (int i = 1; i < ph.Length; i++)
                {
                    double w = Ramp(lifeFrac, ph[i].startFrac, ph[i].startFrac + TornadoCharacter.MorphFrac);
                    if (w <= 0) continue;
                    pg = C.Lerp(pg, ph[i].groundKm, w); pt = C.Lerp(pt, ph[i].topKm, w);
                    profile = C.Lerp(profile, ph[i].profile, w); sinu = C.Lerp(sinu, ph[i].sinuosity, w);
                    pc = C.Lerp(pc, ph[i].cond, w); psd = C.Lerp(psd, ph[i].sideKm, w);
                    swirl = C.Lerp(swirl, ph[i].swirl, w);
                    if (w >= 0.5) phaseShape = ph[i].shape;
                }
            }
            double rope = a[14 * 4 + 3];
            double gWeb = a[14 * 4], tWeb = a[14 * 4 + 1];
            // the vortex's form (StormMarch tornadoForm): its breakdown height and the deficit the funnel needs at the ground
            double zb = a[1 * 4], ztop = Math.Max(zb - 0.6 * a[7 * 4], 0.3), zeta = ztop / Math.Max(zb, 1e-3);
            double zB = BreakdownOf(swirl), qg = QGround(tg.vmax, ztop);
            // the web build's own growth, rope-out and takeover, at this tornado's proportions and in its current shape
            double gScale = pg / Math.Max(tg.webGroundKm, 1e-3), tScale = pt / Math.Max(tg.webTopKm, 1e-3);
            bool webMulti = a[21 * 4 + 3] > 0.5;
            double ground, top, envelope;
            if (webMulti)
            {
                // the web build's second tornado: its takeover and collar already drawn; size it
                ground = gWeb * gScale; top = tWeb * tScale; envelope = a[21 * 4 + 3] - 1;
                a[21 * 4] *= (float)gScale;
            }
            else
            {
                bool big = tg.shape == FunnelShape.Wedge || tg.shape == FunnelShape.MultiVortex || tg.subVortices > 0;
                double takeover = tg.shape == FunnelShape.MultiVortex ? 0.35 : Ramp(lifeFrac, 0.12, Math.Max(0.45, tg.multiFrac + 0.15));
                double multi = tg.subVortices > 0 ? Ramp(lifeFrac, 0.02, 0.06) * (1 - Ramp(lifeFrac, tg.multiFrac * 0.75, tg.multiFrac)) : 0;
                double breakdown = 0;
                envelope = big ? (tg.shape == FunnelShape.MultiVortex ? 0.35 + 0.25 * takeover : takeover) : 0;
                // the central vortex comes down thin (CoreStart of its full width, at least 25 m) and widens in proportion
                // to its full width by a fifth of the life. It is drawn from touchdown: it used to grow unseen from nothing
                // while the funnel was drawn plain, and appear at once, hundreds of metres wide, when the envelope began
                // (user, 2026-10-26: the wedge jumped in size)
                double coreFull = Math.Max(tg.rcKm * 1.6, 0.04), coreStart = Math.Max(coreFull * CoreStart, 0.025);
                double core = big ? coreStart * Math.Pow(coreFull / coreStart, Ramp(lifeFrac, 0.03, 0.2)) : 0;
                ground = gWeb * gScale * (big ? 0.3 + 0.7 * takeover : 1);
                top = tWeb * tScale * (big ? 0.5 + 0.5 * takeover : 1);
                // Rope-out (user, 2026-10-26): as it begins, the envelope's outer part breaks up into a scuddy collar
                // from the ground to the base; its bottom lifts off the ground and rises, the scud breaking up more and
                // thinning, until it is gone into the base (S_tor4.z, StormMarch), while the central vortex narrows to a
                // rope (RopeCore of the foot's width). The envelope keeps about its width meanwhile (the web build's
                // rope-out shrink is undone for it). Before, the envelope stayed whole round a core still hundreds of
                // metres wide, and the lean of a rope-out tipped the lot over: a fat, tilted sausage.
                // (the collar lifts over the whole rope-out, from its start to the tornado's end, 5-12 storm min: over the
                // web build's shrink alone, 3-7 min, it was gone in a moment)
                double r0 = RopeStart(tg);
                double ropeOut = big ? Ramp(lifeFrac, r0, r0 + 0.92 * tg.ropeFrac) : 0;
                breakdown = ropeOut;
                if (rope > 0 && big)
                {
                    double ropeR = C.Clamp(RopeCore * pg, 0.025, 0.06);
                    // (in proportion, so it is rope-thin by half way: shrinking evenly from a kilometre across, it was a fat
                    // rounded column leaning over for most of the rope-out)
                    if (core > ropeR) core = core * Math.Pow(ropeR / core, Ramp(rope, 0.0, 0.5));
                    ground *= C.Lerp(1, 0.85, rope) / Math.Max(1 - 0.75 * rope, 0.25);
                    top *= C.Lerp(1, 0.9, rope) / Math.Max(1 - 0.6 * rope, 0.4);
                }
                // before the collar (user, 2026-10-26): over its last WedgeShrinkMin storm minutes before the rope-out
                // the wedge narrows, foot and top together, to WedgeShrink of its width, and breaks up from there
                if (big)
                {
                    double narrow = C.Lerp(1, WedgeShrink, Ramp(lifeFrac, r0 - ShrinkSpan(tg), r0));
                    ground *= narrow; top *= narrow;
                }
                // a plain funnel's top is where the vortex's form puts it (its foot is the shape's); a wedge's envelope keeps the
                // user's flare (WedgeFlareMin-Max), and the march bends the form through both
                if (!big) top = Math.Max(ground * Form(1, zB, qg, zeta), ground);
                if (!big && FormsThin(tg.shape) && tg.formFrac > 0)
                {
                    // a cone or stovepipe comes down as a thin, nearly even tube (a third to two thirds of its foot's full
                    // width) and widens into its shape; its top leads or lags its foot (DrawFormation)
                    double thin = Math.Max(pg * tg.formThin, 0.012);
                    // (it widens once it is down: the web build's funnel takes about 6 % of the life to reach the ground)
                    double gG = Ramp(lifeFrac, FormDown, FormDown + tg.formFrac);
                    double gT = Ramp(lifeFrac, FormDown, FormDown + C.Clamp(tg.formFrac * (1 - tg.formTopLead), 0.04, 0.45));
                    ground = C.Lerp(Math.Min(thin, ground), ground, gG);
                    top = C.Lerp(Math.Min(thin * 1.6, top), top, gT);
                    profile = C.Lerp(0.12, profile, gG);
                    sinu = C.Lerp(Math.Max(sinu, tg.formSinu), sinu, gG);
                }
                // (a roping-out wedge stays in envelope mode with no envelope: what is left is its central vortex)
                // (a big tornado is drawn in envelope mode all its life, so its funnel is always the central vortex and the
                // envelope round it; switching from plain partway through swapped one funnel for another)
                bool plain = !big;
                Set(21, core, multi, breakdown, plain ? 0 : 1 + envelope);
            }
            a[14 * 4] = (float)Math.Max(ground, 0.008);
            a[14 * 4 + 1] = (float)Math.Max(top, ground);
            // condensation: the web build's reach at this phase's (a dust whirl's funnel stays high)
            double condNow = a[14 * 4 + 2] * pc * (tg.shape == FunnelShape.DustWhirl && phaseShape == FunnelShape.DustWhirl ? tg.condReach / 0.65 : 1);
            a[14 * 4 + 2] = (float)C.Clamp(condNow, 0, 1);
            // lean: fixed relative to the storm's motion, growing as it ropes out
            double la = tg.leanAngle, lean = tg.leanKm * (1 + 0.8 * rope);
            double ldx = Math.Cos(la) * fx + Math.Sin(la) * lx, ldy = Math.Cos(la) * fy + Math.Sin(la) * ly;
            a[13 * 4] += (float)(ldx * lean); a[13 * 4 + 1] += (float)(ldy * lean);
            a[13 * 4 + 3] = (float)profile;
            a[15 * 4 + 3] = tg.ef;
            // suction vortices: the orbit, their radius, how many (the web build's second tornado draws four), the bend
            a[22 * 4] = 0.9f;
            if (!webMulti) a[22 * 4 + 1] = (float)Math.Max(0.07 * ground / 0.8, 0.025);
            a[22 * 4 + 2] = tg.subVortices > 0 ? tg.subVortices : webMulti ? 4 : 0;
            a[22 * 4 + 3] = (float)sinu;
            // the funnel's surface turns at the wind there, v / r, capped so the texture does not alias
            double rv = Math.Max(ground, 0.01), form = Math.Min(1, amount * 1.2);
            double spin = C.Clamp(tg.WindAt(rv) / (rv * 1000), 0.05, 2.0) * (0.6 + 0.4 * form);
            double vmax = tg.vmax * (0.55 + 0.45 * form) * (1 - 0.5 * rope);
            Set(Slot0, spin, vmax, tg.rcKm * (1 - 0.5 * rope), envelope > 0.3 ? 1.35 : 2.4);
            // (rain wrap stays on the character: the march never read it)
            Set(Slot0 + 1, qg, (int)phaseShape, tg.ef, zB);
            Set(Slot0 + 2, tg.albedo, tg.roughness, tg.ghost, tg.striation);
            Set(Slot0 + 3, tg.soilR, tg.soilG, tg.soilB, tg.dirt * Ramp(lifeFrac, 0.02, 0.15));
            // the plane the funnel bends in: across the lean, so a rope's S curve and its lean are not the same bend
            double sa = tg.sideAngle, sdx = Math.Cos(sa) * fx + Math.Sin(sa) * lx, sdy = Math.Cos(sa) * fy + Math.Sin(sa) * ly;
            Set(Slot0 + 4, sdx, sdy, psd, Math.Atan2(fy, fx) + la + Math.PI / 2);
            // satellite: forms at satFrac of the parent's life, orbits it counter-clockwise at about 15 m/s, lives a few minutes
            if (tg.satellite)
            {
                double ts = (lifeFrac - tg.satFrac) * tg.LifeMin * 60;
                double s = Ramp(ts, 0, 25) * (1 - Ramp(ts, tg.satLifeS - 40, tg.satLifeS)) * (1 - rope);
                if (ts >= 0 && ts <= tg.satLifeS && s > 0.02)
                {
                    double ang = tg.satAngle + ts * 15 / (tg.satOrbitKm * 1000);
                    Set(Slot0 + 5, a[13 * 4] + tg.satOrbitKm * Math.Cos(ang), a[13 * 4 + 1] + tg.satOrbitKm * Math.Sin(ang), s * amount, tg.satRadKm);
                }
            }
            // the caption names the shape it has now, when that is one the web build would not have named
            if (amount > 0.15 && rope <= 0.4 && phaseShape != FromWeb(tg.webShape) && F.stage.EndsWith(" tornado") && !F.stage.StartsWith("Anticyclonic"))
                F.stage = ShapeName[(int)phaseShape];
        }
    }
}

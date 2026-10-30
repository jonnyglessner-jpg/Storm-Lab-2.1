// The storm in a Unity scene: StormLab's designed storm (StormDesign, the web build's design.ts) painted into a volume
// and ray-marched for every camera by an HDRP custom pass it installs itself (StormCustomPass). Lighting is the web
// build's, or StormLab 2.0's physical sun, sky, ground and night light (SkyLighting).
//
// World placement: 1 Unity unit = 1 m, +x east, +z north, +y up. The storm's frame (km, x east, y north, z up) moves
// with the storm: its origin is at `origin` (world metres; y is the ground) at storm time 0 and travels at the storm's
// motion. For a floating origin, call ShiftOrigin with the same offset the game applies to its world.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StormLab
{
    public enum SunSource { Light, TimeOfDay, Fixed }
    /// <summary>Unified: the physical model's light as one light field, the same for every part of the storm (user,
    /// 2026-10-28); Physical: StormLab 2.0's, each part with its own formula; Website: the web build's</summary>
    public enum StormLighting { Physical, Website, Unified }
    public enum TornadoModel { Detailed, Website }
    /// <summary>Smooth: the game's wall-cloud underside (soft, with blended scud); Website: the web build's ragged fringe</summary>
    public enum WallBase { Smooth, Website }
    /// <summary>Tilted: the game's leaning updraft drops its rain further off, with a rain-free base and clear air round
    /// a tornado (StormTilt); Website: the web build's layout.</summary>
    public enum PrecipLayout { Tilted, Website }
    /// <summary>Enhanced: finer billows on water cloud near the camera (DetailNoise) and a short self-shadow probe that shades
    /// them; Website: the web build's detail.</summary>
    public enum CloudDetail { Enhanced, Website }
    public enum StormPerformance { Custom }

    [ExecuteAlways, DefaultExecutionOrder(100)]
    public class StormRenderer : MonoBehaviour
    {
        [Header("Storm")]
        [Tooltip("setup (StormPreset key): plains, hail, dixie, ohio, lp, capped, pulse")]
        public string preset = "plains";
        [Tooltip("on: a reproducible varied storm (2 to 4 irregular cycles, varied tornado shapes) from the seed below. " +
                 "Off: the web build's original teaching sequence")]
        public bool varied = true;
        public int seed = 42;
        [Tooltip("Detailed: StormLab 2.0's tornadoes on the web build's cycles (StormTornadoes): its shapes, skins, lean, " +
                 "soil, suction vortices and satellites, and its photo-matched funnel and debris. Website: the web build's tornado")]
        public TornadoModel tornado = TornadoModel.Detailed;
        [Tooltip("Smooth: a soft wall-cloud underside with scud blended in under it. Website: the web build's ragged fringe, " +
                 "drawn from noise round the wall cloud's centre (its streaks point at the centre)")]
        public WallBase wallBase = WallBase.Smooth;
        [Tooltip("Tilted: the updraft leans with the shear and drops its rain further off, leaving a rain-free base and clear air round a tornado (StormTilt); Website: the web build's layout")]
        public PrecipLayout precip = PrecipLayout.Tilted;
        /// <summary>storm minutes at which the storm begins to die (StormLife; DieLeaving sets it from a map); infinity:
        /// it lives out the web build's plan</summary>
        [NonSerialized] public double dieFrom = double.PositiveInfinity;

        [Tooltip("ground travel per storm minute, as a share of the storm's motion: 1 normally; a game that runs storms' life " +
                 "cycles faster than its clock (StormScenario's pace) sets 1 / pace, so storms still cross the ground at their real speed")]
        public double motionScale = 1;
        /// <summary>a game's decisions for this storm's plan (StormScenario); null keeps the web build's storm</summary>
        [NonSerialized] public PlanOverrides planOverrides;

        [Header("Placement")]
        [Tooltip("world position (m) of the storm frame's origin at storm time 0; y is the ground the storm stands on. The " +
                 "updraft starts 16 km upstream of it along the anvil (StormPlan.Updraft)")]
        public Vector3 origin = Vector3.zero;
        [Tooltip("how far below the ground (m) rain shafts and the tornado continue, so they reach terrain lower than " +
                 "origin.y (valleys); the scene depth stops them at the ground. Only with scene depth and no backdrop")]
        public float groundSink = 300f;

        [Tooltip("how fast the clouds turn (StormSpin). Real: every feature at a real-world wind over its radius, in real " +
                 "time (user, 2026-10-27); Website: the web build's rates, for the website comparison")]
        public SpinRates spinRates = SpinRates.Real;
        [Tooltip("in Play, shade the scene the sun lights (terrain, roads, vehicles) with the storms' shadow: a cookie on the sun " +
                 "(StormSceneShadow), shared by every storm")]
        public bool castSceneShadow = true;
        [Tooltip("in Play, dim the scene's ambient light by how much of the sky the storms hide from the camera " +
                 "(StormSceneAmbient), shared by every storm; physical and unified lighting only")]
        public bool darkenSceneAmbient = true;
        [Tooltip("the game's exposure opens at night as the night model's adaptation would (the test map's panel does): " +
                 "lightning is then dimmed by that same gain, so a flash keeps about its daytime brightness on screen")]
        public bool exposureAdaptsAtNight = true;
        [Tooltip("a flash at night on screen, as a share of the same flash by day (with exposureAdaptsAtNight). By day a flash " +
                 "already lights cloud to 10-100 times a sunlit cloud, so 3 still turned the whole storm white at night")]
        public float lightningAtNight = 0.3f;

        [Header("Playback")]
        [Tooltip("storm time shown, minutes (StormTime holds it at full precision)")]
        public float stormMinutes;
        [Tooltip("storm minutes per second: 1/60 real time (the default, user 2026-10-27: everything at real speed), 1 the web " +
                 "build's 1x (an hour of storm in a minute)")]
        public float minutesPerSecond = 1f / 60f;
        public bool playing = true;

        [Header("Quality")]
        [Tooltip("Custom: the settings below as set")]
        public StormPerformance performance = StormPerformance.Custom;
        public StormQuality quality = StormQuality.High;
        [Tooltip("ray-march resolution relative to the camera")]
        [Range(0.25f, 1f)] public float renderScale = 0.5f;
        [Range(64, 1024)] public int maxSteps = 700;
        [Tooltip("Enhanced: finer billows (about 10-100 m) on water cloud within a few km, faded by each pixel's footprint, and " +
                 "a self-shadow probe 60 m sunward so the small lobes shade themselves (user, 2026-10-27). Only the march's " +
                 "look changes: the volume, light grid, radar, sound and lightning are the same. Website: the web build's detail")]
        public CloudDetail cloudDetail = CloudDetail.Enhanced;
        [Tooltip("the detailed tornado's fine march step as a share of the radius of its narrowest part, 6 to 50 m (0: 2.0's " +
                 "fixed 6 m, whose cost near a tornado grew with its width)")]
        [Range(0f, 0.25f)] public float tornadoStep = 0.12f;
        /// <summary>light every sample the detailed tornado's fields touch and bound a wedge as widely, as 2.0 did (the
        /// same image, slower: for timing checks)</summary>
        [NonSerialized] public bool tornadoLightAll;
        /// <summary>timing checks only: 1 = the detailed tornado without its sun and horizon tests, 2 = without its fields,
        /// 3 = the march writes each pixel's step counts (all, and those in the tornado, per thousand) instead of its colour</summary>
        [NonSerialized] public int tornadoCostProbe;
        [Tooltip("weight of the new frame in the temporal resolve (1 = off)")]
        [Range(0.05f, 1f)] public float temporalBlend = 0.35f;
        [Tooltip("march half the pixels each frame, alternating, and fill the rest from their neighbours and the history")]
        public bool checkerboard;
        [Tooltip("repaint the volume when storm time has moved, at most this often, s (the web build: 0.2)")]
        public float rebuildEvery = 0.2f;

        [Header("Sun")]
        [Tooltip("Light: the 'sun' Light below (the web build's evening sun, azimuth 272, 21 degrees up, if none). " +
                 "TimeOfDay: from the local solar time, latitude and day below, and the 'sun' Light is turned to match. " +
                 "Fixed: the azimuth and elevation below")]
        public SunSource sunSource = SunSource.Fixed;
        public Light sun;
        [Range(0f, 24f)] public float solarHour = 18.8f;
        [Range(-66f, 66f)] public float latitude = 37.75f;
        [Range(1, 366)] public int dayOfYear = 145;
        public float sunAzimuth = 272f;
        public float sunElevation = 21f;

        [Header("Lighting")]
        [Tooltip("Physical: StormLab 2.0's multiple-scattering sunlight with a Mie phase function, sky light by how much of " +
                 "the sky each point sees, light bounced from the ground, and the night model. Website: the web build's " +
                 "lighting and colours, for comparison with it. Unified: the physical model as one light field for the whole " +
                 "storm (cloud, wall cloud, funnel, rain, hail, dust and the air under it all lit the same way, differing only " +
                 "in how they scatter), light diffusing through thick cloud, and the green of light through hail")]
        public StormLighting lighting = StormLighting.Unified;
        /// <summary>the physical sky, sun colours and night model (Physical and Unified)</summary>
        public bool PhysicalSky => lighting != StormLighting.Website;
        [Range(5f, 50f)] public float dropletDiameter = 16f;
        [Range(1, 4)] public int scatterOctaves = 4;
        [Range(0.3f, 0.95f)] public float octaveExtinction = 0.7f;
        public float sunScale = 1.26f;
        public float skyScale = 1f;
        public float bounceScale = 1f;
        [Range(0f, 0.5f)] public float skyDiffuse = 0.2f;
        [Tooltip("unified: how fast light diffusing through cloud fades with optical depth, 3/4 (1 - g) per unit of the " +
                 "light grid's depth (more: darker undersides and rain under the storm). Real cloud: about 0.11 per unit of " +
                 "its optical depth (g 0.85); the grid's extinction (CloudMax) is about an eighth of a real storm's, so ~0.6")]
        [Range(0.05f, 2f)] public float diffusion = 0.6f;
        [Tooltip("unified: sunlight diffused through the cloud by many scatterings, as a share of the direct")]
        [Range(0f, 1f)] public float diffuseSun = 0.4f;
        [Tooltip("unified: rain's extinction (as the eye sees it and as it shades light) as a multiple of the drawn rain's " +
                 "(StormVolume.RainMax, 2.6 per km in the heaviest rain). Heavy rain is about 4-7 per km (visibility a few " +
                 "hundred metres); at 1 the low sun lit rain cores right through")]
        [Range(1f, 5f)] public float rainExtinction = 2.5f;
        /// <summary>checks only (unified): 1 the light grid's depths and hail, 2-4 the sun's, sky's, ground's light alone,
        /// 5 the near field without its own sky occlusion</summary>
        [NonSerialized] public int lightDebug;

        [Header("Night (physical lighting)")]
        public Light moon;
        [Range(0f, 1f)] public float moonPhase = 0.5f;
        public float moonAzimuth = 120f;
        public float moonElevation = 30f;
        // (user, 2026-10-26: at 0.08 and 0.045 the storm showed light grey all night, a new-moon night brightest of all,
        // while the game's moonlit ground was black)
        [Tooltip("how bright a moonlit night is shown, as a share of day (the exposure opens to it)")]
        [Range(0.002f, 0.095f)] public float nightBrightness = 0.012f;
        [Tooltip("the dimmest a night is ever shown (a moonless night)")]
        [Range(0f, 0.095f)] public float nightFloor = 0.004f;
        [Range(0f, 1f)] public float purkinje = 0.6f;
        [Range(0f, 1f)] public float lightPollution = 0.35f;
        [Range(0f, 1f)] public float nightFill = 0.35f;
        [Tooltip("brighten night scenes to nightBrightness (standalone previews). Off for a game whose exposure adapts")]
        public bool nightAdaptation = false;

        [Header("Output")]
        [Tooltip("brightness through the web build's display curve (toneMap on)")]
        public float exposure = 1.7f;
        [Tooltip("the web build's display curve, for standalone previews with HDRP's tonemapping off. Off: the storm's " +
                 "light in HDRP's physical units, for the game's own exposure and tonemapping")]
        public bool toneMap = false;
        [Tooltip("luminance (nits) of the storm's unit of light (white cloud in the reference evening sun). 0: from the sun " +
                 "Light's lux x " + nameof(SunToLuminance) + " / pi")]
        public float luminanceScale = 0f;
        public const float SunToLuminance = 0.5f;
        public const float DefaultSunLux = 100000f;
        [Tooltip("draw the web build's sky and section-road fields where the camera sees no geometry (standalone only)")]
        public bool drawBackdrop = false;
        [Tooltip("stop rays at scene geometry (reads the camera depth buffer)")]
        public bool useSceneDepth = true;
        [Tooltip("draw this storm as well as the active one (several storms at once)")]
        public bool drawAlongside;

        /// <summary>The web build's standalone look: its backdrop and display curve, and night adaptation (HDRP's tonemapping off).</summary>
        public void UseStandaloneLook() { toneMap = true; drawBackdrop = true; nightAdaptation = true; }

        // ------------------------------------------------------------------ the storm
        public StormPlan Plan { get; private set; }
        /// <summary>each circulation's tornado as the detailed tornado draws it (null entries: no tornado); tools may replace it</summary>
        public TornadoCharacter[] Tornadoes { get; set; }
        public StormFrame Frame { get; } = new StormFrame();
        /// <summary>the angles the mesocyclone and the detailed tornado have turned (written into Frame each StepTo)</summary>
        public StormSpin Spin { get; } = new StormSpin();
        public StormVolume Volume { get; private set; }
        /// <summary>Optional local lighting adapter (lightning). Null keeps the normal cloud path.</summary>
        [NonSerialized] public IStormTransientLighting TransientLighting;
        /// <summary>raised after a new plan is made (setup or seed changed)</summary>
        public event Action<StormRenderer> PlanChanged;
        /// <summary>raised by ShiftOrigin with the offset applied</summary>
        public event Action<StormRenderer, Vector3> OriginShifted;
        /// <summary>GPU profiler sample names (read with UnityEngine.Profiling.Recorder.Get): the volume work each frame, and
        /// each camera's march, temporal resolve and composite</summary>
        public const string VolumeSampler = "StormLab.Volume", MarchSampler = "StormLab.March", CompositeSampler = "StormLab.Composite";
        public static StormRenderer Active { get; private set; }
        public static readonly List<StormRenderer> Enabled = new List<StormRenderer>();
        /// <summary>A town whose lights glow on cloud bases at night: where it is (world), how far it spreads (m) and how
        /// bright it is (1: a town of about 15,000; StormRenderer.lightPollution scales them all).</summary>
        public struct TownGlow { public Vector3 position; public float radiusM, glow; }
        /// <summary>The towns lighting the storms' bases at night, set by the game (the first StormVolume.MaxTowns count).
        /// None: every base takes lightPollution's town light evenly, as before.</summary>
        public static readonly List<TownGlow> Towns = new List<TownGlow>();
        /// <summary>with towns given: the town light over open country, as a share of the even town light (StormVolume.compute
        /// GroundLight; the horizon's and the haze's town glow, SkyLight.townSky)</summary>
        public const float TownFloor = 0.15f;
        /// <summary>the unified lighting's sun reach (StormCommon.hlsl sunReach) for what the storm shades besides itself
        /// (its ground light, StormSceneShadow); zero for the other lightings</summary>
        public Vector4 SunReach => lighting == StormLighting.Unified ? new Vector4(scatterOctaves, octaveExtinction, diffusion, diffuseSun) : Vector4.zero;
        public bool ReadyToRender => Volume != null && hasFrame;
        /// <summary>wall-clock seconds, for spin inside features (the web build's clock)</summary>
        public float Clock { get => (float)clock; private set => clock = value; }
        double clock;

        /// <summary>
        /// Storm time, minutes, at double precision. The clock adds a frame's step of a few hundred-thousandths of a minute
        /// at real time; in stormMinutes (a float) that step is near its resolution an hour into the storm, and the clock
        /// lost a few per cent. stormMinutes is kept as a copy for the inspector; writing either one sets the time.
        /// </summary>
        public double StormTime
        {
            get { if (stormMinutes != shownMinutes) { time = stormMinutes; shownMinutes = stormMinutes; } return time; }
            set { time = value; stormMinutes = shownMinutes = (float)value; }
        }
        double time; float shownMinutes = float.NaN;

        ComputeShader volCs, marchCs; Material compositeMat; Texture3D noise;
        CommandBuffer frameCmd;
        bool hasFrame; double lastBuilt = double.NaN; float lastBuildWall = -1e9f; Vector3 lastKey; bool lastKeyMoon;
        string planKey; StormQuality volQuality;
        static readonly int LightScaleId = Shader.PropertyToID("_StormLabLight");

        /// <summary>Make a new plan now (call after changing preset, varied or seed from code).</summary>
        public void Replan()
        {
            var found = StormPreset.Find(preset);
            if (found == null) { Debug.LogWarning($"StormLab: no preset '{preset}', using plains"); found = StormPreset.All[0]; }
            Plan = StormLife.Trim(StormGame.Plan(preset, found, varied ? seed : (int?)null, planOverrides), dieFrom);
            Tornadoes = StormTornadoes.Build(Plan, StormTornadoes.RegionOf(preset));
            planKey = PlanKey;
            hasFrame = false; lastBuilt = double.NaN; Spin.Reset();
            foreach (var c in cams.Values) c.valid = false;
            PlanChanged?.Invoke(this);
        }
        string PlanKey => preset + "|" + varied + "|" + seed + "|" + planOverrides + "|" + dieFrom.ToString("0.###");

        /// <summary>The storm dies as it leaves a square map of half-width mapHalfM centred on the world origin (StormLife):
        /// from where it is placed now, no tornado once it is about to leave, then its collapse. Call after placing it.</summary>
        public void DieLeaving(float mapHalfM)
        {
            if (Plan == null) Replan();
            var u = UpdraftWorld(0);
            double die = StormLife.DyingFrom(u.x, u.z, Plan.motion, motionScale, mapHalfM);
            if (Math.Abs(die - dieFrom) < 1e-3 || double.IsInfinity(die) && double.IsInfinity(dieFrom)) return;
            dieFrom = die;
            var keep = origin;
            Replan();
            origin = keep;
        }

        // ------------------------------------------------------------------ coordinates
        /// <summary>Move the storm with a floating-origin shift: add the offset the game applied to every object.</summary>
        public void ShiftOrigin(Vector3 offset)
        {
            origin += offset;
            // camera histories are kept in the storm frame, which moves with origin, so they stay valid
            OriginShifted?.Invoke(this, offset);
        }
        double TravelX(double tMin) => Plan != null ? Plan.motion.x * 0.06 * tMin * motionScale : 0;
        double TravelY(double tMin) => Plan != null ? Plan.motion.y * 0.06 * tMin * motionScale : 0;
        /// <summary>world position (m) to the storm frame (km) at the storm time shown</summary>
        public Vector3 WorldToStorm(Vector3 w)
        {
            double x = (w.x - (double)origin.x) / 1000.0 - TravelX(StormTime), y = (w.z - (double)origin.z) / 1000.0 - TravelY(StormTime);
            return new Vector3((float)x, (float)y, (w.y - origin.y) / 1000f);
        }
        /// <summary>storm-frame position (km) to world (m) at storm time tMin</summary>
        public Vector3 StormToWorld(Vector3 s, double tMin)
            => new Vector3((float)(origin.x + (s.x + TravelX(tMin)) * 1000.0), origin.y + s.z * 1000f, (float)(origin.z + (s.y + TravelY(tMin)) * 1000.0));
        public Vector3 StormToWorld(Vector3 s) => StormToWorld(s, StormTime);
        public static Vector3 WorldToStormDir(Vector3 w) => new Vector3(w.x, w.z, w.y);
        public static Vector3 StormToWorldDir(Vector3 v) => new Vector3(v.x, v.z, v.y);
        /// <summary>the updraft's centre at cloud base, in world metres, at storm time tMin</summary>
        public Vector3 UpdraftWorld(double tMin)
        {
            if (Plan == null) return origin;
            var u = Plan.Updraft;
            return StormToWorld(new Vector3((float)u.x, (float)u.y, (float)Plan.zb), tMin);
        }

        /// <summary>Move the storm so its updraft is over world point `ground` (x, z; y is the ground) at storm time tMin.</summary>
        public void PlaceUpdraftAt(Vector3 ground, double tMin)
        {
            if (Plan == null) Replan();
            var u = Plan.Updraft;
            origin = new Vector3((float)(ground.x - (u.x + TravelX(tMin)) * 1000.0), ground.y, (float)(ground.z - (u.y + TravelY(tMin)) * 1000.0));
        }

        /// <summary>The rain-free base's radius and a tornado's clear radius now, km (StormTilt.Clear; zero for the web
        /// build's layout): what the volume's build and the radar leave clear.</summary>
        public Vector4 ClearParams
        {
            get
            {
                float hide = hideRain ? 1 : 0;
                if (precip != PrecipLayout.Tilted || Plan == null) return new Vector4(0, 0, hide, 0);
                var (b, t) = StormTilt.Clear(Plan, Frame);
                // w: rain only under cloud reaching down to mid levels (StormVolume.compute RainUnderCloud; user, 2026-10-28)
                return new Vector4((float)b, (float)t, hide, StormVolume.CloudMax);
            }
        }
        /// <summary>For checks: the volume's build leaves out all rain (to tell the cloud's shapes from the rain's).</summary>
        [NonSerialized] public bool hideRain;
        /// <summary>For checks: the web build's tail cloud under the game's wall-cloud base (to compare the two tails).</summary>
        [NonSerialized] public bool webTail;
        /// <summary>For checks: no tail cloud at all (to find it in a view).</summary>
        [NonSerialized] public bool hideTail;
        /// <summary>_WallP: x the game's wall-cloud underside, y its tail cloud (both with wallBase Smooth)</summary>
        // w: the game's beaver tail, smaller than the web build's (StormCommon.hlsl tailSize; user, 2026-10-28)
        Vector4 WallParams => new Vector4(wallBase == WallBase.Smooth ? 1 : 0, wallBase == WallBase.Smooth && !webTail ? 1 : 0, hideTail ? 1 : 0, wallBase == WallBase.Smooth && !webTail ? 1 : 0);

        /// <summary>the tornado on the ground now, 0..1 (StormFrame.tornado)</summary>
        public float TornadoAmount => (float)Frame.tornado;
        /// <summary>where the tornado touches the ground now, world metres (meaningful while TornadoAmount > 0)</summary>
        public Vector3 TornadoGroundWorld => StormToWorld(new Vector3(Frame.param[13 * 4], Frame.param[13 * 4 + 1], 0f));
        /// <summary>
        /// The tornado's wind at a world position, m/s (horizontal): a Rankine vortex turning counter-clockwise at the
        /// funnel's ground radius, with a little inflow, scaled to the storm's median tornado and the second tornado's
        /// extra strength. A presentation model for pushing things around, not the design's flow.
        /// </summary>
        public Vector3 TornadoWindAt(Vector3 world)
        {
            float amt = TornadoAmount;
            if (Plan == null || amt <= 0f) return Vector3.zero;
            var c = TornadoGroundWorld;
            var d = new Vector2(world.x - c.x, world.z - c.z); float r = d.magnitude;
            float rc = Mathf.Max(Frame.param[14 * 4] * 1000f, 20f);
            float vmax = (float)Plan.vTor * (Frame.param[21 * 4 + 3] > 0.5f ? 1.5f : 1f) * amt * (1f - 0.6f * Frame.param[14 * 4 + 3]);
            // the detailed tornado carries its own peak wind and radius of maximum wind (S_tor6)
            int t6 = StormTornadoes.Slot0 * 4;
            if (Frame.param[t6 + 1] > 0f) { vmax = Frame.param[t6 + 1] * amt; rc = Mathf.Max(Frame.param[t6 + 2] * 1000f, 10f); }
            float vt = r < rc ? vmax * r / rc : vmax * rc / Mathf.Max(r, 1f);
            if (r < 1f) return Vector3.zero;
            var t = new Vector2(-d.y, d.x) / r; var n = -d / r;
            var v = t * vt + n * (0.3f * vt);
            return new Vector3(v.x, 0f, v.y);
        }

        // ------------------------------------------------------------------ sun, moon and sky
        public static Vector3 SunVol(float azDeg, float elevDeg)
        {
            float az = azDeg * Mathf.Deg2Rad, el = elevDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Cos(az) * Mathf.Cos(el), Mathf.Sin(el));
        }
        /// <summary>The sun's compass bearing and height (degrees) at a local solar time, latitude and day of the year.</summary>
        public static Vector2 SolarPosition(float solarHour, float latDeg, int day)
        {
            double decl = 23.44 * Mathf.Deg2Rad * Math.Sin(2 * Math.PI * (284 + day) / 365.0);
            double H = (solarHour - 12.0) * 15.0 * Mathf.Deg2Rad, lat = latDeg * Mathf.Deg2Rad;
            double sinEl = Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(H);
            double az = Math.Atan2(-Math.Sin(H), Math.Tan(decl) * Math.Cos(lat) - Math.Sin(lat) * Math.Cos(H));
            return new Vector2((float)((az * Mathf.Rad2Deg + 360.0) % 360.0), (float)(Math.Asin(sinEl) * Mathf.Rad2Deg));
        }
        /// <summary>The moon's bearing and height: it trails the sun by its phase, and its declination swings opposite the sun's.</summary>
        public static Vector2 MoonPosition(float solarHour, float latDeg, int day, float phase)
        {
            float h = solarHour - phase * 24f;
            h = ((h % 24f) + 24f) % 24f;
            double declSun = 23.44 * Math.Sin(2 * Math.PI * (284 + day) / 365.0);
            double decl = declSun * Math.Cos(2 * Math.PI * phase) * Mathf.Deg2Rad;
            double H = (h - 12.0) * 15.0 * Mathf.Deg2Rad, lat = latDeg * Mathf.Deg2Rad;
            double sinEl = Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(H);
            double az = Math.Atan2(-Math.Sin(H), Math.Tan(decl) * Math.Cos(lat) - Math.Sin(lat) * Math.Cos(H));
            return new Vector2((float)((az * Mathf.Rad2Deg + 360.0) % 360.0), (float)(Math.Asin(sinEl) * Mathf.Rad2Deg));
        }
        public Vector3 SunDirVol
        {
            get
            {
                switch (sunSource)
                {
                    case SunSource.TimeOfDay: { var a = SolarPosition(solarHour, latitude, dayOfYear); return SunVol(a.x, a.y); }
                    case SunSource.Light: return sun ? WorldToStormDir(-sun.transform.forward) : SunVol(272f, 21f);
                    default: return SunVol(sunAzimuth, sunElevation);
                }
            }
        }
        public Vector3 MoonDirVol
        {
            get
            {
                if (sunSource == SunSource.TimeOfDay) { var a = MoonPosition(solarHour, latitude, dayOfYear, moonPhase); return SunVol(a.x, a.y); }
                if (sunSource == SunSource.Light && moon) return WorldToStormDir(-moon.transform.forward);
                return SunVol(moonAzimuth, moonElevation);
            }
        }
        public NightSettings Night => new NightSettings
        {
            moonPhase = moonPhase, nightBrightness = nightBrightness, nightFloor = nightFloor, purkinje = purkinje,
            lightPollution = lightPollution, adapt = nightAdaptation
        };
        /// <summary>This frame's sun, moon, sky and night light. The web build's lighting keeps its fixed colours.</summary>
        public SkyLight Lighting
        {
            get
            {
                var sunVol = SunDirVol;
                if (PhysicalSky) return SkyLighting.Evaluate(sunVol, MoonDirVol, Night);
                float el = Mathf.Asin(Mathf.Clamp(sunVol.z, -1f, 1f)) * Mathf.Rad2Deg;
                return new SkyLight
                {
                    keyDir = sunVol, keyIsMoon = false, keyC = new Vector4(0.95f, 0.95f * 0.82f, 0.95f * 0.6f, el),
                    skyC = new Vector4(0.42f, 0.52f, 0.68f, 1f), dim = 1f, townLight = 0f, townSky = 1f, level = 1f, gain = 1f, night = 0f, dark = 0f
                };
            }
        }
        /// <summary>
        /// What lightning is scaled by tonight (user, 2026-10-26: blinding at night, now and then glitching): with the
        /// game's exposure opened by the night model's gain, a flash came out about a thousand times brighter on screen
        /// than by day, and could overflow HDRP's half-float buffers. 1 by day, lightningAtNight / gain at night; 1
        /// where the storm adapts itself (nightAdaptation) or the game's exposure does not (the march's own divisor
        /// handles the first).
        /// </summary>
        public float LightningScale
        {
            get
            {
                if (nightAdaptation || !exposureAdaptsAtNight || !PhysicalSky) return 1f;
                var n = Night; n.adapt = true;
                float gain = SkyLighting.Evaluate(SunDirVol, MoonDirVol, n).gain;
                return Mathf.Min(1f, lightningAtNight / Mathf.Max(gain, 1e-6f));
            }
        }
        /// <summary>luminance of the storm's unit of light, nits</summary>
        public float Luminance => luminanceScale > 0f ? luminanceScale : (sun ? sun.intensity : DefaultSunLux) * SunToLuminance / Mathf.PI;

        // ------------------------------------------------------------------ lifecycle
        void OnEnable()
        {
            Active = this; if (!Enabled.Contains(this)) Enabled.Add(this);
            EnsurePass();
        }
        static void EnsurePass()
        {
            if (GraphicsSettings.currentRenderPipeline is UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset) StormCustomPass.EnsureInstalled();
        }
        public void Activate() { Active = this; }
        void OnDisable()
        {
            Enabled.Remove(this);
            if (Active != this) return;
            Active = null;
            for (int i = Enabled.Count - 1; i >= 0; i--) if (Enabled[i] && Enabled[i].drawAlongside) { Active = Enabled[i]; break; }
        }
        void OnDestroy() { Release(); }
        void OnValidate() { if (Plan != null && planKey != PlanKey) Replan(); }

        void LoadAssets()
        {
            if (!volCs) volCs = Resources.Load<ComputeShader>("StormVolume");
            if (!marchCs) marchCs = Resources.Load<ComputeShader>("StormMarch");
            if (!compositeMat) { var sh = Shader.Find("Hidden/StormLab/Composite"); if (sh) compositeMat = new Material(sh) { hideFlags = HideFlags.DontSave }; }
            if (!noise) noise = CloudNoise.Make();
        }

        public void Release()
        {
            Volume?.Dispose(); Volume = null;
            foreach (var c in cams.Values) c.Dispose();
            cams.Clear();
            hasFrame = false;
        }

        void Update()
        {
            // the storms' shadow on the scene's terrain, roads and vehicles: a cookie on the sun (StormSceneShadow)
            if (castSceneShadow && Application.isPlaying && sun && sun.type == LightType.Directional && !sun.GetComponent<StormSceneShadow>())
                StormSceneShadow.For(sun);
            if (darkenSceneAmbient && Application.isPlaying && PhysicalSky) StormSceneAmbient.Ensure();
            double dt = Application.isPlaying ? Time.deltaTime : 0.0;
            clock += dt;
            if (Plan != null && playing && Application.isPlaying) StormTime = Math.Min(Math.Max(StormTime + dt * minutesPerSecond, 0.0), Plan.duration);
            StepTo(StormTime, Clock);
        }

        /// <summary>
        /// Show storm time tMin (minutes) at wall clock `clock` (s): evaluate the design and do this frame's volume work.
        /// The volume is repainted when storm time has moved, at most every rebuildEvery seconds, spread over a few frames.
        /// </summary>
        public bool StepTo(double tMin, float clock, bool rebuildNow = false)
        {
            EnsurePass();
            LoadAssets();
            if (!volCs || !marchCs) return false;
            if (Plan == null || planKey != PlanKey) Replan();
            if (Volume == null || volQuality != quality) { Volume?.Dispose(); Volume = new StormVolume(quality, volCs); volQuality = quality; hasFrame = false; }
            StormTime = tMin; Clock = clock;
            StormDesign.At(Plan, tMin, clock, Frame);
            if (tornado == TornadoModel.Detailed) StormTornadoes.Apply(Plan, Tornadoes, tMin, Frame);
            if (precip == PrecipLayout.Tilted) StormTilt.Apply(Plan, Frame);
            StormLife.Apply(Plan, tMin, Frame);
            Spin.Advance(Frame, tMin, clock, spinRates);
            if (frameCmd == null) frameCmd = new CommandBuffer { name = "Storm volume" };
            frameCmd.Clear();
            frameCmd.BeginSample(VolumeSampler);
            if (sunSource == SunSource.TimeOfDay && sun) sun.transform.rotation = Quaternion.LookRotation(-StormToWorldDir(SunDirVol));
            var lt = Lighting;
            float wall = Application.isPlaying ? Time.realtimeSinceStartup : (float)(DateTime.Now.TimeOfDay.TotalSeconds);
            bool keyMoved = Vector3.Dot(lt.keyDir, lastKey) < 0.99995f || lt.keyIsMoon != lastKeyMoon;
            var wallP = WallParams;
            if (wallP != Volume.wallParams) { Volume.wallParams = wallP; hasFrame = false; }
            Volume.precipParams = ClearParams;
            float rainScale = lighting == StormLighting.Unified ? rainExtinction : 1f;
            if (rainScale != Volume.rainScale) { Volume.rainScale = rainScale; hasFrame = false; }
            Volume.sunReach = SunReach;
            Volume.townCount = 0;
            foreach (var tw in Towns)
            {
                if (Volume.townCount >= StormVolume.MaxTowns) break;
                var tp = WorldToStorm(tw.position);
                Volume.towns[Volume.townCount++] = new Vector4(tp.x, tp.y, tw.radiusM / 1000f, tw.glow);
            }
            if (!hasFrame || rebuildNow || double.IsNaN(lastBuilt))
            {
                Volume.Update(frameCmd, Frame.param, noise, lt.keyDir);
                lastBuilt = tMin; lastBuildWall = wall; hasFrame = true;
                lastKey = lt.keyDir; lastKeyMoon = lt.keyIsMoon;
            }
            else if (!Volume.Rebuilding && (Math.Abs(tMin - lastBuilt) > 0.05 || keyMoved) && wall - lastBuildWall > rebuildEvery)
            {
                Volume.BeginUpdate(Frame.param, noise, lt.keyDir);
                lastBuilt = tMin; lastBuildWall = wall;
                lastKey = lt.keyDir; lastKeyMoon = lt.keyIsMoon;
            }
            Volume.Step(frameCmd);
            frameCmd.EndSample(VolumeSampler);
            Graphics.ExecuteCommandBuffer(frameCmd);
            // the light scale the storm's lightning shares with it: x luminance of a unit, y 1 to apply the camera's exposure
            Shader.SetGlobalVector(LightScaleId, toneMap ? new Vector4(1, 0, 0, 0) : new Vector4(Luminance, 1, 0, 0));
            return true;
        }

        /// <summary>Force a full repaint at the next StepTo.</summary>
        public void ResetVolume() { hasFrame = false; lastBuilt = double.NaN; }

        /// <summary>the parameter block as the passes see it this frame (grid slot filled in)</summary>
        public Vector4[] GpuParams() => Volume.GpuParams(Frame.param);

        // ------------------------------------------------------------------ per-camera march
        sealed class CamState
        {
            public RenderTexture cur, dist, half, shaft; public readonly RenderTexture[] hist = new RenderTexture[2]; public int h;
            public Vector3 prevPos, prevFwd, prevRight, prevUp; public bool valid, wasTransient, shaftApart; public int frame;
            public void Ensure(int w, int hh)
            {
                if (cur && cur.width == w && cur.height == hh) return;
                Dispose();
                cur = RT(w, hh, RenderTextureFormat.ARGBHalf); dist = RT(w, hh, RenderTextureFormat.RFloat); half = RT(w, hh, RenderTextureFormat.RFloat);
                shaft = RT(w, hh, RenderTextureFormat.ARGBHalf);
                hist[0] = RT(w, hh, RenderTextureFormat.ARGBHalf); hist[1] = RT(w, hh, RenderTextureFormat.ARGBHalf);
                valid = false; wasTransient = false;
            }
            static RenderTexture RT(int w, int h, RenderTextureFormat f)
            {
                var r = new RenderTexture(w, h, 0, f, RenderTextureReadWrite.Linear) { enableRandomWrite = true, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave, name = "storm march" };
                r.Create(); return r;
            }
            public void Dispose()
            {
                foreach (var r in new[] { cur, dist, half, shaft, hist[0], hist[1] }) if (r) { r.Release(); UnityEngine.Object.DestroyImmediate(r); }
                cur = dist = half = shaft = hist[0] = hist[1] = null;
            }
        }
        readonly Dictionary<Camera, CamState> cams = new Dictionary<Camera, CamState>();
        /// <summary>Discard a camera's temporal history (after a cut).</summary>
        public void ResetHistory(Camera cam) { if (cams.TryGetValue(cam, out var s)) { s.valid = false; s.frame = 0; } }

        static Texture2DArray noDepth;
        static Texture2DArray NoDepth
        {
            get
            {
                if (noDepth) return noDepth;
                noDepth = new Texture2DArray(1, 1, 1, TextureFormat.RFloat, false, true) { hideFlags = HideFlags.HideAndDontSave, name = "storm no depth" };
                noDepth.SetPixels(new[] { Color.clear }, 0); noDepth.Apply(false, true);
                return noDepth;
            }
        }
        static Texture3D black3D;
        static Texture3D Black3D
        {
            get
            {
                if (black3D) return black3D;
                black3D = new Texture3D(1, 1, 1, TextureFormat.RGBAHalf, false) { hideFlags = HideFlags.HideAndDontSave };
                black3D.SetPixelData(new ushort[4], 0); black3D.Apply(false);
                return black3D;
            }
        }

        /// <summary>
        /// Record the ray-march and temporal resolve for one camera. depth: the camera's depth buffer (or null), not bound
        /// as a target; fullW x fullH: the viewport. Returns the resolved storm texture (rgb light, a transmittance), and in
        /// dist the distance along each ray where the storm hides half of what is behind it, km.
        /// </summary>
        public RenderTexture RecordMarch(CommandBuffer cmd, Camera cam, RTHandle depth, int fullW, int fullH, bool backdrop, out RenderTexture dist,
            RenderTexture otherHalf = null)
        {
            dist = null;
            if (!ReadyToRender || !marchCs) return null;
            int w = Mathf.Max(8, Mathf.RoundToInt(fullW * renderScale)), h = Mathf.Max(8, Mathf.RoundToInt(fullH * renderScale));
            if (!cams.TryGetValue(cam, out var st)) { st = new CamState(); cams[cam] = st; }
            st.Ensure(w, h);
            var tr = cam.transform;
            Vector3 pos = WorldToStorm(tr.position), fwd = WorldToStormDir(tr.forward), right = WorldToStormDir(tr.right), up = WorldToStormDir(tr.up);
            float tanY = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), tanX = tanY * fullW / (float)fullH;
            int kM = marchCs.FindKernel("March"), kR = marchCs.FindKernel("Resolve");
            cmd.BeginSample(MarchSampler);
            SetVariant(cmd);
            cmd.SetComputeVectorArrayParam(marchCs, "_SP", GpuParams());
            cmd.SetComputeVectorParam(marchCs, "_CamPos", pos); cmd.SetComputeVectorParam(marchCs, "_CamFwd", fwd);
            cmd.SetComputeVectorParam(marchCs, "_CamRight", right); cmd.SetComputeVectorParam(marchCs, "_CamUp", up);
            cmd.SetComputeVectorParam(marchCs, "_CamTan", new Vector4(tanX, tanY, 0, 0));
            cmd.SetComputeVectorParam(marchCs, "_Res", new Vector4(w, h, 1f / w, 1f / h));
            var lt = Lighting;
            if (Towns.Count > 0) lt.townSky = TownFloor;
            cmd.SetComputeVectorParam(marchCs, "_Sun", lt.keyDir);
            cmd.SetComputeVectorParam(marchCs, "_SunC", lt.keyC); cmd.SetComputeVectorParam(marchCs, "_SkyC", lt.skyC);
            cmd.SetComputeVectorParam(marchCs, "_SkyP", new Vector4(lt.dim, lt.townLight, lt.keyIsMoon ? 1 : 0, lt.townSky));
            // w: the lightning's scale in the cloud (undoing the storm's own night gain, or the game's: LightningScale)
            cmd.SetComputeVectorParam(marchCs, "_Night", new Vector4(lt.gain, lt.night, nightFill * lt.dark, nightAdaptation ? 1f / Mathf.Max(lt.gain, 1f) : LightningScale));
            float model = lighting == StormLighting.Unified ? 2 : lighting == StormLighting.Physical ? 1 : 0;
            cmd.SetComputeVectorParam(marchCs, "_PL", new Vector4(model, dropletDiameter, sunScale, scatterOctaves));
            cmd.SetComputeVectorParam(marchCs, "_PL2", new Vector4(skyScale, bounceScale, skyDiffuse, octaveExtinction));
            cmd.SetComputeVectorParam(marchCs, "_PL3", new Vector4(0f, diffusion, diffuseSun, lightDebug));
            SkyLighting.Bands(lt, out var bZen, out var bMid, out var bHor, out var bHorK);
            cmd.SetComputeVectorParam(marchCs, "_SkyZen", bZen); cmd.SetComputeVectorParam(marchCs, "_SkyMid", bMid);
            cmd.SetComputeVectorParam(marchCs, "_SkyHor", bHor); cmd.SetComputeVectorParam(marchCs, "_SkyHorK", bHorK);
            cmd.SetComputeVectorParam(marchCs, "_MP", new Vector4(StormVolume.CloudMax, StormVolume.RainMax * (Volume != null ? Volume.rainScale : 1f), exposure, st.frame++));
            cmd.SetComputeVectorParam(marchCs, "_TorP", new Vector4(tornado == TornadoModel.Detailed ? 1 : 0, tornadoStep, tornadoLightAll ? 1 : 0, tornadoCostProbe));
            cmd.SetComputeVectorParam(marchCs, "_WallP", WallParams);
            // the ground pattern of the backdrop moves under the storm as it travels
            var travel = new Vector2((float)(TravelX(StormTime) + origin.x / 1000.0), (float)(TravelY(StormTime) + origin.z / 1000.0));
            cmd.SetComputeVectorParam(marchCs, "_Opt", new Vector4(travel.x, travel.y, drawBackdrop && backdrop ? 1 : 0, toneMap ? 1 : 0));
            float n = cam.nearClipPlane, f = cam.farClipPlane;
            bool rev = SystemInfo.usesReversedZBuffer;
            float zx = rev ? -1f + f / n : 1f - f / n, zy = rev ? 1f : f / n;
            cmd.SetComputeVectorParam(marchCs, "_ZB", new Vector4(zx / f, zy / f, rev ? 1 : 0, 0));
            bool useDepth = depth != null && useSceneDepth;
            if (useDepth) cmd.SetComputeTextureParam(marchCs, kM, "_DepthTex", depth, 0, RenderTextureSubElement.Depth);
            else cmd.SetComputeTextureParam(marchCs, kM, "_DepthTex", NoDepth);
            cmd.SetComputeVectorParam(marchCs, "_DepthP", new Vector4(useDepth ? 1 : 0, fullW, fullH, maxSteps));
            bool sink = useDepth && !drawBackdrop;
            cmd.SetComputeVectorParam(marchCs, "_GroundP", new Vector4(sink ? Mathf.Max(groundSink, 0f) / 1000f : 0f, 0, 0, 0));
            cmd.SetComputeTextureParam(marchCs, kM, "_Dens", Volume.Dens);
            cmd.SetComputeTextureParam(marchCs, kM, "_Light", Volume.LightTex);
            cmd.SetComputeTextureParam(marchCs, kM, "_LightW", Volume.LightWebTex);
            cmd.SetComputeTextureParam(marchCs, kM, "_SkyVis", Volume.SkyVisTex);
            cmd.SetComputeTextureParam(marchCs, kM, "_Ground", Volume.GroundTex);
            cmd.SetComputeTextureParam(marchCs, kM, "_Noise", noise);
            cmd.SetComputeTextureParam(marchCs, kM, "_DetailNoise", DetailNoise.Get());
            cmd.SetComputeVectorParam(marchCs, "_CD", new Vector4(cloudDetail == CloudDetail.Enhanced ? 1 : 0, 0, 0, 0));
            cmd.SetComputeTextureParam(marchCs, kM, "_Out", st.cur);
            cmd.SetComputeTextureParam(marchCs, kM, "_OutDist", st.dist);
            cmd.SetComputeTextureParam(marchCs, kM, "_OutHalf", st.half);
            bool other = otherHalf && otherHalf.width == st.half.width && otherHalf.height == st.half.height;
            cmd.SetComputeTextureParam(marchCs, kM, "_OtherHalf", other ? (Texture)otherHalf : Texture2D.blackTexture);
            // drawn alongside another storm: the light shafts kept apart for the composite (not with the backdrop's tone map)
            bool apart = otherHalf != null && !toneMap;
            cmd.SetComputeTextureParam(marchCs, kM, "_OutShaft", st.shaft);
            cmd.SetComputeVectorParam(marchCs, "_OtherP", new Vector4(other ? 1 : 0, apart ? 1 : 0, 0, 0));
            st.shaftApart = apart;
            cmd.SetComputeIntParam(marchCs, "_TransientLightOn", 0);
            cmd.SetComputeTextureParam(marchCs, kM, "_TransientLightTex", Black3D);
            bool transient = TransientLighting != null && TransientLighting.RecordLighting(cmd, this, cam, marchCs, kM);
            bool fresh = transient || st.wasTransient;
            bool useCheckerboard = checkerboard && !fresh;
            cmd.SetComputeVectorParam(marchCs, "_TP", new Vector4(st.valid && !fresh ? temporalBlend : 1f, useCheckerboard ? 1 : 0, 0, 0));
            DispatchMarch(cmd, kM, w, h, useCheckerboard, pos, fwd, right, up, tanX, tanY);
            st.wasTransient = transient;
            var src = st.hist[st.h]; var dst = st.hist[1 - st.h];
            cmd.SetComputeTextureParam(marchCs, kR, "_Cur", st.cur);
            cmd.SetComputeTextureParam(marchCs, kR, "_CurDist", st.dist);
            cmd.SetComputeTextureParam(marchCs, kR, "_Hist", src);
            cmd.SetComputeTextureParam(marchCs, kR, "_ResolveOut", dst);
            // the previous camera in this frame's storm frame (the frame travels with the storm)
            cmd.SetComputeVectorParam(marchCs, "_PrevPos", st.prevPos); cmd.SetComputeVectorParam(marchCs, "_PrevFwd", st.prevFwd);
            cmd.SetComputeVectorParam(marchCs, "_PrevRight", st.prevRight); cmd.SetComputeVectorParam(marchCs, "_PrevUp", st.prevUp);
            cmd.DispatchCompute(marchCs, kR, (w + 7) / 8, (h + 7) / 8, 1);
            st.h = 1 - st.h; st.valid = true;
            st.prevPos = pos; st.prevFwd = fwd; st.prevRight = right; st.prevUp = up;
            cmd.EndSample(MarchSampler);
            dist = st.half;
            return dst;
        }

        // The march's compiled variants (StormMarch.compute): only the paths this frame can take, so the GPU keeps fewer
        // registers a ray and more rays in flight. Set in the command buffer, as two storms may want different ones.
        LocalKeyword kwUnified, kwNoTornado, kwProbes; ComputeShader kwFor;
        void SetVariant(CommandBuffer cmd)
        {
            if (kwFor != marchCs)
            {
                kwUnified = new LocalKeyword(marchCs, "STORM_UNIFIED"); kwNoTornado = new LocalKeyword(marchCs, "STORM_NO_TORNADO");
                kwProbes = new LocalKeyword(marchCs, "STORM_PROBES"); kwFor = marchCs;
            }
            cmd.SetKeyword(marchCs, kwUnified, lighting == StormLighting.Unified);
            cmd.SetKeyword(marchCs, kwProbes, tornadoCostProbe != 0 || lightDebug != 0 || tornadoLightAll);
        }
        static readonly int TorRectId = Shader.PropertyToID("_TorRect"), TileId = Shader.PropertyToID("_TileP");

        /// <summary>
        /// Dispatch the march (w x h, the march's resolution). The tornado's code alone made every ray's kernel about three
        /// times slower (a young storm from 8 km: 9.0 ms at 3440 x 1440 with it compiled in, 3.0 without: StormPerfCheck,
        /// 2026-10-30), whether the ray came near a tornado or not. So the screen is split: the rectangle where a ray may meet a
        /// tornado (TornadoRect) is marched by the full kernel, the rest by the one without the tornado. Same image.
        /// </summary>
        void DispatchMarch(CommandBuffer cmd, int kM, int w, int h, bool checker, Vector3 pos, Vector3 fwd, Vector3 right, Vector3 up, float tanX, float tanY)
        {
            int gx = checker ? ((w + 1) / 2 + 7) / 8 : (w + 7) / 8, gy = (h + 7) / 8;
            var r = TornadoRect(pos, fwd, right, up, tanX, tanY, w, h);
            bool split = r.width > 0 && r.height > 0 && (float)r.width * r.height < 0.6f * w * h;
            if (r.width <= 0 || r.height <= 0 || split)
            {
                // everything outside the rectangle (all of it when no tornado is in view) without the tornado
                cmd.SetKeyword(marchCs, kwNoTornado, true);
                cmd.SetComputeVectorParam(marchCs, TorRectId, split ? new Vector4(r.xMin, r.yMin, r.xMax, r.yMax) : Vector4.zero);
                cmd.SetComputeVectorParam(marchCs, TileId, Vector4.zero);
                cmd.DispatchCompute(marchCs, kM, gx, gy, 1);
                if (!split) return;
            }
            else r = new RectInt(0, 0, w, h);
            // the rectangle with it: thread offsets in the dispatch's own columns (half the pixels' with the checkerboard)
            cmd.SetKeyword(marchCs, kwNoTornado, false);
            cmd.SetComputeVectorParam(marchCs, TorRectId, new Vector4(r.xMin, r.yMin, r.xMax, r.yMax));
            int x0 = (checker ? r.xMin / 2 : r.xMin) / 8 * 8, y0 = r.yMin / 8 * 8;
            int x1 = checker ? (r.xMax + 1) / 2 : r.xMax;
            cmd.SetComputeVectorParam(marchCs, TileId, new Vector4(x0, y0, 0, 0));
            cmd.DispatchCompute(marchCs, kM, Mathf.Max(1, (x1 - x0 + 7) / 8), Mathf.Max(1, (r.yMax - y0 + 7) / 8), 1);
        }

        /// <summary>
        /// The march pixels whose rays may meet a tornado (main, satellite or anticyclonic, either model): the screen
        /// rectangle round the vertical cylinders the march bounds them by (StormMarch.compute: tornadoBound round the main
        /// one, satelliteBound, the anticyclonic one's 0.4 km), from the ground under the scene's sink to their top. Empty when
        /// there is no tornado or none is in view; the whole screen when the camera is in or beside one.
        /// </summary>
        RectInt TornadoRect(Vector3 cp, Vector3 fwd, Vector3 right, Vector3 up, float tanX, float tanY, int w, int h)
        {
            var p = Frame.param;
            float P(int slot, int c) => p[slot * 4 + c];
            float zb = P(1, 0), z0 = -Mathf.Max(groundSink, 0f) / 1000f - 0.05f, z1 = zb + 0.25f;
            bool det = tornado == TornadoModel.Detailed;
            float xmin = float.MaxValue, ymin = float.MaxValue, xmax = float.MinValue, ymax = float.MinValue;
            bool any = false, full = false;
            void Cyl(float cx, float cy, float rad)
            {
                // the circle as a 16-gon drawn round it (1 / cos(pi / 16)), top and bottom, and a little margin
                rad = rad * 1.02f + 0.05f;
                for (int k = 0; k < 16 && !full; k++)
                {
                    float a = k * Mathf.PI / 8f;
                    for (int e = 0; e < 2; e++)
                    {
                        var d = new Vector3(cx + rad * Mathf.Cos(a), cy + rad * Mathf.Sin(a), e == 0 ? z0 : z1) - cp;
                        float z = Vector3.Dot(d, fwd);
                        if (z < 0.01f) { full = true; break; }
                        float u = (Vector3.Dot(d, right) / (z * tanX) * 0.5f + 0.5f) * w, v = (Vector3.Dot(d, up) / (z * tanY) * 0.5f + 0.5f) * h;
                        xmin = Mathf.Min(xmin, u); xmax = Mathf.Max(xmax, u); ymin = Mathf.Min(ymin, v); ymax = Mathf.Max(ymax, v);
                    }
                }
                any = true;
            }
            float AxisAmp(float zt) => Mathf.Min((0.03f + 0.1f * P(14, 3) + 0.3f * P(22, 3)) * (0.4f + 0.6f * zt), 0.22f * zt + 0.02f);
            float lean = new Vector2(P(13, 0) - P(15, 0), P(13, 1) - P(15, 1)).magnitude;
            if (P(13, 2) > 0f)
            {
                float rad = det ? Mathf.Max(P(14, 1) * 1.6f, P(14, 0) * Mathf.Max(P(23, 3), 1f) * 1.3f) + AxisAmp(zb) + P(27, 2) * 1.35f + 0.35f
                                : Mathf.Max(P(14, 1) * 1.3f + 0.25f * P(6, 2), P(14, 0) * 2.4f + 0.06f) * 1.2f + 0.4f + lean * 0.5f;
                Cyl(0.5f * (P(13, 0) + P(15, 0)), 0.5f * (P(13, 1) + P(15, 1)), rad);
            }
            // the anticyclonic tornado: the march's fine steps start 0.4 km from it (0.75 in the web build's) and it slows
            // the steps of rays passing nearer than a stride; a kilometre round it covers those
            if (P(19, 3) > 0f) Cyl(P(19, 0), P(19, 1), det ? 1.4f : 1.75f);
            if (det && P(28, 2) > 0f)
            {
                float sx = Mathf.Lerp(P(28, 0), P(15, 0), 0.15f), sy = Mathf.Lerp(P(28, 1), P(15, 1), 0.15f);
                Cyl(sx, sy, 0.3f * new Vector2(P(28, 0) - P(15, 0), P(28, 1) - P(15, 1)).magnitude + P(28, 3) * 3.5f + 0.2f + 1f);
            }
            if (!any) return new RectInt(0, 0, 0, 0);
            if (full) return new RectInt(0, 0, w, h);
            int ix0 = Mathf.Clamp(Mathf.FloorToInt(xmin) - 2, 0, w), ix1 = Mathf.Clamp(Mathf.CeilToInt(xmax) + 2, 0, w);
            int iy0 = Mathf.Clamp(Mathf.FloorToInt(ymin) - 2, 0, h), iy1 = Mathf.Clamp(Mathf.CeilToInt(ymax) + 2, 0, h);
            return new RectInt(ix0, iy0, Mathf.Max(0, ix1 - ix0), Mathf.Max(0, iy1 - iy0));
        }

        /// <summary>where this storm's cloud hides half of what is behind it for a camera, from its last march (km along
        /// each ray, -1 where none), or null: for another storm drawn alongside (its light shafts stop there)</summary>
        public RenderTexture HalfFor(Camera cam) => cams.TryGetValue(cam, out var st) && st.valid ? st.half : null;
        /// <summary>this storm's light shafts for a camera when its last march kept them apart (drawn alongside another storm), or null</summary>
        public RenderTexture ShaftFor(Camera cam) => cams.TryGetValue(cam, out var st) && st.shaftApart ? st.shaft : null;

        MaterialPropertyBlock compositeProps;
        /// <summary>Draw the storm over the bound colour target (out = storm.rgb x light scale + storm.a x target) and, with
        /// writeDepth, write its depth where it is opaque. under: the storm composited just before (and its march
        /// texture), which this one is laid behind wherever it is the nearer of the two along the ray.</summary>
        public void RecordComposite(CommandBuffer cmd, Camera cam, Texture storm, Texture dist, int w, int h, bool writeDepth,
            StormRenderer under = null, Texture underTex = null, bool shaftsOwn = true, bool shaftsUnder = false)
        {
            if (!compositeMat || storm == null) return;
            var mpb = compositeProps ??= new MaterialPropertyBlock(); mpb.Clear();
            mpb.SetTexture("_StormTex", storm);
            mpb.SetTexture("_StormDist", dist ? dist : Texture2D.blackTexture);
            mpb.SetVector("_StormView", new Vector4(w, h, 0, toneMap ? 0 : Luminance));
            cams.TryGetValue(cam, out var st);
            CamState ust = null;
            bool layered = under != null && underTex && st != null && under.cams.TryGetValue(cam, out ust);
            mpb.SetTexture("_StormMean", layered ? st.dist : Texture2D.blackTexture);
            mpb.SetTexture("_UnderTex", layered ? underTex : Texture2D.blackTexture);
            mpb.SetTexture("_UnderMean", layered ? ust.dist : Texture2D.blackTexture);
            mpb.SetVector("_UnderP", new Vector4(layered ? 1 : 0, layered ? (under.toneMap ? 0 : under.Luminance) : 0, 0, 0));
            // the light shafts kept apart in the marches (drawn alongside another storm), added over both storms
            var ownS = shaftsOwn ? ShaftFor(cam) : null; var underS = shaftsUnder && under != null ? under.ShaftFor(cam) : null;
            mpb.SetTexture("_ShaftTex", ownS ? ownS : Texture2D.blackTexture);
            mpb.SetTexture("_UnderShaftTex", underS ? underS : Texture2D.blackTexture);
            mpb.SetVector("_ShaftP", new Vector4(ownS ? 1 : 0, underS ? 1 : 0, underS && !under.toneMap ? under.Luminance : 0, 0));
            float tanY = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            mpb.SetVector("_StormTan", new Vector4(tanY * w / (float)h, tanY, 0, 0));
            cmd.BeginSample(CompositeSampler);
            cmd.DrawProcedural(Matrix4x4.identity, compositeMat, 0, MeshTopology.Triangles, 3, 1, mpb);
            if (writeDepth && dist) cmd.DrawProcedural(Matrix4x4.identity, compositeMat, 1, MeshTopology.Triangles, 3, 1, mpb);
            cmd.EndSample(CompositeSampler);
        }
    }
}

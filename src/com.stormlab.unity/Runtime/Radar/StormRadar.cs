// A weather radar on the storms (user, 2026-10-27): the reflectivity a WSR-88D at `site` sees in every storm on
// (StormRenderer.Enabled), as an image of the ground round it (Image, in a radar palette) and in dBZ (Dbz). The antenna
// turns once every sweepSeconds and each frame repaints the wedge it has just swept, so the picture updates as a real
// radar's does. Base reflectivity is the lowest tilt (0.5 deg) with its true beam height and width; composite is the
// most in each column. The echo is a supercell's laid out on each storm's own features (hook, forward-flank shield,
// inflow notch, hail core, debris ball, anvil; StormRadar.compute has the physics); its numbers are here.
using UnityEngine;

namespace StormLab
{
    public enum RadarProduct { BaseReflectivity, CompositeReflectivity }
    /// <summary>Smooth: shaded like RadarScope and GR2 (the user's references); NWS: the NWS steps of 5 dBZ.</summary>
    public enum RadarPalette { Smooth, NWS }

    [DefaultExecutionOrder(110)]   // after the storms (100) have painted this frame
    public class StormRadar : MonoBehaviour
    {
        [Tooltip("the radar's call sign")]
        public string siteName = "KDDC";
        [Tooltip("the antenna, world m")]
        public Vector3 site;
        public RadarProduct product = RadarProduct.BaseReflectivity;
        [Tooltip("the tilt base reflectivity shows, degrees (the WSR-88D's lowest is 0.5)")]
        [Range(0.5f, 19.5f)] public float elevationDeg = 0.5f;
        [Tooltip("half-power beam width, degrees (WSR-88D 0.95)")]
        public float beamWidthDeg = 0.95f;
        [Tooltip("range gate, m (super-resolution 250 m) and radial width, degrees (0.5)")]
        public float gateM = 250f, radialDeg = 0.5f;
        [Tooltip("reflectivity range, km (the WSR-88D's is 460)")]
        public float rangeKm = 460f;
        [Tooltip("seconds for one turn of the antenna (real time); each frame repaints the wedge it swept")]
        public float sweepSeconds = 12f;
        [Tooltip("the imaged square: centre (world x, z), half-width m, texels across")]
        public Vector2 areaCentre;
        public float areaHalfM = 60000f;
        public int resolution = 1024;
        [Tooltip("lowest dBZ drawn (the NWS scale starts at 5)")]
        public float showFromDbz = 5f;
        public RadarPalette palette = RadarPalette.Smooth;
        [Tooltip("gate-to-gate fluctuation, dB (each gate's estimate averages a few pulses; new every scan)")]
        public float gateNoiseDb = 2.5f;

        [Header("Physics (meteorology/ notes)")]
        [Tooltip("rain rate at the rear of a mature storm's forward-flank shield, mm/h")]
        public float coreRainRate = 100f;
        [Tooltip("mean-mass drop diameters, mm (Kumjian, Lebo and Morrison 2015): melted ice (the forward flank), its " +
                 "inflow edge (size-sorted: the ZDR arc), warm rain (the rear-flank downdraft and hook), and their control run's mean")]
        public Vector4 dropDiameters = new Vector4(1.52f, 1.9f, 1.05f, 1.42f);
        [Tooltip("hail core dBZ with no hail and the rise at full hail (Stull: hail 60-75 dBZ)")]
        public Vector2 hailDbz = new Vector2(45f, 25f);
        [Tooltip("tornado debris ball dBZ at EF0 and per EF step (a TDS shows Z > 20 dBZ)")]
        public Vector2 debrisDbz = new Vector2(40f, 4f);

        /// <summary>The NWS reflectivity colours, 5 dBZ steps from 5 dBZ (5-10 light cyan ... 75+ white).</summary>
        public static readonly Color32[] Colours =
        {
            new Color32(4, 233, 231, 255), new Color32(1, 159, 244, 255), new Color32(3, 0, 244, 255),
            new Color32(2, 253, 2, 255), new Color32(1, 197, 1, 255), new Color32(0, 142, 0, 255),
            new Color32(253, 248, 2, 255), new Color32(229, 188, 0, 255), new Color32(253, 149, 0, 255),
            new Color32(253, 0, 0, 255), new Color32(212, 0, 0, 255), new Color32(188, 0, 0, 255),
            new Color32(248, 0, 253, 255), new Color32(152, 84, 198, 255), new Color32(253, 253, 253, 255),
            new Color32(253, 253, 253, 255),
        };
        /// <summary>The smooth palette's anchors (dBZ, sRGB), shaded between: greys and lavender for light echo, greens,
        /// yellow from 35, orange, reds from 50, pinks from 60, purple and white at the top.</summary>
        public static readonly (float dbz, Color32 c)[] Smooth =
        {
            (5f, new Color32(92, 92, 128, 255)), (12f, new Color32(112, 118, 196, 255)), (19.9f, new Color32(128, 150, 240, 255)),
            (20f, new Color32(16, 140, 44, 255)), (27f, new Color32(36, 196, 40, 255)), (34.9f, new Color32(140, 230, 60, 255)),
            (35f, new Color32(250, 244, 40, 255)), (40f, new Color32(246, 196, 22, 255)), (45f, new Color32(250, 132, 12, 255)),
            (50f, new Color32(236, 28, 24, 255)), (55f, new Color32(186, 10, 16, 255)), (59.9f, new Color32(140, 0, 12, 255)),
            (60f, new Color32(255, 128, 214, 255)), (65f, new Color32(232, 40, 232, 255)), (70f, new Color32(150, 60, 226, 255)),
            (75f, new Color32(232, 232, 255, 255)),
        };
        static (float dbz, Color32 c)[] Anchors(RadarPalette p)
        {
            if (p == RadarPalette.Smooth) return Smooth;
            var a = new (float, Color32)[24];
            for (int i = 0; i < 12; i++) { a[2 * i] = (5f + 5f * i, Colours[i]); a[2 * i + 1] = (9.99f + 5f * i, Colours[i]); }
            a[22] = (65f, Colours[12]); a[23] = (70f, Colours[13]);
            return a;
        }
        /// <summary>A dBZ's colour in a palette (sRGB; clear below 5 dBZ).</summary>
        public static Color ColourFor(float dbz, RadarPalette p = RadarPalette.NWS)
        {
            if (dbz < 5f) return Color.clear;
            var a = Anchors(p);
            if (dbz <= a[0].dbz) return (Color)a[0].c;
            for (int i = 1; i < a.Length; i++)
                if (dbz < a[i].dbz) return Color.Lerp(a[i - 1].c, a[i].c, (dbz - a[i - 1].dbz) / Mathf.Max(a[i].dbz - a[i - 1].dbz, 1e-3f));
            return (Color)a[a.Length - 1].c;
        }

        /// <summary>the scan in the NWS colours (linear; alpha 0 where nothing is shown), north up, covering Area</summary>
        public RenderTexture Image => img;
        /// <summary>the scan in dBZ (-99 where nothing), RHalf</summary>
        public RenderTexture Dbz => dbz;
        /// <summary>the imaged square, world x (min, max) and z (min, max)</summary>
        public Rect Area => new Rect(areaCentre.x - areaHalfM, areaCentre.y - areaHalfM, 2f * areaHalfM, 2f * areaHalfM);
        /// <summary>the scan's reflectivity factor before it is turned into dBZ (mm^6 m^-3, RFloat; for checks)</summary>
        public RenderTexture ReflectivityFactor => z;
        /// <summary>how many storms the last frame's sweep read</summary>
        public int StormsScanned { get; private set; }
        /// <summary>where the antenna points now, degrees clockwise from north</summary>
        public float SweepAzimuth => azimuth * Mathf.Rad2Deg;

        RenderTexture z, dbz, img;
        ComputeShader cs; int kAcc = -1, kFin = -1;
        float azimuth; bool full = true; int scan;
        RadarProduct lastProduct; RadarPalette lastPalette; float lastElev; double lastTimes = double.NaN;
        readonly Vector4[] pal = new Vector4[24];
        readonly Vector4[] sp = new Vector4[StormSpin.NSLOT];

        static readonly int ZId = Shader.PropertyToID("_Z"), DbzId = Shader.PropertyToID("_Dbz"), ImgId = Shader.PropertyToID("_Img");
        static readonly int AreaId = Shader.PropertyToID("_RdArea"), SiteId = Shader.PropertyToID("_RdSite"), StormId = Shader.PropertyToID("_RdStorm");
        static readonly int BeamId = Shader.PropertyToID("_RdBeam"), SweepId = Shader.PropertyToID("_RdSweep"), ZRId = Shader.PropertyToID("_RdZR");
        static readonly int DmId = Shader.PropertyToID("_RdDm"), HailId = Shader.PropertyToID("_RdHail"), VolId = Shader.PropertyToID("_RdVol");
        static readonly int PalId = Shader.PropertyToID("_RdPal"), ShowId = Shader.PropertyToID("_RdShow"), ClearId = Shader.PropertyToID("_RdClear"), SPId = Shader.PropertyToID("_SP");

        /// <summary>Repaint the whole picture on the next frame (after a jump in storm time or a new storm).</summary>
        public void ScanAll() => full = true;

        void OnDestroy() { foreach (var t in new[] { z, dbz, img }) if (t) { t.Release(); Destroy(t); } }

        static RenderTexture Target(RenderTexture rt, int n, RenderTextureFormat f, string name)
        {
            if (rt && rt.width == n) return rt;
            if (rt) { rt.Release(); Destroy(rt); }
            rt = new RenderTexture(n, n, 0, f, RenderTextureReadWrite.Linear)
            { enableRandomWrite = true, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point, name = name, hideFlags = HideFlags.DontSave };
            rt.Create();
            return rt;
        }

        void LateUpdate()
        {
            if (!cs) { cs = Resources.Load<ComputeShader>("StormRadar"); if (cs) { kAcc = cs.FindKernel("Accumulate"); kFin = cs.FindKernel("Finish"); } }
            if (!cs || !SystemInfo.supportsComputeShaders) return;
            int n = Mathf.Clamp(resolution, 64, 4096);
            if (!img || img.width != n) full = true;
            z = Target(z, n, RenderTextureFormat.RFloat, "radar Z");
            dbz = Target(dbz, n, RenderTextureFormat.RHalf, "radar dBZ");
            img = Target(img, n, RenderTextureFormat.ARGB32, "radar image");
            if (product != lastProduct || elevationDeg != lastElev || palette != lastPalette) { full = true; lastProduct = product; lastElev = elevationDeg; lastPalette = palette; }
            // a jump in storm time or a storm come or gone: the whole picture at once rather than a sweep of old weather
            double times = 0; int count = 0;
            foreach (var s in StormRenderer.Enabled) if (s && s.isActiveAndEnabled && s.ReadyToRender) { times += s.StormTime; count++; }
            times += 1000.0 * count;
            if (double.IsNaN(lastTimes) || System.Math.Abs(times - lastTimes) > 1.0) full = true;
            lastTimes = times;

            // this frame's wedge of the antenna's turn
            float a0 = azimuth, span = 2f * Mathf.PI * Time.unscaledDeltaTime / Mathf.Max(sweepSeconds, 0.5f);
            if (azimuth + span >= 2f * Mathf.PI) scan++;
            azimuth = Mathf.Repeat(azimuth + span, 2f * Mathf.PI);
            if (full) { span = 7f; full = false; scan++; }

            float mpt = 2f * areaHalfM / n;
            cs.SetVector(AreaId, new Vector4(areaCentre.x - areaHalfM, areaCentre.y - areaHalfM, mpt, n));
            cs.SetVector(SiteId, new Vector4(site.x, site.z, site.y, elevationDeg * Mathf.Deg2Rad));
            cs.SetVector(BeamId, new Vector4(gateM, radialDeg * Mathf.Deg2Rad, beamWidthDeg * Mathf.Deg2Rad, rangeKm * 1000f));
            cs.SetVector(SweepId, new Vector4(a0, span, product == RadarProduct.CompositeReflectivity ? 1 : 0, scan % 1000));
            cs.SetVector(ZRId, new Vector4(coreRainRate, gateNoiseDb, 0, dropDiameters.w));
            cs.SetVector(HailId, new Vector4(hailDbz.x, hailDbz.y, debrisDbz.x, debrisDbz.y));
            var anchors = Anchors(palette);
            int na = Mathf.Min(anchors.Length, pal.Length);
            for (int i = 0; i < na; i++) { var c = ((Color)anchors[i].c).linear; pal[i] = new Vector4(c.r, c.g, c.b, anchors[i].dbz); }   // the image is linear
            cs.SetVectorArray(PalId, pal);
            // the smooth palette fades its lightest echo in, as RadarScope's does
            cs.SetVector(ShowId, palette == RadarPalette.Smooth ? new Vector4(showFromDbz, 0.45f, 20f, na) : new Vector4(showFromDbz, 1f, showFromDbz + 0.01f, na));
            cs.SetTexture(kAcc, ZId, z);
            int g = (n + 7) / 8;

            bool first = true;
            foreach (var s in StormRenderer.Enabled)
            {
                if (!s || !s.isActiveAndEnabled || !s.ReadyToRender || s.Plan == null) continue;
                var prm = s.Frame.param;
                for (int i = 0; i < sp.Length; i++) sp[i] = new Vector4(prm[i * 4], prm[i * 4 + 1], prm[i * 4 + 2], prm[i * 4 + 3]);
                cs.SetVectorArray(SPId, sp);
                var o = s.WorldToStorm(site);
                cs.SetVector(StormId, new Vector4(o.x, o.y, o.z, first ? 1 : 0));
                cs.SetVector(DmId, new Vector4(dropDiameters.x, dropDiameters.y, dropDiameters.z, (float)s.Plan.fzlKm));
                cs.SetVector(VolId, new Vector4(1, 0, 0, 0));
                var cl = s.ClearParams;
                cs.SetVector(ClearId, new Vector4(cl.x, cl.y, s.precip == PrecipLayout.Tilted ? (float)StormTilt.Slope(s.Plan) : 0f, 0));
                cs.Dispatch(kAcc, g, g, 1);
                first = false;
            }
            StormsScanned = 0;
            foreach (var s in StormRenderer.Enabled) if (s && s.isActiveAndEnabled && s.ReadyToRender && s.Plan != null) StormsScanned++;
            if (first)
            {
                // no storm: the wedge clears
                cs.SetVector(StormId, new Vector4(0, 0, 0, 1));
                cs.SetVector(VolId, Vector4.zero);
                cs.Dispatch(kAcc, g, g, 1);
            }
            cs.SetTexture(kFin, ZId, z);
            cs.SetTexture(kFin, DbzId, dbz);
            cs.SetTexture(kFin, ImgId, img);
            cs.Dispatch(kFin, g, g, 1);
        }
    }
}

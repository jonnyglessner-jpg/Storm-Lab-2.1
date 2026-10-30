// The test map's electric lights (user, 2026-10-26): a yard light over the main door of every barn (TestMapBuilder puts
// a spot light and a glowing bulb there) and the truck's lights. The yard lights are dusk-to-dawn: on while the
// sun is under sunElevationOn degrees, and only those within activeRadius of the camera exist as live objects (2,158 of
// them, even switched off, cost the CPU about 0.8 ms a frame while lightning flashed).
// The truck's lights (user, 2026-10-30: on at any time, covering more ground): L cycles off, low beams, high beams with
// the roof light bar. They start on low beams in the dark and off by day, and only L changes them after that. Each beam
// is a level spot light whose shape is a cookie made at Start: a sharp top edge, and below it an intensity that falls
// as the ground comes nearer (tan^-2.2 of the angle down), so the road is lit far ahead without a white pool at the
// bumper. Low beams: wide, lit well to about 60 m; high beams: a narrow hot spot to about 150 m; the bar: a wide flood
// off the cab roof to about 80 m. They look the same however dark it is: they follow the panel's exposure, at their set
// brightness on a full-moon night (user, 2026-10-26: they grew brighter as the exposure opened through dusk into night),
// but no further than headlightMaxEV, so by day they are lamps, not floodlights in the sun. The high beams and the bar
// are made at Start beside the scene's headlights, so a scene built before them has them too. Their bulbs and the
// truck's lamps glow from runtime copies of their materials, so Play never edits the material assets.
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace StormLab.TestMap
{
    public class TestMapNightLights : MonoBehaviour
    {
        public enum Headlights { Off, Low, High }

        public StormRenderer storm;
        public TestMapCar car;
        [Tooltip("the yard lights' parent (TestMapBuilder: Yard lights)")]
        public Transform yardLights;
        [Tooltip("the farmsteads' parent, whose meshes carry the bulb material")]
        public Transform farmsteads;
        [Tooltip("the yard lights' bulb material (an asset: a copy is lit at runtime)")]
        public Material bulb;
        [Tooltip("the truck's headlights (spot lights: the low beams), lamp lenses and tail lights")]
        public Light[] headlights = new Light[0];
        public Renderer[] lamps = new Renderer[0], tails = new Renderer[0];
        [Tooltip("the lights come on when the sun is lower than this, degrees")]
        public float sunElevationOn = 2f;
        [Tooltip("yard lights further from the camera than this are left inactive, m (they are culled at 1.5 km anyway)")]
        public float activeRadius = 1600f;
        [Tooltip("luminance of a lit bulb and a lit lamp lens, nits; a tail light's")]
        public float bulbNits = 40000f, lampNits = 60000f, tailNits = 8f;
        [Tooltip("L cycles off, low beams, high beams (with the light bar)")]
        public Headlights headlightMode = Headlights.Off;
        [Tooltip("the exposure (EV100) at which the truck's lights, lenses and tail lights show at their set brightness; " +
                 "under any other exposure they are scaled to look the same (a full-moon night is about 3)")]
        public float headlightReferenceEV = 3f;
        [Tooltip("above this exposure (EV100) the truck's lights are no longer scaled up: by day they are weaker than at night")]
        public float headlightMaxEV = 11f;

        [Header("Beams (candela at the peak of each lamp, at the reference exposure; reach: where the road is lit to)")]
        public float lowCandela = 900000f;
        [Tooltip("low beams: m ahead where the beam's full intensity meets the road")]
        public float lowReach = 60f;
        public float highCandela = 10000000f, highReach = 150f;
        public float barCandela = 750000f, barReach = 80f;
        [Tooltip("the light bar's luminance, nits")]
        public float barNits = 60000f;

        Light[] yard = new Light[0];
        Light[] high = new Light[0];
        Light bar;
        TestMapStormPanel panel;
        Vector3[] yardAt = new Vector3[0];
        Material bulbLive, lampLive, tailLive, barLive;
        Texture2D lowCookie, highCookie, barCookie;
        GameObject barMesh;
        bool? yardOn; Headlights? headShown;
        float lastNear = -1e9f;
        static readonly int EmissiveId = Shader.PropertyToID("_EmissiveColor");

        public bool Dark { get; private set; }
        public bool HeadlightsOn => headlightMode != Headlights.Off;
        /// <summary>the truck's lights' scale for the exposure now (1 at headlightReferenceEV)</summary>
        public float Boost { get; private set; } = 1f;

        void Start()
        {
            if (!storm) storm = FindAnyObjectByType<StormRenderer>();
            panel = GetComponent<TestMapStormPanel>();
            if (!panel) panel = FindAnyObjectByType<TestMapStormPanel>();
            if (yardLights)
            {
                yard = yardLights.GetComponentsInChildren<Light>(true);
                yardAt = new Vector3[yard.Length];
                for (int i = 0; i < yard.Length; i++) { yardAt[i] = yard[i].transform.position; yard[i].gameObject.SetActive(false); }
            }
            var lampAsset = lamps.Length > 0 && lamps[0] ? lamps[0].sharedMaterial : null;
            if (bulb && farmsteads) bulbLive = Swap(farmsteads.GetComponentsInChildren<Renderer>(true), bulb, "yard bulb (lit)");
            if (lampAsset) lampLive = Swap(lamps, lampAsset, "car lamp (lit)");
            if (tails.Length > 0 && tails[0]) tailLive = Swap(tails, tails[0].sharedMaterial, "car tail (lit)");
            BuildBeams(lampAsset);
            // on low beams if the drive starts in the dark
            headlightMode = SunElevation() < sunElevationOn ? Headlights.Low : Headlights.Off;
        }

        // ------------------------------------------------------------------ the truck's beams

        /// <summary>The low beams (the scene's headlights) reshaped, the high beams beside them and the roof light bar.</summary>
        void BuildBeams(Material lampAsset)
        {
            lowCookie = Cookie("low beam cookie", 512, 120f, (a, b) =>
                Beam(b, Elev(1f, lowReach), 2.2f, 0.6f, 0.03f) * Side(a, 0.3f, 24f, 58f));
            highCookie = Cookie("high beam cookie", 512, 60f, (a, b) =>
                Beam(b, Elev(1f, highReach), 2.2f, 2.5f, 0.06f) * (0.75f * Mathf.Exp(-(a / 8f) * (a / 8f)) + 0.25f * Mathf.Exp(-(a / 20f) * (a / 20f))));
            barCookie = Cookie("light bar cookie", 512, 150f, (a, b) =>
                Beam(b, Elev(2.05f, barReach), 2.2f, 3f, 0.04f) * Side(a, 0.4f, 40f, 72f));
            Transform body = null;
            foreach (var l in headlights) if (l) { Shape(l, lowCookie, 120f, 450f); body = l.transform.parent; }
            high = new Light[headlights.Length];
            for (int i = 0; i < headlights.Length; i++)
            {
                if (!headlights[i]) continue;
                high[i] = NewSpot($"High beam {(i == 0 ? "L" : "R")}", headlights[i].transform.parent, headlights[i].transform.localPosition + new Vector3(0f, 0.02f, 0f));
                Shape(high[i], highCookie, 60f, 900f);
            }
            if (!body && car) body = car.transform.Find("Body");
            if (!body) return;
            // the bar: across the front of the cab roof (TestMapBuilder's cab: top 1.98 m, front edge 1.6 m)
            bar = NewSpot("Light bar", body, new Vector3(0f, 2.06f, 1.25f));
            Shape(bar, barCookie, 150f, 500f);
            barMesh = GameObject.CreatePrimitive(PrimitiveType.Cube);
            barMesh.name = "Light bar lamp";
            Destroy(barMesh.GetComponent<Collider>());
            barMesh.transform.SetParent(body, false);
            barMesh.transform.localPosition = new Vector3(0f, 2.03f, 1.2f);
            barMesh.transform.localScale = new Vector3(1.5f, 0.09f, 0.12f);
            var mr = barMesh.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (lampAsset)
            {
                barLive = new Material(lampAsset) { name = "light bar (lit)", hideFlags = HideFlags.DontSave };
                barLive.EnableKeyword("_EMISSIVE_COLOR");
                mr.sharedMaterial = barLive;
            }
        }

        static Light NewSpot(string name, Transform parent, Vector3 at)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = at;
            var l = go.AddComponent<Light>(); l.type = LightType.Spot;
            go.AddComponent<HDAdditionalLightData>();
            l.color = new Color(1f, 0.97f, 0.9f); l.shadows = LightShadows.None;
            l.enabled = false;
            return l;
        }

        /// <summary>A beam: aimed level along the truck, its shape all in the cookie, in candela (set each frame).</summary>
        static void Shape(Light l, Texture cookie, float angle, float range)
        {
            l.transform.localRotation = Quaternion.identity;
            l.lightUnit = LightUnit.Candela;
            l.spotAngle = angle; l.innerSpotAngle = angle * 0.98f; l.range = range;
            l.enableSpotReflector = false;
            l.cookie = cookie;
            var hd = l.GetComponent<HDAdditionalLightData>();
            if (hd) { hd.volumetricDimmer = 0f; hd.fadeDistance = 1500f; }
        }

        /// <summary>degrees below level at which a lamp at height h meets the road d m ahead</summary>
        static float Elev(float h, float d) => Mathf.Atan2(h, d) * Mathf.Rad2Deg;

        /// <summary>
        /// A beam's intensity by elevation b (degrees, up positive) for full intensity reaching the road b0 degrees down:
        /// below that it falls as (tan b0 / tan b)^p, so the road nearer the truck takes less (about even light out to the
        /// reach); above, a soft top over `top` degrees; `stray` is the glare left above it.
        /// </summary>
        static float Beam(float b, float b0, float p, float top, float stray)
        {
            if (b < -b0) return Mathf.Pow(Mathf.Tan(b0 * Mathf.Deg2Rad) / Mathf.Tan(-b * Mathf.Deg2Rad), p);
            float t = Mathf.Clamp01((b + b0) / Mathf.Max(top, 0.01f));
            return Mathf.Lerp(1f, stray, t * t * (3f - 2f * t)) * (b > top ? Mathf.Exp(-(b - top) / 8f) : 1f);
        }

        /// <summary>a beam's spread across: `floor` of the peak at the sides, a broad centre `width` degrees wide, nothing past `edge`</summary>
        static float Side(float a, float floor, float width, float edge)
        {
            float x = Mathf.Abs(a);
            return (floor + (1f - floor) * Mathf.Exp(-(x / width) * (x / width))) * Mathf.Clamp01((edge - x) / 6f);
        }

        /// <summary>A spot light cookie of n x n texels for a cone of `angle` degrees: pattern(across, up) in degrees.</summary>
        static Texture2D Cookie(string name, int n, float angle, System.Func<float, float, float> pattern)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGBAHalf, false, true)
            {
                name = name, hideFlags = HideFlags.DontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
            };
            float T = Mathf.Tan(angle * 0.5f * Mathf.Deg2Rad);
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float tx = ((x + 0.5f) / n * 2f - 1f) * T, ty = ((y + 0.5f) / n * 2f - 1f) * T;
                    float a = Mathf.Atan(tx) * Mathf.Rad2Deg, b = Mathf.Atan2(ty, Mathf.Sqrt(1f + tx * tx)) * Mathf.Rad2Deg;
                    float v = Mathf.Clamp01(pattern(a, b));
                    px[y * n + x] = new Color(v, v, v, 1f);
                }
            tex.SetPixels(px); tex.Apply(false, false);
            return tex;
        }

        // ------------------------------------------------------------------ per frame

        /// <summary>A runtime copy of `asset`, put in its place on these renderers.</summary>
        static Material Swap(Renderer[] rs, Material asset, string name)
        {
            if (!asset) return null;
            var live = new Material(asset) { name = name, hideFlags = HideFlags.DontSave };
            live.EnableKeyword("_EMISSIVE_COLOR");
            foreach (var r in rs)
            {
                if (!r) continue;
                var ms = r.sharedMaterials; bool any = false;
                for (int i = 0; i < ms.Length; i++) if (ms[i] == asset) { ms[i] = live; any = true; }
                if (any) r.sharedMaterials = ms;
            }
            return live;
        }

        float SunElevation() => storm ? Mathf.Asin(Mathf.Clamp(storm.SunDirVol.z, -1f, 1f)) * Mathf.Rad2Deg : 30f;

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.lKey.wasPressedThisFrame) headlightMode = (Headlights)(((int)headlightMode + 1) % 3);
            Dark = SunElevation() < sunElevationOn;
            if (yardOn != Dark)
            {
                yardOn = Dark;
                Glow(bulbLive, Dark, new Color(1f, 0.86f, 0.68f) * bulbNits);
                lastNear = -1e9f;
            }
            // the yard lights near the camera, twice a second (and at once when night falls or ends)
            if (Time.unscaledTime - lastNear > 0.5f)
            {
                lastNear = Time.unscaledTime;
                var cam = Camera.main;
                var c = cam ? cam.transform.position : Vector3.zero;
                float r2 = activeRadius * activeRadius;
                for (int i = 0; i < yard.Length; i++)
                {
                    bool on = Dark && (yardAt[i] - c).sqrMagnitude < r2;
                    if (yard[i] && yard[i].gameObject.activeSelf != on) yard[i].gameObject.SetActive(on);
                }
            }
            // (TestMapWorld adds the panel at its Start when the scene has none, which can be after this one's Start: before
            // 2026-10-30 that left the lights unscaled, about 80 times too dim at dusk)
            if (!panel) { panel = GetComponent<TestMapStormPanel>(); if (!panel) panel = FindAnyObjectByType<TestMapStormPanel>(); }
            bool low = headlightMode != Headlights.Off, hi = headlightMode == Headlights.High;
            if (headShown != headlightMode)
            {
                headShown = headlightMode;
                foreach (var l in headlights) if (l) l.enabled = low;
                foreach (var l in high) if (l) l.enabled = hi;
                if (bar) bar.enabled = hi;
            }
            if (low)
            {
                // the same on screen under any exposure up to headlightMaxEV: 2^(EV - reference) more light where the
                // exposure is closed down
                float k = Boost = panel ? Mathf.Pow(2f, Mathf.Min(panel.ExposureEV, headlightMaxEV) - headlightReferenceEV) : 1f;
                foreach (var l in headlights) if (l) l.intensity = lowCandela * k;
                foreach (var l in high) if (l) l.intensity = highCandela * k;
                if (bar) bar.intensity = barCandela * k;
                Glow(lampLive, true, new Color(1f, 0.97f, 0.9f) * (lampNits * (hi ? 1.6f : 1f) * k));
                Glow(tailLive, true, new Color(1f, 0.05f, 0.03f) * (tailNits * k));
                Glow(barLive, hi, new Color(1f, 0.98f, 0.94f) * (barNits * k));
            }
            else
            {
                Glow(lampLive, false, Color.black);
                Glow(tailLive, false, Color.black);
                Glow(barLive, false, Color.black);
            }
        }

        static void Glow(Material m, bool on, Color c) { if (m) m.SetColor(EmissiveId, on ? c : Color.black); }

        void OnDestroy()
        {
            foreach (var m in new[] { bulbLive, lampLive, tailLive, barLive }) if (m) Destroy(m);
            foreach (var t in new[] { lowCookie, highCookie, barCookie }) if (t) Destroy(t);
        }
    }
}

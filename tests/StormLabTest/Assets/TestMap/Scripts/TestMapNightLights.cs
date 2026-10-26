// The test map's electric lights (user, 2026-10-26): a yard light over the main door of every barn (TestMapBuilder puts
// a spot light and a glowing bulb there) and the truck's headlights. The yard lights are dusk-to-dawn: on while the
// sun is under sunElevationOn degrees, and only those within activeRadius of the camera exist as live objects (2,158 of
// them, even switched off, cost the CPU about 0.8 ms a frame while lightning flashed). L cycles the headlights: auto (on with the yard lights), on, off. Their bulbs and
// the truck's lamps glow from runtime copies of their materials, so Play never edits the material assets.
using UnityEngine;
using UnityEngine.InputSystem;

namespace StormLab.TestMap
{
    public class TestMapNightLights : MonoBehaviour
    {
        public enum Headlights { Auto, On, Off }

        public StormRenderer storm;
        public TestMapCar car;
        [Tooltip("the yard lights' parent (TestMapBuilder: Yard lights)")]
        public Transform yardLights;
        [Tooltip("the farmsteads' parent, whose meshes carry the bulb material")]
        public Transform farmsteads;
        [Tooltip("the yard lights' bulb material (an asset: a copy is lit at runtime)")]
        public Material bulb;
        [Tooltip("the truck's headlights (spot lights), lamp lenses and tail lights")]
        public Light[] headlights = new Light[0];
        public Renderer[] lamps = new Renderer[0], tails = new Renderer[0];
        [Tooltip("the lights come on when the sun is lower than this, degrees")]
        public float sunElevationOn = 2f;
        [Tooltip("yard lights further from the camera than this are left inactive, m (they are culled at 1.5 km anyway)")]
        public float activeRadius = 1600f;
        [Tooltip("luminance of a lit bulb and a lit lamp lens, nits; a tail light's")]
        public float bulbNits = 40000f, lampNits = 60000f, tailNits = 8f;
        public Headlights headlightMode = Headlights.Auto;

        Light[] yard = new Light[0];
        Vector3[] yardAt = new Vector3[0];
        Material bulbLive, lampLive, tailLive;
        bool? yardOn, headOn;
        float lastNear = -1e9f;
        static readonly int EmissiveId = Shader.PropertyToID("_EmissiveColor");

        public bool Dark { get; private set; }
        public bool HeadlightsOn => headOn == true;

        void Start()
        {
            if (!storm) storm = FindAnyObjectByType<StormRenderer>();
            if (yardLights)
            {
                yard = yardLights.GetComponentsInChildren<Light>(true);
                yardAt = new Vector3[yard.Length];
                for (int i = 0; i < yard.Length; i++) { yardAt[i] = yard[i].transform.position; yard[i].gameObject.SetActive(false); }
            }
            if (bulb && farmsteads) bulbLive = Swap(farmsteads.GetComponentsInChildren<Renderer>(true), bulb, "yard bulb (lit)");
            if (lamps.Length > 0 && lamps[0]) lampLive = Swap(lamps, lamps[0].sharedMaterial, "car lamp (lit)");
            if (tails.Length > 0 && tails[0]) tailLive = Swap(tails, tails[0].sharedMaterial, "car tail (lit)");
        }

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

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.lKey.wasPressedThisFrame) headlightMode = (Headlights)(((int)headlightMode + 1) % 3);
            float el = storm ? Mathf.Asin(Mathf.Clamp(storm.SunDirVol.z, -1f, 1f)) * Mathf.Rad2Deg : 30f;
            Dark = el < sunElevationOn;
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
            bool head = headlightMode == Headlights.On || headlightMode == Headlights.Auto && Dark;
            if (headOn != head)
            {
                headOn = head;
                foreach (var l in headlights) if (l) l.enabled = head;
                Glow(lampLive, head, new Color(1f, 0.97f, 0.9f) * lampNits);
                Glow(tailLive, head, new Color(1f, 0.05f, 0.03f) * tailNits);
            }
        }

        static void Glow(Material m, bool on, Color c) { if (m) m.SetColor(EmissiveId, on ? c : Color.black); }

        void OnDestroy()
        {
            foreach (var m in new[] { bulbLive, lampLive, tailLive }) if (m) Destroy(m);
        }
    }
}

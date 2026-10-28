// The storms' darkening of the scene's ambient light (user, 2026-10-28: "storms darken the skies, not brighten them").
// StormSceneShadow takes the storms' shadow off the sun; HDRP's ambient (sky) light still came from the clear sky, so the
// ground, roads and vehicles under a storm stayed lit as in open air. This reads how much of the sky the camera sees from
// each storm's sky-visibility grid (the SkyVis bands, as the march's sky light weighs them) and scales HDRP's indirect
// diffuse and reflection light, and the fog's sky colour, by it: what the storm hides is replaced by its underside, a tenth
// as bright as the sky it hides. One shared global volume over the scene's own, made when the first storm needs it.
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace StormLab
{
    [DefaultExecutionOrder(1000)]
    public class StormSceneAmbient : MonoBehaviour
    {
        [Tooltip("the storm's underside, as a share of the sky it hides from the ground")]
        [Range(0f, 0.5f)] public float underside = 0.1f;
        [Tooltip("how much the fog's sky colour follows (0: not at all, 1: as the ambient light)")]
        [Range(0f, 1f)] public float fogFollows = 0.6f;
        [Tooltip("seconds to settle after the sky changes")]
        public float settleSeconds = 1.5f;
        public float refreshSeconds = 0.25f;

        /// <summary>the scale on the scene's ambient light now (1: open sky)</summary>
        public float Ambient { get; private set; } = 1f;

        Volume volume; VolumeProfile profile; IndirectLightingController ilc; Fog fog;
        float target = 1f, last = -1e9f;
        int pending; float acc = 1f;

        static StormSceneAmbient instance;
        /// <summary>the shared one, if a storm has made it (it is DontSave, so FindAnyObjectByType does not see it)</summary>
        public static StormSceneAmbient Current => instance;

        /// <summary>The shared one, made if there is none.</summary>
        public static StormSceneAmbient Ensure()
        {
            if (instance) return instance;
            var go = new GameObject("Storm scene ambient") { hideFlags = HideFlags.DontSave };
            return instance = go.AddComponent<StormSceneAmbient>();
        }

        void OnEnable()
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>(); profile.hideFlags = HideFlags.DontSave;
            ilc = profile.Add<IndirectLightingController>();
            fog = profile.Add<Fog>();
            volume = gameObject.AddComponent<Volume>();
            volume.isGlobal = true; volume.priority = 100f; volume.sharedProfile = profile;
            Apply(1f);
        }

        void OnDisable()
        {
            if (volume) Destroy(volume);
            if (profile) Destroy(profile);
            Ambient = 1f;
        }

        void Update()
        {
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime / Mathf.Max(settleSeconds, 0.01f));
            Ambient += (target - Ambient) * k;
            Apply(Ambient);
            if (Time.unscaledTime - last < refreshSeconds || pending > 0) return;
            last = Time.unscaledTime;
            var cam = Camera.main;
            if (!cam || !SystemInfo.supportsAsyncGPUReadback) return;
            acc = 1f;
            foreach (var s in StormRenderer.Enabled)
            {
                if (!s || !s.isActiveAndEnabled || !s.ReadyToRender || !s.PhysicalSky) continue;
                var vol = s.Volume;
                var p = s.WorldToStorm(cam.transform.position);   // km: x east, y north, z up
                float L = StormVolume.L;
                if (Mathf.Abs(p.x) > L || Mathf.Abs(p.y) > L) continue;
                int ix = Mathf.Clamp(Mathf.FloorToInt((p.x + L) / (2f * L) * vol.SN), 0, vol.SN - 1);
                int iy = Mathf.Clamp(Mathf.FloorToInt((p.y + L) / (2f * L) * vol.SN), 0, vol.SN - 1);
                int iz = Mathf.Clamp(Mathf.FloorToInt(Mathf.Max(p.z, 0.05f) / StormVolume.ZT * vol.SZ), 0, vol.SZ - 1);
                pending++;
                AsyncGPUReadback.Request(vol.SkyVisTex, 0, ix, 1, iy, 1, iz, 1, TextureFormat.RGBAHalf, req =>
                {
                    pending--;
                    if (this == null || req.hasError) return;
                    var d = req.GetData<ushort>();
                    if (d.Length < 4) return;
                    // the sky's share the camera sees: the cap, the ring and the horizon by their solid angles (skyLight)
                    float sv = Mathf.Clamp01(0.18f * Mathf.HalfToFloat(d[0]) + 0.477f * Mathf.HalfToFloat(d[1]) + 0.342f * Mathf.HalfToFloat(d[2]));
                    acc *= sv + (1f - sv) * underside;
                    if (pending == 0) target = acc;
                });
            }
            if (pending == 0) target = 1f;
        }

        void Apply(float a)
        {
            if (!ilc || !fog) return;
            ilc.indirectDiffuseLightingMultiplier.Override(a);
            ilc.reflectionLightingMultiplier.Override(a);
            float f = Mathf.Lerp(1f, a, fogFollows);
            fog.tint.Override(new Color(f, f, f, 1f));
        }
    }
}

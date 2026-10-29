// The storms' darkening of the scene's ambient light (user, 2026-10-28: "storms darken the skies, not brighten them").
// StormSceneShadow takes the storms' shadow off the sun; HDRP's ambient (sky) light still came from the clear sky, so the
// ground, roads and vehicles under a storm stayed lit as in open air.
// Local (the default): a map round the camera of how much of the sky the ground sees, from each storm's sky-visibility
// grid (StormShadow.compute: Ambient), set as the scene's ambient occlusion by a decal projected straight down (HDRP's AO
// darkens a surface's sky light and reflections, not the sun). The ground under a storm is dark wherever the camera is,
// and sunny ground away from it stays lit while the camera is under the base. Needs decals with their AO channel (the HDRP
// asset's Decals: Metal and Ambient Occlusion properties).
// Without that, or with local off: one scale on HDRP's indirect diffuse light from what the camera sees of the sky.
// Either way, the reflections and the fog's sky colour follow what the camera sees: what the storm hides is replaced by
// its underside, a tenth as bright as the sky it hides. One shared global volume over the scene's own, made when the first
// storm needs it.
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
        [Tooltip("darken the ambient light where the storm hides the sky from the ground (a decal), not by what the camera sees")]
        public bool local = true;
        [Tooltip("local: how wide the map round the camera is, km (outside it the scene keeps its open-sky ambient)")]
        public float sizeKm = 80f;
        [Tooltip("local: texels across the map (the storms' sky grid is about 2.6 km a cell)")]
        public int resolution = 256;

        /// <summary>the scale on the scene's ambient light the camera sees now (1: open sky)</summary>
        public float Ambient { get; private set; } = 1f;
        /// <summary>true while the ambient is set where the ground is (the decal), not for the whole scene</summary>
        public bool LocalActive { get; private set; }
        /// <summary>the local map (g: the ambient occlusion), for checks</summary>
        public RenderTexture LocalMap => map;
        /// <summary>checks only: the camera-wide scale (0..1) and the local map's ambient occlusion (0..1) forced to a
        /// value; negative leaves them to the storms</summary>
        [System.NonSerialized] public float forceAmbient = -1f, forceLocal = -1f;

        Volume volume; VolumeProfile profile; IndirectLightingController ilc; Fog fog;
        float target = 1f, last = -1e9f;
        int pending; float acc = 1f;
        // local
        GameObject decalGo; DecalProjector decal; Material decalMat; RenderTexture map;
        ComputeShader cs; int kernel = -1; bool warned;

        static StormSceneAmbient instance;
        /// <summary>the shared one, if a storm has made it (it is DontSave, so FindAnyObjectByType does not see it)</summary>
        public static StormSceneAmbient Current => instance;

        static readonly int SkyVisId = Shader.PropertyToID("_SkyVis"), OutId = Shader.PropertyToID("_AmbOut");
        static readonly int OId = Shader.PropertyToID("_AmO"), PId = Shader.PropertyToID("_AmP"), VId = Shader.PropertyToID("_CkV");

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
            DropLocal();
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
            if (!cam) return;
            UpdateLocal(cam);
            if (!SystemInfo.supportsAsyncGPUReadback) return;
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
            // (local: the decal's ambient occlusion darkens the sky light where the ground is shaded)
            if (forceAmbient >= 0f) a = forceAmbient;
            ilc.indirectDiffuseLightingMultiplier.Override(LocalActive ? 1f : a);
            ilc.reflectionLightingMultiplier.Override(a);
            float f = Mathf.Lerp(1f, a, fogFollows);
            fog.tint.Override(new Color(f, f, f, 1f));
        }

        /// <summary>HDRP draws decals with their ambient occlusion: decals on, and their metal and AO channels.</summary>
        static bool DecalAOSupported()
        {
            var hd = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            if (!hd) return false;
            var rp = hd.currentPlatformRenderPipelineSettings;
            return rp.supportDecals && rp.decalSettings.perChannelMask;
        }

        void UpdateLocal(Camera cam)
        {
            bool want = local && DecalAOSupported();
            if (local && !want && !warned) { warned = true; Debug.LogWarning("StormSceneAmbient: local ambient needs the HDRP asset's decals with Metal and Ambient Occlusion properties; using the camera's view of the sky for the whole scene."); }
            if (!want) { DropLocal(); return; }
            if (!cs) { cs = Resources.Load<ComputeShader>("StormShadow"); if (cs) kernel = cs.FindKernel("Ambient"); }
            if (!cs) { DropLocal(); return; }
            int n = Mathf.ClosestPowerOfTwo(Mathf.Clamp(resolution, 32, 2048));
            if (!map || map.width != n)
            {
                if (map) { map.Release(); Destroy(map); }
                map = new RenderTexture(n, n, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                { enableRandomWrite = true, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "storm ambient map", hideFlags = HideFlags.DontSave };
                map.Create();
                if (decalMat) decalMat.SetTexture("_MaskMap", map);
            }
            if (!decal) MakeDecal();

            // centred on the camera, snapped to the map's texels so it does not shimmer as the camera moves
            float size = sizeKm * 1000f, texel = size / n;
            var c = cam.transform.position;
            var centre = new Vector3(Mathf.Round(c.x / texel) * texel, c.y, Mathf.Round(c.z / texel) * texel);
            bool first = true;
            foreach (var s in StormRenderer.Enabled)
            {
                if (!s || !s.isActiveAndEnabled || !s.ReadyToRender || !s.PhysicalSky) continue;
                var o = s.WorldToStorm(centre);
                cs.SetTexture(kernel, SkyVisId, s.Volume.SkyVisTex);
                cs.SetTexture(kernel, OutId, map);
                // (the ground under the camera: the storm frame's z of the camera, less its height above the ground)
                cs.SetVector(OId, new Vector4(o.x, o.y, Mathf.Max(o.z - 0.002f, 0f), sizeKm));
                cs.SetVector(PId, new Vector4(first ? 1 : 0, n, underside, 0));
                cs.SetVector(VId, new Vector4(StormVolume.L, StormVolume.ZT, 0, 0));
                cs.Dispatch(kernel, (n + 7) / 8, (n + 7) / 8, 1);
                first = false;
            }
            if (first)
            {
                // no storm: no shade
                decal.enabled = false; LocalActive = false;
                return;
            }
            if (forceLocal >= 0f)
            {
                var prev = RenderTexture.active; RenderTexture.active = map;
                GL.Clear(false, true, new Color(0f, forceLocal, 1f, 0f)); RenderTexture.active = prev;
            }
            map.IncrementUpdateCount();   // HDRP re-copies a decal's texture into its atlas when this changes
            decalGo.transform.SetPositionAndRotation(centre + Vector3.up * 3000f, Quaternion.Euler(90f, 0f, 0f));
            decal.size = new Vector3(size, size, 6000f);
            decal.pivot = new Vector3(0f, 0f, 3000f);
            decal.enabled = true; LocalActive = true;
        }

        void MakeDecal()
        {
            var sh = Shader.Find("HDRP/Decal");
            if (!sh) { local = false; return; }
            decalMat = new Material(sh) { name = "storm ambient decal", hideFlags = HideFlags.DontSave };
            // ambient occlusion only, from the map's green, at the map's blue opacity
            decalMat.SetFloat("_AffectAlbedo", 0f); decalMat.SetFloat("_AffectNormal", 0f); decalMat.SetFloat("_AffectMetal", 0f);
            decalMat.SetFloat("_AffectSmoothness", 0f); decalMat.SetFloat("_AffectEmission", 0f); decalMat.SetFloat("_AffectAO", 1f);
            decalMat.SetFloat("_MaskBlendSrc", 1f); decalMat.SetFloat("_DecalMaskMapBlueScale", 1f); decalMat.SetFloat("_DecalBlend", 1f);
            decalMat.SetFloat("_AORemapMin", 0f); decalMat.SetFloat("_AORemapMax", 1f);
            decalMat.SetTexture("_MaskMap", map);
            HDMaterial.ValidateMaterial(decalMat);
            decalGo = new GameObject("Storm ambient decal") { hideFlags = HideFlags.DontSave };
            decalGo.transform.SetParent(transform, false);
            decal = decalGo.AddComponent<DecalProjector>();
            decal.material = decalMat;
            decal.drawDistance = 1e6f;
            decal.fadeScale = 0.9f;   // (fades only past its draw distance plus its own size, which it never is)
            decal.fadeFactor = 1f;
        }

        void DropLocal()
        {
            LocalActive = false;
            if (decalGo) Destroy(decalGo);
            if (decalMat) Destroy(decalMat);
            if (map) { map.Release(); Destroy(map); }
            decalGo = null; decal = null; decalMat = null; map = null;
        }
    }
}

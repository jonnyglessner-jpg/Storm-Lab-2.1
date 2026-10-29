// The storms' shadow on the game's scene (user, 2026-10-26: the updraft and the downdrafts' rain cast none). HDRP's sun
// lights the terrain, roads and truck, and knew nothing of the storm; this gives it a cookie of how much sunlight gets
// through every storm on (StormRenderer.Enabled), each multiplying its shadow in (StormShadow.compute), centred on the
// camera and refreshed every refreshSeconds. A directional cookie is projected along the light from the light's position,
// so the component keeps its light there. StormRenderer adds it to its sun when castSceneShadow is on, in Play.
// The storm's own lighting is unchanged: its march does not read HDRP's sun. Under the unified lighting the shadow takes
// the storm's rain extinction and its sun reach (light diffused through the cloud), as the cloud's own light does.
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace StormLab
{
    [RequireComponent(typeof(Light))]
    public class StormSceneShadow : MonoBehaviour
    {
        [Tooltip("how wide the shadowed area round the camera is, km (outside it, no storm shadow)")]
        public float sizeKm = 120f;
        [Tooltip("texels across the shadow (sizeKm / resolution: about 120 m at the defaults)")]
        public int resolution = 1024;
        [Tooltip("seconds between updates (the storm volume itself is repainted every few seconds)")]
        public float refreshSeconds = 0.25f;

        /// <summary>the cookie (r: the share of sunlight through the storms), centred on the camera in the light's plane, for checks</summary>
        public RenderTexture Cookie => rt;

        Light sun; HDAdditionalLightData hd;
        RenderTexture rt; ComputeShader cs; int kernel = -1;
        float last = -1e9f; bool showing;

        static readonly int DensId = Shader.PropertyToID("_Dens"), CookieId = Shader.PropertyToID("_Cookie");
        static readonly int OId = Shader.PropertyToID("_CkO"), RId = Shader.PropertyToID("_CkR"), UId = Shader.PropertyToID("_CkU");
        static readonly int LId = Shader.PropertyToID("_CkL"), VId = Shader.PropertyToID("_CkV"), PId = Shader.PropertyToID("_CkP");
        static readonly int MId = Shader.PropertyToID("_CkM");

        /// <summary>The shadow on a sun, added if it has none.</summary>
        public static StormSceneShadow For(Light sun)
        {
            var s = sun.GetComponent<StormSceneShadow>();
            return s ? s : sun.gameObject.AddComponent<StormSceneShadow>();
        }

        void OnEnable() { sun = GetComponent<Light>(); hd = GetComponent<HDAdditionalLightData>(); last = -1e9f; }

        void OnDisable()
        {
            if (sun && showing) sun.cookie = null;
            showing = false;
        }

        void OnDestroy() { if (rt) { rt.Release(); Destroy(rt); } }

        void LateUpdate()
        {
            if (!sun || sun.type != LightType.Directional) return;
            if (Time.unscaledTime - last < refreshSeconds) return;
            last = Time.unscaledTime;
            if (!cs) { cs = Resources.Load<ComputeShader>("StormShadow"); if (cs) kernel = cs.FindKernel("Cookie"); }
            if (!cs) return;
            int n = Mathf.ClosestPowerOfTwo(Mathf.Clamp(resolution, 64, 4096));   // HDRP's cookie atlas takes powers of two
            if (!rt || rt.width != n)
            {
                if (rt) { rt.Release(); Destroy(rt); }
                rt = new RenderTexture(n, n, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                { enableRandomWrite = true, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "storm shadow cookie", hideFlags = HideFlags.DontSave };
                rt.Create();
            }

            // centred on the camera: the cookie lies in the light's plane through the light's position
            var cam = Camera.main;
            var centre = cam ? cam.transform.position : transform.position;
            transform.position = centre;
            Vector3 right = transform.right, up = transform.up, toLight = -transform.forward;
            float size = sizeKm * 1000f;

            bool first = true;
            foreach (var s in StormRenderer.Enabled)
            {
                if (!s || !s.isActiveAndEnabled || s.Volume == null || s.Plan == null) continue;
                cs.SetTexture(kernel, DensId, s.Volume.Dens);
                cs.SetTexture(kernel, CookieId, rt);
                var o = s.WorldToStorm(centre);
                cs.SetVector(OId, new Vector4(o.x, o.y, o.z, sizeKm));
                cs.SetVector(RId, StormRenderer.WorldToStormDir(right));
                cs.SetVector(UId, StormRenderer.WorldToStormDir(up));
                var l = StormRenderer.WorldToStormDir(toLight);
                cs.SetVector(LId, new Vector4(l.x, l.y, l.z, n));
                // rain and the light through the cloud as the storm's own lighting has them (unified: its rain extinction and
                // sun reach), so the ground under a rain shaft is no brighter than the rain above it
                cs.SetVector(VId, new Vector4(StormVolume.L, StormVolume.ZT, StormVolume.CloudMax, StormVolume.RainMax * s.Volume.rainScale));
                cs.SetVector(MId, s.SunReach);
                cs.SetVector(PId, new Vector4(first ? 1 : 0, 0, 0, 0));
                cs.Dispatch(kernel, (n + 7) / 8, (n + 7) / 8, 1);
                first = false;
            }
            if (first)
            {
                // no storm: no cookie
                if (showing) { sun.cookie = null; showing = false; }
                return;
            }
            rt.IncrementUpdateCount();   // HDRP re-copies a cookie into its atlas when this changes
            if (!showing || sun.cookie != rt)
            {
                if (hd) hd.SetCookie(rt, new Vector2(size, size)); else { sun.cookie = rt; sun.cookieSize2D = new Vector2(size, size); }
                showing = true;
            }
        }
    }
}

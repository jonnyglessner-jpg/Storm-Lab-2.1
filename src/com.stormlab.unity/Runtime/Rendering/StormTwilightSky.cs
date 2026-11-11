// The sky after sunset (user, 2026-11-10: "the dusk lighting does not look right at all. The sky goes black way too
// soon"; a storm lit pink and grey against a black sky with the sun 2.5 degrees down). HDRP's physically based sky is
// lit by the scene's sun Light, and whoever turns that Light fades it out as the sun sets (a Light below the horizon
// lights nothing), so HDRP's sky was black from a degree down, through all of twilight, while the storm kept its own
// model's twilight sky (SkyLighting) and the exposure followed that model. This draws that same sky where HDRP's has
// gone: the march's skyRad for the sun alone (the moon's sky is still HDRP's, from the moon Light), times the share of
// the sun Light that has been faded away, on the camera's sky pixels, before the storm is composited over them.
// Whoever fades the sun Light sets Share (the test map's panel); StormCustomPass draws it.
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace StormLab
{
    public static class StormTwilightSky
    {
        /// <summary>the share of the sun's sky HDRP no longer shows: 0 by day, 1 once the sun Light is off</summary>
        public static float Share;
        /// <summary>the sky's light for the sun alone (SkyLighting.Evaluate with no moon)</summary>
        public static SkyLight Light;
        /// <summary>nits a storm unit of light (StormRenderer.Luminance)</summary>
        public static float Luminance;
        /// <summary>The model's sky as shown, against the model's sky as it lights the storm: by day the model's dome is two
        /// to three times HDRP's, and at full strength its pale horizon came out white from sunset on, far over the
        /// storm it lights. At this share it joins HDRP's sky as that fades.</summary>
        public static float Scale = 0.4f;

        static Material mat;
        static MaterialPropertyBlock props;

        /// <summary>Set the sky to add from a storm's clock: its sun, with no moon.</summary>
        public static void Set(StormRenderer storm, float share)
        {
            Share = 0f;
            if (!storm || !storm.PhysicalSky || share <= 1e-4f) return;
            var night = storm.Night; night.moonPhase = 0f;
            Light = SkyLighting.Evaluate(storm.SunDirVol, storm.MoonDirVol, night);
            Luminance = storm.Luminance; Share = share;
        }

        /// <summary>Add the sky to the camera's sky pixels (the colour and depth buffers are left bound).</summary>
        public static void Draw(CommandBuffer cmd, HDCamera hd, RTHandle colour, RTHandle depth)
        {
            if (Share <= 1e-4f || Luminance <= 0f) return;
            if (!mat) { var sh = Shader.Find("Hidden/StormLab/TwilightSky"); if (!sh) return; mat = new Material(sh) { hideFlags = HideFlags.DontSave }; }
            var cam = hd.camera; var tr = cam.transform;
            float tanY = Mathf.Tan(0.5f * cam.fieldOfView * Mathf.Deg2Rad), tanX = tanY * hd.actualWidth / Mathf.Max(hd.actualHeight, 1);
            var p = props ??= new MaterialPropertyBlock(); p.Clear();
            p.SetVector("_TwFwd", StormRenderer.WorldToStormDir(tr.forward));
            p.SetVector("_TwRight", StormRenderer.WorldToStormDir(tr.right) * tanX);
            p.SetVector("_TwUp", StormRenderer.WorldToStormDir(tr.up) * tanY);
            var L = Light; var warm = SkyLighting.Warm(L);
            float glowK = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-6f, 1.5f, L.keyC.w)) * L.dim;
            p.SetVector("_TwSun", new Vector4(L.keyDir.x, L.keyDir.y, L.keyDir.z, L.skyC.w));
            p.SetVector("_TwWarm", new Vector4(warm.x * glowK, warm.y * glowK, warm.z * glowK, L.dim));
            p.SetVector("_TwP", new Vector4(Luminance * Share * Scale, hd.actualWidth, hd.actualHeight, 0f));
            CoreUtils.SetRenderTarget(cmd, colour, depth, ClearFlag.None);
            cmd.DrawProcedural(Matrix4x4.identity, mat, 0, MeshTopology.Triangles, 3, 1, p);
        }
    }
}

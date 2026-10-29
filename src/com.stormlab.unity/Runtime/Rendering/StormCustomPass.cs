// HDRP custom pass (from StormLab 2.0's StormSkinCustomPass): ray-marches the active StormRenderer for each camera,
// composites it over the camera colour and writes its depth where it is opaque. Storms marked drawAlongside are drawn
// too: all are marched first, then composited farthest first, each in front of or behind the one before it per pixel.
//
// Injection point: BeforeTransparent, after HDRP has drawn the sky, applied its fog to opaques and combined its
// volumetric clouds. Those passes treat any pixel without depth as open sky, so a storm drawn earlier gets HDRP's sky
// clouds and fog painted over it. Transparents drawn after the storm (rain, particles, lightning) are depth-tested
// against the depth it writes.
//
// StormRenderer registers one of these with HDRP as a global custom pass (EnsureInstalled); a scene can also hold its
// own CustomPassVolume with this pass in it, and then the global one stands down.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace StormLab
{
    [System.Serializable]
    public class StormCustomPass : CustomPass
    {
        [Tooltip("write the storm's depth where it is opaque, so transparents behind it are hidden")]
        public bool writeDepth = true;
        List<StormRenderer> drawn;
        /// <summary>how many times any storm pass has run (diagnostics: 0 means HDRP never reached it)</summary>
        public static int Executions { get; private set; }
        /// <summary>how many storms it has composited</summary>
        public static int Composites { get; private set; }

        protected override bool executeInSceneView => true;

        protected override void Execute(CustomPassContext ctx)
        {
            Executions++;
            var hd = ctx.hdCamera; var cam = hd.camera;
            if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;
            if (this == global && SceneHasOwnPass()) return;
            drawn ??= new List<StormRenderer>();
            drawn.Clear();
            var active = StormRenderer.Active;
            if (active != null && active.isActiveAndEnabled && active.ReadyToRender) drawn.Add(active);
            foreach (var s in StormRenderer.Enabled)
                if (s != null && s != active && s.drawAlongside && s.isActiveAndEnabled && s.ReadyToRender) drawn.Add(s);
            if (drawn.Count == 0) return;
            if (drawn.Count > 1)
            {
                var eye = cam.transform.position;
                drawn.Sort((a, b) => (b.UpdraftWorld(b.stormMinutes) - eye).sqrMagnitude.CompareTo((a.UpdraftWorld(a.stormMinutes) - eye).sqrMagnitude));
            }
            int w = hd.actualWidth, h = hd.actualHeight;
            // Every storm is marched against the scene's depth before any is composited: a storm's depth (written where
            // it hides half of what is behind it) stopped the next storm's march there, so a nearer storm's rain cut a
            // farther one off along that line, straight up the rain column (user, 2026-10-27).
            marched ??= new List<(StormRenderer, RenderTexture, RenderTexture)>();
            marched.Clear();
            for (int i = 0; i < drawn.Count; i++)
            {
                var storm = drawn[i];
                // the march reads the depth buffer as a texture, so nothing may have it bound as a target meanwhile
                CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
                // (the nearest other storm's cloud from its last march: this one's light shafts stop behind it)
                int n = drawn.Count;
                var other = n < 2 ? null : drawn[i == n - 1 ? n - 2 : n - 1];
                var tex = storm.RecordMarch(ctx.cmd, cam, ctx.cameraDepthBuffer, w, h, i == 0, out var dist, other != null ? other.HalfFor(cam) : null);
                if (tex != null) marched.Add((storm, tex, dist));
            }
            CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ctx.cameraDepthBuffer, ClearFlag.None);
            for (int i = 0; i < marched.Count; i++)
            {
                var (storm, tex, dist) = marched[i];
                // farthest first; each storm is laid in front of or behind the one before it pixel by pixel (their
                // mean distances along the ray), since a near storm's forward-flank rain can reach far past its updraft
                var under = i > 0 ? marched[i - 1].Item1 : null;
                // (the light shafts, kept apart when two or more storms are drawn, go on once all the storms' cloud is laid:
                // the first storm's with the second's composite, each later storm's with its own)
                bool many = marched.Count > 1;
                storm.RecordComposite(ctx.cmd, cam, tex, dist, w, h, writeDepth, under, under != null ? marched[i - 1].Item2 : null,
                    shaftsOwn: !many || i > 0, shaftsUnder: many && i == 1);
                Composites++;
            }
        }
        List<(StormRenderer, RenderTexture, RenderTexture)> marched;

        static StormCustomPass global;
        public static StormCustomPass Global => global;
        /// <summary>
        /// Make sure the storm is drawn: register one StormCustomPass with HDRP as a global custom pass at BeforeTransparent.
        /// HDRP clears its global passes when scripts reload (entering Play mode, too), so StormRenderer calls this every frame.
        /// </summary>
        public static void EnsureInstalled()
        {
            global ??= new StormCustomPass { name = "StormLab storm" };
            CustomPassVolume.RegisterUniqueGlobalCustomPass(CustomPassInjectionPoint.BeforeTransparent, global);
        }

        static readonly List<CustomPassVolume> volumes = new List<CustomPassVolume>();
        static readonly CustomPassInjectionPoint[] points = (CustomPassInjectionPoint[])System.Enum.GetValues(typeof(CustomPassInjectionPoint));
        static bool SceneHasOwnPass()
        {
            foreach (var p in points)
            {
                CustomPassVolume.GetActivePassVolumes(p, volumes);
                foreach (var v in volumes)
                    foreach (var c in v.customPasses)
                        if (c is StormCustomPass && c.enabled) return true;
            }
            return false;
        }
    }
}

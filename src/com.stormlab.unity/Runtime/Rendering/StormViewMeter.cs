// The frame's own light, for an exposure that follows what the camera sees (user, 2026-11-10: "another full lighting
// overhaul"; in the photographs the storm fills the frame at a readable grey and the horizon under it is near white,
// where the game's frames near a storm came out two to three stops under them). A camera's meter reads the picture, not
// the light falling on the camera: under a storm the two part company, since what is in the frame (the base, the rain,
// the ground in its shade) is far darker than a grey card in that light would be.
// An HDRP custom pass at BeforePostProcess (after the storm is composited, before tonemapping) copies the main camera's
// colour to a small target and reads it back: the picture in scene-linear light at the exposure in use, so 0.18 is a
// mid grey. LogAverage is its centre-weighted log-average luminance. Whoever sets the exposure (the test map's panel)
// closes the loop.
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace StormLab
{
    public static class StormViewMeter
    {
        const int W = 64, H = 36;

        /// <summary>the main camera's picture, centre-weighted log-average luminance in scene-linear light at the exposure
        /// in use (0.18: mid grey); negative until a frame has been read</summary>
        public static float LogAverage { get; private set; } = -1f;
        /// <summary>the share of the picture above 2.0 (a stop and a half over white's knee: sky that will clip)</summary>
        public static float Bright { get; private set; }
        /// <summary>Time.unscaledTime of the last reading</summary>
        public static float ReadAt { get; private set; } = -1e9f;
        /// <summary>seconds between readings</summary>
        public static float interval = 0.12f;

        sealed class MeterPass : CustomPass
        {
            RTHandle small; bool reading; float last = -1e9f;
            static float[] weight;

            protected override void Execute(CustomPassContext ctx)
            {
                var cam = ctx.hdCamera.camera;
                if (cam.cameraType != CameraType.Game || cam != Camera.main) return;
                if (reading || Time.unscaledTime - last < interval || !SystemInfo.supportsAsyncGPUReadback) return;
                small ??= RTHandles.Alloc(W, H, colorFormat: UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat, name: "storm view meter");
                HDUtils.BlitCameraTexture(ctx.cmd, ctx.cameraColorBuffer, small, 0f, true);
                CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ctx.cameraDepthBuffer, ClearFlag.None);
                reading = true; last = Time.unscaledTime;
                ctx.cmd.RequestAsyncReadback(small.rt, 0, TextureFormat.RGBAHalf, req =>
                {
                    reading = false;
                    if (req.hasError) return;
                    var d = req.GetData<ushort>();
                    if (d.Length < W * H * 4) return;
                    if (weight == null)
                    {
                        // centre-weighted: the middle of the frame counts three times its corners
                        weight = new float[W * H];
                        for (int y = 0; y < H; y++)
                            for (int x = 0; x < W; x++)
                            {
                                float u = (x + 0.5f) / W * 2f - 1f, v = (y + 0.5f) / H * 2f - 1f;
                                weight[y * W + x] = 1f + 2f * Mathf.Exp(-(u * u + v * v) / 0.5f);
                            }
                    }
                    double sum = 0, wsum = 0, bright = 0;
                    for (int i = 0; i < W * H; i++)
                    {
                        float r = Mathf.HalfToFloat(d[i * 4]), g = Mathf.HalfToFloat(d[i * 4 + 1]), b = Mathf.HalfToFloat(d[i * 4 + 2]);
                        float l = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                        if (float.IsNaN(l) || float.IsInfinity(l)) continue;
                        float w = weight[i];
                        sum += w * Mathf.Log(Mathf.Clamp(l, 1e-4f, 64f), 2f); wsum += w;
                        if (l > 2f) bright += w;
                    }
                    if (wsum <= 0) return;
                    LogAverage = Mathf.Pow(2f, (float)(sum / wsum)); Bright = (float)(bright / wsum);
                    ReadAt = Time.unscaledTime;
                });
            }

            protected override void Cleanup() { small?.Release(); small = null; }
        }

        static MeterPass pass;

        /// <summary>Make sure the meter runs: HDRP drops its global custom passes when scripts reload, so whoever uses the
        /// meter calls this every frame (as the storm does for its own pass).</summary>
        public static void EnsureInstalled()
        {
            pass ??= new MeterPass { name = "StormLab view meter" };
            CustomPassVolume.RegisterUniqueGlobalCustomPass(CustomPassInjectionPoint.BeforePostProcess, pass);
        }
    }
}

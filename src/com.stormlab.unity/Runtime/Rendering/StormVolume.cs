// The storm volume on the GPU: the density volume painted from the parameter block (the web build's BUILD), its light
// grid (LIGHT), and StormLab 2.0's sky visibility and ground light. As in the web build (renderer.ts) a repaint is
// spread over a few frames, and the light grid is redone when the repaint finishes.
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace StormLab
{
    public enum StormQuality { High, Low }

    public sealed class StormVolume : IDisposable
    {
        /// <summary>cells across and levels of the density volume; the light grid is a third of that, the sky grid half the light grid</summary>
        public readonly int NV, NZ, LN, LZ, SN, SZ;
        /// <summary>volume half-width and top, km (the web build's VOL_L, VOL_TOP)</summary>
        public const float L = 90f, ZT = 18f;
        /// <summary>peak extinction for cloud and for precipitation, 1/km (the web build's CMAX, PMAX)</summary>
        public const float CloudMax = 26f, RainMax = 2.6f;
        /// <summary>number of frames a repaint is spread over (the web build's SLABS)</summary>
        public const int Slabs = 4;

        /// <summary>the wall-cloud underside the build draws (_WallP; StormRenderer.wallBase)</summary>
        public Vector4 wallParams;
        /// <summary>the tilted updraft's rain-free base and a tornado's clear air (_PrecipP; StormTilt.Clear, km)</summary>
        public Vector4 precipParams;
        /// <summary>rain's extinction in the light grids as a multiple of RainMax (the unified lighting's rainExtinction; 1 otherwise)</summary>
        public float rainScale = 1f;
        /// <summary>the unified lighting's sun reach (StormCommon.hlsl sunReach: octaves, octave extinction, diffusion,
        /// diffused sun) for the ground light; zero for the other lightings (sunT)</summary>
        public Vector4 sunReach;
        /// <summary>the towns lighting the base at night, storm frame (km: x, y, radius, glow), and how many (StormRenderer.Towns)</summary>
        public readonly Vector4[] towns = new Vector4[MaxTowns];
        public int townCount;
        public const int MaxTowns = 16;

        public RenderTexture Dens => dens;
        public RenderTexture LightTex => light;
        /// <summary>the web build's sun transmittance on the light grid (its lighting mode)</summary>
        public RenderTexture LightWebTex => lightWeb;
        public RenderTexture SkyVisTex => skyVis;
        public RenderTexture GroundTex => ground;
        public long VramBytes => 4L * NV * NV * NZ + 9L * LN * LN * LZ + 8L * SN * SN * SZ + 8L * LN * LN + 2L * NV * NV;

        readonly RenderTexture dens, light, lightWeb, skyVis, ground;
        // RainUnderCloud's share of the rain kept per column, from the last repaint (a slab repaint applies it as it writes)
        readonly RenderTexture rainKeep;
        bool rainKeepValid;
        readonly ComputeShader cs;
        readonly int kBuild, kLight, kSky, kGround, kRain;
        readonly Vector4[] sp = new Vector4[StormSpin.NSLOT];
        int slab = -1, slabZ;
        float pendingBase;
        Vector3 pendingKey;
        Texture noise;

        public StormVolume(StormQuality q, ComputeShader shader)
        {
            cs = shader;
            kBuild = cs.FindKernel("Build"); kLight = cs.FindKernel("Light"); kSky = cs.FindKernel("SkyVis"); kGround = cs.FindKernel("GroundLight"); kRain = cs.FindKernel("RainUnderCloud");
            // the web build's QUALITY_HIGH and QUALITY_LOW
            if (q == StormQuality.Low) { NV = 256; NZ = 72; } else { NV = 416; NZ = 144; }
            LN = Mathf.Max(8, NV / 3); LZ = Mathf.Max(4, NZ / 3);
            SN = Mathf.Max(8, LN / 2); SZ = Mathf.Max(4, LZ / 2);
            dens = Vol(NV, NZ, "storm volume", RenderTextureFormat.ARGB32);
            light = Vol(LN, LZ, "storm light", RenderTextureFormat.ARGBHalf);       // r: key light optical depth
            lightWeb = Vol(LN, LZ, "storm light (web build)", RenderTextureFormat.R8);
            skyVis = Vol(SN, SZ, "storm sky visibility", RenderTextureFormat.ARGBHalf);
            ground = new RenderTexture(LN, LN, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                enableRandomWrite = true, useMipMap = false, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                name = "storm ground light", hideFlags = HideFlags.DontSave
            };
            ground.Create();
            rainKeep = new RenderTexture(NV, NV, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear)
            {
                enableRandomWrite = true, useMipMap = false, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point,
                name = "storm rain kept", hideFlags = HideFlags.DontSave
            };
            rainKeep.Create();
        }

        static RenderTexture Vol(int n, int nz, string name, RenderTextureFormat format)
        {
            var rt = new RenderTexture(n, n, 0, format, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Tex3D, volumeDepth = nz, enableRandomWrite = true, useMipMap = false,
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = name, hideFlags = HideFlags.DontSave
            };
            rt.Create();
            return rt;
        }

        static int G(int n, int t) => (n + t - 1) / t;

        /// <summary>The parameter block as the passes take it: the grid slot filled in.</summary>
        public Vector4[] GpuParams(float[] param)
        {
            for (int i = 0; i < StormSpin.NSLOT; i++) sp[i] = new Vector4(param[i * 4], param[i * 4 + 1], param[i * 4 + 2], param[i * 4 + 3]);
            sp[17] = new Vector4(NV, NZ, L, ZT);
            sp[20] = Vector4.zero;
            return sp;
        }

        void Build(CommandBuffer cmd, float[] param, int z0, int nz, bool applyKeep)
        {
            var p = GpuParams(param);
            p[20] = new Vector4(z0, 0, 0, 0);
            cmd.SetComputeVectorArrayParam(cs, "_SP", p);
            cmd.SetComputeVectorParam(cs, "_WallP", wallParams);
            cmd.SetComputeVectorParam(cs, "_PrecipP", precipParams);
            cmd.SetComputeTextureParam(cs, kBuild, "_Noise", noise);
            cmd.SetComputeTextureParam(cs, kBuild, "_OutD", dens);
            cmd.SetComputeTextureParam(cs, kBuild, "_RainKeep", rainKeep);
            cmd.SetComputeVectorParam(cs, "_RainKeepP", new Vector4(applyKeep && rainKeepValid && precipParams.w > 0f ? 1 : 0, 0, 0, 0));
            cmd.DispatchCompute(cs, kBuild, G(NV, 4), G(NV, 4), G(nz, 4));
        }

        /// <summary>The game's layout (precipParams.w): rain only out of cloud that reaches down to mid levels
        /// (RainUnderCloud), once the whole volume is built: its map of the rain kept for the next slab repaint, applied
        /// to the volume now too when it was built all at once (inPlace). Uses the last Build's parameters.</summary>
        void TrimRain(CommandBuffer cmd, bool inPlace)
        {
            cmd.SetComputeTextureParam(cs, kRain, "_OutD", dens);
            cmd.SetComputeTextureParam(cs, kRain, "_OutKeep", rainKeep);
            cmd.SetComputeVectorParam(cs, "_RainKeepP", new Vector4(0, inPlace ? 1 : 0, 0, 0));
            cmd.DispatchCompute(cs, kRain, G(NV, 8), G(NV, 8), 1);
            rainKeepValid = true;
        }

        /// <summary>The light grid, sky visibility and ground light for a key light (sun or moon), all at once.</summary>
        public void Relight(CommandBuffer cmd, Vector3 keyVol, float zbKm)
        {
            cmd.SetComputeTextureParam(cs, kLight, "_Dens", dens);
            cmd.SetComputeTextureParam(cs, kLight, "_OutL", light);
            cmd.SetComputeTextureParam(cs, kLight, "_OutLW", lightWeb);
            cmd.SetComputeVectorParam(cs, "_LSun", keyVol);
            cmd.SetComputeVectorParam(cs, "_LDims", new Vector4(LN, LZ, L, ZT));
            cmd.SetComputeVectorParam(cs, "_LP", new Vector4(CloudMax, RainMax * rainScale, 0.8f, zbKm));
            cmd.SetComputeVectorParam(cs, "_LSlab", Vector4.zero);
            cmd.DispatchCompute(cs, kLight, G(LN, 4), G(LN, 4), G(LZ, 4));

            cmd.SetComputeTextureParam(cs, kSky, "_Dens", dens);
            cmd.SetComputeTextureParam(cs, kSky, "_OutS", skyVis);
            cmd.SetComputeVectorParam(cs, "_LDims", new Vector4(SN, SZ, L, ZT));
            cmd.DispatchCompute(cs, kSky, G(SN, 4), G(SN, 4), G(SZ, 4));

            cmd.SetComputeTextureParam(cs, kGround, "_LightIn", light);
            cmd.SetComputeTextureParam(cs, kGround, "_OutG", ground);
            cmd.SetComputeVectorParam(cs, "_LM", sunReach);
            cmd.SetComputeVectorArrayParam(cs, "_Towns", towns);
            cmd.SetComputeVectorParam(cs, "_TownP", new Vector4(townCount, StormRenderer.TownFloor, 0, 0));
            cmd.SetComputeVectorParam(cs, "_LDims", new Vector4(LN, LZ, L, ZT));
            cmd.DispatchCompute(cs, kGround, G(LN, 8), G(LN, 8), 1);
        }

        /// <summary>Paint and light the storm for one moment, all at once (the web build's update).</summary>
        public void Update(CommandBuffer cmd, float[] param, Texture noiseTex, Vector3 keyVol)
        {
            noise = noiseTex;
            slab = -1;
            Build(cmd, param, 0, NZ, false);
            TrimRain(cmd, true);
            Relight(cmd, keyVol, param[4]);
        }

        /// <summary>Start a repaint spread over Slabs frames (the web build's beginUpdate); call Step once per frame.</summary>
        public void BeginUpdate(float[] param, Texture noiseTex, Vector3 keyVol)
        {
            noise = noiseTex;
            Array.Copy(param, pending, pending.Length);
            pendingBase = param[4]; pendingKey = keyVol;
            slab = 0; slabZ = Mathf.CeilToInt(NZ / (float)Slabs / 4f) * 4;
        }
        readonly float[] pending = new float[StormSpin.NSLOT * 4];

        /// <summary>This frame's share of a repaint in progress; true when it (and the relight) finished.</summary>
        public bool Step(CommandBuffer cmd)
        {
            if (slab < 0) return false;
            int z0 = slab * slabZ;
            Build(cmd, pending, z0, Mathf.Min(slabZ, NZ - z0), true);
            slab++;
            bool done = slab * slabZ >= NZ;
            if (done) { TrimRain(cmd, false); Relight(cmd, pendingKey, pendingBase); slab = -1; }
            return done;
        }
        public bool Rebuilding => slab >= 0;

        public void Dispose()
        {
            foreach (var r in new[] { dens, light, lightWeb, skyVis, ground, rainKeep }) if (r) { r.Release(); UnityEngine.Object.DestroyImmediate(r); }
        }
    }
}

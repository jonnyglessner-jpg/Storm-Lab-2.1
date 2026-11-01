// Connects the independent lightning module to a StormRenderer (from StormLab 2.0's LightningCloudBridge): flashes
// start inside the drawn cloud, light it from within, and are hidden by the cloud and rain between them and the camera.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace StormLab.Lightning
{
    [ExecuteAlways, DefaultExecutionOrder(120)]
    public sealed class LightningCloudBridge : MonoBehaviour, IStormTransientLighting
    {
        public LightningSystem lightning;
        public StormRenderer storm;
        [Min(0)] public float brightness = 24;
        [Min(.1f)] public float sourceRadiusKm = 1.5f;
        [Tooltip("Effective extinction for unresolved multiple scattering. 1 = direct extinction only.")]
        [Range(.01f,1)] public float scatteringExtinction = .04f;
        [Range(4,32)] public int lightTraceSamples = 12;
        [Tooltip("cells across the local light field, which covers the storm's whole 180 x 180 x 18 km volume (96: about 1.9 km cells)")]
        [Range(24,96)] public int gridWidth = 96;
        [Range(12,48)] public int gridHeight = 32;
        public bool channelOcclusion = true;
        [Tooltip("Reject flashes without an origin inside the current cloud field. In-cloud flashes require both endpoints inside cloud.")]
        public bool anchorChannelsToCloud = true;
        [Range(0,1)] public float positiveAnvilFraction = .7f;
        /// <summary>Optional external local-light source list. Null uses the LightningSystem's channel samples.</summary>
        [NonSerialized] public IReadOnlyList<LightningIllumination> externalSources;

        public RenderTexture LightField => lightField;
        ComputeShader compute;
        Texture3D noise;
        // an empty volume for the other-storm slots not in use (a compute dispatch refuses an unset texture)
        Texture3D black3D;
        RenderTexture lightField;
        StormRenderer boundStorm;
        LightningSystem boundLightning;
        ComputeBuffer anchorBuffer;
        readonly Vector4[] anchorCandidates = new Vector4[256];
        readonly Vector4[] sources = new Vector4[8];

        void OnEnable() { Attach(); }
        void LateUpdate() { Attach(); RefreshChannelOcclusion(); }
        void Attach()
        {
            if (!lightning) lightning = GetComponent<LightningSystem>();
            if (boundLightning != lightning || (lightning && anchorChannelsToCloud && lightning.ResolveCloudAnchors != ResolveAnchors) ||
                (lightning && !anchorChannelsToCloud && lightning.ResolveCloudAnchors == ResolveAnchors))
            {
                if (boundLightning && boundLightning.ResolveCloudAnchors == ResolveAnchors) boundLightning.ResolveCloudAnchors = null;
                boundLightning = lightning;
                if (boundLightning && anchorChannelsToCloud) boundLightning.ResolveCloudAnchors = ResolveAnchors;
            }
            if (boundStorm == storm) return;
            if (boundStorm && ReferenceEquals(boundStorm.TransientLighting,this)) boundStorm.TransientLighting = null;
            boundStorm = storm;
            if (boundStorm)
            {
                if (boundStorm.TransientLighting != null && !ReferenceEquals(boundStorm.TransientLighting,this))
                    throw new InvalidOperationException("This storm already has a transient lighting adapter.");
                boundStorm.TransientLighting = this;
            }
        }

        /// <summary>Call after assigning the adapter fields from code.</summary>
        public void Connect() { Attach(); }

        /// <summary>Search the same coarse cloud medium used for channel occlusion; no clear-air fallback.</summary>
        public LightningAnchors? ResolveAnchors(FlashEvent e)
        {
            if (!isActiveAndEnabled || !storm || !storm.ReadyToRender) return null;
            EnsureAssets();
            if (anchorBuffer == null) anchorBuffer = new ComputeBuffer(256,16);
            int kernel = compute.FindKernel("FindAnchors");
            using (var cmd = new CommandBuffer { name = "Lightning cloud anchors" })
            {
                BindMedium(cmd,compute,kernel);
                cmd.SetComputeIntParam(compute,"_SLAnchorSeed",unchecked((int)e.seed));
                cmd.SetComputeIntParam(compute,"_SLAnchorKind",(int)e.kind);
                cmd.SetComputeFloatParam(compute,"_SLAnvilFraction",positiveAnvilFraction);
                cmd.SetComputeFloatParam(compute,"_SLReach",Mathf.Clamp01(e.reach));
                cmd.SetComputeBufferParam(compute,kernel,"_SLAnchors",anchorBuffer);
                cmd.DispatchCompute(compute,kernel,4,1,1);
                Graphics.ExecuteCommandBuffer(cmd);
            }
            // One small synchronous readback per new event, never per rendered frame.
            anchorBuffer.GetData(anchorCandidates);
            int origin = -1, target = -1;
            for (int i = 0; i < 128; i++)
                if (anchorCandidates[i].w >= .5f) { origin = i; break; }
            if (origin < 0) return null;
            for (int i = 128; i < 256; i++)
                if (anchorCandidates[i].w >= .5f && Vector3.Distance(anchorCandidates[origin],anchorCandidates[i]) >= .5f)
                { target = i; break; }
            // In-cloud flashes need a second cloud point; a crawler needs one out in the anvil.
            if ((e.kind == FlashKind.InCloud || e.kind == FlashKind.Crawler) && target < 0) return null;
            Vector3 a = storm.StormToWorld(anchorCandidates[origin]);
            Vector3 b = storm.StormToWorld(anchorCandidates[target < 0 ? origin : target]);
            return new LightningAnchors(new System.Numerics.Vector3(a.x,a.y,a.z),new System.Numerics.Vector3(b.x,b.y,b.z));
        }

        public bool RecordLighting(CommandBuffer cmd, StormRenderer target, Camera camera, ComputeShader march, int kernel)
        {
            if (!isActiveAndEnabled || target != storm || target.Volume == null) return false;
            EnsureAssets();
            RefreshChannelOcclusion();
            var list = externalSources ?? (lightning ? lightning.Illumination : null);
            Array.Clear(sources,0,sources.Length);
            int count = 0;
            if (list != null)
            foreach (var sample in list)
            {
                if (!(sample.intensity > .0005f)) continue;
                Vector3 p = storm.WorldToStorm(sample.position);
                // Keep the strongest eight samples deterministically; no random or camera-dependent selection.
                int at = Math.Min(count,7);
                if (count >= 8 && sample.intensity <= sources[7].w) continue;
                while (at > 0 && sources[at-1].w < sample.intensity) { if(at<8) sources[at]=sources[at-1]; at--; }
                sources[at]=new Vector4(p.x,p.y,p.z,sample.intensity); count=Math.Min(8,count+1);
            }
            if (count == 0 || brightness <= 0) return false;
            int build = compute.FindKernel("BuildLight");
            BindMedium(cmd,compute,build);
            cmd.SetComputeVectorArrayParam(compute,"_SLLights",sources);
            cmd.SetComputeIntParam(compute,"_SLLightCount",count);
            cmd.SetComputeVectorParam(compute,"_SLLightSettings",new Vector4(brightness,Mathf.Max(.1f,sourceRadiusKm),scatteringExtinction,lightTraceSamples));
            cmd.SetComputeVectorParam(compute,"_SLGrid",new Vector4(lightField.width,lightField.height,lightField.volumeDepth,0));
            cmd.SetComputeTextureParam(compute,build,"_SLLightOut",lightField);
            cmd.BeginSample("Lightning cloud light");
            cmd.DispatchCompute(compute,build,(lightField.width+3)/4,(lightField.height+3)/4,(lightField.volumeDepth+3)/4);
            cmd.EndSample("Lightning cloud light");
            cmd.SetComputeTextureParam(march,kernel,"_TransientLightTex",lightField);
            cmd.SetComputeIntParam(march,"_TransientLightOn",1);
            return true;
        }

        void EnsureAssets()
        {
            if (!compute) compute=Resources.Load<ComputeShader>("StormLightningLight");
            if (!compute) throw new InvalidOperationException("Missing StormLightningLight compute shader.");
            if (!noise) noise=CloudNoise.Make();
            if (!black3D)
            {
                black3D=new Texture3D(1,1,1,TextureFormat.RGBA32,false) { hideFlags=HideFlags.DontSave, name="Lightning empty storm" };
                black3D.SetPixels32(new[] { new Color32(0,0,0,0) }); black3D.Apply(false,true);
            }
            int width=Mathf.Clamp(gridWidth,24,96),height=Mathf.Clamp(gridHeight,12,48);
            if (lightField && lightField.width==width && lightField.volumeDepth==height) return;
            if (lightField) { lightField.Release(); Release(lightField); }
            lightField=new RenderTexture(width,width,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear)
            {
                dimension=TextureDimension.Tex3D,volumeDepth=height,enableRandomWrite=true,
                filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp,
                hideFlags=HideFlags.DontSave,name="Lightning local cloud radiance"
            };
            lightField.Create();
        }

        public void RefreshChannelOcclusion()
        {
            if (!lightning || !lightning.ChannelMaterial) return;
            var material=lightning.ChannelMaterial;
            bool valid=channelOcclusion && storm && storm.ReadyToRender;
            material.SetFloat("_SLActive",valid ? 1 : 0);
            if(!valid) return;
            EnsureAssets();
            material.SetVectorArray("_SP",storm.GpuParams());
            material.SetTexture("_Noise",noise);
            material.SetTexture("_SLDens",storm.Volume.Dens);
            material.SetVector("_SLVolume",VolumeSettings());
            material.SetVector("_SLOrigin",storm.StormToWorld(Vector3.zero));
            // the other storms drawn hide this storm's channels too (the game's other storm overhead: its rain and cloud,
            // which the far storm's anvil crawlers showed through, user 2026-10-31)
            int n=GatherOthers();
            for(int i=0;i<Others;i++) material.SetTexture(OtherDensName[i],i<n ? otherDens[i] : black3D);
            material.SetVectorArray("_SLOther",others);
            material.SetInt("_SLOtherCount",n);
            // the nearest other's block, for its wall cloud, tail and flanking line drawn analytically (nearField_o)
            if(n>0) material.SetVectorArray("_SP_o",nearest.GpuParams());
            material.SetTexture("_Noise_o",noise);
        }

        /// <summary>how many other storms a channel can be hidden by (StormLightningMedium.hlsl SL_OTHERS)</summary>
        public const int Others = 3;
        static readonly string[] OtherDensName = { "_SLDensO0", "_SLDensO1", "_SLDensO2" };
        readonly Vector4[] others = new Vector4[Others];
        readonly Texture[] otherDens = new Texture[Others];
        StormRenderer nearest;
        readonly List<StormRenderer> gathered = new List<StormRenderer>();
        /// <summary>The other storms drawn (StormRenderer.Enabled: ready, not this one), the nearest Others of them, into
        /// otherDens (their volumes) and others (their frames: world origin m, cloud base km), nearest first. Returns how many.</summary>
        int GatherOthers()
        {
            Vector3 here = storm.StormToWorld(Vector3.zero);
            gathered.Clear();
            foreach (var other in StormRenderer.Enabled)
                if (other && other != storm && other.isActiveAndEnabled && other.ReadyToRender) gathered.Add(other);
            // nearest first: the nearest gets its low features drawn analytically
            gathered.Sort((a, b) => (a.StormToWorld(Vector3.zero) - here).sqrMagnitude.CompareTo((b.StormToWorld(Vector3.zero) - here).sqrMagnitude));
            int n = Math.Min(Others, gathered.Count);
            for (int i = 0; i < n; i++)
            {
                Vector3 o = gathered[i].StormToWorld(Vector3.zero);
                others[i] = new Vector4(o.x, o.y, o.z, gathered[i].GpuParams()[1].x);
                otherDens[i] = gathered[i].Volume.Dens;
            }
            for (int i = n; i < Others; i++) { others[i] = Vector4.zero; otherDens[i] = null; }
            nearest = n > 0 ? gathered[0] : null;
            return n;
        }

        /// <summary>The transmittance the channel shader gives between two world points (m): through this storm's cloud
        /// and rain alone, or through every storm drawn as well (otherStorms), the way the channels are hidden. One small
        /// synchronous readback, for checks. NaN while this storm is not ready.</summary>
        public float ProbeTransmittance(Vector3 worldA, Vector3 worldB, bool otherStorms)
        {
            if (!storm || !storm.ReadyToRender) return float.NaN;
            EnsureAssets();
            int kernel = compute.FindKernel("Probe");
            using var buffer = new ComputeBuffer(1, 4);
            using (var cmd = new CommandBuffer { name = "Lightning transmittance probe" })
            {
                BindMedium(cmd, compute, kernel);
                int n = otherStorms ? GatherOthers() : 0;
                for (int i = 0; i < Others; i++) cmd.SetComputeTextureParam(compute, kernel, OtherDensName[i], i < n ? otherDens[i] : black3D);
                cmd.SetComputeVectorArrayParam(compute, "_SLOther", others);
                cmd.SetComputeIntParam(compute, "_SLOtherCount", n);
                if (n > 0) cmd.SetComputeVectorArrayParam(compute, "_SP_o", nearest.GpuParams());
                cmd.SetComputeTextureParam(compute, kernel, "_Noise_o", noise);
                cmd.SetComputeVectorParam(compute, "_SLProbeA", worldA);
                cmd.SetComputeVectorParam(compute, "_SLProbeB", worldB);
                cmd.SetComputeIntParam(compute, "_SLProbeOthers", otherStorms ? 1 : 0);
                cmd.SetComputeBufferParam(compute, kernel, "_SLProbeOut", buffer);
                cmd.DispatchCompute(compute, kernel, 1, 1, 1);
                Graphics.ExecuteCommandBuffer(cmd);
            }
            var result = new float[1]; buffer.GetData(result);
            return result[0];
        }

        /// <summary>Shared binding also used by GPU probes. Does not change density or the sunlight grids.</summary>
        public void BindMedium(CommandBuffer cmd, ComputeShader shader, int kernel)
        {
            EnsureAssets();
            cmd.SetComputeVectorArrayParam(shader,"_SP",storm.GpuParams());
            cmd.SetComputeVectorParam(shader,"_SLVolume",VolumeSettings());
            cmd.SetComputeVectorParam(shader,"_SLOrigin",storm.StormToWorld(Vector3.zero));
            cmd.SetComputeTextureParam(shader,kernel,"_Noise",noise);
            cmd.SetComputeTextureParam(shader,kernel,"_SLDens",storm.Volume.Dens);
        }
        static Vector4 VolumeSettings() => new Vector4(StormVolume.L,StormVolume.ZT,StormVolume.CloudMax,StormVolume.RainMax);
        void OnDisable()
        {
            if(boundLightning && boundLightning.ResolveCloudAnchors==ResolveAnchors) boundLightning.ResolveCloudAnchors=null;
            boundLightning=null;
            if(anchorBuffer!=null) { anchorBuffer.Release(); anchorBuffer=null; }
            if(boundStorm && ReferenceEquals(boundStorm.TransientLighting,this)) boundStorm.TransientLighting=null;
            boundStorm=null;
            if(lightning && lightning.ChannelMaterial) lightning.ChannelMaterial.SetFloat("_SLActive",0);
            if(lightField) { lightField.Release(); Release(lightField); lightField=null; }
            if(noise) { Release(noise); noise=null; }
            if(black3D) { Release(black3D); black3D=null; }
        }
        static void Release(UnityEngine.Object obj) { if(Application.isPlaying) Destroy(obj); else DestroyImmediate(obj); }
    }
}

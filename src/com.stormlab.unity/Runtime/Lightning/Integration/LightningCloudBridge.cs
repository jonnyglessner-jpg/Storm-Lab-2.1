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
        }

        /// <summary>Shared binding also used by GPU probes. Does not change density or the sunlight grids.</summary>
        public void BindMedium(CommandBuffer cmd, ComputeShader shader, int kernel)
        {
            EnsureAssets();
            cmd.SetComputeVectorArrayParam(shader,"_SP",storm.GpuParams());
            cmd.SetComputeVectorParam(shader,"_SLVolume",VolumeSettings());
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
        }
        static void Release(UnityEngine.Object obj) { if(Application.isPlaying) Destroy(obj); else DestroyImmediate(obj); }
    }
}

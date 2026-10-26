#ifndef STORM_LIGHTNING_MEDIUM
#define STORM_LIGHTNING_MEDIUM
// The storm's cloud and rain as an extinction field, for lightning: channel occlusion (StormLightning.shader), the local
// light field and the cloud-anchor search (StormLightningLight.compute). From StormLab 2.0's medium, reading this
// package's volume, which covers the whole storm (no far anvil beyond it). Private sampler names avoid HDRP's globals.
#define sampler_LinearClamp sl_LinearClamp
#define sampler_LinearRepeat sl_LinearRepeat
#include "Packages/com.stormlab.unity/Runtime/Rendering/Shaders/StormCommon.hlsl"
#undef sampler_LinearClamp
#undef sampler_LinearRepeat

Texture3D<float4> _SLDens;
// xy volume half-width and height km; zw cloud and rain extinction 1/km.
float4 _SLVolume;
// World origin of the storm frame, in metres.
float4 _SLOrigin;
float _SLActive;

float3 slWorldToVolume(float3 world) { return (world.xzy - _SLOrigin.xzy) * .001; }
float3 slUVW(float3 p) { return float3(p.xy / (2 * _SLVolume.x) + .5, p.z / _SLVolume.y); }
float slSmooth(float a, float b, float v) { float t = saturate((v-a)/(b-a)); return t*t*(3-2*t); }

// Coarse extinction matching the march's water/ice thresholds and rain. It deliberately omits the march's sub-voxel
// billows: a second moving cloud field would disagree with the one drawn.
float2 slMedium(float3 p)
{
    bool inVolume = all(abs(p.xy) < _SLVolume.x) && p.z >= 0 && p.z < _SLVolume.y;
    if (!inVolume) return 0;
    float4 s = _SLDens.SampleLevel(sl_LinearClamp, slUVW(p), 0);
    float ice = 1-saturate(s.a/.3);
    float water = slSmooth(.26,.4,s.r) * (.4+.6*slSmooth(.4,.75,s.r));
    float iceDensity = slSmooth(.04,.8,s.r*.65)*.55;
    float cloud = lerp(water,iceDensity,slSmooth(0,1,ice));
    // the low features are drawn analytically in the march; the volume keeps a coarse copy of where they are
    if (p.z < S_base.x+1.2 && s.b > .05) cloud += slSmooth(.22,.38,nearField(p,0))*.8;
    float rain = s.g*s.g*_SLVolume.w;
    return max(0,float2(cloud*_SLVolume.z,rain));
}
float slExtinction(float3 p) { float2 medium=slMedium(p); return medium.x+medium.y; }

// Slab intersection handles parallel and negative directions without a sign-changing epsilon.
bool slBox(float3 origin,float3 direction,float3 low,float3 high,out float enter,out float leave)
{
    enter=0; leave=1e6;
    [unroll] for(int axis=0;axis<3;axis++)
    {
        if(abs(direction[axis])<1e-7)
        { if(origin[axis]<low[axis] || origin[axis]>high[axis]) return false; }
        else
        {
            float a=(low[axis]-origin[axis])/direction[axis],b=(high[axis]-origin[axis])/direction[axis];
            enter=max(enter,min(a,b)); leave=min(leave,max(a,b));
        }
    }
    return leave>enter;
}

float slOpticalDepth(float3 a,float3 b,int budget,float extinctionScale)
{
    float3 delta=b-a; float distance=length(delta);
    if(distance<1e-5) return 0;
    float3 direction=delta/distance;
    float enter,leave;
    if(!slBox(a,direction,float3(-_SLVolume.x,-_SLVolume.x,0),float3(_SLVolume.x,_SLVolume.x,_SLVolume.y),enter,leave)) return 0;
    leave=min(leave,distance); if(leave<=enter) return 0;
    int count=clamp((int)ceil((leave-enter)/.12),1,budget);
    float stepLength=(leave-enter)/count, tau=0;
    [loop] for(int i=0;i<count;i++)
    {
        tau+=slExtinction(a+direction*(enter+(i+.5)*stepLength))*stepLength*extinctionScale;
        if(tau>14) break;
    }
    return tau;
}
#endif

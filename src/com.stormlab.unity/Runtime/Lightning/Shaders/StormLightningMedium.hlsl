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

// The other storms drawn (the game's; StormRenderer.Enabled, bound by LightningCloudBridge): a channel of this storm seen
// through another storm's cloud and rain is hidden by them too (user, 2026-10-31: anvil crawlers of the other storm seen
// through the rain and cloud of the storm overhead; each storm's lightning saw only its own volume). Their volumes give
// their cloud and rain as this storm's does. The nearest other storm's low features (wall cloud, tail, flanking line) are
// the march's analytic nearField with that storm's own parameter block: StormCommon.hlsl compiled a second time below
// with every top-level name suffixed _o (its parameters _SP_o, its noise _Noise_o; the names are the macros, the file is
// untouched). Any further storm's come from its volume's coarse copy of them (b, StormVolume.compute).
// A new top-level symbol in StormCommon.hlsl must be added to this list, or the second copy redefines it.
#undef STORM_COMMON
#define _SP _SP_o
#define _Noise _Noise_o
#define _WallP _WallP_o
#define sampler_LinearClamp slo_LinearClamp
#define sampler_LinearRepeat slo_LinearRepeat
#define ss ss_o
#define sunT sunT_o
#define sunReach sunReach_o
#define n3 n3_o
#define g_fp g_fp_o
#define nlod nlod_o
#define n3k n3k_o
#define safeNorm safeNorm_o
#define fbm fbm_o
#define rot2 rot2_o
#define billow billow_o
#define mesoCentre mesoCentre_o
#define mesoExtra mesoExtra_o
#define baseFlare baseFlare_o
#define mesoShoulder mesoShoulder_o
#define underbellyRise underbellyRise_o
#define vaultAngle vaultAngle_o
#define baseLift baseLift_o
#define horseshoeArc horseshoeArc_o
#define clearSlot clearSlot_o
#define tailDir tailDir_o
#define tailRoot tailRoot_o
#define tailHP tailHP_o
#define tailSize tailSize_o
#define tailAmt tailAmt_o
#define tailCloudAim tailCloudAim_o
#define inTailCloud inTailCloud_o
#define beaverTail beaverTail_o
#define beaverTailBand beaverTailBand_o
#define wallNoise wallNoise_o
#define nearField nearField_o
#include "Packages/com.stormlab.unity/Runtime/Rendering/Shaders/StormCommon.hlsl"
#undef _SP
#undef _Noise
#undef _WallP
#undef sampler_LinearClamp
#undef sampler_LinearRepeat
#undef ss
#undef sunT
#undef sunReach
#undef n3
#undef g_fp
#undef nlod
#undef n3k
#undef safeNorm
#undef fbm
#undef rot2
#undef billow
#undef mesoCentre
#undef mesoExtra
#undef baseFlare
#undef mesoShoulder
#undef underbellyRise
#undef vaultAngle
#undef baseLift
#undef horseshoeArc
#undef clearSlot
#undef tailDir
#undef tailRoot
#undef tailHP
#undef tailSize
#undef tailAmt
#undef tailCloudAim
#undef inTailCloud
#undef beaverTail
#undef beaverTailBand
#undef wallNoise
#undef nearField
Texture3D<float4> _SLDens;
// xy volume half-width and height km; zw cloud and rain extinction 1/km.
float4 _SLVolume;
// World origin of the storm frame, in metres.
float4 _SLOrigin;
float _SLActive;

#define SL_OTHERS 3
Texture3D<float4> _SLDensO0, _SLDensO1, _SLDensO2;
// per other storm, nearest first: xyz world origin of its frame m, w its cloud base km
float4 _SLOther[SL_OTHERS];
int _SLOtherCount;

float3 slWorldToVolume(float3 world) { return (world.xzy - _SLOrigin.xzy) * .001; }
float3 slWorldToOther(int i, float3 world) { return (world.xzy - _SLOther[i].xzy) * .001; }
float3 slUVW(float3 p) { return float3(p.xy / (2 * _SLVolume.x) + .5, p.z / _SLVolume.y); }
float slSmooth(float a, float b, float v) { float t = saturate((v-a)/(b-a)); return t*t*(3-2*t); }
bool slInVolume(float3 p) { return all(abs(p.xy) < _SLVolume.x) && p.z >= 0 && p.z < _SLVolume.y; }

// Coarse extinction matching the march's water/ice thresholds and rain, from a volume sample s at p (km); baseKm the
// storm's cloud base, near the low features' density there. It deliberately omits the march's sub-voxel billows: a
// second moving cloud field would disagree with the one drawn.
float2 slMediumFrom(float4 s, float3 p, float baseKm, float near)
{
    float ice = 1-saturate(s.a/.3);
    float water = slSmooth(.26,.4,s.r) * (.4+.6*slSmooth(.4,.75,s.r));
    float iceDensity = slSmooth(.04,.8,s.r*.65)*.55;
    float cloud = lerp(water,iceDensity,slSmooth(0,1,ice));
    if (p.z < baseKm+1.2 && s.b > .05) cloud += slSmooth(.22,.38,near)*.8;
    float rain = s.g*s.g*_SLVolume.w;
    return max(0,float2(cloud*_SLVolume.z,rain));
}
float2 slMedium(float3 p)
{
    if (!slInVolume(p)) return 0;
    float4 s = _SLDens.SampleLevel(sl_LinearClamp, slUVW(p), 0);
    // the low features are drawn analytically in the march; the volume keeps a coarse copy of where they are
    float near = 0;
    [branch] if (p.z < S_base.x+1.2 && s.b > .05) near = nearField(p,0);
    return slMediumFrom(s, p, S_base.x, near);
}
float slExtinction(float3 p) { float2 medium=slMedium(p); return medium.x+medium.y; }
// another storm's medium at p in its own frame: the nearest's low features analytic with its block (nearField_o), any
// further one's from the volume's copy (b holds the square root of their density)
float slExtinctionOther(int i, float3 p)
{
    if (!slInVolume(p)) return 0;
    float4 s;
    if (i == 0) s = _SLDensO0.SampleLevel(sl_LinearClamp, slUVW(p), 0);
    else if (i == 1) s = _SLDensO1.SampleLevel(sl_LinearClamp, slUVW(p), 0);
    else s = _SLDensO2.SampleLevel(sl_LinearClamp, slUVW(p), 0);
    float near = 0;
    [branch] if (p.z < _SLOther[i].w+1.2 && s.b > .05) { if (i == 0) near = nearField_o(p,0); else near = s.b*s.b; }
    float2 medium = slMediumFrom(s, p, _SLOther[i].w, near);
    return medium.x+medium.y;
}
// which: -1 this storm, else the other storm's index
float slExtinctionOf(int which, float3 p)
{
    float e;
    [branch] if (which < 0) e = slExtinction(p); else e = slExtinctionOther(which, p);
    return e;
}

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

// optical depth from a to b (km, in the frame of the storm `which`: -1 this one) through that storm's medium
float slOpticalDepthOf(int which,float3 a,float3 b,int budget,float extinctionScale)
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
        tau+=slExtinctionOf(which,a+direction*(enter+(i+.5)*stepLength))*stepLength*extinctionScale;
        if(tau>14) break;
    }
    return tau;
}
float slOpticalDepth(float3 a,float3 b,int budget,float extinctionScale) { return slOpticalDepthOf(-1,a,b,budget,extinctionScale); }

// optical depth between two world points (m) through this storm and, with others bound, every other storm drawn
float slOpticalDepthWorld(float3 worldA,float3 worldB,int budget,bool others)
{
    float tau=slOpticalDepth(slWorldToVolume(worldA),slWorldToVolume(worldB),budget,1);
    if(!others) return tau;
    [loop] for(int i=0;i<min(_SLOtherCount,SL_OTHERS);i++)
    {
        if(tau>14) break;
        tau+=slOpticalDepthOf(i,slWorldToOther(i,worldA),slWorldToOther(i,worldB),budget,1);
    }
    return tau;
}
#endif

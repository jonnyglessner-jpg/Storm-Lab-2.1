// The storm model's sky after sunset, added to the camera's sky pixels (StormTwilightSky.cs): the march's skyRad
// (StormMarch.compute; SkyLighting.SkyRad is its C# copy) for the sun alone, times the share of its light HDRP's sky
// no longer shows. Only where nothing has written depth (the far plane), before the storm is composited, so the storm
// dims it as it dims HDRP's sky.
Shader "Hidden/StormLab/TwilightSky"
{
    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    // the camera's forward, right x tan(half width) and up x tan(half height), in the storm's frame (x east, y north, z up)
    float4 _TwFwd, _TwRight, _TwUp;
    // xyz the sun's direction (storm frame), w the sky's level (skyK)
    float4 _TwSun;
    // rgb the warm horizon's colour x its strength (warm x glowK), w the sky dome's dimming (dim)
    float4 _TwWarm;
    // x luminance (nits a storm unit) x the share to add, y viewport width, z height
    float4 _TwP;
    struct V { float4 pos : SV_Position; };
    V vert(uint id : SV_VertexID)
    {
        V o;
        float2 p = float2((id << 1) & 2, id & 2);
        o.pos = float4(p * 2.0 - 1.0, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
        return o;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "StormTwilightSky"
            // the far plane passes only where nothing was drawn: the sky
            ZTest LEqual ZWrite Off Cull Off
            Blend One One, Zero One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(V i) : SV_Target
            {
                float2 ndc = float2(i.pos.x / _TwP.y * 2.0 - 1.0, 1.0 - i.pos.y / _TwP.z * 2.0);
                float3 d = normalize(_TwFwd.xyz + ndc.x * _TwRight.xyz + ndc.y * _TwUp.xyz);
                // (skyRad, without its town light: that is not the sun's)
                float skyK = _TwSun.w;
                float el = saturate(d.z), cg = dot(d, _TwSun.xyz);
                float spread = lerp(2.0, 3.0, saturate(skyK));
                // (as shown, the pale horizon turns to the dome's blue as the sky's level falls, and is darker on the side
                // away from the sun, the earth's shadow: the model's stays a pale grey all round until the sun is 3.4
                // degrees down, which lit as a sky is fine and seen as one was a white strip under the storm at dusk)
                float tw = 1.0 - saturate(skyK);
                float3 pale = lerp(float3(0.78, 0.8, 0.84), float3(0.42, 0.54, 0.84), tw) * lerp(1.0, 0.55, tw * saturate(0.5 - 0.5 * cg));
                float3 hz = lerp(pale * lerp(0.6, 1.0, saturate(skyK)) * _TwWarm.w, _TwWarm.rgb, pow(max(cg, 0.0), spread) * 0.8);
                float up = 1.0 - pow(1.0 - el, 3.0);
                float3 c = hz * (1.0 - up) + float3(0.33, 0.47, 0.72) * skyK * up
                         + 0.5 * pow(max(cg, 0.0), 24.0) * _TwWarm.rgb / float3(0.95, 0.82, 0.66) * float3(1.0, 0.85, 0.6);
                return float4(min(c * (_TwP.x * GetCurrentExposureMultiplier()), 60000.0), 0.0);
            }
            ENDHLSL
        }
    }
}

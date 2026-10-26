// Composites the storm (rgb = light the storm adds, a = transmittance) over the camera colour: out = rgb x scale + a x
// scene, scale 1 for the standalone look or the skin's luminance x the camera's exposure (HDRP's colour buffer holds
// pre-exposed physical light). Pass 1 writes the storm's depth where it hides more than half of what is behind it, at
// the distance along the ray where it starts to (the march's _OutHalf), so what shows in front of it stays in front.
Shader "Hidden/StormLab/Composite"
{
    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    Texture2D _StormTex; Texture2D<float> _StormDist;
    SamplerState sampler_LinearClamp_StormComposite, sampler_PointClamp_StormComposite;
    // x, y viewport size; z 1 to flip rows (the march's run bottom-up); w luminance (0: the standalone look)
    float4 _StormView;
    // tangent of the half field of view, horizontal and vertical
    float4 _StormTan;
    struct V { float4 pos : SV_Position; };
    V vert(uint id : SV_VertexID)
    {
        V o;
        float2 p = float2((id << 1) & 2, id & 2);
        o.pos = float4(p * 2.0 - 1.0, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
        return o;
    }
    float2 StormUV(float4 pos)
    {
        float2 uv = pos.xy / _StormView.xy;
        if (_StormView.z > 0.5) uv.y = 1.0 - uv.y;
        return uv;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "StormComposite"
            ZTest Always ZWrite Off Cull Off
            // colour: storm + transmittance x scene; alpha (HDRP's alpha output, if on) is left as it was
            Blend One SrcAlpha, Zero One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(V i) : SV_Target
            {
                float4 s = _StormTex.SampleLevel(sampler_LinearClamp_StormComposite, StormUV(i.pos), 0);
                float k = _StormView.w > 0.0 ? _StormView.w * GetCurrentExposureMultiplier() : 1.0;
                // (capped below the half-float limit: a value past it is infinite in HDRP's buffers and spreads)
                return float4(min(s.rgb * k, 60000.0), s.a);
            }
            ENDHLSL
        }
        Pass
        {
            Name "StormDepth"
            ZTest LEqual ZWrite On Cull Off ColorMask 0
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(V i, out float depth : SV_Depth) : SV_Target
            {
                float2 uv = StormUV(i.pos);
                float T = _StormTex.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0).a;
                clip(0.5 - T);
                // distance along the ray (km) to eye depth (m), then to the device depth HDRP's buffer holds
                float2 ndc = uv * 2.0 - 1.0;
                float cosRay = rsqrt(1.0 + dot(ndc * _StormTan.xy, ndc * _StormTan.xy));
                float d = _StormDist.SampleLevel(sampler_PointClamp_StormComposite, uv, 0);
                // only the storm's own cloud: the standalone backdrop is opaque too, but it is the sky, not the storm
                clip(d);
                float eye = max(d * 1000.0 * cosRay, _ProjectionParams.y);
                depth = (1.0 / eye - _ZBufferParams.w) / _ZBufferParams.z;
                return 0;
            }
            ENDHLSL
        }
    }
}

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
    // the storm composited just before this one, to lay this one behind it where it is nearer: its march (rgb, a =
    // transmittance), both storms' mean distances along the ray (km); x 1 when there is one, y its luminance (0: standalone)
    Texture2D _UnderTex; Texture2D<float> _StormMean, _UnderMean;
    float4 _UnderP;
    // light shafts kept apart in the marches (StormMarch.compute _OutShaft), added over the storms: this storm's (x), the
    // storm before's (y; z its luminance, 0: standalone)
    Texture2D _ShaftTex, _UnderShaftTex;
    float4 _ShaftP;
    // ---- the game's composite of several storms at once (pass 2, StormRenderer.RecordCompositeAll; user, 2026-11-07:
    // "the anvils do not mix at all when there are more than two storms"). Each storm was laid against only the one
    // composited just before it, which holds for two storms; with three or more the target already held a third storm and
    // the layering was wrong, so overlapping anvils showed one storm's edges cut through another's. Here every storm's
    // light is dimmed by every other's transmittance as far as that other is in front of it along the ray: a soft order,
    // w_ij = smoothstep(-kd, kd, d_i - d_j) (the same few km as the pair's), each storm's light the mixture of the two
    // orders, lerp(1, T_j, w_ij) (exact for two storms; T_j^w_ij turned two opaque anvils at one distance black), so storms
    // whose media interleave (two anvils at one height) blend into one deck, with no line where the order flips.
    // out = sum_i C_i prod_j!=i lerp(1, T_j, w_ij) + scene prod_i T_i.
    Texture2D _NTex0, _NTex1, _NTex2, _NTex3;
    Texture2D<float> _NMean0, _NMean1, _NMean2, _NMean3;
    Texture2D _NShaft0, _NShaft1, _NShaft2, _NShaft3;
    float4 _NK;       // each storm's luminance scale (0: standalone, 1)
    float4 _NShaftOn; // 1 where that storm's shafts are kept apart
    float4 _NP;       // x: how many storms (2-4)
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
                float3 rgb = s.rgb * k;
                if (_UnderP.x > 0.5)
                {
                    // the storm composited before this one (target = u.rgb + u.a x scene): where it is the nearer, put this
                    // one behind it, u.rgb + u.a x (rgb + s.a x scene) = target x s.a + u.rgb x (1 - s.a) + u.a x rgb.
                    // Blended over a few km of their mean distances, so the order never flips on a hard line: 3 km and a
                    // quarter of the nearer's distance (a storm's distance along the ray jumps where its haze or rain starts,
                    // and a flip within 3 km drew hard-edged flat patches 20-40 km off; user, 2026-10-28)
                    float2 uv = StormUV(i.pos);
                    float4 u = _UnderTex.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0);
                    float ku = _UnderP.y > 0.0 ? _UnderP.y * GetCurrentExposureMultiplier() : 1.0;
                    float dThis = _StormMean.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0);
                    float dUnder = _UnderMean.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0);
                    float kd = 3.0 + 0.25 * min(dThis, dUnder);
                    float behind = smoothstep(-kd, kd, dThis - dUnder);
                    rgb = lerp(rgb, min(u.rgb * ku, 60000.0) * (1.0 - s.a) + u.a * rgb, behind);
                }
                if (_ShaftP.x > 0.5) rgb += _ShaftTex.SampleLevel(sampler_LinearClamp_StormComposite, StormUV(i.pos), 0).rgb * k;
                if (_ShaftP.y > 0.5)
                {
                    float ks = _ShaftP.z > 0.0 ? _ShaftP.z * GetCurrentExposureMultiplier() : 1.0;
                    rgb += _UnderShaftTex.SampleLevel(sampler_LinearClamp_StormComposite, StormUV(i.pos), 0).rgb * ks;
                }
                // (capped below the half-float limit: a value past it is infinite in HDRP's buffers and spreads)
                return float4(min(rgb, 60000.0), s.a);
            }
            ENDHLSL
        }
        Pass
        {
            Name "StormCompositeN"
            ZTest Always ZWrite Off Cull Off
            Blend One SrcAlpha, Zero One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment fragN
            float4 fragN(V i) : SV_Target
            {
                float2 uv = StormUV(i.pos);
                int n = (int)_NP.x;
                float4 c[4]; float d[4]; float3 sh[4];
                c[0] = _NTex0.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0); d[0] = _NMean0.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0);
                c[1] = _NTex1.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0); d[1] = _NMean1.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0);
                c[2] = n > 2 ? _NTex2.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0) : float4(0, 0, 0, 1); d[2] = n > 2 ? _NMean2.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0) : 1e6;
                c[3] = n > 3 ? _NTex3.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0) : float4(0, 0, 0, 1); d[3] = n > 3 ? _NMean3.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0) : 1e6;
                sh[0] = _NShaftOn.x > 0.5 ? _NShaft0.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0).rgb : 0.0;
                sh[1] = _NShaftOn.y > 0.5 ? _NShaft1.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0).rgb : 0.0;
                sh[2] = _NShaftOn.z > 0.5 ? _NShaft2.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0).rgb : 0.0;
                sh[3] = _NShaftOn.w > 0.5 ? _NShaft3.SampleLevel(sampler_LinearClamp_StormComposite, uv, 0).rgb : 0.0;
                float exposure = GetCurrentExposureMultiplier();
                float k[4] = { _NK.x, _NK.y, _NK.z, _NK.w };
                float3 rgb = 0.0; float T = 1.0;
                [unroll] for (int a = 0; a < 4; a++)
                {
                    if (a >= n) break;
                    float ka = k[a] > 0.0 ? k[a] * exposure : 1.0;
                    float att = 1.0;
                    [unroll] for (int b = 0; b < 4; b++)
                    {
                        if (b >= n || b == a) continue;
                        float kd = 3.0 + 0.25 * min(d[a], d[b]);
                        float front = smoothstep(-kd, kd, d[a] - d[b]);   // how far b is in front of a
                        att *= lerp(1.0, c[b].a, front);
                    }
                    rgb += min(c[a].rgb * ka, 60000.0) * att + sh[a] * ka;
                    T *= c[a].a;
                }
                return float4(min(rgb, 60000.0), T);
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

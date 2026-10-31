Shader "StormLab/Lightning"
{
    Properties { [HDR] _Color ("Emission", Color) = (6,7,10,1) }
    SubShader
    {
        Tags { "RenderPipeline"="HDRenderPipeline" "Queue"="Transparent+20" "RenderType"="Transparent" }
        Pass
        {
            // Drawn by LightningSystem's own custom pass (ChannelPass, BeforePostProcess), after the storm: HDRP draws
            // every transparent before the BeforeTransparent custom passes when Refraction is off in the frame settings,
            // and the storm then composited its transmittance over the channels on top of their own occlusion.
            Tags { "LightMode"="StormLightning" }
            Blend SrcAlpha One
            ZWrite Off
            // No depth test: the storm writes its cloud's depth where it is opaque (StormComposite's StormDepth pass),
            // and a test against it would cut a channel wherever it is seen against cloud. Cloud and rain hide a channel
            // by its optical depth below; a bolt behind a near building or tree shows through it for the frames it lasts.
            ZTest Always
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma only_renderers d3d11 playstation xboxone xboxseries vulkan metal switch
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            #include "Packages/com.stormlab.unity/Runtime/Lightning/Shaders/StormLightningMedium.hlsl"
            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; float3 world : TEXCOORD1; };
            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
            CBUFFER_END
            // the storm's light scale (StormSkin): x luminance of a unit (0: none set, 1), y 1 to apply the camera's exposure
            float4 _StormLabLight;
            Varyings Vert(Attributes v)
            {
                Varyings o;
                float3 rws = TransformObjectToWorld(v.positionOS.xyz);   // camera-relative
                o.positionCS = TransformWorldToHClip(rws); o.uv = v.uv; o.color = v.color;
                o.world = GetAbsolutePositionWS(rws); return o;
            }
            half4 Frag(Varyings i) : SV_Target
            {
                float across = abs(i.uv.y * 2 - 1);
                // One narrow radiating core within a broad, dim optical halo. The CPU sizes the
                // ribbon per camera, keeping the core sampled even at long viewing distances. The halo is a
                // small fraction of the core, which is hundreds of times a sunlit cloud (LightningSystem.emission).
                float core = exp2(-across * across * 36);
                float halo = exp2(-across * across * 10) * .008;
                float profile = (core + halo) * (1 - smoothstep(.8, 1, across));
                half3 tint = lerp(half3(.56, .66, 1), half3(1, .97, 1), core);
                float visibility = 1;
                if (_SLActive > .5)
                    visibility = exp(-slOpticalDepth(slWorldToVolume(_WorldSpaceCameraPos),slWorldToVolume(i.world),384,1));
                float scale = (_StormLabLight.x > 0 ? _StormLabLight.x : 1) * (_StormLabLight.y > .5 ? GetCurrentExposureMultiplier() : 1);
                return half4(_Color.rgb * i.color.rgb * tint * visibility * scale, profile * _Color.a * i.color.a);
            }
            ENDHLSL
        }
    }
}

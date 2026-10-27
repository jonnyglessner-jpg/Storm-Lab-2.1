// The minimap's drawing (TestMapMinimap, GL in OnGUI): vertex colour times a texture, with the base map's grade
// (_Grade x: how much colour is taken out, y: brightness).
Shader "Hidden/StormLab/TestMapMapDraw"
{
    Properties { _MainTex ("Texture", 2D) = "white" {} }
    SubShader
    {
        ZTest Always ZWrite Off Cull Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Grade;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 col : COLOR; };
            v2f vert(appdata_full v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                o.col = v.color;
                return o;
            }
            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                float l = dot(c.rgb, float3(0.3, 0.59, 0.11));
                c.rgb = lerp(c.rgb, l.xxx, _Grade.x) * _Grade.y;
                return c * i.col;
            }
            ENDCG
        }
    }
}

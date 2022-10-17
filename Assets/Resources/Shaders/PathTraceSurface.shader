Shader "Unlit/PathTraceSurface"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            sampler2D _PathTraceTexture;
            float _PathTraceDownscaleFactor;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                UNITY_FOG_COORDS(1)
                float4 vertex : SV_POSITION;
                float4 screenPos : TEXCOORD0;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.screenPos = ComputeScreenPos(o.vertex);
                UNITY_TRANSFER_FOG(o,o.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3x3 gaussianKernel = {
                    0.075, 0.124, 0.075,
                    0.124, 0.204, 0.124,
                    0.075, 0.124, 0.075
                };
                fixed4 col = 0;
                const float2 screenUV = i.screenPos / i.screenPos.w;
                // TODO: adjust this based on normal
                float dx = _PathTraceDownscaleFactor / _ScreenParams.x;
                float dy = _PathTraceDownscaleFactor / _ScreenParams.y;
                col += tex2D(_PathTraceTexture, screenUV) * gaussianKernel._m11;
                col += tex2D(_PathTraceTexture, screenUV + float2(-dx, -dy)) * gaussianKernel._m00;
                col += tex2D(_PathTraceTexture, screenUV + float2(-dx, 0)) * gaussianKernel._m01;
                col += tex2D(_PathTraceTexture, screenUV + float2(-dx, dy)) * gaussianKernel._m02;
                col += tex2D(_PathTraceTexture, screenUV + float2(0, dy)) * gaussianKernel._m12;
                col += tex2D(_PathTraceTexture, screenUV + float2(dx, dy)) * gaussianKernel._m22;
                col += tex2D(_PathTraceTexture, screenUV + float2(dx, 0)) * gaussianKernel._m21;
                col += tex2D(_PathTraceTexture, screenUV + float2(dx, -dy)) * gaussianKernel._m20;
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDCG
        }
    }
}

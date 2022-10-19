Shader "Unlit/PathTraceSurface"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog

            #include "UnityCG.cginc"
            #include "Assets/Resources/Shaders/Includes/Interpolators.hlsl"
            
            int _PathTraceInterpolationType;
            
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
                const float2 screenUV = i.screenPos / i.screenPos.w;
                // TODO: adjust this based on normal
                float dx = _PathTraceDownscaleFactor / _ScreenParams.x;
                float dy = _PathTraceDownscaleFactor / _ScreenParams.y;
                fixed4 col;
                switch (_PathTraceInterpolationType)
                 {
                     case 1:
                         {
                             col = _PathTraceTexture.Sample(linear_clamp_sampler, screenUV);
                             break;
                         }
                     case 2:
                         {
                             col = BicubicHermiteSample(screenUV);
                             break;
                         }
                     case 3:
                         {
                             col = LanczosSample(screenUV+ float2(dx,dy) * 0.5);
                             break;
                         }
                     default:
                         {
                             col = _PathTraceTexture.Sample(point_clamp_sampler, screenUV + float2(dx,dy) * 0.5);
                             break;
                         }
                }
                
                UNITY_APPLY_FOG(i.fogCoord, col);
                return col;
            }
            ENDHLSL
        }
    }
}

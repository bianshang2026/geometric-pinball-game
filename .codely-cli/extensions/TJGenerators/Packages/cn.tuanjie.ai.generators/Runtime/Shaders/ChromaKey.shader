Shader "TJGenerators/ChromaKey"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _ChromaTolerance ("Tolerance", Range(0.01, 0.5)) = 0.16
        _ChromaFeather ("Feather", Range(0.0, 0.5)) = 0.04
        _SpillRemoval ("Spill Removal", Range(0.0, 1.0)) = 0.7
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        LOD 100
        Cull Off ZWrite Off Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _ChromaTolerance;
            float _ChromaFeather;
            float _SpillRemoval;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            // HSV conversion
            float3 RGBtoHSV(float3 c)
            {
                float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
                float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
                float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
                float d = q.x - min(q.w, q.y);
                float e = 1.0e-10;
                return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);

                float3 hsv = RGBtoHSV(col.rgb);
                float hue = hsv.x;
                float sat = hsv.y;
                float val = hsv.z;

                // Green hue is around 1/3 (120 degrees)
                float hueDist = abs(hue - 1.0 / 3.0);
                hueDist = min(hueDist, 1.0 - hueDist);

                float hueGate = saturate(1.0 - hueDist / lerp(0.22, 0.08, saturate(_ChromaTolerance * 2.0)));
                float satGate = saturate((sat - 0.12) / 0.35);
                float lumGate = saturate((val - 0.08) / 0.25);
                float dominance = saturate((col.g - max(col.r, col.b) - 0.01) / max(0.02, _ChromaTolerance));
                float similarity = 1.0 - distance(col.rgb, float3(0, 1, 0)) / 1.73205;
                similarity = saturate(similarity);

                float key = hueGate * satGate * lumGate * dominance * similarity;
                float soften = max(0.001, _ChromaFeather * 2.0 + 0.015);
                key = smoothstep(0.0, 1.0, saturate((key - 0.08) / soften));

                col.a *= (1.0 - key);

                // Spill removal: reduce green in semi-transparent areas
                if (col.a > 0.001)
                {
                    float maxRb = max(col.r, col.b);
                    float despill = key * _SpillRemoval * saturate(1.0 - col.a);
                    col.g = lerp(col.g, maxRb, despill);
                }

                return col;
            }
            ENDCG
        }
    }
}

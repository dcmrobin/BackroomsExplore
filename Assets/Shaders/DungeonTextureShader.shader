Shader "Custom/DungeonTextureShader"
{
    Properties
    {
        // Textures
        _WallTex ("Wall Texture", 2D) = "white" {}
        _FloorTex ("Floor Texture", 2D) = "white" {}
        _CeilingTex ("Ceiling Texture", 2D) = "white" {}
        
        // Basic lighting
        _Ambient ("Ambient Light", Range(0, 1)) = 0.15
        _MaxLight ("Max Light Level", Range(0, 1)) = 1.0
        
        // Texture Tiling
        _TextureScale ("Texture Scale", Vector) = (1, 1, 0, 0)
        
        // Smooth lighting parameters
        _LightSmoothness ("Light Smoothness", Range(0, 1)) = 0.5
        _LightContrast ("Light Contrast", Range(0.5, 2)) = 1.0
    }
    
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
            #pragma multi_compile _ SMOOTH_LIGHTING_ON
            
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float fogCoord : TEXCOORD1;
                float4 vertex : SV_POSITION;
            };

            sampler2D _WallTex;
            sampler2D _FloorTex;
            sampler2D _CeilingTex;
            
            float _Ambient;
            float _MaxLight;
            float2 _TextureScale;
            
            float _LightSmoothness;
            float _LightContrast;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                UNITY_TRANSFER_FOG(o, o.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float materialIDFloat = i.color.r * 3.0;
                int materialID = (int)round(materialIDFloat);
                float voxelLight = i.color.g;
                
                fixed4 texColor;
                if (materialID == 1)
                {
                    texColor = tex2D(_FloorTex, i.uv * _TextureScale);
                }
                else if (materialID == 2)
                {
                    texColor = tex2D(_CeilingTex, i.uv * _TextureScale);
                }
                else if (materialID == 3)
                {
                    texColor = fixed4(1, 1, 0.9, 1);
                }
                else
                {
                    texColor = tex2D(_WallTex, i.uv * _TextureScale);
                }
                
                #ifdef SMOOTH_LIGHTING_ON
                    float smoothLight = smoothstep(0, 1, voxelLight);
                    smoothLight = lerp(voxelLight, smoothLight, _LightSmoothness);
                    smoothLight = pow(smoothLight, _LightContrast);
                    
                    float totalLight = _Ambient + smoothLight * _MaxLight;
                #else
                    float totalLight = _Ambient + voxelLight * _MaxLight;
                #endif
                
                totalLight = saturate(totalLight);
                
                fixed4 finalColor = texColor;
                finalColor.rgb *= totalLight;
                
                if (materialID == 3)
                {
                    finalColor.rgb = texColor.rgb * 2.0;
                    
                    float flicker = sin(_Time.y * 3.0 + i.uv.x * 5.0) * 0.05 + 0.95;
                    finalColor.rgb *= flicker;
                }
                
                UNITY_APPLY_FOG(i.fogCoord, finalColor);
                
                return finalColor;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}
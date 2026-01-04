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
            
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR; // Material ID in R, Light level in G
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float fogCoord : TEXCOORD1;
                float4 vertex : SV_POSITION;
            };

            // Textures
            sampler2D _WallTex;
            sampler2D _FloorTex;
            sampler2D _CeilingTex;
            
            // Lighting properties
            float _Ambient;
            float _MaxLight;
            float2 _TextureScale;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv * _TextureScale;
                o.color = v.color;
                UNITY_TRANSFER_FOG(o, o.vertex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Decode vertex color data
                int materialID = (int)(i.color.r * 255.0);
                float voxelLight = i.color.g; // Pre-calculated light level for this voxel
                
                // Select base texture
                fixed4 texColor;
                if (materialID == 1) // Floor
                {
                    texColor = tex2D(_FloorTex, i.uv);
                }
                else if (materialID == 2) // Ceiling
                {
                    texColor = tex2D(_CeilingTex, i.uv);
                }
                else if (materialID == 3) // Light source
                {
                    texColor = fixed4(1, 1, 0.9, 1); // Warm white for light sources
                }
                else // Wall (default or materialID == 0)
                {
                    texColor = tex2D(_WallTex, i.uv);
                }
                
                // Minecraft-style lighting: ambient + voxel light
                float totalLight = _Ambient + voxelLight * _MaxLight;
                totalLight = saturate(totalLight);
                
                // Apply lighting
                fixed4 finalColor = texColor;
                finalColor.rgb *= totalLight;
                
                // Light sources glow
                if (materialID == 3)
                {
                    // Bright glow for light sources
                    finalColor.rgb = texColor.rgb * 2.0;
                    
                    // Subtle flicker
                    float flicker = sin(_Time.y * 3.0 + i.uv.x * 5.0) * 0.05 + 0.95;
                    finalColor.rgb *= flicker;
                }
                
                // Fog
                UNITY_APPLY_FOG(i.fogCoord, finalColor);
                
                return finalColor;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}
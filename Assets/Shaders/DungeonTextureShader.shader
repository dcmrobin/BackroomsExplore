Shader "Custom/DungeonTextureShader"
{
    Properties
    {
        _WallTex ("Wall Texture", 2D) = "white" {}
        _FloorTex ("Floor Texture", 2D) = "white" {}
        _CeilingTex ("Ceiling Texture", 2D) = "white" {}
        _TextureScale ("Texture Scale", Vector) = (1, 1, 0, 0)
        _AmbientLight ("Ambient Light", Range(0, 1)) = 0.3
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
                float3 normal : NORMAL;
                float4 color : COLOR; // Contains material ID in red channel
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float fogCoord : TEXCOORD1;
                float3 normal : TEXCOORD2;
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
            };

            sampler2D _WallTex;
            sampler2D _FloorTex;
            sampler2D _CeilingTex;
            float4 _WallTex_ST;
            float4 _FloorTex_ST;
            float4 _CeilingTex_ST;
            float2 _TextureScale;
            float _AmbientLight;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv * _TextureScale; // Apply texture scaling
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.color = v.color;
                
                // Fog
                UNITY_TRANSFER_FOG(o, o.vertex);
                
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Decode material ID from vertex color (red channel)
                int materialID = (int)(i.color.r * 255);
                
                fixed4 col;
                
                // Select texture based on material ID
                if (materialID == 1) // Floor
                {
                    col = tex2D(_FloorTex, i.uv);
                }
                else if (materialID == 2) // Ceiling
                {
                    col = tex2D(_CeilingTex, i.uv);
                }
                else // Wall (default or materialID == 0)
                {
                    col = tex2D(_WallTex, i.uv);
                }
                
                // Simple directional lighting based on normals
                float3 lightDir = normalize(float3(0.3, 1, 0.2)); // Directional light
                float NdotL = max(dot(normalize(i.normal), lightDir), 0);
                float lighting = _AmbientLight + (1 - _AmbientLight) * NdotL;
                
                col.rgb *= lighting;
                
                // Apply fog
                UNITY_APPLY_FOG(i.fogCoord, col);
                
                return col;
            }
            ENDCG
        }
    }
    FallBack "Diffuse"
}
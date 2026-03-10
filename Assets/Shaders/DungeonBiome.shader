Shader "Dungeon/BiomeSurface"
{
    Properties
    {
        _WallTex       ("Wall Texture",       2D)           = "white" {}
        _FloorTex      ("Floor Texture",      2D)           = "white" {}
        _CeilTex       ("Ceiling Texture",    2D)           = "white" {}
        _LightTint     ("Light Tint",         Color)        = (1,1,1,1)
        _LightColor    ("Light Source Color", Color)        = (1,0.95,0.7,1)

        // --- Lighting controls (all tweakable in the Material inspector) ---
        _Ambient       ("Ambient",            Range(0,1))   = 0.08
        _MaxLight      ("Max Light",          Range(0,1))   = 1.0
        // Gamma curve on the baked light value.
        // < 1 lifts shadows (brighter overall), > 1 deepens them (moodier).
        _LightGamma    ("Light Gamma",        Range(0.3,2)) = 0.85
        _LightEmissive ("Light Emissive",     Range(0,2))   = 1.2

        // --- Triplanar controls ---
        _TriplanarScale("Triplanar Scale",    Float)        = 0.05
        _BlendSharp    ("Blend Sharpness",    Range(1,8))   = 4.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Lambert vertex:vert finalcolor:ApplyFog
        #pragma target 3.0
        #pragma multi_compile_fog

        #include "UnityCG.cginc"

        sampler2D _WallTex;
        sampler2D _FloorTex;
        sampler2D _CeilTex;
        fixed4    _LightTint;
        fixed4    _LightColor;
        float     _Ambient;
        float     _MaxLight;
        float     _LightGamma;
        float     _LightEmissive;
        float     _TriplanarScale;
        float     _BlendSharp;

        struct Input
        {
            float3 worldPos;
            float3 worldNormal;
            fixed4 color : COLOR;
            UNITY_FOG_COORDS(0)
        };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.worldPos    = mul(unity_ObjectToWorld, v.vertex).xyz;
            o.worldNormal = UnityObjectToWorldNormal(v.normal);
            o.color       = v.color;
            // Compute fog factor from clip position
            float4 clipPos = UnityObjectToClipPos(v.vertex);
            UNITY_TRANSFER_FOG(o, clipPos);
        }

        // finalcolor callback — applies Unity fog after surf runs
        void ApplyFog(Input IN, SurfaceOutput o, inout fixed4 color)
        {
            UNITY_APPLY_FOG(IN.fogCoord, color);
        }

        fixed4 TriplanarSample(sampler2D tex, float3 wpos, float3 blend, float scale)
        {
            fixed4 xSample = tex2D(tex, wpos.yz * scale);
            fixed4 ySample = tex2D(tex, wpos.xz * scale);
            fixed4 zSample = tex2D(tex, wpos.xy * scale);
            return xSample * blend.x + ySample * blend.y + zSample * blend.z;
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            // --- Triplanar blend weights ---
            float3 blend = pow(abs(IN.worldNormal), _BlendSharp);
            blend /= (blend.x + blend.y + blend.z + 0.0001);

            // --- Material ID from vertex colour red channel ---
            // 0=wall  0.333=floor  0.667=ceiling  1=light emitter
            float matID = IN.color.r * 3.0;
            float isFloor   = step(0.9, matID) * (1.0 - step(1.9, matID));
            float isCeiling = step(1.9, matID) * (1.0 - step(2.9, matID));
            float isLight   = step(2.9, matID);
            float isWall    = 1.0 - isFloor - isCeiling - isLight;

            // --- Triplanar texture sampling ---
            fixed4 wallCol  = TriplanarSample(_WallTex,  IN.worldPos, blend, _TriplanarScale);
            fixed4 floorCol = TriplanarSample(_FloorTex, IN.worldPos, blend, _TriplanarScale);
            fixed4 ceilCol  = TriplanarSample(_CeilTex,  IN.worldPos, blend, _TriplanarScale);

            fixed4 texColor = wallCol  * isWall
                            + floorCol * isFloor
                            + ceilCol  * isCeiling
                            + _LightColor * isLight;

            // --- Light level from green vertex channel ---
            // Formula matches your original shader: Ambient + voxelLight * MaxLight
            // with an optional gamma curve to shape the falloff.
            float voxelLight = IN.color.g;
            float litLevel   = pow(voxelLight, _LightGamma);
            float totalLight = saturate(_Ambient + litLevel * _MaxLight);

            // Light emitter voxels glow with a flicker
            float flicker = isLight * (sin(_Time.y * 3.0 + IN.worldPos.x * 5.0) * 0.05 + 0.95);

            o.Albedo   = texColor.rgb * _LightTint.rgb * totalLight;
            o.Emission = texColor.rgb * (isLight * _LightEmissive) * (0.95 + flicker * 0.05);
            o.Alpha    = 1.0;
        }
        ENDCG
    }

    Fallback "Diffuse"
}

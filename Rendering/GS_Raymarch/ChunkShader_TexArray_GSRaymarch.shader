Shader "Voxelis/ChunkShader_TexArray_GSRaymarch"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTexArr("Textures", 2DArray) = "" {}
        _BlockLUT("Block LookUp Tex", 2D) = "white" {}
        _FSTex("Fine Stuctures Tex", 3D) = "white" {}
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        _Metallic ("Metallic", Range(0,1)) = 0.0
    }
    SubShader
    {
        //Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" }
        Tags { "RenderType"="Opaque" }
        LOD 200

        // TODO: change to Unlit
        //Cull Off

        CGPROGRAM
        // Physically based Standard lighting model, and enable shadows on all light types
        #pragma surface surf Standard addshadow vertex:vert
        //#pragma surface surf Standard fullforwardshadows addshadow vertex:vert
        #include "../../Data/Block.cginc"
        #include "UnityCG.cginc"

        #pragma target 5.0
        #pragma require 2darray

        struct appdata
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            fixed4 color : COLOR;
            float2 texcoord : TEXCOORD0;
            //float4 texcoord2 : TEXCOORD2;
            uint vid : SV_VertexID;
        };

        struct Input
        {
            float3 blockSpacePos;
            float3 viewDir;

            float3 arrayUV;
            fixed4 color : COLOR;
        };

        struct cs_Vertex
        {
            float3 position;
            float3 normal;
            float2 uv;
            int block_vert_info;
            Block data;
        };

        // Ray march use
        float blockSize;
        float3 FStexGridSize;

        half _Glossiness;
        half _Metallic;
        fixed4 _Color;

        float4x4 _LocalToWorld;
        float4x4 _WorldToLocal;

        sampler2D _BlockLUT;
        sampler3D _FSTex;
        UNITY_DECLARE_TEX2DARRAY(_MainTexArr);

#ifdef SHADER_API_D3D11
        uniform StructuredBuffer<cs_Vertex> cs_vbuffer;
#endif

        void vert(inout appdata v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);

#ifdef SHADER_API_D3D11
            v.vertex = float4(cs_vbuffer[v.vid].position, 1.0f);// +float4(2.5f * sin(_Time.x * 5.0 + cs_vbuffer[v.vid].position.x * 0.3f + cs_vbuffer[v.vid].position.y * 0.15f - cs_vbuffer[v.vid].position.z * 1.3f), 0, 0, 0);
            v.normal = cs_vbuffer[v.vid].normal;
            o.blockSpacePos = float3(
                (cs_vbuffer[v.vid].block_vert_info & 4) / 4, 
                (cs_vbuffer[v.vid].block_vert_info & 2) / 2,
                (cs_vbuffer[v.vid].block_vert_info & 1) / 1);
            
            // Find block ID and LUT values
            Block blk = cs_vbuffer[v.vid].data;
            uint fid = GetBlockID(blk);
            float4 lut = tex2Dlod(_BlockLUT, float4(float(fid / 256) / 256.0, float(fid % 256) / 256.0, 0, 0));
            uint _a = asuint(lut.a); // float interpreted as uint (get bits)

            // Check if it is FS16 - if so we need use RayMarch so no need to calculate UV / colors
            if (((_a >> 22) & (0x7)) == 4) // FINE_16 = 4
            {
                // "default value"
                v.color = fixed4(1, 0, 0, 1);
                v.texcoord = float2(0, 0);
                o.arrayUV = float3(cs_vbuffer[v.vid].uv, -1.0f); // Make uv.z < 0 to tell Frag
            }
            else // Regular cubes
            {
                uint meta = GetBlockMeta(blk);
                v.color = (GetBlockID(blk) > 0) * fixed4(
                    float((meta >> 12) & (0x000F)) / 15.0,
                    float((meta >> 8) & (0x000F)) / 15.0,
                    float((meta >> 4) & (0x000F)) / 15.0,
                    float((meta) & (0x000F)) / 15.0
                );

                // Calculate UV
                float3 uv_org = clamp(0, 1, lut.rgb);
                float2 uv_step = float2(float((_a >> 11) & 2047) / 2048.0, float(_a & 2047) /
                    2048.0);

                v.texcoord = cs_vbuffer[v.vid].uv;
                o.arrayUV = uv_org + float3(cs_vbuffer[v.vid].uv * uv_step, 0);
                v.color = lerp(v.color, float4(1, 1, 1, 1), (_a & 0x10000000) == 0); // tint or not

                //o.arrayUV = float3(cs_vbuffer[v.vid].uv, 0);
                //v.color = float4(uv_step, 0, 1);
            }
#else
            v.vertex = float4(0.0f, 0.0f, 0.0f, 1.0f);
            v.normal = float3(0.0f, 1.0f, 0.0f);
            v.color = fixed4(1.0, 0.5, 0.0, 1.0);
            v.texcoord = float2(0, 0);
            o.arrayUV = float3(v.texcoord, 0);
#endif
            //v.texcoord1 = float4(0, 0, 0, 0);
            //v.texcoord2 = float4(0, 0, 0, 0);

            // Transform modification
            unity_ObjectToWorld = _LocalToWorld;
            unity_WorldToObject = _WorldToLocal;
        }

        // Add instancing support for this shader. You need to check 'Enable Instancing' on materials that use the shader.
        // See https://docs.unity3d.com/Manual/GPUInstancing.html for more information about instancing.
        // #pragma instancing_options assumeuniformscaling
        UNITY_INSTANCING_BUFFER_START(Props)
            // put more per-instance properties here
        UNITY_INSTANCING_BUFFER_END(Props)

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            // Fine Structures
            if (IN.arrayUV.z < 0)
            {
                // Following http://jojendersie.de/rendering-huge-amounts-of-voxels-2/#comment-400

                // origin in tex3D
                float3 origin = float3(IN.arrayUV.xy, 0);

                // size in tex3D
                float3 size = FStexGridSize;
                float3 step = size / 16.0f; // Fixed for FS16

                // Ray position
                float3 p = IN.blockSpacePos;

                // Ray direction
                //float3 d = mul((float3x3)_WorldToLocal, -IN.viewDir) * step;
                float3 d = -IN.viewDir / 16.0f;
                float3 absd = abs(d);

                int3 dirSign;
                dirSign.x = d.x < 0 ? -1 : 1;
                dirSign.y = d.y < 0 ? -1 : 1;
                dirSign.z = d.z < 0 ? -1 : 1;

                // Initial fetch
                //float v = floor(tex3D(_FSTex, origin + size * p) * 65536);
                //float v = floor(tex3D(_FSTex, origin + float3(0.0001, 0.0001, 0.0001)).a * 65536);
                float v = tex3D(_FSTex, origin + p * size).a;
                //float v = (length(p - float3(0.5, 0.5, 0.5)) - 0.3);

                // Continue only when grid empty
                int i;
                /*if (v > 0) { o.Albedo = float3(0, 1, 0); }
                else { o.Albedo = float3(1, 0, 0); }*/
                o.Albedo = float3(0, 0, 1);
                for(i = 0; i < 64; i++)
                {
                    // Step ray
                    p += d / 1.732f; // lol

                    // Ray has left voxel
                    //if (any(p > float3(1.02, 1.02, 1.02)) || any(p < float3(-0.02, -0.02, -0.02)))
                    if (any(p > float3(1,1,1)) || any(p < float3(0,0,0)))
                    {
                        clip(-1.0);
                        break;
                    }

                    //v = floor(tex3D(_FSTex, origin + size * p).a * 65536);
                    v = tex3D(_FSTex, origin + p * size).a;
                    //v = v = (length(p - float3(0.5, 0.5, 0.5)) - 0.3);

                    if (v > 0)
                    {
                        o.Albedo = float3(0, 1, 0);
                        break;
                    }
                }

                ////////////////////// Raymarch Done //////////////////////

                //o.Albedo = IN.blockSpacePos;
                o.Metallic = _Metallic;
                o.Smoothness = _Glossiness;
                o.Alpha = 1.0f;
            }

            // Regular Blocks
            else 
            {
                float4 tex = UNITY_SAMPLE_TEX2DARRAY(_MainTexArr, IN.arrayUV);
                if (tex.a <= 0.5f) { discard; } // cutoff
                // Albedo comes from a texture tinted by color
                if (IN.color.a <= 0.9f)
                {
                    //o.Emission = 2.0f * (IN.color.a / 0.5f) * float4(IN.color.rgb, 1.5);
                    o.Albedo = IN.color.rgb * tex.rgb;
                }
                else
                {
                    o.Albedo = IN.color.rgb * tex.rgb;
                }
                // Metallic and smoothness come from slider variables
                o.Metallic = _Metallic;
                o.Smoothness = _Glossiness;
                o.Alpha = 1.0f;

                //o.Alpha = c.a;
            }
        }
        ENDCG
    }
    FallBack "Diffuse"
}

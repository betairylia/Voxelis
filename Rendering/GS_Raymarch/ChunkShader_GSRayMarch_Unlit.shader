Shader "Voxelis/ChunkShader_TexArray_GSRayMarch_Unlit"
{
    Properties
    {
        _MainTex("Albedo (RGB)", 2D) = "white" {}

        _Color("Color", Color) = (1,1,1,1)
        _MainTexArr("Textures", 2DArray) = "" {}
        _BlockLUT("Block LookUp Tex", 2D) = "white" {}
        _FSTex("Fine Stuctures Tex", 3D) = "white" {}
        _Glossiness("Smoothness", Range(0,1)) = 0.5
        _Metallic("Metallic", Range(0,1)) = 0.0
    }
    SubShader
    {

        Pass 
        {

            Tags { "RenderType" = "Opaque" "LightMode" = "Deferred" }

            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase nolightmap nodirlightmap nodynlightmap novertexlight
            #pragma target 5.0
            #pragma require 2darray

            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "UnityPBSLighting.cginc"
            #include "UnityLightingCommon.cginc"
            #include "AutoLight.cginc"

            #include "../../Data/Block.cginc"

            struct cs_Vertex
            {
                float3 position;
                float3 normal;
                float2 uv;
                int block_vert_info;
                Block data;
            };

        #if SHADER_TARGET >= 45
            StructuredBuffer<cs_Vertex> cs_vbuffer;
        #endif

            sampler2D _MainTex;

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

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 arrayUV : TEXCOORD0;

                float4 worldPos : POSITION2;
                float3 worldNormal : NORMAL;

                float4 color : TEXCOORD1;

                float3 blockSpacePos : TEXCOORD2;
                float3 viewDir : TEXCOORD3;
                half2 clipZW : TEXCOORD4;

            #ifdef LIGHTMAP_OFF
                #if UNITY_SHOULD_SAMPLE_SH
                    half3 sh : TEXCOORD5;
                #endif
            #endif

                //SHADOW_COORDS(5)
            };

            struct GBufferOut
            {
                half4 diffuse : SV_Target0;
                half4 specular : SV_Target1;
                half4 normal : SV_Target2;
                half4 emission : SV_Target3;
                float depth : SV_DEPTH;
            };

            GBufferOut ApplyUnityStandardLit(v2f i, GBufferOut o, SurfaceOutputStandard so);

            inline float4 GetBlockLUT(uint fid)
            {
                return tex2Dlod(_BlockLUT, float4(float(fid / 256) / 256.0, float(fid % 256) / 256.0, 0, 0));
            }

            inline float3 GetArrayUV(float4 lut, float2 uv)
            {
                uint _a = asuint(lut.a);
                float3 uv_org = clamp(0, 1, lut.rgb);
                float2 uv_step = float2(float((_a >> 11) & 2047) / 2048.0, float(_a & 2047) /
                    2048.0);

                return uv_org + float3(uv * uv_step, 0);
            }

            v2f vert(appdata_full v, uint vID : SV_VertexID)
            {
                v2f o;

            #if SHADER_TARGET >= 45
                cs_Vertex data = cs_vbuffer[vID];

                float4 worldPosition = mul(_LocalToWorld, float4(data.position, 1.0f));
                float3 worldNormal = mul((float3x3)_LocalToWorld, data.normal);

                o.worldPos = worldPosition;
                o.worldNormal = worldNormal;
                o.blockSpacePos = float3(
                    (data.block_vert_info & 4) / 4,
                    (data.block_vert_info & 2) / 2,
                    (data.block_vert_info & 1) / 1);
                o.viewDir = UnityWorldSpaceViewDir(worldPosition.xyz);

                // Find block ID and LUT values
                Block blk = data.data;
                uint fid = GetBlockID(blk);
                float4 lut = GetBlockLUT(fid);
                uint _a = asuint(lut.a); // float interpreted as uint (get bits)

                // TEST
                if (((_a >> 22) & (0x7)) == 4) // FINE_16 = 4
                {
                    // "default value"
                    o.color = fixed4(1, 0, 0, 1);
                    o.arrayUV = float3(data.uv, -1.0f); // Make uv.z < 0 to tell Frag
                }
                else
                {
                    uint meta = GetBlockMeta(blk);
                    o.color = (GetBlockID(blk) > 0) * fixed4(
                        float((meta >> 12) & (0x000F)) / 15.0,
                        float((meta >> 8) & (0x000F)) / 15.0,
                        float((meta >> 4) & (0x000F)) / 15.0,
                        float((meta) & (0x000F)) / 15.0
                    );

                    // Calculate UV
                    o.arrayUV = GetArrayUV(lut, data.uv);
                    o.color = lerp(o.color, float4(1, 1, 1, 1), (_a & 0x10000000) == 0); // tint or not
                }

                // TEST

                float3 ndotl = saturate(dot(worldNormal, _WorldSpaceLightPos0.xyz));
                float3 ambient = ShadeSH9(float4(worldNormal, 1.0f));
                float3 diffuse = (ndotl * _LightColor0.rgb);
                float3 color = v.color;

                o.pos = UnityWorldToClipPos(worldPosition);
                o.clipZW = o.pos.zw;

                //o.ambient = ambient;
                //o.diffuse = diffuse;
                //TRANSFER_SHADOW(o)
            #endif

            #ifdef LIGHTMAP_OFF
                #ifndef SPHERICAL_HARMONICS_PER_PIXEL
                    #if UNITY_SHOULD_SAMPLE_SH
                        o.sh = 0;
                        o.sh = ShadeSHPerVertex(o.worldNormal, o.sh);
                    #endif
                #endif
            #endif

                return o;
            }

            float CalcDepth(float3 vert) 
            {
                float4 pos_clip = mul(UNITY_MATRIX_VP, float4(vert, 1));
                return pos_clip.z / pos_clip.w;
            }

            GBufferOut frag(v2f i)// : SV_Target
            {
                i.viewDir = normalize(i.viewDir);

                SurfaceOutputStandard so;

                UNITY_INITIALIZE_OUTPUT(SurfaceOutputStandard, so);
                so.Albedo = float3(1, 1, 1);// i.color;
                so.Metallic = _Metallic;
                so.Smoothness = _Glossiness;
                so.Emission = 0.0;
                so.Alpha = 1.0;
                so.Occlusion = 1.0;
                so.Normal = i.worldNormal;

                // Fine Structures
                if (i.arrayUV.z < 0)
                {
                    // Following http://jojendersie.de/rendering-huge-amounts-of-voxels-2/#comment-400

                    // origin in tex3D
                    float3 origin = float3(i.arrayUV.xy, 0);

                    // size in tex3D
                    float3 size = FStexGridSize;

                    // Ray position
                    float3 p0 = i.blockSpacePos * 16.0f;
                    float3 p = p0;

                    // Ray direction
                    //float3 d = mul((float3x3)_WorldToLocal, -i.viewDir) * step;
                    float3 d = -i.viewDir;
                    float t = 0; // Distance traveled along the ray

                    // Output surface normal
                    float3 normal;

                    // Save current sampled voxel data
                    uint v;
                    //float tv;

                    int4 dirSign;
                    dirSign.x = d.x < 0 ? -1 : 1;
                    dirSign.y = d.y < 0 ? -1 : 1;
                    dirSign.z = d.z < 0 ? -1 : 1;
                    dirSign.w = 0;

                    // Continue only when grid empty
                    int rayStep;
                    so.Albedo = float3(0, 0, 1);
                    for (rayStep = 0; rayStep < 100; rayStep++)
                    {
                        // Step ray
                        p = p0 + d * t;

                        // Ray has left voxel
                        //if (any(p > float3(1.02, 1.02, 1.02)) || any(p < float3(-0.02, -0.02, -0.02)))
                        if (any(p > float3(16.1, 16.1, 16.1)) || any(p < float3(-0.1, -0.1, -0.1)))
                        {
                            //so.Albedo = float3(rayStep / 64.0, 0, 0);
                            clip(-1.0);
                            break;
                        }

                        // BlockID
                        // Pos
                        int3 p_voxel = trunc(p);
                        p_voxel = clamp(p_voxel, 0, 15);
                        v = trunc(tex3Dlod(_FSTex, float4(origin + (p_voxel + float3(0.5, 0.5, 0.5)) / 16.0f * size, 0)).r * 65536.0);

                        if (v > 0)
                        {
                            so.Albedo = float3(0, rayStep / 64.0, 0);
                            normal = abs(p - round(p));
                            break;
                        }

                        // Step advancement
                        float3 deltas = (step(0, d) - frac(p)) / d;
                        t += max(min(min(deltas.x, deltas.y), deltas.z), 0.001);
                    }

                    ////////////////////// Raymarch Done //////////////////////

                    // Color for test
                    so.Albedo = float3(0, 1, 0);

                    // Retrieve normal & block uv
                    float2 blockuv;
                    if (normal.x < 0.001) { so.Normal = dirSign.xww; blockuv = p.yz / 16.0; }
                    if (normal.y < 0.001) { so.Normal = dirSign.wyw; blockuv = p.xz / 16.0; }
                    if (normal.z < 0.001) { so.Normal = dirSign.wwz; blockuv = p.xy / 16.0; }

                    // Retrieve world pos, world normal & depth
                    i.worldPos = i.worldPos + float4(mul(_LocalToWorld, float4((p - p0) / 16.0, 0)).xyz, 0);
                    i.worldNormal = mul((float3x3)_LocalToWorld, so.Normal);
                    i.clipZW = UnityWorldToClipPos(i.worldPos).zw;

                    // Retrieve block UVs
                    float4 lut = GetBlockLUT(v);
                    i.arrayUV = GetArrayUV(lut, blockuv);

                    // Convert color
                    i.color = float4(1, 1, 1, 1);

                    //so.Albedo = i.blockSpacePos + float4(mul(_LocalToWorld, float4((p - p0) / 16.0, 0)).xyz, 0);
                    so.Metallic = _Metallic;
                    so.Smoothness = _Glossiness;
                    so.Alpha = 1.0f;

                    /*so.Albedo = float4(
                        float((v >> 24) % 256) / 256, 
                        float((v >> 16) % 256) / 256,
                        float((v >>  8) % 256) / 256, 
                        float((v) % 256) / 256);*/
                    /*if(tv == (4.0 / 65536.0)){ so.Albedo = float4(1, 0, 0, 1); }
                    else
                    {
                        so.Albedo = float4(tv * 256, 0, 0, 1);
                    }*/
                }

                ////////////////////// Regular Blocks Rendering //////////////////////

                float4 tex = UNITY_SAMPLE_TEX2DARRAY(_MainTexArr, i.arrayUV);
                if (tex.a <= 0.5f) { discard; } // cutoff
                // Albedo comes from a texture tinted by color
                if (i.color.a <= 0.9f)
                {
                    //so.Emission = 2.0f * (i.color.a / 0.5f) * float4(i.color.rgb, 1.5);
                    so.Albedo = i.color.rgb * tex.rgb;
                }
                else
                {
                    so.Albedo = i.color.rgb * tex.rgb;
                }
                // Metallic and smoothness come from slider variables
                so.Metallic = _Metallic;
                so.Smoothness = _Glossiness;
                so.Alpha = 1.0f;

                /////////////////////////// Fill G-Buffer ///////////////////////////
                
                //so.Albedo = float4(i.clipPos / 100.0);
                //so.Albedo = float4(i.worldPos / 100.0);
                GBufferOut o;
                //o.depth = i.depth.x * _ProjectionParams.w;
                o.depth = i.clipZW.x / i.clipZW.y;// / i.pos.w; // Apperently it was linear in deferred, KEKW
                o = ApplyUnityStandardLit(i, o, so);

                return o;
            }

            // THANK YOU https://github.com/hecomi/uRaymarching/blob/master/Assets/uRaymarching/Shaders/Include/Legacy/DeferredStandard.cginc
            // http://tips.hecomi.com/entry/2016/10/01/000232
            // THANK YOU GOD HECOMI Y~Orz
            GBufferOut ApplyUnityStandardLit(v2f i, GBufferOut o, SurfaceOutputStandard so)
            {
                UnityGI gi;
                UNITY_INITIALIZE_OUTPUT(UnityGI, gi);
                gi.indirect.diffuse = 0;
                gi.indirect.specular = 0;
                gi.light.color = 0;
                gi.light.dir = half3(0, 1, 0);
                gi.light.ndotl = LambertTerm(i.worldNormal, gi.light.dir);

                UnityGIInput giInput;
                UNITY_INITIALIZE_OUTPUT(UnityGIInput, giInput);
                giInput.light = gi.light;
                giInput.worldPos = i.worldPos;
                giInput.worldViewDir = i.viewDir;
                giInput.atten = 1;

#if defined(LIGHTMAP_ON) || defined(DYNAMICLIGHTMAP_ON)
                //giInput.lightmapUV = i.lmap;
                giInput.lightmapUV = 0.0;
                // Doesn't support it rn
#else
                giInput.lightmapUV = 0.0;
#endif

#if UNITY_SHOULD_SAMPLE_SH
#ifdef SPHERICAL_HARMONICS_PER_PIXEL
                giInput.ambient = ShadeSHPerPixel(worldNormal, 0.0, worldPos); // ?
#else
                giInput.ambient.rgb = i.sh;
#endif
#else
                giInput.ambient.rgb = 0.0;
#endif

                giInput.probeHDR[0] = unity_SpecCube0_HDR;
                giInput.probeHDR[1] = unity_SpecCube1_HDR;

#if UNITY_SPECCUBE_BLENDING || UNITY_SPECCUBE_BOX_PROJECTION
                giInput.boxMin[0] = unity_SpecCube0_BoxMin; // .w holds lerp value for blending
#endif

#if UNITY_SPECCUBE_BOX_PROJECTION
                giInput.boxMax[0] = unity_SpecCube0_BoxMax;
                giInput.probePosition[0] = unity_SpecCube0_ProbePosition;
                giInput.boxMax[1] = unity_SpecCube1_BoxMax;
                giInput.boxMin[1] = unity_SpecCube1_BoxMin;
                giInput.probePosition[1] = unity_SpecCube1_ProbePosition;
#endif

                LightingStandard_GI(so, giInput, gi);

                o.emission = LightingStandard_Deferred(so, i.viewDir, gi, o.diffuse, o.specular, o.normal);
#if defined(SHADOWS_SHADOWMASK) && (UNITY_ALLOWED_MRT_COUNT > 4)
                //outShadowMask = UnityGetRawBakedOcclusions(IN.lmap.xy, worldPos);
                outShadowMask = UnityGetRawBakedOcclusions(float2(0, 0), worldPos);
#endif
#ifndef UNITY_HDR_ON
                //o.emission.rgb = exp2(-o.emission.rgb);
#endif

                UNITY_OPAQUE_ALPHA(o.diffuse.a);

                return o;
            }

            ENDCG
        }
    }
    FallBack "Diffuse"
}

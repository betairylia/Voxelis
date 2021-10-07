Shader "GTW/CPUOptim_ChunkShader_FlatPalette"
{
    Properties
    {
        _MainTex ("Albedo (RGB)", 2D) = "white" { }

        _Color ("Color", Color) = (1, 1, 1, 1)
        _MainAtlas ("MainAtlas", 2D) = "" { }
        _Random ("Random RGB Tex", 2D) = "white" { }
        _Glossiness ("Smoothness", Range(0, 1)) = 0.5
        _Metallic ("Metallic", Range(0, 1)) = 0.0
        _ShadowExtraBias ("Shadow extra bias", Float) = 0.0

        _ChiselTexSizeX ("Packed chisel tex size X", Int) = 1.0
        _ChiselTexSizeY ("Packed chisel tex size Y", Int) = 1.0

        _LOD0 ("LOD 0 distance", Float) = 64.0
        _LOD1 ("LOD 1 distance", Float) = 128.0
        _LOD2 ("LOD 2 distance", Float) = 256.0

        _ShadowLOD0 ("Shadow LOD 0 distance", Float) = 32.0
        _ShadowLOD1 ("Shadow LOD 1 distance", Float) = 64.0
        _ShadowLOD2 ("Shadow LOD 2 distance", Float) = 96.0

        _ShadowLODHardBias ("Shadow LOD Bias", Int) = 0

        // _InvAtlasSize ("Inverse atlas size", Vector) = (0.0009766, 0.0009766)
        _SunDirc ("Sun Direction (Normalized)", Vector) = (0, 1, 0, 0)
        _SunFogFactorPower ("Sun Fog Factor Power", range(1.0, 10.0)) = 1.0
    }

    HLSLINCLUDE

    #pragma target 5.0

    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    // #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
    #include "../Cginc/URP_customPBR.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

    #define MAT_EMPTY 0x1
    #define MAT_OPAQUE 0x2
    #define MAT_CUTOFF 0x4
    #define MAT_TRANSPARENT 0x8
    #define MAT_ERROR 0x10
    #define MAT_ERROR_NOTEX 0x20
    #define MAT_SOLID_COLOR 0x40
    #define MAT_NOTEX 0x80

    #define MATPROP_BIG_BIT 0x01
    #define MATPROP_CTMENUM_BIT 0x02
    #define MATPROP_CTMCHISEL_BIT 0x04
    #define MATPROP_RANDOM_BIT 0x08
    #define MATPROP_ANIMATED_BIT 0x10
    #define MATPROP_SSR_BIT 0x20
    #define MATPROP_SSS_BIT 0x40
    #define MATPROP_FOILAGE_BIT 0x80
    #define MATPROP_ZEROSPEC_BIT 0x100

    #define FACEINFO_CHISEL 6

    CBUFFER_START(UnityPerMaterial)
        TEXTURE2D(_MainAtlas);SAMPLER(sampler_MainAtlas);
        TEXTURE2D(_Random);SAMPLER(sampler_Random);

        // Ray march use
        float blockSize;
        float3 FStexGridSize;
        uint iChiselTexSizeY;

        float _LOD0;
        float _LOD1;
        float _LOD2;

        float _ShadowLOD0;
        float _ShadowLOD1;
        float _ShadowLOD2;

        int _ShadowLODHardBias;

        half _Glossiness;
        half _Metallic;
        half4 _Color;
        half _ShadowAlpha;

        float4x4 _LocalToWorld;
        float4x4 _WorldToLocal;

        float4 _SunDirc;
        float _SunFogFactorPower;

        TEXTURE2D(_BlockLUT);SAMPLER(sampler_BlockLUT);
        TEXTURE3D(_FSTex);SAMPLER(sampler_FSTex);

        int ErrorMatID;
    CBUFFER_END

    static const float3 normals[6] = {
        float3(+1, 0, 0),
        float3(-1, 0, 0),
        float3(0, +1, 0),
        float3(0, -1, 0),
        float3(0, 0, +1),
        float3(0, 0, -1),
    };

    static const float3 tangents[6] = {
        float3(0, +1, 0),
        float3(0, -1, 0),
        float3(-1, 0, 0),
        float3(+1, 0, 0),
        float3(0, +1, 0),
        float3(0, +1, 0),
    };

    static const float3 binormals[6] = {
        float3(0, 0, +1),
        float3(0, 0, +1),
        float3(0, 0, +1),
        float3(0, 0, +1),
        float3(-1, 0, 0),
        float3(+1, 0, 0),
    };

    inline uint FaceInfo(uint x)
    {
        return(x & 0xf);
    }

    inline uint BlockID(uint x)
    {
        return(x & 0xffff0000) >> 16;
    }

    inline float2 InFacePosition(uint x)
    {
        return float2(
            (x & 0x0000fc00) >> 10,
            (x & 0x000003f0) >> 4
        );
    }

    inline float3 InBlockPosition(uint x)
    {
        return float3(
            (x >> 22) & 1,
            (x >> 21) & 1,
            (x >> 20) & 1
        );
    }

    inline float3 ChiselPackedUVW(uint x)
    {
        uint uvIdx = (x >> 4) & (0xffff);
        float2 uvStart = float2(
            float(uvIdx / iChiselTexSizeY) * FStexGridSize.x,
            float(uvIdx % iChiselTexSizeY) * FStexGridSize.y
        );

        return float3(uvStart, -0.5f); // Make uv.z < 0 to tell Frag; (uint)-uv.z is LOD level (-0, -1, -2, -3)

    }

    inline uint ChiselID(uint x)
    {
        return(x >> 4) & (0xffff);
    }

    // From https://github.com/hecomi/uRaymarching
    float _ShadowExtraBias;
    inline bool IsCameraPerspective()
    {
        return any(UNITY_MATRIX_P[3].xyz);
    }
    inline float3 GetCameraForward()
    {
        return -UNITY_MATRIX_V[2].xyz;
    }
    // End From https://github.com/hecomi/uRaymarching

    struct appdata_t
    {
        float4 vertex : POSITION;
        float3 normal : NORMAL;
        float2 uv : TEXCOORD0;
        float2 lightmapUV : TEXCOORD1;
        UNITY_VERTEX_INPUT_INSTANCE_ID
        //float2 uv2 : TEXCOORD1;

    };

    struct BlockMatInfo
    {
        uint flags;

        uint properties;

        uint2 spriteSheets;

        float smoothness;
        float metallic;
        float4 emission;

        float SSR;
        float SSS;

        float2 uvOrigin;
        float2 uvSpan;

        float2 uvOrigin_n; // Normal
        float2 uvSpan_n;

        float2 uvOrigin_d; // Displacement
        float2 uvSpan_d;

        float2 uvOrigin_m; // Metallic
        float2 uvSpan_m;

        float2 uvOrigin_r; // Roughness
        float2 uvSpan_r;
    };
    StructuredBuffer<BlockMatInfo> blockMaterials;

    inline uint GetMaterialID(uint blockID, uint faceID)
    {
        return blockID * 6 + faceID;
    }

    inline BlockMatInfo GetMaterial(uint materialID)
    {
        return blockMaterials[materialID];
    }

    float CalcDepth(float3 vert)
    {
        float4 pos_clip = mul(UNITY_MATRIX_VP, float4(vert, 1));
        return pos_clip.z / pos_clip.w;
    }

    struct v2f
    {
        float4 pos : SV_POSITION;
        half3 arrayUV : VAR_BASE_UV; // Chisel: UV in 3D texture; Regular: UV origin in 2D alias
        uint matID : VAR_NORMAL2;

        float4 worldPos : VAR_POSITION2;
        float3 worldNormal : VAR_NORMAL;

        half4 color : TEXCOORD1;

        half3 blockSpacePos : TEXCOORD2; // Chisel: blockSpacePos; Regular: X = aliasUV.x (should be modded by 1 for tiling in alias), Y = aliasUV.y
        float3 viewDir : TEXCOORD3; // Chisel: viewDir, Regular: XY = UV size in 2D alias
        float2 clipZW : TEXCOORD4;

        // #ifdef LIGHTMAP_OFF
        //     #if UNITY_SHOULD_SAMPLE_SH
        //         half3 sh : TEXCOORD5;
        //     #endif
        // #endif
        // DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 5);
        // half3 vertexLight: TEXCOORD9;

        uint chiselID : TEXCOORD6;
        uint faceID : TEXCOORD7;
        float4 fogFactorAndvertexLight : TEXCOORD8;
        UNITY_VERTEX_INPUT_INSTANCE_ID
        //SHADOW_COORDS(5)

    };

    // It seems tedious in unity shaderlab to work with integer samplers (with integer uvs), so ... SturcutredBuffer instead ... !
    Buffer<uint> ChiselBuffer;
    static const int mipOffset[4] = {
        0, // + 16 x 16 x 16 / 2 - LOD0
        2048, // + 8 x 8 x 8 / 2 - LOD1
        2304, // + 4 x 4 x 4 / 2 - LOD2
        2336  // + 2 x 2 x 2 / 2 - LOD3

    };
    static const int CHISEL_SIZE = 2340;

    uint SampleChiselBuffer(Buffer<uint> buffer, uint chiselID, uint3 coord, uint mip)
    {
        // #define CHISEL_DEBUG
        #if defined(CHISEL_DEBUG)
            uint result = max(0, 32 - coord.x - coord.y - coord.z);
            return result;
        #else
            uint bits = (4 - mip);
            uint combined_data = buffer[
            (chiselID * CHISEL_SIZE) +
            mipOffset[mip] +
            ((
                (coord.x << (bits + bits)) +
                (coord.y << (bits)) +
                coord.z
            ) >> 1)];

            return((combined_data >> ((coord.z % 2) * 16)) & 0xffff);
        #endif
        // return chiselID;

    }

    // TODO: FIXME: Check all parts that used this variable and kill them for performance
    float _ShowErrorBlocks;

    inline half4 GetErrorColor()
    {
        half4 blink = lerp(half4(1, 0, 0, 1), half4(5, 0, 5, 1), sin(_Time.w * 2) * 0.5 + 0.5);
        return lerp(half4(0, 0, 0, 0), blink, _ShowErrorBlocks);
    }

    inline half4 GetNotexColor()
    {
        half4 blink = lerp(half4(0, 1, 0, 1), half4(5, 5, 0, 1), sin(_Time.w * 2) * 0.5 + 0.5);
        return lerp(half4(0, 1, 0, 1), blink, _ShowErrorBlocks);
    }

    v2f UnpackVertex(appdata_t v)
    {
        v2f o;
        UNITY_SETUP_INSTANCE_ID(v);
        UNITY_TRANSFER_INSTANCE_ID(v, o);
        // Unpack vertex
        uint packedVert = asuint(v.uv.x);
        
        // TODO: color (v.uv.y) being ignored rn

        float3 localPosition = v.vertex;
        float2 aliasUV = float2(
            (packedVert >> 6) & 0x3f,
            (packedVert) & 0x3f
        );

        float4 worldPosition = mul(unity_ObjectToWorld, float4(localPosition, 1));

        o.worldPos = worldPosition;
        o.pos = TransformWorldToHClip(worldPosition);

        // TEST
        if (FaceInfo(packedVert) == FACEINFO_CHISEL) // Chisel mark

        {
            // "default value"
            o.color = half4(1, 0, 0, 1);
            // o.arrayUV = ChiselPackedUVW(packedVert);
            o.arrayUV = float3(asfloat(ChiselID(packedVert)), 0.0f, -0.5f);
            o.chiselID = asuint(v.uv.y);

            if (IsCameraPerspective())
            {
                o.viewDir = GetWorldSpaceViewDir(worldPosition.xyz);

                // Calculate LOD level
                // Per-vertex rn, ugly but lazy
                float dist = length(_WorldSpaceCameraPos - worldPosition.xyz);
                o.arrayUV.z += -1.0f * (dist > _LOD0);
                o.arrayUV.z += -1.0f * (dist > _LOD1);
                o.arrayUV.z += -1.0f * (dist > _LOD2);
            }
            else
            {
                o.viewDir = -GetCameraForward();
                o.viewDir = normalize(o.viewDir);

                // Calculate LOD level for ortho
                // Per-vertex rn, ugly but lazy
                float3 viewVec = _WorldSpaceCameraPos - worldPosition.xyz;
                viewVec -= dot(viewVec, o.viewDir) * o.viewDir;
                float dist = length(viewVec);
                
                o.arrayUV.z += -1.0f * (dist > _ShadowLOD0);
                o.arrayUV.z += -1.0f * (dist > _ShadowLOD1);
                o.arrayUV.z += -1.0f * (dist > _ShadowLOD2);

                o.arrayUV.z -= _ShadowLODHardBias;
                // o.arrayUV.z -= 1.0f;

            }

            o.worldNormal = float3(0, 0, 0);
            o.blockSpacePos = InBlockPosition(packedVert);
        }
        else // Regular block

        {
            uint faceid = FaceInfo(packedVert);
            // TODO: FIXME: This is UV hotfix
            o.faceID = faceid;
            float3 worldNormal = TransformObjectToWorldNormal(normals[faceid]);
            o.worldNormal = worldNormal;

            uint bid = BlockID(packedVert);

            // FIXME: DEMO-ONLY: Demo pure color block in RGB555
            if (bid >= 32768)
            {
                o.color = float4(
                    float((bid >> 10) & 0x1f) / 31.0,
                    float((bid >> 5) & 0x1f) / 31.0,
                    float((bid) & 0x1f) / 31.0,
                    1);

                // I HATE GAMMA WORLD ...
                o.color = pow(o.color, 2.2);

                o.matID = ErrorMatID;
                o.viewDir = float3(0, 0, 0);
                o.arrayUV = float3(0, 0, 0);
                o.blockSpacePos = float3(InFacePosition(packedVert), 0); // TODO: FIXME: fix this or are we okay?

            }
            else
            {
                o.matID = GetMaterialID(bid, faceid);
                BlockMatInfo blkMat = GetMaterial(o.matID);

                // TODO: Implement colors
                o.color = half4(1, 1, 1, 1);

                o.blockSpacePos = float3(InFacePosition(packedVert), 0); // TODO: FIXME: fix this or are we okay?
                o.viewDir = float3(blkMat.uvSpan, 0);

                // Calculate UV
                o.arrayUV = float3(blkMat.uvOrigin, 0); // uv origin
                // o.color = lerp(o.color, float4(1, 1, 1, 1), (_a & 0x10000000) == 0); // tint or not

                // Errors
                if ((blkMat.flags & MAT_ERROR) > 0)
                {
                    o.color = GetErrorColor();
                }
                else
                {
                    if ((blkMat.flags & 32) > 0)
                    {
                        o.color = GetNotexColor();
                    }
                    if (length(o.viewDir.xy) < 0.0001)
                    {
                        o.color = GetNotexColor();
                    }// TODO: FIXME: Why above only doesn't work ??

                }
            }
        }

        o.clipZW = o.pos.zw;

        //TRANSFER_SHADOW(o)

        return o;
    }

    struct RaycastResult
    {
        // TODO: transparency

        uint blockID;
        uint faceID;

        float2 blockUV;
        float3 blockspacePosOffset;
        float3 blockspaceNormal;
    };

    // Following http://jojendersie.de/rendering-huge-amounts-of-voxels-2/#comment-400
    RaycastResult Raycast_old(float3 texUVWOrigin, float3 blockspace_originPos, float3 viewDir, uint LOD = 0)
    {
        RaycastResult res;

        uint LOD_exp = 1 << LOD;

        // Calculated FS resolution
        uint resolution = 16 / LOD_exp;
        float resolutionF = float(resolution);
        float res_eps = resolutionF + 0.1f;
        float3 center_offset = float3(0.5, 0.5, 0.5) / float(LOD_exp);

        // origin in tex3D
        float3 origin = texUVWOrigin;

        // size in tex3D
        float3 size = FStexGridSize;

        // Ray position
        float3 p0 = blockspace_originPos * resolutionF;
        float3 p = p0;

        // Ray direction in block space - // TODO: FIXME: Modify this so rotation works
        float3 d = -viewDir;
        float t = 0; // Distance traveled along the ray

        // Save current sampled voxel data
        uint v;

        int4 dirSign;
        dirSign.x = d.x < 0 ? - 1 : 1;
        dirSign.y = d.y < 0 ? - 1 : 1;
        dirSign.z = d.z < 0 ? - 1 : 1;
        dirSign.w = 0;

        // Output surface normal
        float3 normal;

        // Continue only when grid empty
        int rayStep;
        for (rayStep = 0; rayStep < 100; rayStep++)
        {
            // Step ray
            p = p0 + d * t;

            // Ray has left voxel
            if (any(p > float3(res_eps, res_eps, res_eps)) || any(p < float3(-0.1, -0.1, -0.1)))
            {
                clip(-1.0);
                break;
            }

            // BlockID
            // Pos
            int3 p_voxel = trunc(p);
            p_voxel = clamp(p_voxel, 0, resolution - 1);

            /*【不确定】
            v = trunc(SAMPLE_TEXTURE3D_LOD(_FSTex, sampler_FSTex,float4(origin + (p_voxel + center_offset) / resolutionF * size)).r * 65536.0，5.0);

            if (v > 0)
            {
                normal = abs(p - round(p));
                break;
            }
            */

            // Step advancement
            float3 deltas = (step(0, d) - frac(p)) / d;
            t += max(min(min(deltas.x, deltas.y), deltas.z), 0.001);
        }

        ////////////////////// Raymarch Done //////////////////////

        res.blockID = v;

        // Retrieve normal & block uv
        // Face normal is oppsite to ray direction
        if (normal.x < 0.001)
        {
            res.blockspaceNormal = -dirSign.xww; res.blockUV = p.yz / resolutionF; res.faceID = 0 + (dirSign.x > 0);
        }
        if (normal.y < 0.001)
        {
            res.blockspaceNormal = -dirSign.wyw; res.blockUV = p.xz / resolutionF; res.faceID = 2 + (dirSign.y > 0);
        }
        if (normal.z < 0.001)
        {
            res.blockspaceNormal = -dirSign.wwz; res.blockUV = p.xy / resolutionF; res.faceID = 4 + (dirSign.z > 0);
        }

        res.blockspacePosOffset = (p - p0) / resolutionF;

        return res;
    }

    // Following http://jojendersie.de/rendering-huge-amounts-of-voxels-2/#comment-400
    RaycastResult Raycast_(uint chiselID, float3 blockspace_originPos, float3 viewDir, uint LOD = 0)
    {
        RaycastResult res;

        uint LOD_exp = 1 << LOD;

        // Calculated FS resolution
        uint resolution = 16 / LOD_exp;
        float resolutionF = float(resolution);
        float res_eps = resolutionF + 0.001f;
        float3 center_offset = float3(0.5, 0.5, 0.5) / float(LOD_exp);

        // origin in tex3D
        // float3 origin = texUVWOrigin;

        // size in tex3D
        float3 size = FStexGridSize;

        // Ray position
        float3 p0 = blockspace_originPos * resolutionF;
        float3 p = p0;

        // Ray direction in block space - // TODO: FIXME: Modify this so rotation works
        float3 d = -viewDir;
        float t = 0; // Distance traveled along the ray

        // Save current sampled voxel data
        uint v;

        int4 dirSign;
        dirSign.x = d.x < 0 ? - 1 : 1;
        dirSign.y = d.y < 0 ? - 1 : 1;
        dirSign.z = d.z < 0 ? - 1 : 1;
        dirSign.w = 0;

        // Output surface normal
        float3 normal;

        // Continue only when grid empty
        int rayStep;
        for (rayStep = 0; rayStep < 100; rayStep++)
        {
            // Step ray
            p = p0 + d * t;

            // Ray has left voxel
            if (any(p > float3(res_eps, res_eps, res_eps)) || any(p < float3(-0.001f, -0.001f, -0.001f)))
            {
                clip(-1.0);
                break;
            }

            // BlockID
            // Pos
            uint3 p_voxel = trunc(p);
            p_voxel = clamp(p_voxel, 0, resolution - 1);
            // p_voxel = (min(p_voxel, resolution - 1) & (resolution - 1)); // Why this doesn't work ??
            // v = trunc(tex3Dlod(_FSTex, float4(origin + (p_voxel + center_offset) / resolutionF * size, LOD)).r * 65536.0);
            v = SampleChiselBuffer(ChiselBuffer, chiselID, p_voxel, LOD);

            if (v > 0)
            {
                normal = abs(p - round(p));
                break;
            }

            // Step advancement
            float3 deltas = (step(0, d) - frac(p)) / d;
            t += max(min(min(deltas.x, deltas.y), deltas.z), 0.001);
        }

        ////////////////////// Raymarch Done //////////////////////

        res.blockID = v;

        // Retrieve normal & block uv
        // Face normal is oppsite to ray direction
        if (normal.x < 0.001)
        {
            res.blockspaceNormal = -dirSign.xww; res.blockUV = p.yz / resolutionF; res.faceID = 0 + (dirSign.x > 0);
        }
        if (normal.y < 0.001)
        {
            res.blockspaceNormal = -dirSign.wyw; res.blockUV = p.xz / resolutionF; res.faceID = 2 + (dirSign.y > 0);
        }
        if (normal.z < 0.001)
        {
            res.blockspaceNormal = -dirSign.wwz; res.blockUV = p.xy / resolutionF; res.faceID = 4 + (dirSign.z > 0);
        }

        res.blockspacePosOffset = (p - p0) / resolutionF;

        return res;
    }

    // Returns flipped blockSpacePosition.
    float3 FlipFace(float3 blockSpacePos, float3 blockSpaceViewDir, int3 dirSign, float blockSpaceDepth)
    {
        dirSign = -dirSign;
        float3 projLength = 1.0 / (abs(blockSpaceViewDir) + 0.0001);
        
        float3 d = blockSpacePos;

        // Set initial d
        if (dirSign.x == 1)
        {
            d.x = 1 - d.x;
        }
        if (dirSign.y == 1)
        {
            d.y = 1 - d.y;
        }
        if (dirSign.z == 1)
        {
            d.z = 1 - d.z;
        }

        // Calculate step length for flipping
        d *= projLength;

        bool3 cmp = bool3(
            d.x < d.y && d.x < d.z,
            d.y < d.x && d.y < d.z,
            d.z < d.x && d.z < d.y
        );

        // Uncomment this and comment following line to view inside of a chisel
        // Also need to set proper blockSpaceDepth from fragment shader
        // May lead to unstable results so currently disabled
        // float t = min(blockSpaceDepth, length(d * cmp));
        float t = length(d * cmp);

        return blockSpacePos - t * (blockSpaceViewDir);
    }

    // Strictly Following http://jojendersie.de/rendering-huge-amounts-of-voxels-2/#comment-400
    // But above is too slow. (not sure now, previously it was because no "break" after clip(-1.0) ... @($!^))
    // Using implementations from https://www.shadertoy.com/view/XdtcRM instead
    RaycastResult Raycast(
        uint chiselID,
        float3 blockspace_originPos,
        float3 viewDir,
        uint LOD = 0,
        float blockspace_depth = 4)
    {
        RaycastResult res;

        uint LOD_exp = 1 << LOD;

        // Calculated FS resolution
        uint resolution = 16 / LOD_exp;
        float resolutionF = float(resolution);

        // Ray direction in block space - // TODO: FIXME: Modify this so rotation works
        float3 chunkSpaceDir = -viewDir;
        float3 absDir = abs(chunkSpaceDir);
        float3 projLength = 1.0 / (absDir + 0.0000001); // 2.0 for coarse stage

        // Save current sampled voxel data
        uint v;

        int4 dirSign;
        dirSign.x = chunkSpaceDir.x < 0 ? - 1 : 1;
        dirSign.y = chunkSpaceDir.y < 0 ? - 1 : 1;
        dirSign.z = chunkSpaceDir.z < 0 ? - 1 : 1;
        dirSign.w = 0;

        // Flip to FrontFace intersection point from BackFace raster result
        float3 newPosBS = FlipFace(blockspace_originPos, chunkSpaceDir, dirSign, blockspace_depth);
        res.blockspacePosOffset = newPosBS - blockspace_originPos;
        blockspace_originPos = newPosBS;

        // Ray position
        float3 voxelPos = blockspace_originPos * (resolutionF - 0.0000001);
        uint3 rayPos = uint3(voxelPos);

        // Begin raymarch
        float3 d, invd, _t3;
        float _t;
        bool3 cmp;
        int rayStep;

        if (LOD < 3)
        {
            // Coarse stage
            LOD += 1;
            resolution >>= 1;
            rayPos >>= 1; // align to high-level LOD
            voxelPos /= 2.0f;

            d = voxelPos - rayPos;

            // Set initial d
            if (dirSign.x == 1)
            {
                d.x = 1 - d.x;
            }
            if (dirSign.y == 1)
            {
                d.y = 1 - d.y;
            }
            if (dirSign.z == 1)
            {
                d.z = 1 - d.z;
            }
            d *= projLength;

            // Set initial preceeding direction
            invd = (projLength - d);
            cmp = bool3(
                invd.x < invd.y && invd.x < invd.z,
                invd.y < invd.x && invd.y < invd.z,
                invd.z < invd.x && invd.z < invd.y
            );

            _t3 = float3(0, 0, 0); // I'm so weak, any approaches better than this ?
            
            // Coarse search stage
            for (rayStep = 0; rayStep < 100; rayStep++)
            {
                v = SampleChiselBuffer(ChiselBuffer, chiselID, rayPos, LOD);

                if (any(rayPos >= uint3(resolution, resolution, resolution)))// || any(rayPos < int3(0,0,0)))
                // if(any(rayPos >= float3(resolution + 0.001,resolution + 0.001,resolution + 0.001)) || any(rayPos < float3(-0.001,-0.001,-0.001)))

                {
                    clip(-1.0);
                    break;
                }

                // if(v > 0)
                if (
                    v > 0 &&
                    (
                        (_ShowErrorBlocks > 0.5) ||
                        (_ShowErrorBlocks < 0.5 && v != (ErrorMatID / 6))
                        )
                        )
                    {
                        break;
                    }

                    cmp = bool3(
                        d.x < d.y && d.x < d.z,
                        d.y < d.x && d.y < d.z,
                        d.z < d.x && d.z < d.y
                    );

                    rayPos += dirSign * cmp;
                    _t3 = d; // TODO: How to remove this ??? I literally blowed up by this annoying thing
                    d += projLength * cmp;
                }

                // Fine search stage
                LOD -= 1;
                resolution <<= 1;
                _t3 *= 2.0f;
                voxelPos *= 2.0f;

                _t = min(_t3.x, min(_t3.y, _t3.z));
                _t3 = float3(0, 0, 0);
                voxelPos = (voxelPos + _t * chunkSpaceDir);
                rayPos = uint3(voxelPos);

                d = voxelPos - rayPos;

                // Reset d
                if (dirSign.x == 1)
                {
                    d.x = 1 - d.x;
                }
                if (dirSign.y == 1)
                {
                    d.y = 1 - d.y;
                }
                if (dirSign.z == 1)
                {
                    d.z = 1 - d.z;
                }
                d *= projLength;
            }
            else
            {
                d = voxelPos - rayPos;

                // Set initial d
                if (dirSign.x == 1)
                {
                    d.x = 1 - d.x;
                }
                if (dirSign.y == 1)
                {
                    d.y = 1 - d.y;
                }
                if (dirSign.z == 1)
                {
                    d.z = 1 - d.z;
                }
                d *= projLength;

                // Set initial preceeding direction
                invd = (projLength - d);
                cmp = bool3(
                    invd.x < invd.y && invd.x < invd.z,
                    invd.y < invd.x && invd.y < invd.z,
                    invd.z < invd.x && invd.z < invd.y
                );

                _t3 = float3(0, 0, 0); // I'm so weak, any approaches better than this ?
                rayStep = 0;
            }

            for (; rayStep < 100; rayStep++)
            {
                v = SampleChiselBuffer(ChiselBuffer, chiselID, rayPos, LOD);

                if (any(rayPos >= uint3(resolution, resolution, resolution)))// || any(rayPos < int3(0,0,0)))
                // if(any(rayPos >= float3(resolution + 0.001,resolution + 0.001,resolution + 0.001)) || any(rayPos < float3(-0.001,-0.001,-0.001)))

                {
                    clip(-1.0);
                    break;
                }

                // if(v > 0)
                if (
                    v > 0 &&
                    (
                        (_ShowErrorBlocks > 0.5) ||
                        (_ShowErrorBlocks < 0.5 && v != (ErrorMatID / 6))
                        )
                        )
                    {
                        break;
                    }

                    cmp = bool3(
                        d.x < d.y && d.x < d.z,
                        d.y < d.x && d.y < d.z,
                        d.z < d.x && d.z < d.y
                    );

                    rayPos += dirSign * cmp;
                    _t3 = d; // TODO: How to remove this ??? I literally blowed up by this annoying thing
                    d += projLength * cmp;
                }

                // d = abs(d - projLength);

                ////////////////////// Raymarch Done //////////////////////

                // Output surface normal
                float3 normal;

                res.blockID = v;
                _t = min(_t3.x, min(_t3.y, _t3.z));
                voxelPos = (voxelPos + _t * chunkSpaceDir) / (resolutionF);

                // Retrieve normal & block uv
                // Face normal is oppsite to ray direction
                // if (d.x < 0.001f)
                if (cmp.x == true)
                {
                    res.blockspaceNormal = -dirSign.xww; res.blockUV = voxelPos.yz; res.faceID = 0 + (dirSign.x > 0);
                }
                // if (d.y < 0.001f)
                if (cmp.y == true)
                {
                    res.blockspaceNormal = -dirSign.wyw; res.blockUV = voxelPos.zx; res.faceID = 2 + (dirSign.y > 0);
                }
                // if (d.z < 0.001f)
                if (cmp.z == true)
                {
                    res.blockspaceNormal = -dirSign.wwz; res.blockUV = voxelPos.xy; res.faceID = 4 + (dirSign.z > 0);
                }

                // res.blockspaceNormal = cmp * float3(rayStep, 1, 1);

                // res.blockspacePos = (p) / resolutionF;
                res.blockspacePosOffset += voxelPos - blockspace_originPos;

                return res;
            }

            ENDHLSL

            SubShader
            {
                Pass
                {

                    Tags { "LightMode" = "UniversalForward" "RenderType" = "Opaque" }

                    HLSLPROGRAM

                    #pragma vertex vert
                    #pragma fragment frag
                    #pragma multi_compile_fwdbase nolightmap nodirlightmap nodynlightmap novertexlight
                    // -------------------------------------
                    // Universal Pipeline keywords
                    #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
                    #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
                    #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
                    #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
                    #pragma multi_compile_fragment _ _SHADOWS_SOFT
                    #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
                    #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
                    #pragma multi_compile _ SHADOWS_SHADOWMASK

                    // -------------------------------------
                    // Unity defined keywords
                    #pragma multi_compile _ DIRLIGHTMAP_COMBINED
                    #pragma multi_compile _ LIGHTMAP_ON
                    #pragma multi_compile_fog

                    //--------------------------------------
                    // GPU Instancing
                    #pragma multi_compile_instancing
                    #pragma multi_compile _ DOTS_INSTANCING_ON
                    // #pragma require 2darray


                    //PBR
                    float DistributionGGX(float NoH, float a)
                    {
                        float a2 = a * a;
                        float NoH2 = NoH * NoH;

                        float nom = a2;
                        float denom = NoH2 * (a2 - 1) + 1;
                        denom = denom * denom * PI;
                        return nom / denom;
                    }

                    float GeometrySchlickGGX(float NoV, float k)
                    {
                        float nom = NoV;
                        float denom = NoV * (1.0 - k) + k;
                        return nom / denom;
                    }

                    float GeometrySmith(float NoV, float NoL, float k)
                    {
                        float ggx1 = GeometrySchlickGGX(NoV, k);
                        float ggx2 = GeometrySchlickGGX(NoL, k);
                        return ggx1 * ggx2;
                    }

                    float3 FresnelSchlick(float cosTheta, float3 F0)
                    {
                        return F0 + pow(1.0 - cosTheta, 5.0);
                    }

                    float3 IndirFresnelSchlick(float cosTheta, float3 F0, float roughness)
                    {
                        return F0 + (max(float3(1, 1, 1) * (1 - roughness), F0) - F0) * pow(1.0 - cosTheta, 5.0);
                    }


                    float3 SH_Process(float3 N)
                    {
                        float4 SH[7];
                        SH[0] = unity_SHAr;
                        SH[1] = unity_SHAg;
                        SH[2] = unity_SHAb;
                        SH[3] = unity_SHBr;
                        SH[4] = unity_SHBr;
                        SH[5] = unity_SHBr;
                        SH[6] = unity_SHC;

                        return max(0.0, SampleSH9(SH, N));
                    }

                    real CustomComputeFogIntensity(real fogFactor)
                    {
                        real fogIntensity = 0.0h;
                        #if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
                            #if defined(FOG_EXP)
                                // factor = exp(-density*z)
                                // fogFactor = density*z compute at vertex
                                fogIntensity = saturate(exp2(-fogFactor));
                            #elif defined(FOG_EXP2)
                                // factor = exp(-(density*z)^2)
                                // fogFactor = density*z compute at vertex
                                fogIntensity = saturate(exp2(-fogFactor * fogFactor));
                            #elif defined(FOG_LINEAR)
                                fogIntensity = fogFactor;
                            #endif
                        #endif
                        return fogIntensity;
                    }

                    half3 CustomMixFogColor(real3 fragColor, real3 fogColor, real fogFactor, real3 N, real3 L, real3 V, real3 LightColor)
                    {
                        #if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
                            real fogIntensity = CustomComputeFogIntensity(fogFactor);
                            real NoL = saturate(dot(N, L));
                            V = -V;
                            real VoL = pow(saturate(dot(V, L)), _SunFogFactorPower * _SunFogFactorPower);
                            if (L.y >= 0.0)
                            {
                                fogColor = lerp(fogColor, LightColor, VoL);
                            }
                            else
                            {
                                fogColor = lerp(fogColor, LightColor, VoL * saturate(1 + 5 * L.y));
                            }
                            fragColor = lerp(fogColor, fragColor, fogIntensity);
                        #endif
                        return fragColor;
                    }

                    half3 CustomMixFog(real3 fragColor, real fogFactor, real3 N, real3 L, real3 V, real3 LightColor)
                    {
                        return CustomMixFogColor(fragColor, unity_FogColor.rgb, fogFactor, N, L, V, LightColor);
                    }
                    
                    //PBR

                    //【LOD】
                    void ClipLOD(float2 positionCS, float fade)
                    {
                        #if defined(LOD_FADE_CROSSFADE)
                            float dither = InterleavedGradientNoise(positionCS.xy, 0);//每32个像素进行过渡
                            clip(fade + (fade < 0.0 ? dither : - dither));//通过抖色剔除
                        #endif
                    }

                    half3 CustomVertexLighting(float3 positionWS, half3 normalWS)
                    {
                        half3 vertexLightColor = half3(0.0, 0.0, 0.0);

                        #ifdef _ADDITIONAL_LIGHTS_VERTEX
                            uint lightsCount = GetAdditionalLightsCount();
                            for (uint lightIndex = 0u; lightIndex < lightsCount; ++lightIndex)
                            {
                                Light light = GetAdditionalLight(lightIndex, positionWS);
                                half3 lightColor = light.color * light.distanceAttenuation;
                                vertexLightColor += LightingLambert(lightColor, light.direction, normalWS);
                            }
                        #endif

                        return vertexLightColor;
                    }

                    v2f vert(appdata_t v)
                    {
                        v2f o = UnpackVertex(v);

                        UNITY_SETUP_INSTANCE_ID(v);
                        UNITY_TRANSFER_INSTANCE_ID(v, o);

                        #ifdef LIGHTMAP_OFF
                            #ifndef SPHERICAL_HARMONICS_PER_PIXEL
                                #if UNITY_SHOULD_SAMPLE_SH
                                    o.sh = 0;
                                    o.sh = ShadeSHPerVertex(o.worldNormal, o.sh);
                                #endif
                            #endif
                        #endif
                        o.fogFactorAndvertexLight.a = ComputeFogFactor(o.pos.z);
                        o.fogFactorAndvertexLight.rgb = CustomVertexLighting(o.worldPos, o.worldNormal);

                        return o;
                    }


                    float4 frag(v2f i, out float depth : SV_DEPTH) : SV_Target
                    {
                        UNITY_SETUP_INSTANCE_ID(i);
                        SurfaceData so;
                        so = (SurfaceData)0;
                        ClipLOD(i.pos.xy, unity_LODFade.x);

                        so.albedo = float3(1, 1, 1);// i.color;
                        so.metallic = _Metallic;
                        so.smoothness = _Glossiness;
                        so.emission = 0.0;
                        so.alpha = 1.0;
                        so.occlusion = 1.0;
                        so.normalTS = i.worldNormal;

                        PBRStylizedData style;
                        style.hasSpecular = true;
                        style.foilageness = 0.0;

                        float2 targetUV = i.arrayUV.xy;
                        BlockMatInfo material;

                        bool sampleTexture = false;

                        // Fine Structures
                        if (i.arrayUV.z < 0)
                        {
                            // #define DEBUG
                            #ifdef DEBUG
                                // i.color.rgb = float3(i.arrayUV.xy, 0);
                                //i.color.rgb = float3(i.blockSpacePos);
                                i.color.rgb = float3((float) (i.chiselID % 2048) / 2048.0, (float)i.chiselID / 2048.0, 0);
                            #else
                                // Following http://jojendersie.de/rendering-huge-amounts-of-voxels-2/#comment-400

                                i.viewDir = normalize(i.viewDir);

                                // Current LOD level (0, 1, 2, 3, 4)
                                uint LOD = (uint) (-i.arrayUV.z);

                                ////////////////////////////////////////////////////////////////////////////////////////////

                                // RaycastResult res = Raycast(float3(i.arrayUV.xy, 0), i.blockSpacePos, i.viewDir, LOD);
                                // RaycastResult res = Raycast(asuint(i.arrayUV.x), i.blockSpacePos, i.viewDir, LOD);
                                RaycastResult res = Raycast(i.chiselID, i.blockSpacePos, i.viewDir, LOD);
                                // RaycastResult res = Raycast_old_uintID(i.chiselID, i.blockSpacePos, i.viewDir, LOD);

                                ////////////////////////////////////////////////////////////////////////////////////////////

                                // Color for test
                                so.albedo = float3(0, 1, 0);

                                // Retrieve world pos, world normal & depth
                                // TODO: maybe use clip space in FS and do the mul's in VS ...
                                i.worldPos = i.worldPos + float4(mul(_LocalToWorld, float4(res.blockspacePosOffset, 0)).xyz, 0);
                                i.worldNormal = mul((float3x3)_LocalToWorld, res.blockspaceNormal);
                                i.clipZW = TransformWorldToHClip(i.worldPos).zw;

                                // Retrieve block UVs

                                // Color block
                                if (res.blockID >= 32768)
                                {
                                    i.color = float4(
                                        float((res.blockID >> 10) & 0x1f) / 31.0,
                                        float((res.blockID >> 5) & 0x1f) / 31.0,
                                        float((res.blockID) & 0x1f) / 31.0,
                                        1);

                                    // I HATE GAMMA WORLD ...
                                    i.color = pow(i.color, 2.2);

                                    material = GetMaterial(ErrorMatID);
                                    i.viewDir = float3(0, 0, 0);
                                    i.arrayUV = float3(0, 0, 0);
                                    targetUV = float2(0, 0);
                                }
                                else
                                {
                                    material = GetMaterial(GetMaterialID(res.blockID, res.faceID));
                                    i.arrayUV.xy = material.uvOrigin;
                                    i.viewDir.xy = material.uvSpan;
                                    targetUV = res.blockUV;
                                    i.color = float4(1, 1, 1, 1);

                                    // Errors
                                    if ((material.flags & MAT_ERROR) > 0)
                                    {
                                        i.color = GetErrorColor();
                                    }
                                    else
                                    {
                                        if ((material.flags & 32) > 0)
                                        {
                                            i.color = GetNotexColor();
                                        }
                                        if (length(i.viewDir.xy) < 0.0001)
                                        {
                                            i.color = GetNotexColor();
                                        }// TODO: FIXME: Why above only doesn't work ??

                                    }
                                }

                                // TODO: FIXME: This is UV hotfix
                                i.faceID = res.faceID;
                                
                                so.normalTS = i.worldNormal;
                                so.metallic = _Metallic;
                                so.smoothness = _Glossiness;
                                so.alpha = 1.0f;

                                // i.color = float4(res.blockspaceNormal, 1);
                            #endif
                        }
                        else
                        {
                            material = GetMaterial(i.matID);
                            targetUV = float2(frac(i.blockSpacePos.x), frac(i.blockSpacePos.y));
                        }

                        // TODO: FIXME: This is UV Hotfix
                        if (i.faceID < 2)
                        {
                            targetUV.xy = targetUV.yx;
                        }
                        //i.color.rg = targetUV;

                        // TODO: Warp UV unpacking into some function for shadowcasters

                        // Apply material properties
                        uint2 idx2 = uint2(0, 0);
                        float test = 0.0f;
                        float2 size = float2(1.0f, 1.0f) / float2(material.spriteSheets);
                        
                        // TODO: Branchless ? how ...
                        // TODO: move some to VS ?
                        if ((material.properties & MATPROP_BIG_BIT) > 0)
                        {
                            // TODO: Carry information from vertex stage, otherwise this won't work for rotation / scaling

                            int2 faceworldpos;
                            if (i.faceID < 6)
                            {
                                faceworldpos = i.worldPos.xy;
                            }// Zn, Zp
                            if (i.faceID < 4)
                            {
                                faceworldpos = i.worldPos.zx;
                            }// Yn, Yp
                            if (i.faceID < 2)
                            {
                                faceworldpos = i.worldPos.zy;
                            }// Xn, Xp

                            faceworldpos += int2(65536, 65536); // must be larger than world size

                            idx2 = faceworldpos % material.spriteSheets;
                        }

                        // TODO: MATPROP_CTMENUM_BIT
                        // TODO: MATPROP_CTMCHISEL_BIT

                        if ((material.properties & MATPROP_RANDOM_BIT) > 0)
                        {
                            // TODO: random
                            float2 faceworldpos;
                            if (i.faceID < 6)
                            {
                                faceworldpos = i.worldPos.xy;
                            }// Zn, Zp
                            if (i.faceID < 4)
                            {
                                faceworldpos = i.worldPos.zx;
                            }// Yn, Yp
                            if (i.faceID < 2)
                            {
                                faceworldpos = i.worldPos.zy;
                            }// Xn, Xp

                            faceworldpos = faceworldpos / 512.0;

                            int LOD = (uint) (-i.arrayUV.z);

                            uint2 rng = SAMPLE_TEXTURE2D_LOD(_Random, sampler_Random, faceworldpos.xy, LOD).rg * 256;//【不确定】
                            idx2 = uint2(rng % material.spriteSheets);
                        }

                        if ((material.properties & MATPROP_ANIMATED_BIT) > 0)
                        {
                            // TODO: control speed
                            // TODO: Add total frame count in material info
                            // uint idx = uint(trunc(_Time.y * 8)) % (); // 8FPS
                            // idx2 = ;

                        }

                        // TODO: MATPROP_SSR_BIT
                        // TODO: MATPROP_SSS_BIT
                        // TODO: MATPROP_FOILAGE_BIT

                        // Ugly fix for uv bleeding
                        // well at least it works xD
                        // padding should always be smaller than 1 over "actual subsprite" resolution
                        // which should be 1/32 = 0.03125.
                        targetUV = clamp(targetUV, 0.01, 0.99);

                        // Convert from subsprite UV to sprite UV
                        targetUV = targetUV * size + idx2 * size;

                        // Convert from sprite UV to atlas UV
                        float2 atlasUV = i.arrayUV.xy + targetUV * i.viewDir.xy;
                        sampleTexture = length(i.viewDir.xy) > 0.0001;

                        ////////////////////// Regular Blocks Rendering //////////////////////

                        #ifdef DEBUG
                            // so.Albedo = i.color.rgb;
                            so.albedo = float3(float(idx2.x % 5) / 5, float(idx2.y % 5) / 5, test);
                        #else
                            if (i.color.a <= 0.001f)
                            {
                                clip(-1.0);
                            }

                            float4 tex;
                            if (sampleTexture)
                            {
                                //tex = tex2Dlod(_MainAtlas, float4(atlasUV, 0, 0));
                                //int LOD = (uint) (-i.arrayUV.z);
                                //tex = SAMPLE_TEXTURE2D_LOD(_MainAtlas, sampler_MainAtlas, atlasUV, LOD);
                                tex = SAMPLE_TEXTURE2D(_MainAtlas, sampler_MainAtlas, atlasUV);
                            }
                            else
                            {
                                tex = float4(1, 1, 1, 1);
                            }

                            //float4 tex = float4(1, 1, 1, 1);
                            
                            if (tex.a <= 0.5f)
                            {
                                discard;
                            }// cutoff

                            // Seems like it is not being used like this ... ;w;
                            //if ((material.flags & MAT_CUTOFF > 0) && tex.a <= 0.5f) { discard; } // cutoff
                            
                            // Albedo comes from a texture tinted by color
                            if (i.color.a <= 0.9f)
                            {
                                //so.Emission = 2.0f * (i.color.a / 0.5f) * float4(i.color.rgb, 1.5);
                                so.albedo = i.color.rgb * tex.rgb;
                            }
                            else
                            {
                                so.albedo = i.color.rgb * tex.rgb;
                            }
                        #endif
                        
                        style.foilageness = 1.0 * ((material.properties & MATPROP_FOILAGE_BIT) > 0);
                        style.hasSpecular = ((material.properties & MATPROP_ZEROSPEC_BIT) == 0);

                        // Metallic and smoothness come from slider variables
                        // TODO: Anything better than branching?
                        
                        /////////////// METALLIC
                        if(material.uvSpan_m.x > 0)
                        {
                            so.metallic = SAMPLE_TEXTURE2D(
                                _MainAtlas, sampler_MainAtlas, 
                                material.uvOrigin_m + targetUV * material.uvSpan_m.xy).x;
                        }
                        else
                        {
                            so.metallic = material.metallic + _Metallic;
                        }

                        /////////////// SMOOTHNESS (ROUGHNESS)
                        if(material.uvSpan_r.x > 0)
                        {
                            so.smoothness = 1.0 - SAMPLE_TEXTURE2D(
                                _MainAtlas, sampler_MainAtlas, 
                                material.uvOrigin_r + targetUV * material.uvSpan_r.xy).x;
                            so.smoothness = 1.0;
                        }
                        else
                        {
                            so.smoothness = (material.smoothness + _Glossiness);
                        }

                        /////////////// NORMAL
                        if(material.uvSpan_n.x > 0)
                        {
                            half4 n = SAMPLE_TEXTURE2D(
                                _MainAtlas, sampler_MainAtlas, 
                                material.uvOrigin_n + targetUV * material.uvSpan_n.xy);
                            so.normalTS = UnpackNormal(n);

                            float3 tangent = tangents[i.faceID];
                            float3 binormal = binormals[i.faceID];

                            i.worldNormal = TransformTangentToWorld(
                                so.normalTS,
                                half3x3(tangent.xyz, binormal.xyz, i.worldNormal.xyz)
                            );
                        }

                        /////////////// HEIGHT
                        // TODO

                        // so.metallic = _Metallic;
                        // so.smoothness = _Glossiness;

                        so.emission = material.emission;
                        so.alpha = 1.0f;

                        float4 SHADOW_COORDS = TransformWorldToShadowCoord(i.worldPos);
                        Light mainLight = GetMainLight(SHADOW_COORDS);
                        float3 L = normalize(mainLight.direction);

                        so.occlusion = 1.0f;

                        /////////////////////////// Fill G-Buffer ///////////////////////////
                        
                        //so.Albedo = float4(i.clipPos / 100.0);
                        //so.Albedo = float4(i.worldPos / 100.0);
                        //o.depth = i.depth.x * _ProjectionParams.w;

                        #if (SHADER_API_OPENGL || SHADER_API_GLCORE || SHADER_API_GLES || SHADER_API_GLES3)
                            depth = (i.clipZW.x / i.clipZW.y) * .5 + .5;// / i.pos.w; // Apperently it was linear in deferred, KEKW
                        #else
                            depth = (i.clipZW.x / i.clipZW.y);// / i.pos.w; // Apperently it was linear in deferred, KEKW
                        #endif

                        //PBR
                        //base things
                        
                        // 因光照计算问题暂时切到Unity PBR
                        // 多光源下只会用主光源朝向，不知道是trick还是什么但是看起来很难受;w;;
                        // 2021-09-07 @betairylia
                        /*
                        uint pixelLightCount = GetAdditionalLightsCount();
                        float3 AdditionalLightColor = float3(0.0, 0.0, 0.0);
                        half shadow = 1.0f;
                        for (uint lightIndex = 0u; lightIndex < pixelLightCount; ++lightIndex)
                        {
                            Light light = GetAdditionalLight(lightIndex, i.worldPos);
                            shadow *= AdditionalLightRealtimeShadow(lightIndex, i.worldPos);
                            AdditionalLightColor += light.color * light.distanceAttenuation * light.shadowAttenuation;
                            // 这里只加了光照颜色，pbr只用了主光源方向做了一次
                        }
                        float4 SHADOW_COORDS = TransformWorldToShadowCoord(i.worldPos);
                        Light mainLight = GetMainLight(SHADOW_COORDS);
                        mainLight.color.rgb += (AdditionalLightColor + i.fogFactorAndvertexLight.rgb);
                        
                        #if defined(_SCREEN_SPACE_OCCLUSION)
                            float2 normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.pos);
                            AmbientOcclusionFactor aoFactor = GetScreenSpaceAmbientOcclusion(normalizedScreenSpaceUV);
                            mainLight.color *= aoFactor.directAmbientOcclusion;
                            so.occlusion = min(so.occlusion, aoFactor.indirectAmbientOcclusion);
                        #endif

                        shadow *= MainLightRealtimeShadow(SHADOW_COORDS);

                        float3 BaseColor = so.albedo;
                        float Metallic = so.metallic;
                        float3 F0 = lerp(0.04, BaseColor, Metallic);
                        float AO = so.occlusion;
                        float smoothness = so.smoothness;
                        float TEMProughness = 1 - smoothness;
                        float roughness = pow(TEMProughness, 2);

                        float3 position = i.worldPos;
                        float3 N = i.worldNormal;
                        float3 L = normalize(mainLight.direction);
                        float3 V = normalize(_WorldSpaceCameraPos - position);
                        float3 H = normalize(V + L);
                        float3 R = reflect(-V, N);

                        float NoV = max(saturate(dot(N, V)), 0.000001);
                        float NoL = max(
                            max(saturate(dot(N, L)), 0.000001),
                            ((material.properties & MATPROP_FOILAGE_BIT) > 0) * 0.75
                        );
                        float HoV = max(saturate(dot(H, V)), 0.000001);
                        float NoH = max(saturate(dot(H, N)), 0.000001);
                        float LoH = max(saturate(dot(H, L)), 0.000001);

                        float D = DistributionGGX(NoH, roughness);
                        float k = pow(1 + roughness, 2) / 8;
                        float G = GeometrySmith(NoV, NoL, k);
                        float3 F = FresnelSchlick(LoH, F0);

                        float3 specular = D * G * F / (4 * NoV * NoL);

                        float3 ks = F;
                        float3 kd = (1 - ks) * (1 - Metallic);
                        float3 diffuse = kd * BaseColor / PI;

                        float3 DirectColor = (
                            diffuse
                            + specular
                        ) * NoL * PI * mainLight.color;

                        float3 SH = SH_Process(N) * AO;
                        float3 IndirKS = IndirFresnelSchlick(NoV, F0, roughness);
                        float3 IndirKD = (1 - IndirKS) * (1 - Metallic);
                        float3 IndirDiffColor = SH * IndirKD * BaseColor;

                        roughness = roughness * (1.7 - 0.7 * roughness);
                        float mip_level = roughness * 6.0;
                        float3 IndirSpecularBaseColor = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, R, mip_level) * AO;

                        float surfaceReduction = 1.0 / (roughness * roughness + 1.0);
                        float ReflectivitySpecular;
                        #if defined(SHADER_API_GLES)
                            ReflectivitySpecular = specular.r; // Red channel - because most metals are either monocrhome or with redish/yellowish tint
                        #else
                            ReflectivitySpecular = max(max(specular.r, specular.g), specular.b);
                        #endif
                        half grazingTerm = saturate((1 - roughness) + (1 - (1 - ReflectivitySpecular)));
                        half t = pow(1 - NoV, 5);
                        float3 FresnelLerp = lerp(F0, grazingTerm, t);

                        float3 IndirSpecularResult = IndirSpecularBaseColor * FresnelLerp * surfaceReduction;

                        float3 IndirColor = IndirSpecularResult + IndirDiffColor;
                        
                        float3 final_color = IndirColor * shadow + DirectColor * shadow + so.emission;
                        */

                        //PBR

                        
                        //Unity PBR

                        // Fog computation
                        float3 position = i.worldPos;
                        float3 V = normalize(_WorldSpaceCameraPos - position);

                        InputData inputData;
                        inputData = (InputData)0;
                        inputData.positionWS = i.worldPos;
                        inputData.normalWS = normalize(i.worldNormal);
                        // inputData.normalWS = so.normalTS;

                        inputData.shadowCoord = SHADOW_COORDS;
                        inputData.viewDirectionWS = normalize(_WorldSpaceCameraPos - i.worldPos);
                        inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.pos);

                        inputData.bakedGI = SH_Process(inputData.normalWS);

                        float4 final_color = UniversalFragmentPBR_custom(inputData, so, style);

                        //Unity PBR
                        
                        _ShadowAlpha = tex.a;

                        final_color.rgb = CustomMixFog(final_color.rgb, i.fogFactorAndvertexLight.a, i.worldNormal, L, V, mainLight.color.rgb);

                        // return half4(inputData.normalWS.rgb, 1.0);
                        return half4(final_color.rgb, 1.0);
                    }

                    ENDHLSL

                }
                pass
                {
                    Tags { "LightMode" = "ShadowCaster" }
                    HLSLPROGRAM

                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"


                    #pragma vertex vert
                    #pragma fragment frag
                    #pragma exclude_renderers gles gles3 glcore
                    #pragma target 4.5

                    // -------------------------------------
                    // Material Keywords
                    #pragma shader_feature_local_fragment _ALPHATEST_ON
                    #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

                    //--------------------------------------
                    // GPU Instancing
                    #pragma multi_compile_instancing
                    #pragma multi_compile _ DOTS_INSTANCING_ON

                    float3 _LightDirection;

                    // #pragma require 2darray
                    v2f vert(appdata_t v)
                    {
                        v2f o = UnpackVertex(v);
                        Light mainLight = GetMainLight();
                        UNITY_SETUP_INSTANCE_ID(v);
                        UNITY_TRANSFER_INSTANCE_ID(v, o);
                        o.worldPos.xyz = TransformObjectToWorld(v.vertex.xyz);
                        o.worldNormal = TransformObjectToWorldNormal(v.normal);
                        #ifdef LIGHTMAP_OFF
                            #ifndef SPHERICAL_HARMONICS_PER_PIXEL
                                #if UNITY_SHOULD_SAMPLE_SH
                                    o.sh = 0;
                                    o.sh = ShadeSHPerVertex(o.worldNormal, o.sh);
                                #endif
                            #endif
                        #endif

                        #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                            float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                        #else
                            float3 lightDirectionWS = _LightDirection;
                        #endif

                        float invNdotL = 1.0 - saturate(dot(lightDirectionWS, o.worldNormal));
                        float scale = invNdotL * _ShadowBias.y;

                        // normal bias is negative since we want to apply an inset normal offset
                        o.worldPos.xyz = lightDirectionWS.xyz * _ShadowBias.xxx + o.worldPos.xyz;
                        o.worldPos.xyz = o.worldNormal * scale.xxx + o.worldPos.xyz;

                        o.pos = TransformWorldToHClip(o.worldPos.xyz);//【不确定】遗留一个深度未解决，死活汇入不了光照方向
                        #if UNITY_REVERSED_Z
                            o.pos.z = min(o.pos.z, o.pos.w * UNITY_NEAR_CLIP_VALUE);
                        #else
                            o.pos.z = max(o.pos.z, o.pos.w * UNITY_NEAR_CLIP_VALUE);
                        #endif

                        o.clipZW.xy = o.pos.zw;

                        return o;
                    }

                    float4 frag(v2f i, out float depth : SV_DEPTH) : SV_Target
                    {
                        
                        UNITY_SETUP_INSTANCE_ID(i);
                        SurfaceData so;
                        so = (SurfaceData)0;

                        so.normalTS = i.worldNormal;

                        float2 targetUV = i.arrayUV.xy;
                        BlockMatInfo material;

                        bool sampleTexture = false;

                        // Fine Structures
                        if (i.arrayUV.z < 0)
                        {
                            // #define DEBUG
                            // Following http://jojendersie.de/rendering-huge-amounts-of-voxels-2/#comment-400

                            i.viewDir = normalize(i.viewDir);

                            // Current LOD level (0, 1, 2, 3, 4)
                            uint LOD = (uint) (-i.arrayUV.z);

                            ////////////////////////////////////////////////////////////////////////////////////////////

                            // RaycastResult res = Raycast(float3(i.arrayUV.xy, 0), i.blockSpacePos, i.viewDir, LOD);
                            // RaycastResult res = Raycast(asuint(i.arrayUV.x), i.blockSpacePos, i.viewDir, LOD);
                            RaycastResult res = Raycast(i.chiselID, i.blockSpacePos, i.viewDir, LOD);
                            // RaycastResult res = Raycast_old_uintID(i.chiselID, i.blockSpacePos, i.viewDir, LOD);

                            ////////////////////////////////////////////////////////////////////////////////////////////

                            // Color for test

                            // Retrieve world pos, world normal & depth
                            // TODO: maybe use clip space in FS and do the mul's in VS ...
                            i.worldPos = i.worldPos + float4(mul(_LocalToWorld, float4(res.blockspacePosOffset, 0)).xyz, 0);
                            i.worldNormal = mul((float3x3)_LocalToWorld, res.blockspaceNormal);
                            i.clipZW = TransformWorldToHClip(i.worldPos).zw;

                            // Retrieve block UVs

                            // Color block
                            if (res.blockID >= 32768)
                            {

                                material = GetMaterial(ErrorMatID);
                                i.viewDir = float3(0, 0, 0);
                                i.arrayUV = float3(0, 0, 0);
                                targetUV = float2(0, 0);
                            }
                            else
                            {
                                material = GetMaterial(GetMaterialID(res.blockID, res.faceID));
                                i.arrayUV.xy = material.uvOrigin;
                                i.viewDir.xy = material.uvSpan;
                                targetUV = res.blockUV;
                                i.color = float4(1, 1, 1, 1);
                            }

                            // TODO: FIXME: This is UV hotfix
                            i.faceID = res.faceID;
                            
                            so.alpha = 1.0f;

                            // i.color = float4(res.blockspaceNormal, 1);

                        }
                        else
                        {
                            material = GetMaterial(i.matID);
                            targetUV = float2(frac(i.blockSpacePos.x), frac(i.blockSpacePos.y));
                        }

                        // TODO: FIXME: This is UV Hotfix
                        if (i.faceID < 2)
                        {
                            targetUV.xy = targetUV.yx;
                        }
                        //i.color.rg = targetUV;

                        // TODO: Warp UV unpacking into some function for shadowcasters

                        // Apply material properties
                        uint2 idx2 = uint2(0, 0);
                        float test = 0.0f;
                        float2 size = float2(1.0f, 1.0f) / float2(material.spriteSheets);
                        
                        // TODO: Branchless ? how ...
                        // TODO: move some to VS ?
                        if ((material.properties & MATPROP_BIG_BIT) > 0)
                        {
                            // TODO: Carry information from vertex stage, otherwise this won't work for rotation / scaling

                            int2 faceworldpos;
                            if (i.faceID < 6)
                            {
                                faceworldpos = i.worldPos.xy;
                            }// Zn, Zp
                            if (i.faceID < 4)
                            {
                                faceworldpos = i.worldPos.zx;
                            }// Yn, Yp
                            if (i.faceID < 2)
                            {
                                faceworldpos = i.worldPos.zy;
                            }// Xn, Xp

                            faceworldpos += int2(65536, 65536); // must be larger than world size

                            idx2 = faceworldpos % material.spriteSheets;
                        }

                        // TODO: MATPROP_CTMENUM_BIT
                        // TODO: MATPROP_CTMCHISEL_BIT

                        if ((material.properties & MATPROP_RANDOM_BIT) > 0)
                        {
                            // TODO: random
                            float2 faceworldpos;
                            if (i.faceID < 6)
                            {
                                faceworldpos = i.worldPos.xy;
                            }// Zn, Zp
                            if (i.faceID < 4)
                            {
                                faceworldpos = i.worldPos.zx;
                            }// Yn, Yp
                            if (i.faceID < 2)
                            {
                                faceworldpos = i.worldPos.zy;
                            }// Xn, Xp

                            faceworldpos = faceworldpos / 512.0;

                            int LOD = (uint) (-i.arrayUV.z);

                            uint2 rng = SAMPLE_TEXTURE2D_LOD(_Random, sampler_Random, faceworldpos.xy, LOD).rg * 256;//【不确定】
                            idx2 = uint2(rng % material.spriteSheets);
                        }

                        if ((material.properties & MATPROP_ANIMATED_BIT) > 0)
                        {
                            // TODO: control speed
                            // TODO: Add total frame count in material info
                            // uint idx = uint(trunc(_Time.y * 8)) % (); // 8FPS
                            // idx2 = ;

                        }

                        // TODO: MATPROP_SSR_BIT
                        // TODO: MATPROP_SSS_BIT
                        // TODO: MATPROP_FOILAGE_BIT

                        // Ugly fix for uv bleeding
                        // well at least it works xD
                        // padding should always be smaller than 1 over "actual subsprite" resolution
                        // which should be 1/32 = 0.03125.
                        targetUV = clamp(targetUV, 0.01, 0.99);

                        // Convert from subsprite UV to sprite UV
                        targetUV = targetUV * size + idx2 * size;

                        // Convert from sprite UV to atlas UV
                        targetUV = i.arrayUV.xy + targetUV * i.viewDir.xy;
                        sampleTexture = length(i.viewDir.xy) > 0.0001;

                        ////////////////////// Regular Blocks Rendering //////////////////////

                        #ifdef DEBUG
                            // so.Albedo = i.color.rgb;
                            so.albedo = float3(float(idx2.x % 5) / 5, float(idx2.y % 5) / 5, test);
                        #else
                            if (i.color.a <= 0.001f)
                            {
                                clip(-1.0);
                            }

                            float4 tex;
                            if (sampleTexture)
                            {
                                //int LOD = (uint) (-i.arrayUV.z);
                                //tex = tex2Dlod(_MainAtlas, float4(targetUV, 0, 0));
                                tex = SAMPLE_TEXTURE2D(_MainAtlas, sampler_MainAtlas, targetUV);
                            }
                            else
                            {
                                tex = float4(1, 1, 1, 1);
                            }

                            //float4 tex = float4(1, 1, 1, 1);
                            
                            if (tex.a <= 0.5f)
                            {
                                discard;
                            }// cutoff

                            // Seems like it is not being used like this ... ;w;
                            //if ((material.flags & MAT_CUTOFF > 0) && tex.a <= 0.5f) { discard; } // cutoff
                            
                            // Albedo comes from a texture tinted by color
                        #endif
                        // Metallic and smoothness come from slider variables

                        if ((material.properties & MATPROP_FOILAGE_BIT) > 0)
                        {
                            so.normalTS = -_SunDirc.xyz;
                            // so.Emission = _SunDirc.xyz;
                            // so.Albedo = half3(0, 0, 0);
                            // so.Albedo = so.Albedo * 0.4;

                        }

                        // so.Occlusion = 0;

                        /////////////////////////// Fill G-Buffer ///////////////////////////
                        
                        //so.Albedo = float4(i.clipPos / 100.0);
                        //so.Albedo = float4(i.worldPos / 100.0);
                        //o.depth = i.depth.x * _ProjectionParams.w;

                        #if (SHADER_API_OPENGL || SHADER_API_GLCORE || SHADER_API_GLES || SHADER_API_GLES3)
                            depth = (i.clipZW.x / i.clipZW.y) * .5 + .5;// / i.pos.w; // Apperently it was linear in deferred, KEKW
                        #else
                            depth = (i.clipZW.x / i.clipZW.y);// / i.pos.w; // Apperently it was linear in deferred, KEKW
                        #endif
                        /*
                        o = ApplyUnityStandardLit(i, o, so);

                        if ((material.properties & MATPROP_ZEROSPEC_BIT) > 0)
                        {
                            half spec = 0.0f;
                            o.specular = half4(spec, spec, spec, spec);
                        }
                        */
                        _ShadowAlpha = tex.a;
                        return half4(0.0, 0.0, 0.0, 1.0);
                    }

                    ENDHLSL

                }
                
                pass
                {
                    Tags { "LightMode" = "DepthOnly" }

                    ZWrite On
                    ColorMask 0
                    HLSLPROGRAM

                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"


                    #pragma vertex vert
                    #pragma fragment frag
                    #pragma exclude_renderers gles gles3 glcore
                    #pragma target 4.5

                    // -------------------------------------
                    // Material Keywords
                    #pragma shader_feature_local_fragment _ALPHATEST_ON
                    #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

                    //--------------------------------------
                    // GPU Instancing
                    #pragma multi_compile_instancing
                    #pragma multi_compile _ DOTS_INSTANCING_ON

                    // #pragma require 2darray
                    v2f vert(appdata_t v)
                    {
                        v2f o = UnpackVertex(v);
                        Light mainLight = GetMainLight();
                        UNITY_SETUP_INSTANCE_ID(v);
                        UNITY_TRANSFER_INSTANCE_ID(v, o);
                        // o.pos = TransformObjectToHClip(v.vertex);
                        // o.worldPos.xyz = TransformObjectToWorld(v.vertex.xyz);
                        // o.worldNormal = TransformObjectToWorldNormal(v.normal);
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

                    float4 frag(v2f i, out float depth : SV_DEPTH) : SV_Target
                    {

                        UNITY_SETUP_INSTANCE_ID(i);
                        SurfaceData so;
                        so = (SurfaceData)0;

                        so.normalTS = i.worldNormal;

                        float2 targetUV = i.arrayUV.xy;
                        BlockMatInfo material;

                        bool sampleTexture = false;

                        // Fine Structures
                        if (i.arrayUV.z < 0)
                        {
                            // #define DEBUG
                            // Following http://jojendersie.de/rendering-huge-amounts-of-voxels-2/#comment-400

                            i.viewDir = normalize(i.viewDir);

                            // Current LOD level (0, 1, 2, 3, 4)
                            uint LOD = (uint) (-i.arrayUV.z);

                            ////////////////////////////////////////////////////////////////////////////////////////////

                            // RaycastResult res = Raycast(float3(i.arrayUV.xy, 0), i.blockSpacePos, i.viewDir, LOD);
                            // RaycastResult res = Raycast(asuint(i.arrayUV.x), i.blockSpacePos, i.viewDir, LOD);
                            RaycastResult res = Raycast(i.chiselID, i.blockSpacePos, i.viewDir, LOD);
                            // RaycastResult res = Raycast_old_uintID(i.chiselID, i.blockSpacePos, i.viewDir, LOD);

                            ////////////////////////////////////////////////////////////////////////////////////////////

                            // Color for test

                            // Retrieve world pos, world normal & depth
                            // TODO: maybe use clip space in FS and do the mul's in VS ...
                            i.worldPos = i.worldPos + float4(mul(_LocalToWorld, float4(res.blockspacePosOffset, 0)).xyz, 0);
                            i.worldNormal = mul((float3x3)_LocalToWorld, res.blockspaceNormal);
                            i.clipZW = TransformWorldToHClip(i.worldPos).zw;

                            // Retrieve block UVs

                            // Color block
                            if (res.blockID >= 32768)
                            {

                                material = GetMaterial(ErrorMatID);
                                i.viewDir = float3(0, 0, 0);
                                i.arrayUV = float3(0, 0, 0);
                                targetUV = float2(0, 0);
                            }
                            else
                            {
                                material = GetMaterial(GetMaterialID(res.blockID, res.faceID));
                                i.arrayUV.xy = material.uvOrigin;
                                i.viewDir.xy = material.uvSpan;
                                targetUV = res.blockUV;
                                i.color = float4(1, 1, 1, 1);
                            }

                            // TODO: FIXME: This is UV hotfix
                            i.faceID = res.faceID;
                            
                            so.alpha = 1.0f;

                            // i.color = float4(res.blockspaceNormal, 1);

                        }
                        else
                        {
                            material = GetMaterial(i.matID);
                            targetUV = float2(frac(i.blockSpacePos.x), frac(i.blockSpacePos.y));
                        }

                        // TODO: FIXME: This is UV Hotfix
                        if (i.faceID < 2)
                        {
                            targetUV.xy = targetUV.yx;
                        }
                        //i.color.rg = targetUV;

                        // TODO: Warp UV unpacking into some function for shadowcasters

                        // Apply material properties
                        uint2 idx2 = uint2(0, 0);
                        float test = 0.0f;
                        float2 size = float2(1.0f, 1.0f) / float2(material.spriteSheets);
                        
                        // TODO: Branchless ? how ...
                        // TODO: move some to VS ?
                        if ((material.properties & MATPROP_BIG_BIT) > 0)
                        {
                            // TODO: Carry information from vertex stage, otherwise this won't work for rotation / scaling

                            int2 faceworldpos;
                            if (i.faceID < 6)
                            {
                                faceworldpos = i.worldPos.xy;
                            }// Zn, Zp
                            if (i.faceID < 4)
                            {
                                faceworldpos = i.worldPos.zx;
                            }// Yn, Yp
                            if (i.faceID < 2)
                            {
                                faceworldpos = i.worldPos.zy;
                            }// Xn, Xp

                            faceworldpos += int2(65536, 65536); // must be larger than world size

                            idx2 = faceworldpos % material.spriteSheets;
                        }

                        // TODO: MATPROP_CTMENUM_BIT
                        // TODO: MATPROP_CTMCHISEL_BIT

                        if ((material.properties & MATPROP_RANDOM_BIT) > 0)
                        {
                            // TODO: random
                            float2 faceworldpos;
                            if (i.faceID < 6)
                            {
                                faceworldpos = i.worldPos.xy;
                            }// Zn, Zp
                            if (i.faceID < 4)
                            {
                                faceworldpos = i.worldPos.zx;
                            }// Yn, Yp
                            if (i.faceID < 2)
                            {
                                faceworldpos = i.worldPos.zy;
                            }// Xn, Xp

                            faceworldpos = faceworldpos / 512.0;

                            int LOD = (uint) (-i.arrayUV.z);

                            uint2 rng = SAMPLE_TEXTURE2D_LOD(_Random, sampler_Random, faceworldpos.xy, LOD).rg * 256;//【不确定】
                            idx2 = uint2(rng % material.spriteSheets);
                        }

                        if ((material.properties & MATPROP_ANIMATED_BIT) > 0)
                        {
                            // TODO: control speed
                            // TODO: Add total frame count in material info
                            // uint idx = uint(trunc(_Time.y * 8)) % (); // 8FPS
                            // idx2 = ;

                        }

                        // TODO: MATPROP_SSR_BIT
                        // TODO: MATPROP_SSS_BIT
                        // TODO: MATPROP_FOILAGE_BIT

                        // Ugly fix for uv bleeding
                        // well at least it works xD
                        // padding should always be smaller than 1 over "actual subsprite" resolution
                        // which should be 1/32 = 0.03125.
                        targetUV = clamp(targetUV, 0.01, 0.99);

                        // Convert from subsprite UV to sprite UV
                        targetUV = targetUV * size + idx2 * size;

                        // Convert from sprite UV to atlas UV
                        targetUV = i.arrayUV.xy + targetUV * i.viewDir.xy;
                        sampleTexture = length(i.viewDir.xy) > 0.0001;

                        ////////////////////// Regular Blocks Rendering //////////////////////

                        #ifdef DEBUG
                            // so.Albedo = i.color.rgb;
                            so.albedo = float3(float(idx2.x % 5) / 5, float(idx2.y % 5) / 5, test);
                        #else
                            if (i.color.a <= 0.001f)
                            {
                                clip(-1.0);
                            }

                            float4 tex;
                            if (sampleTexture)
                            {
                                //int LOD = (uint) (-i.arrayUV.z);
                                //tex = tex2Dlod(_MainAtlas, float4(targetUV, 0, 0));
                                tex = SAMPLE_TEXTURE2D(_MainAtlas, sampler_MainAtlas, targetUV);
                            }
                            else
                            {
                                tex = float4(1, 1, 1, 1);
                            }

                            //float4 tex = float4(1, 1, 1, 1);
                            
                            if (tex.a <= 0.5f)
                            {
                                discard;
                            }// cutoff

                            // Seems like it is not being used like this ... ;w;
                            //if ((material.flags & MAT_CUTOFF > 0) && tex.a <= 0.5f) { discard; } // cutoff
                            
                            // Albedo comes from a texture tinted by color
                        #endif
                        // Metallic and smoothness come from slider variables

                        if ((material.properties & MATPROP_FOILAGE_BIT) > 0)
                        {
                            so.normalTS = -_SunDirc.xyz;
                            // so.Emission = _SunDirc.xyz;
                            // so.Albedo = half3(0, 0, 0);
                            // so.Albedo = so.Albedo * 0.4;

                        }

                        // so.Occlusion = 0;

                        /////////////////////////// Fill G-Buffer ///////////////////////////
                        
                        //so.Albedo = float4(i.clipPos / 100.0);
                        //so.Albedo = float4(i.worldPos / 100.0);
                        //o.depth = i.depth.x * _ProjectionParams.w;

                        #if (SHADER_API_OPENGL || SHADER_API_GLCORE || SHADER_API_GLES || SHADER_API_GLES3)
                            depth = (i.clipZW.x / i.clipZW.y) * .5 + .5;// / i.pos.w; // Apperently it was linear in deferred, KEKW
                        #else
                            depth = (i.clipZW.x / i.clipZW.y);// / i.pos.w; // Apperently it was linear in deferred, KEKW
                        #endif
                        /*
                        o = ApplyUnityStandardLit(i, o, so);

                        if ((material.properties & MATPROP_ZEROSPEC_BIT) > 0)
                        {
                            half spec = 0.0f;
                            o.specular = half4(spec, spec, spec, spec);
                        }
                        */
                        _ShadowAlpha = tex.a;
                        return half4(0.0, 0.0, 0.0, 1.0);
                    }
                    ENDHLSL

                }
                pass
                {
                    Tags { "LightMode" = "DepthNormals" }

                    ZWrite On
                    HLSLPROGRAM

                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
                    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"


                    #pragma vertex vert
                    #pragma fragment frag
                    #pragma exclude_renderers gles gles3 glcore
                    #pragma target 4.5

                    // -------------------------------------
                    // Material Keywords
                    #pragma shader_feature_local _NORMALMAP
                    #pragma shader_feature_local_fragment _ALPHATEST_ON
                    #pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

                    //--------------------------------------
                    // GPU Instancing
                    #pragma multi_compile_instancing
                    #pragma multi_compile _ DOTS_INSTANCING_ON

                    // #pragma require 2darray
                    v2f vert(appdata_t v)
                    {
                        v2f o = UnpackVertex(v);
                        Light mainLight = GetMainLight();
                        UNITY_SETUP_INSTANCE_ID(v);
                        UNITY_TRANSFER_INSTANCE_ID(v, o);
                        // o.pos = TransformObjectToHClip(v.vertex);
                        // o.worldPos.xyz = TransformObjectToWorld(v.vertex.xyz);
                        // // o.worldNormal = TransformObjectToWorldNormal(v.normal);
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

                    float4 frag(v2f i, out float depth : SV_DEPTH) : SV_Target
                    {

                        UNITY_SETUP_INSTANCE_ID(i);
                        SurfaceData so;
                        so = (SurfaceData)0;

                        so.normalTS = i.worldNormal;

                        float2 targetUV = i.arrayUV.xy;
                        BlockMatInfo material;

                        bool sampleTexture = false;

                        // Fine Structures
                        if (i.arrayUV.z < 0)
                        {
                            // #define DEBUG
                            // Following http://jojendersie.de/rendering-huge-amounts-of-voxels-2/#comment-400

                            i.viewDir = normalize(i.viewDir);

                            // Current LOD level (0, 1, 2, 3, 4)
                            uint LOD = (uint) (-i.arrayUV.z);

                            ////////////////////////////////////////////////////////////////////////////////////////////

                            // RaycastResult res = Raycast(float3(i.arrayUV.xy, 0), i.blockSpacePos, i.viewDir, LOD);
                            // RaycastResult res = Raycast(asuint(i.arrayUV.x), i.blockSpacePos, i.viewDir, LOD);
                            RaycastResult res = Raycast(i.chiselID, i.blockSpacePos, i.viewDir, LOD);
                            // RaycastResult res = Raycast_old_uintID(i.chiselID, i.blockSpacePos, i.viewDir, LOD);

                            ////////////////////////////////////////////////////////////////////////////////////////////

                            // Color for test

                            // Retrieve world pos, world normal & depth
                            // TODO: maybe use clip space in FS and do the mul's in VS ...
                            i.worldPos = i.worldPos + float4(mul(_LocalToWorld, float4(res.blockspacePosOffset, 0)).xyz, 0);
                            i.worldNormal = mul((float3x3)_LocalToWorld, res.blockspaceNormal);
                            i.clipZW = TransformWorldToHClip(i.worldPos).zw;

                            // Retrieve block UVs

                            // Color block
                            if (res.blockID >= 32768)
                            {

                                material = GetMaterial(ErrorMatID);
                                i.viewDir = float3(0, 0, 0);
                                i.arrayUV = float3(0, 0, 0);
                                targetUV = float2(0, 0);
                            }
                            else
                            {
                                material = GetMaterial(GetMaterialID(res.blockID, res.faceID));
                                i.arrayUV.xy = material.uvOrigin;
                                i.viewDir.xy = material.uvSpan;
                                targetUV = res.blockUV;
                                i.color = float4(1, 1, 1, 1);
                            }

                            // TODO: FIXME: This is UV hotfix
                            i.faceID = res.faceID;
                            
                            so.alpha = 1.0f;

                            // i.color = float4(res.blockspaceNormal, 1);

                        }
                        else
                        {
                            material = GetMaterial(i.matID);
                            targetUV = float2(frac(i.blockSpacePos.x), frac(i.blockSpacePos.y));
                        }

                        // TODO: FIXME: This is UV Hotfix
                        if (i.faceID < 2)
                        {
                            targetUV.xy = targetUV.yx;
                        }
                        //i.color.rg = targetUV;

                        // TODO: Warp UV unpacking into some function for shadowcasters

                        // Apply material properties
                        uint2 idx2 = uint2(0, 0);
                        float test = 0.0f;
                        float2 size = float2(1.0f, 1.0f) / float2(material.spriteSheets);
                        
                        // TODO: Branchless ? how ...
                        // TODO: move some to VS ?
                        if ((material.properties & MATPROP_BIG_BIT) > 0)
                        {
                            // TODO: Carry information from vertex stage, otherwise this won't work for rotation / scaling

                            int2 faceworldpos;
                            if (i.faceID < 6)
                            {
                                faceworldpos = i.worldPos.xy;
                            }// Zn, Zp
                            if (i.faceID < 4)
                            {
                                faceworldpos = i.worldPos.zx;
                            }// Yn, Yp
                            if (i.faceID < 2)
                            {
                                faceworldpos = i.worldPos.zy;
                            }// Xn, Xp

                            faceworldpos += int2(65536, 65536); // must be larger than world size

                            idx2 = faceworldpos % material.spriteSheets;
                        }

                        // TODO: MATPROP_CTMENUM_BIT
                        // TODO: MATPROP_CTMCHISEL_BIT

                        if ((material.properties & MATPROP_RANDOM_BIT) > 0)
                        {
                            // TODO: random
                            float2 faceworldpos;
                            if (i.faceID < 6)
                            {
                                faceworldpos = i.worldPos.xy;
                            }// Zn, Zp
                            if (i.faceID < 4)
                            {
                                faceworldpos = i.worldPos.zx;
                            }// Yn, Yp
                            if (i.faceID < 2)
                            {
                                faceworldpos = i.worldPos.zy;
                            }// Xn, Xp

                            faceworldpos = faceworldpos / 512.0;

                            int LOD = (uint) (-i.arrayUV.z);

                            uint2 rng = SAMPLE_TEXTURE2D_LOD(_Random, sampler_Random, faceworldpos.xy, LOD).rg * 256;//【不确定】
                            idx2 = uint2(rng % material.spriteSheets);
                        }

                        if ((material.properties & MATPROP_ANIMATED_BIT) > 0)
                        {
                            // TODO: control speed
                            // TODO: Add total frame count in material info
                            // uint idx = uint(trunc(_Time.y * 8)) % (); // 8FPS
                            // idx2 = ;

                        }

                        // TODO: MATPROP_SSR_BIT
                        // TODO: MATPROP_SSS_BIT
                        // TODO: MATPROP_FOILAGE_BIT

                        // Ugly fix for uv bleeding
                        // well at least it works xD
                        // padding should always be smaller than 1 over "actual subsprite" resolution
                        // which should be 1/32 = 0.03125.
                        targetUV = clamp(targetUV, 0.01, 0.99);

                        // Convert from subsprite UV to sprite UV
                        targetUV = targetUV * size + idx2 * size;

                        // Convert from sprite UV to atlas UV
                        targetUV = i.arrayUV.xy + targetUV * i.viewDir.xy;
                        sampleTexture = length(i.viewDir.xy) > 0.0001;

                        ////////////////////// Regular Blocks Rendering //////////////////////

                        #ifdef DEBUG
                            // so.Albedo = i.color.rgb;
                            so.albedo = float3(float(idx2.x % 5) / 5, float(idx2.y % 5) / 5, test);
                        #else
                            if (i.color.a <= 0.001f)
                            {
                                clip(-1.0);
                            }

                            float4 tex;
                            if (sampleTexture)
                            {
                                //int LOD = (uint) (-i.arrayUV.z);
                                //tex = tex2Dlod(_MainAtlas, float4(targetUV, 0, 0));
                                tex = SAMPLE_TEXTURE2D(_MainAtlas, sampler_MainAtlas, targetUV);
                            }
                            else
                            {
                                tex = float4(1, 1, 1, 1);
                            }

                            //float4 tex = float4(1, 1, 1, 1);
                            
                            if (tex.a <= 0.5f)
                            {
                                discard;
                            }// cutoff

                            // Seems like it is not being used like this ... ;w;
                            //if ((material.flags & MAT_CUTOFF > 0) && tex.a <= 0.5f) { discard; } // cutoff
                            
                            // Albedo comes from a texture tinted by color
                        #endif
                        // Metallic and smoothness come from slider variables

                        // so.Occlusion = 0;

                        /////////////////////////// Fill G-Buffer ///////////////////////////
                        
                        //so.Albedo = float4(i.clipPos / 100.0);
                        //so.Albedo = float4(i.worldPos / 100.0);
                        //o.depth = i.depth.x * _ProjectionParams.w;

                        #if (SHADER_API_OPENGL || SHADER_API_GLCORE || SHADER_API_GLES || SHADER_API_GLES3)
                            depth = (i.clipZW.x / i.clipZW.y) * .5 + .5;// / i.pos.w; // Apperently it was linear in deferred, KEKW
                        #else
                            depth = (i.clipZW.x / i.clipZW.y);// / i.pos.w; // Apperently it was linear in deferred, KEKW
                        #endif
                        /*
                        o = ApplyUnityStandardLit(i, o, so);

                        if ((material.properties & MATPROP_ZEROSPEC_BIT) > 0)
                        {
                            half spec = 0.0f;
                            o.specular = half4(spec, spec, spec, spec);
                        }
                        */
                        _ShadowAlpha = tex.a;
                        return float4(PackNormalOctRectEncode(TransformWorldToViewDir(i.worldNormal, true)), 0.0, 0.0);
                    }
                    ENDHLSL

                }
            }

            SubShader
            {
                Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }

                ZWrite Off
                Blend SrcAlpha OneMinusSrcAlpha

                Pass
                {
                    HLSLPROGRAM

                    #pragma vertex vert
                    #pragma fragment frag
                    #pragma multi_compile_fwdbase nolightmap nodirlightmap nodynlightmap novertexlight
                    // #pragma require 2darray


                    v2f vert(appdata_t v)
                    {
                        v2f o = UnpackVertex(v);

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

                    half4 frag(v2f i) : SV_Target
                    {
                        //                 SurfaceOutputStandard so;

                        //                 UNITY_INITIALIZE_OUTPUT(SurfaceOutputStandard, so);
                        //                 so.Albedo = float3(1, 1, 1);// i.color;
                        //                 so.Metallic = _Metallic;
                        //                 so.Smoothness = _Glossiness;
                        //                 so.Emission = 0.0;
                        //                 so.Alpha = 1.0;
                        //                 so.Occlusion = 1.0;
                        //                 so.Normal = i.worldNormal;

                        //                 so.Alpha = 0.4;

                        //                 /////////////////////////// Fill G-Buffer ///////////////////////////
                        
                        //                 //so.Albedo = float4(i.clipPos / 100.0);
                        //                 //so.Albedo = float4(i.worldPos / 100.0);
                        //                 GBufferOut o;
                        //                 //o.depth = i.depth.x * _ProjectionParams.w;

                        // #if (SHADER_API_OPENGL || SHADER_API_GLCORE || SHADER_API_GLES || SHADER_API_GLES3)
                        //                 o.depth = (i.clipZW.x / i.clipZW.y) * .5 + .5;// / i.pos.w; // Apperently it was linear in deferred, KEKW
                        // #else
                        //                 o.depth = (i.clipZW.x / i.clipZW.y);// / i.pos.w; // Apperently it was linear in deferred, KEKW
                        // #endif

                        //                 o = ApplyUnityStandardLit(i, o, so);

                        //                 // if((material.properties & MATPROP_ZEROSPEC_BIT) > 0)
                        //                 // {
                        //                 //     half spec = 0.00f;
                        //                 //     o.specular = half4(spec, spec, spec, spec);
                        //                 // }

                        //                 return o;
                        return half4(0.3, 1, 0.7, 0.2);
                    }

                    ENDHLSL

                }
            }
        }

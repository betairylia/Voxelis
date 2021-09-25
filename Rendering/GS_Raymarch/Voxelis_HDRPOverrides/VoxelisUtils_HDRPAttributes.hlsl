#ifndef VOXELIS_UTILS_HDRPATTRIBUTES
#define VOXELIS_UTILS_HDRPATTRIBUTES

// #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/VertMesh.hlsl"
#include "../../../Data/Block.cginc"

struct cs_Vertex
{
    float3 position;
    float3 normal;
    float2 uv;
    uint block_vert_info;
    Block data;
};

float4x4 _LocalToWorld;
float4x4 _WorldToLocal;
StructuredBuffer<cs_Vertex> cs_vbuffer;

void UnpackVertex(int vertexID, inout AttributesMesh inputMesh)
{
    cs_Vertex data = cs_vbuffer[vertexID];

    inputMesh.positionOS = mul(_LocalToWorld, float4(data.position, 1.0f));

#ifdef ATTRIBUTES_NEED_NORMAL
    inputMesh.normalOS = mul((float3x3)_LocalToWorld, data.normal);
#endif
    
#ifdef ATTRIBUTES_NEED_COLOR
    Block blk = data.data;
    uint bid = GetBlockID(blk);
    uint meta = GetBlockMeta(blk);
    
    inputMesh.color = (bid > 0) * half4(
        float((meta >> 12) & (0x000F)) / 15.0,
        float((meta >> 8) & (0x000F)) / 15.0,
        float((meta >> 4) & (0x000F)) / 15.0,
        float((meta) & (0x000F)) / 15.0
    );
#endif
}

#endif // VOXELIS_UTILS
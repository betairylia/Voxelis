#ifndef VOXELIS_UTILS
#define VOXELIS_UTILS

#include "../../Data/Block.cginc"

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

void UnpackVertex_float(int vertexID, out float3 position, out float3 normal, out half4 color)
{
    cs_Vertex data = cs_vbuffer[vertexID];

    position = mul(_LocalToWorld, float4(data.position, 1.0f));
    normal = mul((float3x3)_LocalToWorld, data.normal);
    
    Block blk = data.data;
    uint bid = GetBlockID(blk);
    uint meta = GetBlockMeta(blk);
    
    color = (bid > 0) * half4(
        float((meta >> 12) & (0x000F)) / 15.0,
        float((meta >> 8) & (0x000F)) / 15.0,
        float((meta >> 4) & (0x000F)) / 15.0,
        float((meta) & (0x000F)) / 15.0
    );
}

#endif // VOXELIS_UTILS
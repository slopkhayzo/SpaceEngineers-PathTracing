// The game's packed vertex normal and tangent (VB1 of its meshes:
// MyVertexFormatTexcoordNormalTangentTexindices and kin), read by the traced
// passes (scene_common.hlsli) and written back by the skinning pass
// (skin_vertices.hlsl).

#ifndef VERTEX_PACKING_HLSLI
#define VERTEX_PACKING_HLSLI

// R16G16_UINT: 15-bit x with the z sign in bit 15, 16-bit y.
float3 DecodeNormal(uint raw)
{
    uint u = raw & 0xFFFF;
    uint v = raw >> 16;
    float2 xy = float2(u & 0x7FFF, v) / 32767.0 * 2 - 1;
    float zsign = (u & 0x8000) != 0 ? 1 : -1;
    return float3(xy, zsign * sqrt(saturate(1 - dot(xy, xy))));
}

// R8G8B8A8: two 15-bit values; the z sign in bit 7 of byte 1, the bitangent sign in bit 7 of byte 3.
float4 DecodeTangent(uint raw)
{
    uint b0 = raw & 0xFF, b1 = (raw >> 8) & 0xFF, b2 = (raw >> 16) & 0xFF, b3 = raw >> 24;
    float2 xy = float2(b0 | ((b1 & 0x7F) << 8), b2 | ((b3 & 0x7F) << 8)) / 32767.0 * 2 - 1;
    float zsign = (b1 & 0x80) != 0 ? 1 : -1;
    return float4(xy, zsign * sqrt(saturate(1 - dot(xy, xy))), (b3 & 0x80) != 0 ? 1 : -1);
}

// DecodeNormal's inverse for a unit vector.
uint EncodeNormal(float3 n)
{
    uint2 q = (uint2)round(saturate(n.xy * 0.5 + 0.5) * 32767);
    return q.x | (n.z > 0 ? 0x8000u : 0u) | (q.y << 16);
}

// DecodeTangent's inverse for a unit vector and the bitangent sign.
uint EncodeTangent(float3 t, float bitangentSign)
{
    uint2 q = (uint2)round(saturate(t.xy * 0.5 + 0.5) * 32767);
    uint b0 = q.x & 0xFF;
    uint b1 = ((q.x >> 8) & 0x7F) | (t.z > 0 ? 0x80u : 0u);
    uint b2 = q.y & 0xFF;
    uint b3 = ((q.y >> 8) & 0x7F) | (bitangentSign > 0 ? 0x80u : 0u);
    return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
}

#endif

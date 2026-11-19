// Skinned meshes posed for one object (D3D12 compute, cs_6_0;
// src/native/rt/scene.cpp PoseSkins): the game's vertex skinning
// (USE_SKINNING in Geometry/VertexTemplateBase.hlsli) into the object's own
// geometry, which its BLAS is built from and its geometry records point at.
//
// One thread per index of a part, as the game draws the part with its own
// bone palette (the proxy's bone matrices, through its BonesMapping when it has
// one): the index is copied (32-bit), its vertex posed and written; the
// threads of a vertex write the same values. Source: the mesh's geometry
// (VB0 MyVertexFormatPositionSkinning: half4 position whose w scales xyz,
// half4 bone weights, byte4 bone indices; VB1 MyVertexFormatTexcoordNormalTangentTexindices:
// packed normal, packed tangent, half2 texcoord). Output, per vertex: float3
// position; packed normal, packed tangent, texcoord (12 bytes, VB1's order,
// as the traced passes read old-pipeline attributes); and the previous
// frame's float3 position (kModeCopy), from which the traced views take the
// animation's motion (scene_common.hlsli PreviousPosePoint).
//
// The game: skin = sum of weight * bone; p' = (p, 1) * skin, then divided by
// its w (the weights' sum for affine bones); normal and tangent by the skin's
// 3x3 (normalized later). A bone here is three float4, the columns of its
// VRageMath matrix (row vectors): x' = dot(column0, (p, 1)).

#include "vertex_packing.hlsli"

ByteAddressBuffer Source : register(t0);    // the mesh's geometry page
StructuredBuffer<float4> Bones : register(t1);
RWByteAddressBuffer Output : register(u0);  // the skinned page

cbuffer Skin : register(b0)
{
    uint IndexOffset;            // bytes in Source: the part's first index
    uint Count;                  // posing: the part's indices; copying: the mesh's vertices
    uint IndexSize;              // 2 or 4
    uint BaseVertex;             // added to the part's indices
    uint VertexOffset;           // bytes in Source: VB0 vertex 0 of the mesh
    uint VertexStride;
    uint AttributeOffset;        // bytes in Source: VB1 vertex 0 of the mesh, or 0xFFFFFFFF
    uint AttributeStride;
    uint OutputIndexOffset;      // bytes in Output: the part's first index
    uint OutputPositionOffset;   // bytes in Output: vertex 0 of the mesh (12 bytes each)
    uint OutputAttributeOffset;  // bytes in Output: vertex 0 of the mesh (12 bytes each)
    uint FirstBone;              // in Bones (three float4 each): the part's palette
    uint LastBone;               // the object's last bone
    uint FirstIndex;             // of this dispatch (an index or a vertex)
    uint OutputPreviousOffset;   // bytes in Output: vertex 0's position in the previous frame (12 bytes each)
    uint Mode;                   // kModePose, kModeCopy
};

static const uint kNoAttributes = 0xFFFFFFFF;
static const uint kModePose = 0;
// The positions so far become the previous frame's, per vertex (before posing: the motion vectors of the
// animation; right after a first pose: no motion).
static const uint kModeCopy = 1;

float4 Half4(uint2 raw)
{
    return float4(f16tof32(raw.x), f16tof32(raw.x >> 16), f16tof32(raw.y), f16tof32(raw.y >> 16));
}

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint i = FirstIndex + id.x;
    if (i >= Count)
        return;
    if (Mode == kModeCopy)
    {
        Output.Store3(OutputPreviousOffset + i * 12, Output.Load3(OutputPositionOffset + i * 12));
        return;
    }
    uint index;
    if (IndexSize == 4)
    {
        index = Source.Load(IndexOffset + i * 4);
    }
    else
    {
        uint address = IndexOffset + i * 2;
        uint word = Source.Load(address & ~3u);
        index = (address & 2) != 0 ? word >> 16 : word & 0xFFFF;
    }
    Output.Store(OutputIndexOffset + i * 4, index);

    uint vertex = BaseVertex + index;
    uint address = VertexOffset + vertex * VertexStride;
    float4 packed = Half4(Source.Load2(address));
    float3 position = packed.xyz * packed.w;
    float4 weights = Half4(Source.Load2(address + 8));
    uint indices = Source.Load(address + 16);
    float3x4 skin = 0;
    [unroll]
    for (uint k = 0; k < 4; k++)
    {
        uint bone = min(FirstBone + ((indices >> (k * 8)) & 0xFF), LastBone) * 3;
        skin += weights[k] * float3x4(Bones[bone], Bones[bone + 1], Bones[bone + 2]);
    }
    float total = dot(weights, 1);
    bool posed = total > 1e-6;
    Output.Store3(OutputPositionOffset + vertex * 12,
                  asuint(posed ? mul(skin, float4(position, 1)) / total : position));

    if (AttributeOffset == kNoAttributes)
        return;
    uint3 attributes = Source.Load3(AttributeOffset + vertex * AttributeStride);
    if (posed)
    {
        float3 normal = mul((float3x3)skin, DecodeNormal(attributes.x));
        float4 tangent = DecodeTangent(attributes.y);
        float3 posedTangent = mul((float3x3)skin, tangent.xyz);
        if (dot(normal, normal) > 0)
            attributes.x = EncodeNormal(normalize(normal));
        if (dot(posedTangent, posedTangent) > 0)
            attributes.y = EncodeTangent(normalize(posedTangent), tangent.w);
    }
    Output.Store3(OutputAttributeOffset + vertex * 12, attributes);
}

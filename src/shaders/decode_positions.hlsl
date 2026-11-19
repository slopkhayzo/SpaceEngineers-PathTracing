// Vertex positions of a mesh in the transport buffer -> float3, the BLAS
// build's input (D3D12 compute, cs_6_0; src/native/rt/scene.cpp).
//
// Format 0: the game's packed position, a half4 whose w scales xyz
// (PositionPacker; unpack_position_and_scale in VertexTransformations.hlsli).
// Format 1: float3 (voxels). The position is the first element of a vertex.

ByteAddressBuffer Source : register(t0);
RWByteAddressBuffer Positions : register(u0);

cbuffer Decode : register(b0)
{
    uint SourceOffset;       // bytes: vertex 0 in Source
    uint Stride;             // bytes per source vertex
    uint Count;              // vertices of the mesh
    uint DestinationOffset;  // bytes: vertex 0 in Positions (12 bytes each)
    uint Format;
    uint FirstVertex;        // of this dispatch
};

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint vertex = FirstVertex + id.x;
    if (vertex >= Count)
        return;
    uint address = SourceOffset + vertex * Stride;
    float3 position;
    if (Format == 0)
    {
        uint2 raw = Source.Load2(address);
        float4 packed = float4(f16tof32(raw.x), f16tof32(raw.x >> 16), f16tof32(raw.y), f16tof32(raw.y >> 16));
        position = packed.xyz * packed.w;
    }
    else
    {
        position = asfloat(Source.Load3(address));
    }
    Positions.Store3(DestinationOffset + vertex * 12, asuint(position));
}

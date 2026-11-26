// D3D11 (cs_5_0), the geometry transport's fallback path (scene.cpp): the bytes D3D11 copied into the staging
// buffer, written into the NT-shared R32_UINT texture, 4096 words per row. D3D12 copies the rows back into a buffer
// with a 16 KB row pitch, so the words come out in their order. For drivers that do not share legacy-shared buffers
// with D3D12 (NT-handle textures are the sharing path that works both ways; research/docs/amd-fsr-plan.md §4.8).

ByteAddressBuffer Staged : register(t0);
RWTexture2D<uint> Transport : register(u0);

cbuffer Constants : register(b0)
{
    uint Words;  // the words to write, from the start
    uint3 Unused;
};

static const uint kRowWords = 4096;

// Dispatch(kRowWords / 256, rows, 1).
[numthreads(256, 1, 1)]
void main(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
{
    uint x = group.x * 256 + thread.x;
    uint index = group.y * kRowWords + x;
    if (index < Words)
        Transport[uint2(x, group.y)] = Staged.Load(index * 4);
}

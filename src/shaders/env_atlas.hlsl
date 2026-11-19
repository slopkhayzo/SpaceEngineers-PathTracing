// One of the game's environment probe cubemaps (MyEnvironmentProbe, the IBL
// its ambient light samples) into a 2D atlas D3D12 can open: the first mip of
// its six faces (+X, -X, +Y, -Y, +Z, -Z) side by side (D3D11 compute, cs_5_0;
// rt_core.cpp runs it at the gate, primary_view.hlsl samples the atlas). The
// cubemaps themselves (R11G11B10, mipped) cannot be shared with D3D12.

Texture2DArray<float4> Faces : register(t0);
RWTexture2D<float4> Atlas : register(u0);

cbuffer AtlasConstants : register(b0)
{
    uint FaceSize;
    uint3 Padding;
};

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= FaceSize * 6 || id.y >= FaceSize)
        return;
    Atlas[id.xy] = float4(Faces.Load(int4(id.x % FaceSize, id.y, id.x / FaceSize, 0)).rgb, 1);
}

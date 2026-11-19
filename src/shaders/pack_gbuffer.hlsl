// G-buffer pack (D3D11 compute, cs_5_0), run at the gate right before the
// lighting replays: converts the game's G-buffer (read the same way as its
// GBuffer/GBuffer.hlsli) into what the D3D12 side reads, in D3D11-owned
// shared textures: linear view depth, octahedral world normal + roughness,
// linear base color + metalness.

Texture2D<float> Depth : register(t0);       // R32_FLOAT_X8X24 view of the game's depth, reverse Z
Texture2D<float4> Gbuffer0 : register(t1);   // rgb base color (sRGB view: linear on load), a LOD
Texture2D<float4> Gbuffer1 : register(t2);   // xy view-space normal (spheremap transform), z material AO
Texture2D<float4> Gbuffer2 : register(t3);   // r metalness, g gloss, b emissive, a coverage

RWTexture2D<float> ViewZ : register(u0);                 // view distance (m); 0 = sky
RWTexture2D<float4> NormalRoughness : register(u1);      // xy octahedral world normal, z roughness,
                                                         // w (2-bit alpha) 1/3 grass, 2/3 emissive
RWTexture2D<float4> BaseColorMetalness : register(u2);   // rgb linear base color, a metalness

cbuffer Pack : register(b0)
{
    row_major float4x4 InvViewAt0;  // VRageMath row vectors: view -> camera-relative world
    float ProjM33;
    float ProjM43;
    uint Width;
    uint Height;
};

float3 UnpackSpheremap(float2 enc)
{
    float2 fenc = enc * 4 - 2;
    float f = dot(fenc, fenc);
    float g = sqrt(1 - f / 4);
    return float3(fenc * g, 1 - f / 2);
}

float2 OctahedralEncode(float3 n)
{
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    float2 folded = (1 - abs(n.yx)) * float2(n.x >= 0 ? 1 : -1, n.y >= 0 ? 1 : -1);
    return (n.z >= 0 ? n.xy : folded) * 0.5 + 0.5;
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
        return;
    float depth = Depth[id.xy];
    if (!(depth > 0))
    {
        ViewZ[id.xy] = 0;
        NormalRoughness[id.xy] = float4(0.5, 0.5, 1, 0);
        BaseColorMetalness[id.xy] = 0;
        return;
    }
    // compute_depth (Frame.hlsli) with the infinite complementary projection.
    ViewZ[id.xy] = ProjM43 / (max(depth, 1e-36) + ProjM33);
    float3 normal = normalize(mul(UnpackSpheremap(Gbuffer1[id.xy].xy), (float3x3)InvViewAt0));
    float4 material = Gbuffer2[id.xy];
    // Grass and pebbles (Foliage/Foliage.hlsl) are the only writers of
    // coverage 0; every mesh writes COVERAGE_MASK_ALL (GBufferWrite.hlsli).
    // Otherwise whether the surface emits (the traced materials' check).
    float kind = material.a < 0.5 / 255 ? 1.0 / 3 : material.b > 0.5 / 255 ? 2.0 / 3 : 0;
    NormalRoughness[id.xy] = float4(OctahedralEncode(normal), 1 - material.g, kind);
    BaseColorMetalness[id.xy] = float4(Gbuffer0[id.xy].rgb, material.r);
}

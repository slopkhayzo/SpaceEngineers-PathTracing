// Debug views of what the D3D12 side receives from the game (D3D12 compute,
// cs_6_0), written into the overlay or replace target inside the gate.
//
//   0 moving stripes (test pattern: a missing or late hand-off shows)
//   1 world normal
//   2 base color
//   3 world-space grid (CellSize cells) from depth and the camera
//   4 reprojection error: each pixel's position from depth and this frame's
//     camera, moved into the previous frame's camera and compared with the
//     depth stored there. Static geometry gives zero when the camera data is
//     right; Stats counts pixels per error band.

Texture2D<float> ViewZ : register(t0);
Texture2D<float4> NormalRoughness : register(t1);
Texture2D<float4> BaseColorMetalness : register(t2);
Texture2D<float> PreviousViewZ : register(t3);
RWTexture2D<float4> Output : register(u0);
// uint counters: [0] compared, [1] error < 0.1 %, [2] < 1 %, [3] >= 1 %, [4] left the screen.
RWByteAddressBuffer Stats : register(u1);

cbuffer View : register(b0)
{
    row_major float4x4 InvViewAt0;
    row_major float4x4 PreviousViewProjectionAt0;
    float3 CameraDelta;     // camera position now minus before (m)
    float CellSize;
    float3 CameraFraction;  // camera position modulo CellSize (m)
    float Strength;
    float4 ScreenRayTerms;  // 1/P11, 1/P22, P31/P11, P32/P22 of the projection
    uint Mode;
    uint Frame;
    uint Width;
    uint Height;
    uint HasPrevious;
    uint3 Padding;
};

float3 OctahedralDecode(float2 e)
{
    e = e * 2 - 1;
    float3 n = float3(e, 1 - abs(e.x) - abs(e.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0 ? -t : t, n.y >= 0 ? -t : t);
    return normalize(n);
}

// compute_screen_ray (Frame.hlsli): view-space direction with z = -1.
float3 ScreenRay(float2 uv)
{
    return float3(ScreenRayTerms.z + lerp(-ScreenRayTerms.x, ScreenRayTerms.x, uv.x),
                  ScreenRayTerms.w - lerp(-ScreenRayTerms.y, ScreenRayTerms.y, uv.y), -1);
}

float3 Heat(float error)
{
    float t = saturate(log10(max(error, 1e-5)) / 3 + 5.0 / 3);  // 1e-5 .. 1e-2 -> 0 .. 1
    return t < 0.5 ? lerp(float3(0, 0.05, 0.3), float3(0, 0.8, 0.8), t * 2)
                   : lerp(float3(0.9, 0.9, 0), float3(1, 0, 0), t * 2 - 1);
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
        return;
    float3 color;
    float z = ViewZ[id.xy];
    if (Mode == 0)
    {
        float stripe = frac((id.x + id.y + Frame * 2) / 96.0) < 0.5 ? 1.0 : 0.0;
        color = float3(stripe, 0.15, 1.0 - stripe);
    }
    else if (z <= 0)
    {
        color = float3(0.02, 0.02, 0.05);  // sky
    }
    else
    {
        float2 uv = (id.xy + 0.5) / float2(Width, Height);
        float3 normal = OctahedralDecode(NormalRoughness[id.xy].xy);
        // Camera-relative world position (ReconstructWorldPosition, Frame.hlsli).
        float3 position = mul(z * ScreenRay(uv), (float3x3)InvViewAt0);
        if (Mode == 1)
        {
            color = normal * 0.5 + 0.5;
        }
        else if (Mode == 2)
        {
            color = BaseColorMetalness[id.xy].rgb;
        }
        else if (Mode == 3)
        {
            float3 cell = floor((position + CameraFraction) / CellSize);
            float checker = fmod(abs(cell.x + cell.y + cell.z), 2.0);
            float light = 0.35 + 0.65 * saturate(dot(normal, normalize(float3(0.3, 0.9, 0.3))));
            color = lerp(float3(0.9, 0.9, 0.9), float3(0.25, 0.25, 0.3), checker) * light;
        }
        else if (HasPrevious == 0)
        {
            color = float3(0.1, 0.1, 0.1);
        }
        else
        {
            float4 clip = mul(float4(position + CameraDelta, 1), PreviousViewProjectionAt0);
            float2 ndc = clip.xy / clip.w;
            float2 previousUv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
            if (clip.w <= 0 || any(previousUv < 0) || any(previousUv >= 1))
            {
                Stats.InterlockedAdd(16, 1);
                color = float3(0.25, 0, 0.25);
            }
            else
            {
                uint2 texel = min(uint2(previousUv * float2(Width, Height)), uint2(Width - 1, Height - 1));
                float stored = PreviousViewZ[texel];
                // With a perspective projection, clip.w is the view distance.
                float error = stored > 0 ? abs(clip.w - stored) / stored : 1;
                Stats.InterlockedAdd(0, 1);
                Stats.InterlockedAdd(error < 0.001 ? 4 : (error < 0.01 ? 8 : 12), 1);
                color = Heat(error);
            }
        }
    }
    Output[id.xy] = float4(color, Strength);
}

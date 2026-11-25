// Frame generation's inputs of the presenter's (src/native/rt/presenter.cpp), one pass:
//
// The user interface layer: DLSS Frame Generation keeps the HUD sharp when it knows where it is
// (kBufferTypeUIColorAndAlpha: the UI's colour premultiplied by its alpha, the alpha 0 where there is none;
// final = UI.rgb + (1 - UI.a) * HUD-less). The game draws its HUD straight into the back buffer, so the layer is
// where the finished frame differs from the frame captured before the HUD: opaque there (the HUD's own
// translucency is not recovered), its colour the finished frame's. Both inputs hold the back buffer's bytes (unorm
// views: no sRGB conversion); the output too.
//
// The depth: DLSS-G reads the hardware depth of the projection in its constants (kBufferTypeDepth; its linear
// depth tag is Ray Reconstruction's alone), so the traced linear view depth (primary_view.hlsl RrDepth, far for
// the sky) becomes the game's reverse-Z depth, M43 / z - M33 (1 at the near plane, 0 at the far one), at the
// render size.

Texture2D<float4> Final : register(t0);    // the finished frame (HUD included)
Texture2D<float4> Hudless : register(t1);  // the frame before the HUD
RWTexture2D<float4> Ui : register(u0);
RWTexture2D<float> LinearDepth : register(u1);  // the traced frame's (left in its unordered-access state)
RWTexture2D<float> Depth : register(u2);

cbuffer Constants : register(b0)
{
    uint Width;         // the frame's size (the UI layer's)
    uint Height;
    uint RenderWidth;   // the traced frame's (the depth's)
    uint RenderHeight;
    float ProjM33;      // the game's projection (rt_core.cpp FillPrimaryConstants)
    float ProjM43;
};

// Differences below this (in 8-bit steps) are the same pixel.
static const float kThreshold = 1.5 / 255.0;

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x < Width && id.y < Height)
    {
        float4 final = Final[id.xy];
        float3 difference = abs(final.rgb - Hudless[id.xy].rgb);
        float alpha = max(difference.r, max(difference.g, difference.b)) > kThreshold ? 1.0 : 0.0;
        Ui[id.xy] = float4(final.rgb * alpha, alpha);
    }
    if (id.x < RenderWidth && id.y < RenderHeight)
    {
        float z = LinearDepth[id.xy];
        Depth[id.xy] = z > 0.0 ? saturate(ProjM43 / z - ProjM33) : 0.0;
    }
}

// The lit view's spatial denoiser (D3D12 compute, cs_6_0; research/docs/
// space-engineers-rt-plan.md §6.8). primary_view.hlsl has blended each lit
// pixel's traced light into its history (the diffuse light without the albedo,
// the specular light, the diffuse luminance's second moment) and noted the
// surface's albedo and emission; here an edge-aware a-trous filter (after
// SVGF, Schied et al. 2017) smooths both lights where they are still noisy, and
// the result is composed into the output the patched sun pass blends in. One
// dispatch per step (rt_core.cpp RecordFrame), by Pass:
//   0 variance: each lit pixel's light, no brighter than the brightest of its
//     3x3 neighbours on the same surface (single-pixel outliers, "fireflies",
//     are the rare bright paths one sample per frame cannot average away), and
//     the variance of its mean: the diffuse light's from the temporal moments
//     (from the neighbours where the history is shorter than 4 frames), the
//     specular light's from the 3x3 neighbours; into the Filter textures
//     1 - Source
//   1 a-trous: one iteration of 3x3 taps Step pixels apart, Filter Source into
//     1 - Source; taps weighted by the B3 spline and stopped at other surfaces
//     (distance from the centre's tangent plane, normal) and at light
//     differences the variance does not explain; the specular light only
//     while Step stays within its lobe (16 * roughness^2 pixels)
//   2 compose: Output = albedo * (emission + diffuse) + specular
//   3 Ray Reconstruction's output over the lit pixels (lit-rr view), over the output's size: Step and Source
//     carry it (it is larger than Width x Height where Ray Reconstruction upscales, DLSS modes)
// Filter alpha: the variance; -1 where the pixel is not lit (the game's own
// light stays there).

Texture2D<float> ViewZ : register(t0);
Texture2D<float4> NormalRoughness : register(t1);
RWTexture2D<float4> Output : register(u0);
RWTexture2D<float4> HistoryDiffuse : register(u2);   // this frame's: rgb the diffuse light, a its length
RWTexture2D<float4> HistorySpecular : register(u3);  // rgb the specular light, a the diffuse luminance squared
RWTexture2D<float4> AlbedoEmission : register(u4);   // rgb the albedo, a the emission (times the albedo)
RWTexture2D<float4> FilterDiffuse[2] : register(u5);
RWTexture2D<float4> FilterSpecular[2] : register(u7);
// Pass 3 (lit-rr): Ray Reconstruction's input (alpha: the lit pixels) and output (rt_core.cpp).
RWTexture2D<float4> RrColor : register(u19);
RWTexture2D<float4> RrOutput : register(u24);

// The leading members of primary_view.hlsl's cbuffer Primary (the same buffer).
cbuffer Primary : register(b0)
{
    row_major float4x4 InvViewAt0;
    float4 ScreenRayTerms;  // 1/P11, 1/P22, P31/P11, P32/P22 of the projection
    float NearPlane;
    float FarPlane;
    uint Mode;
    uint Frame;
    uint Width;
    uint Height;
};

cbuffer Denoise : register(b2)
{
    uint Pass;
    uint Step;    // a-trous: the distance between taps (pixels)
    uint Source;  // the Filter textures read
    uint Padding;
};

static const float kKernel[2] = {0.5, 0.25};  // B3 spline, 3 taps: centre, side
static const float kNormalPower = 64;
static const float kPlaneTolerance = 0.01;  // of the view depth
static const float kLuminanceSigma = 4;     // in standard deviations of the mean

float Luminance(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

float3 OctahedralDecode(float2 e)
{
    e = e * 2 - 1;
    float3 n = float3(e, 1 - abs(e.x) - abs(e.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0 ? -t : t, n.y >= 0 ? -t : t);
    return normalize(n);
}

bool Inside(int2 p)
{
    return all(p >= 0) && p.x < (int)Width && p.y < (int)Height;
}

// The camera-relative point a pixel's G-buffer depth gives (this frame's camera at the origin).
float3 PositionAt(int2 pixel, float z)
{
    float2 uv = (pixel + 0.5) / float2(Width, Height);
    float3 ray = float3(ScreenRayTerms.z + lerp(-ScreenRayTerms.x, ScreenRayTerms.x, uv.x),
                        ScreenRayTerms.w - lerp(-ScreenRayTerms.y, ScreenRayTerms.y, uv.y), -1);
    return mul(ray, (float3x3)InvViewAt0) * z;
}

struct Centre
{
    float3 position;
    float3 normal;
    float z;
};

Centre LoadCentre(int2 p)
{
    Centre c;
    c.z = ViewZ[p];
    c.position = PositionAt(p, c.z);
    c.normal = OctahedralDecode(NormalRoughness[p].xy);
    return c;
}

// How surely a neighbour lies on the centre's surface: its distance from the
// centre's tangent plane (relative to the view depth) and its normal.
float GeometryWeight(Centre c, int2 q)
{
    float z = ViewZ[q];
    if (z <= 0)
        return 0;
    float3 n = OctahedralDecode(NormalRoughness[q].xy);
    float plane = abs(dot(c.normal, PositionAt(q, z) - c.position));
    return exp(-plane / (kPlaneTolerance * c.z)) * pow(saturate(dot(c.normal, n)), kNormalPower);
}

// `light` scaled down to the luminance `limit` if it is brighter.
float3 Bounded(float3 light, float limit)
{
    float l = Luminance(light);
    return l > limit ? light * (limit / l) : light;
}

void Variance(int2 p)
{
    float4 d = HistoryDiffuse[p];
    if (d.a <= 0)
    {
        FilterDiffuse[1 - Source][p] = float4(0, 0, 0, -1);
        FilterSpecular[1 - Source][p] = float4(0, 0, 0, -1);
        return;
    }
    float4 s = HistorySpecular[p];
    float frames = d.a;
    Centre c = LoadCentre(p);
    // The 3x3 neighbours on the same surface: the brightest bounds the centre; the spread of their
    // specular light is its variance.
    float specular = Luminance(s.rgb);
    float brightestDiffuse = 0;
    float brightestSpecular = 0;
    bool neighbours = false;
    float weightSum = 1;
    float sum1 = specular;
    float sum2 = specular * specular;
    [unroll]
    for (int y = -1; y <= 1; ++y)
    {
        [unroll]
        for (int x = -1; x <= 1; ++x)
        {
            int2 q = p + int2(x, y);
            if ((x == 0 && y == 0) || !Inside(q))
                continue;
            float4 n = HistoryDiffuse[q];
            if (n.a <= 0)
                continue;
            float w = GeometryWeight(c, q);
            if (w < 0.25)
                continue;
            float l = Luminance(HistorySpecular[q].rgb);
            brightestDiffuse = max(brightestDiffuse, Luminance(n.rgb));
            brightestSpecular = max(brightestSpecular, l);
            neighbours = true;
            sum1 += w * l;
            sum2 += w * l * l;
            weightSum += w;
        }
    }
    float specularVariance = max(0, sum2 / weightSum - (sum1 / weightSum) * (sum1 / weightSum));
    if (neighbours)
    {
        d.rgb = Bounded(d.rgb, brightestDiffuse);
        s.rgb = Bounded(s.rgb, brightestSpecular);
    }
    float mean = Luminance(d.rgb);
    float diffuseVariance;
    if (frames >= 4)
    {
        // The samples' variance from the temporal moments; the mean's is smaller by the frame count.
        diffuseVariance = max(0, s.a - mean * mean) / frames;
    }
    else
    {
        // Too few frames for the moments: the spread of the neighbours' light on the same surface,
        // raised where the history is shortest (SVGF).
        float spreadWeight = 0;
        float spread1 = 0;
        float spread2 = 0;
        [unroll]
        for (int sy = -3; sy <= 3; ++sy)
        {
            [unroll]
            for (int sx = -3; sx <= 3; ++sx)
            {
                int2 q = p + int2(sx, sy);
                if (!Inside(q))
                    continue;
                float4 n = HistoryDiffuse[q];
                if (n.a <= 0)
                    continue;
                float w = sx == 0 && sy == 0 ? 1 : GeometryWeight(c, q);
                float l = Luminance(n.rgb);
                spread1 += w * l;
                spread2 += w * l * l;
                spreadWeight += w;
            }
        }
        spread1 /= spreadWeight;
        spread2 /= spreadWeight;
        diffuseVariance = max(0, spread2 - spread1 * spread1) * (4 / frames);
    }
    FilterDiffuse[1 - Source][p] = float4(d.rgb, diffuseVariance);
    FilterSpecular[1 - Source][p] = float4(s.rgb, specularVariance);
}

void Atrous(int2 p)
{
    float4 d = FilterDiffuse[Source][p];
    float4 s = FilterSpecular[Source][p];
    if (d.a < 0)
    {
        FilterDiffuse[1 - Source][p] = d;
        FilterSpecular[1 - Source][p] = s;
        return;
    }
    Centre centre = LoadCentre(p);
    float roughness = NormalRoughness[p].z;
    bool specularToo = Step <= 16 * roughness * roughness;
    float diffuseLuminance = Luminance(d.rgb);
    float specularLuminance = Luminance(s.rgb);
    float diffuseScale = kLuminanceSigma * sqrt(d.a) + 1e-6;
    float specularScale = kLuminanceSigma * sqrt(s.a) + 1e-6;
    float centreWeight = kKernel[0] * kKernel[0];
    float3 diffuseSum = d.rgb * centreWeight;
    float diffuseVariance = d.a * centreWeight * centreWeight;
    float diffuseWeight = centreWeight;
    float3 specularSum = s.rgb * centreWeight;
    float specularVariance = s.a * centreWeight * centreWeight;
    float specularWeight = centreWeight;
    [unroll]
    for (int y = -1; y <= 1; ++y)
    {
        [unroll]
        for (int x = -1; x <= 1; ++x)
        {
            if (x == 0 && y == 0)
                continue;
            int2 q = p + int2(x, y) * (int)Step;
            if (!Inside(q))
                continue;
            float4 n = FilterDiffuse[Source][q];
            if (n.a < 0)
                continue;
            float g = kKernel[abs(x)] * kKernel[abs(y)] * GeometryWeight(centre, q);
            float w = g * exp(-abs(Luminance(n.rgb) - diffuseLuminance) / diffuseScale);
            diffuseSum += n.rgb * w;
            diffuseVariance += n.a * w * w;
            diffuseWeight += w;
            if (specularToo)
            {
                float4 m = FilterSpecular[Source][q];
                float ws = g * exp(-abs(Luminance(m.rgb) - specularLuminance) / specularScale);
                specularSum += m.rgb * ws;
                specularVariance += m.a * ws * ws;
                specularWeight += ws;
            }
        }
    }
    FilterDiffuse[1 - Source][p] = float4(diffuseSum / diffuseWeight, diffuseVariance / (diffuseWeight * diffuseWeight));
    FilterSpecular[1 - Source][p] = specularToo ? float4(specularSum / specularWeight,
                                                         specularVariance / (specularWeight * specularWeight))
                                                : s;
}

// Ray Reconstruction's light on the lit pixels (the others keep the primary pass's alpha 0). Upscaled from a
// smaller size, the primary pass wrote no output: every pixel here, lit where the game drew a surface other than
// grass (what full coverage, the only coverage then, lights; primary_view.hlsl), the game's own light elsewhere.
void ComposeRayReconstruction(int2 p, bool upscaled)
{
    if (upscaled)
    {
        float viewZ = ViewZ[p];
        bool grass = abs(NormalRoughness[p].w - 1.0 / 3) < 0.1;  // pack_gbuffer.hlsl
        Output[p] = viewZ > 0 && !grass ? float4(RrOutput[p].rgb, 1) : float4(0, 0, 0, 0);
    }
    else if (RrColor[p].a > 0)
        Output[p] = float4(RrOutput[p].rgb, 1);
}

// The filtered light on the pixels with a history; the primary pass's alpha stays (with full coverage a pixel
// can be lit without being composed: where the game drew sky).
void Compose(int2 p)
{
    float4 d = FilterDiffuse[Source][p];
    if (d.a < 0)
        return;
    float4 surface = AlbedoEmission[p];
    Output[p] = float4(surface.rgb * (surface.a + d.rgb) + FilterSpecular[Source][p].rgb, Output[p].a);
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    // Pass 3 runs over the output (its size in Step and Source), the others over the traced pixels.
    uint2 size = Pass == 3 ? uint2(Step, Source) : uint2(Width, Height);
    if (id.x >= size.x || id.y >= size.y)
        return;
    int2 p = int2(id.xy);
    if (Pass == 0)
        Variance(p);
    else if (Pass == 1)
        Atrous(p);
    else if (Pass == 2)
        Compose(p);
    else
        ComposeRayReconstruction(p, size.x != Width || size.y != Height);
}

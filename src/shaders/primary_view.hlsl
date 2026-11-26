// Primary rays through the scene mirror, compared with the game's G-buffer
// (D3D12 compute, cs_6_5, inline ray queries; research/docs/space-engineers-rt-plan.md
// phase 2 and §11 P1). One ray per pixel from the camera (the origin of the
// camera-relative TLAS); the direction is scaled so that the hit distance is
// the view depth the pack pass wrote (ViewZ). Alpha-masked parts are tested
// against their alpha masks during traversal.
//
//   Mode 0 parts:    every geometry record its own color, headlight shading
//   Mode 1 mismatch: grey where the ray depth matches the game's (< 1 %),
//                    magenta: the ray hit something nearer, orange: farther,
//                    red: the ray missed a surface the game drew, cyan: the
//                    ray hit where the game drew sky, dark green: grass (the
//                    mirror has none; not counted as a mismatch)
//   Mode 2 faces:    green front faces, red back faces (the instances flip
//                    DXR's winding rule to the game's; back faces are culled
//                    except on double-sided meshes)
//   Mode 3 clay:     white surfaces lit by the game's sun through one soft
//                    shadow ray and by a constant sky through two occlusion
//                    rays (5 m), one sample per frame, no denoising
//   Mode 4 material: the traced base color (textures, paint, voxels' triplanar
//                    materials)
//   Mode 5 material compare: where the ray hit the game's surface, the traced
//                    base color against the G-buffer's: green < 2 %, yellow
//                    < 5 %, orange < 15 %, red beyond (sRGB, worst channel);
//                    statistics also for normal, roughness and metalness, and
//                    for the material without each of the game's adjustments
//                    (pattern offset, per-pixel tangent frame, multipliers)
//   Mode 6 paths:    how the hit object reaches the mirror: blue models, grey
//                    old-pipeline objects (cyan with bones), purple generic
//                    instances, green cube blocks, yellow deformed cube blocks
//                    (red: a fractional pattern offset), dark voxels
//   Mode 7 lit:      traced light (P2): emissive, the sun through a soft
//                    shadow ray, the listed emissive triangles and the game's
//                    lamps (point and spot lights) through one shadow ray to
//                    a resampled point of one of them inside the light grid
//                    (SampledLight; the patched lamp passes of the game skip
//                    those pixels), one diffuse and one specular bounce
//                    (sunlit hits, the diffuse bounce's hit also lit by one
//                    sampled lamp or emitter (BounceLight); the game's far
//                    environment probe for escaping rays; the diffuse bounce
//                    leaves the listed triangles' emission out), with the
//                    game's BRDF and
//                    constants; averaged over
//                    time through reprojection (the light history: diffuse
//                    light without the albedo, specular light; moving
//                    objects through their previous transforms); alpha 1
//                    where the ray found the game's surface (depth within
//                    1 %), where the patched sun pass takes it
//                    instead of its own; denoise.hlsl filters the diffuse
//                    light afterwards and composes the output again.
//                    Full coverage (FullCoverage: the lit views in replace
//                    mode): every hit is lit and every surface pixel the
//                    game drew takes the traced light, whatever the ray
//                    found there (the environment where it missed), with no
//                    depth test, so that the rays can be jittered within
//                    their pixels (Jitter) for anti-aliasing
//   Mode 8 lit-raw:  the same without the spatial filter
//   Mode 9 emitters: the light of the emissive surfaces and lamps alone
//                    (their own emission and SampledLight), averaged like
//                    mode 8
//   Mode 13 emitters-reference: mode 9 without reusing light samples (the
//                    new candidates alone, unbiased): what mode 9 must match
//                    on average
//   Mode 14 emitters-power-grid: mode 9 with P2p's light grid (its lights
//                    drawn by power alone, each point's candidates from its
//                    own cell): the A/B of the cells' lists
//   Mode 10 motion:  each pixel's motion since the previous recorded frame
//                    (Motion, written by every mode): grey at rest, tinted
//                    by direction, full at 16 pixels
//   Mode 11 lit-rr:  mode 7's light, denoised by DLSS Ray Reconstruction
//                    instead of denoise.hlsl: this pass writes the frame's
//                    light before the accumulation and the guide images
//                    (Rr*), rt_core.cpp evaluates it and denoise.hlsl pass 3
//                    composes its output on the lit pixels
//
// Probe pixels (up to 8, Probes) record what their ray found in Stats after the
// counters (modes 4-6), for the log.
//
// The material formulas follow what the game's G-buffer shaders compute
// (normal map, Toksvig gloss, paint by HSV key color), written anew here.

#include "scene_common.hlsli"
#include "lights.hlsli"
RWByteAddressBuffer LightTree : register(u27);  // the light tree (emissive_lights.hlsl builds it), read here
#include "light_tree.hlsli"

// An entry of the game's voxel material table (scene.cpp VoxelMaterialRecord;
// TriplanarMaterialConstants' StandardTiling and color_far3).
struct VoxelMaterial
{
    float4 DistanceAndScale;  // initial scale, initial distance, scale multiplier, distance multiplier
    float4 Far1;              // scale, distance (0: off), texture index; TilingScale
    float4 Far2;              // scale, distance, texture index
    float4 Far3;              // scale, distance, texture index
    float4 ColorFar3;         // the color beyond the far textures
    uint4 Textures[5];        // descriptor slots: component (cm, ng, ext) * 6 + distance * 2 + axis (XZnY, Y)
};

Texture2D<float> ViewZ : register(t0);
Texture2D<float4> NormalRoughness : register(t1);
Texture2D<float4> BaseColorMetalness : register(t2);
Texture2D<float> PreviousViewZ : register(t3);
RWTexture2D<float4> Output : register(u0);
// The lit view's light history, this frame's and the previous frame's (see ReprojectHistory).
RWTexture2D<float4> HistoryDiffuse : register(u2);   // rgb the diffuse light without the albedo, a its length
RWTexture2D<float4> HistorySpecular : register(u3);  // rgb the specular light, a the diffuse luminance squared
Texture2D<float4> PreviousDiffuse : register(t11);
Texture2D<float4> PreviousSpecular : register(t12);
RWTexture2D<float4> AlbedoEmission : register(u4);  // lit pixels: rgb the albedo, a the emission (denoise.hlsl)
// The game's environment probe cubemaps (its ambient light's IBL) as atlases:
// the first mip's faces +X, -X, +Y, -Y, +Z, -Z side by side (env_atlas.hlsl).
Texture2D<float4> EnvironmentFar : register(t9);
Texture2D<float4> EnvironmentClose : register(t10);
// 128 uint counters (offset = 4 * index, see the Count calls below; some of
// them the light list's, emissive_lights.hlsl), then the probe records
// (kProbeOffset).
RWByteAddressBuffer Stats : register(u1);
StructuredBuffer<VoxelMaterial> VoxelMaterials : register(t8);
// Per TLAS instance, three float4: its transform in the previous recorded frame, around that frame's camera
// (scene.cpp, expand_blocks.hlsl): where a moving object's surface was (motion vectors).
StructuredBuffer<float4> PreviousTransforms : register(t16);
// Per pixel: where the surface it shows was in the previous recorded frame's image minus where it is now
// (pixels; previous = current + motion, DLSS's convention), written by every traced view.
RWTexture2D<float2> Motion : register(u18);
// Mode 11 (lit-rr): DLSS Ray Reconstruction's inputs (rt_core.cpp, dlss.cpp): this frame's light before any
// accumulation (alpha: the lit pixels, where its output replaces the light), the guide images, the depth.
RWTexture2D<float4> RrColor : register(u19);
RWTexture2D<float4> RrDiffuseAlbedo : register(u20);
RWTexture2D<float4> RrSpecularAlbedo : register(u21);
RWTexture2D<float4> RrNormalRoughness : register(u22);   // world normal, roughness
RWTexture2D<float> RrDepth : register(u23);              // linear view depth (far for the sky)
RWTexture2D<float> RrSpecularHitDistance : register(u25);  // the specular bounce's length (far: it escaped)
// The game's reverse-Z depth of what each pixel shows, for FSR and frame generation (Ray Reconstruction's view and
// UpscaleInputs; 0 for the sky).
RWTexture2D<float> UpscaleDepth : register(u29);
// Per geometry record (its index x 4): OpenGeneration once the camera saw one of its surfaces through a face of
// their own object (rt_core.cpp seenThrough): the pixels that show its surfaces look whether they are seen so.
RWByteAddressBuffer SeenThrough : register(u26);
// The frame's projected decals (ApplyProjectedDecals): their boxes as procedural primitives of one instance, by
// index (their draw order), and their records.
RaytracingAccelerationStructure DecalScene : register(t17);

// Mirrors GpuDecal in scene.cpp.
struct ProjectedDecal
{
    float4 toBox[3];  // camera-relative world -> the decal's box [-0.5, 0.5]^3: box.x = dot(toBox[0], float4(p, 1))
    float3 axis;      // the direction the game projects it along (the box's -z), unit
    float fade;       // the game's FadeAlpha (with age and distance)
    uint material;    // into Materials: ColorMetal, NormalGloss, Extensions, Alphamask
    uint maps;        // kDecalColor | kDecalNormal | kDecalExtensions: the maps it has (the game's DecalType)
    uint2 padding;
};
StructuredBuffer<ProjectedDecal> ProjectedDecals : register(t18);

static const uint kNoProbe = 0xFFFFFFFF;
static const uint kMaxProbes = 8;
static const uint kProbeOffset = 512;  // bytes: after the 128 counters (rt_core.cpp kProbeOffset)
// 48 uints per probe (rt_core.cpp decodes them): what the ray hit (24), then the triangles it crosses
// (RecordProbeCandidates).
static const uint kProbeBytes = 192;
static const uint kProbeCandidates = 6;

// compute_screen_ray (Frame.hlsli): view-space direction with z = -1.
float3 ScreenRay(float2 uv)
{
    return float3(ScreenRayTerms.z + lerp(-ScreenRayTerms.x, ScreenRayTerms.x, uv.x),
                  ScreenRayTerms.w - lerp(-ScreenRayTerms.y, ScreenRayTerms.y, uv.y), -1);
}

struct Surface
{
    float3 geometricNormal;  // world, facing the ray
    float3 normal;           // interpolated vertex normal, world
    float4 tangent;          // world xyz, bitangent sign
    float2 uv;
    float lodBase;           // texture LOD without the texture's size (ray cone)
    bool hasAttributes;
    // The triangle's world edges and UV deltas (the armor tangent frame).
    float3 edge1;
    float3 edge2;
    float2 uvDelta1;
    float2 uvDelta2;
    GeometryRecord record;
    uint geometryIndex;       // the part in its mesh (PartOverride); set by the caller
    // Object space (voxels' triplanar materials): the hit, its interpolated
    // vertex normal, the triangle's edges; the object-to-world rotation.
    float3 objectPosition;
    float3 objectNormal;
    float3 objectEdge1;
    float3 objectEdge2;
    float3x3 toWorld;
    float footprint;          // log2 of the ray cone's width across the surface
    float cameraDistance;     // set by the caller
    // Voxel cells: the triangle's three materials (the first vertex's), their
    // interpolated weights and the interpolated HSV color shift.
    uint3 voxelMaterials;
    float3 voxelWeights;
    float3 colorShift;
};

// unpack_voxel_weights (VertexTransformations.hlsli): the w byte of a voxel
// vertex's material says which of the triangle's three materials it carries.
float3 VoxelVertexWeights(uint material)
{
    int index = (int)(material >> 24);
    index = index >= 128 ? index - 256 : index;
    return float3(index <= 0 ? 1 : 0, index == 1 ? 1 : 0, index >= 2 ? 1 : 0);
}

// unpack_color_shift_hsv: x value %, y saturation %, z/w the hue (degrees), signed bytes.
float3 VoxelColorShift(uint packed)
{
    int4 b = int4(packed & 0xFF, (packed >> 8) & 0xFF, (packed >> 16) & 0xFF, packed >> 24);
    b = select(b >= 128, b - 256, b);
    int hue = (b.w << 8) | (b.z & 0xFF);
    return float3(hue / 360.0, b.y / 100.0, b.x / 100.0);
}

// The surface of triangle `primitive` of `record` at `barycentrics`, placed by
// `toWorld`; `coneWidth` is the ray cone's width there.
Surface LoadSurface(GeometryRecord record, uint primitive, float2 barycentrics, float3x3 toWorld, float3 direction,
                    float coneWidth)
{
    Surface surface;
    surface.record = record;
    surface.geometryIndex = 0;
    uint3 corners = LoadTriangle(record, primitive);
    float3 p0 = LoadPosition(record, corners.x);
    float3 p1 = LoadPosition(record, corners.y);
    float3 p2 = LoadPosition(record, corners.z);
    float3 e1 = mul(toWorld, p1 - p0);
    float3 e2 = mul(toWorld, p2 - p0);
    float3 n = cross(e1, e2);
    float worldArea = length(n);
    float3 view = normalize(direction);
    surface.geometricNormal = worldArea > 0 ? n / worldArea : -view;
    if (dot(surface.geometricNormal, view) > 0)
        surface.geometricNormal = -surface.geometricNormal;
    float3 weights = float3(1 - barycentrics.x - barycentrics.y, barycentrics.x, barycentrics.y);
    float2 uv0 = LoadUv(record, corners.x);
    float2 uv1 = LoadUv(record, corners.y);
    float2 uv2 = LoadUv(record, corners.z);
    surface.uv = weights.x * uv0 + weights.y * uv1 + weights.z * uv2;
    float2 d1 = uv1 - uv0;
    float2 d2 = uv2 - uv0;
    surface.edge1 = e1;
    surface.edge2 = e2;
    surface.uvDelta1 = d1;
    surface.uvDelta2 = d2;
    float uvArea = abs(d1.x * d2.y - d1.y * d2.x);
    float cosine = max(abs(dot(surface.geometricNormal, view)), 0.05);
    surface.lodBase = log2(max(coneWidth, 1e-9) / cosine) + 0.5 * log2(max(uvArea, 1e-12) / max(worldArea, 1e-12));
    surface.footprint = log2(max(coneWidth, 1e-9) / cosine);
    surface.hasAttributes = record.AttributeOffset != kNoAttributes;
    surface.normal = surface.geometricNormal;
    surface.tangent = float4(0, 0, 0, 1);
    surface.toWorld = toWorld;
    surface.objectPosition = weights.x * p0 + weights.y * p1 + weights.z * p2;
    surface.objectEdge1 = p1 - p0;
    surface.objectEdge2 = p2 - p0;
    float3 objectNormal = cross(p1 - p0, p2 - p0);
    surface.objectNormal = dot(objectNormal, objectNormal) > 0 ? normalize(objectNormal) : float3(0, 0, 1);
    surface.cameraDistance = 0;
    surface.voxelMaterials = 0;
    surface.voxelWeights = float3(1, 0, 0);
    surface.colorShift = 0;
    if (Kind(record) == 2)
    {
        // VB0: float3 position, then the material bytes (R8G8B8A8_SINT).
        ByteAddressBuffer page = Pages[NonUniformResourceIndex(record.Page)];
        uint vertexStride = VertexStride(record);
        uint m0 = page.Load(record.VertexOffset + corners.x * vertexStride + 12);
        uint m1 = page.Load(record.VertexOffset + corners.y * vertexStride + 12);
        uint m2 = page.Load(record.VertexOffset + corners.z * vertexStride + 12);
        surface.voxelMaterials = uint3(m0 & 0xFF, (m0 >> 8) & 0xFF, (m0 >> 16) & 0xFF);
        surface.voxelWeights = weights.x * VoxelVertexWeights(m0) + weights.y * VoxelVertexWeights(m1) +
                               weights.z * VoxelVertexWeights(m2);
        if (record.AttributeOffset != kNoAttributes)
        {
            // VB1: the packed normal, then the color shift (R8G8B8A8_SINT).
            uint attributeStride = AttributeStride(record);
            surface.colorShift = weights.x * VoxelColorShift(page.Load(record.AttributeOffset + corners.x * attributeStride + 4)) +
                                 weights.y * VoxelColorShift(page.Load(record.AttributeOffset + corners.y * attributeStride + 4)) +
                                 weights.z * VoxelColorShift(page.Load(record.AttributeOffset + corners.z * attributeStride + 4));
        }
    }
    if (record.AttributeOffset != kNoAttributes)
    {
        ByteAddressBuffer page = Pages[NonUniformResourceIndex(record.Page)];
        uint stride = AttributeStride(record);
        uint2 a0 = page.Load2(record.AttributeOffset + corners.x * stride);
        uint2 a1 = page.Load2(record.AttributeOffset + corners.y * stride);
        uint2 a2 = page.Load2(record.AttributeOffset + corners.z * stride);
        float3 vertexNormal = weights.x * DecodeNormal(a0.x) + weights.y * DecodeNormal(a1.x) +
                              weights.z * DecodeNormal(a2.x);
        if (dot(vertexNormal, vertexNormal) > 0)
            surface.objectNormal = normalize(vertexNormal);
        // The game transforms normals with the instance matrix itself (VertexTemplateBase.hlsli).
        float3 worldNormal = mul(toWorld, vertexNormal);
        if (dot(worldNormal, worldNormal) > 0)
            surface.normal = normalize(worldNormal);
        if (Kind(record) != 2)
        {
            float4 t0 = DecodeTangent(a0.y);
            float3 vertexTangent = weights.x * t0.xyz + weights.y * DecodeTangent(a1.y).xyz +
                                   weights.z * DecodeTangent(a2.y).xyz;
            float3 worldTangent = mul(toWorld, vertexTangent);
            surface.tangent = float4(dot(worldTangent, worldTangent) > 0 ? normalize(worldTangent) : 0, t0.w);
        }
    }
    return surface;
}

// --- Traversal (AlphaSolid, TraceClosest, Occluded: scene_common.hlsli) ------

// Diagnostics: how far along the way (0-1; 1 nothing) the nearest shadow caster stops a ray, with the
// given ray flags (back faces culled or not).
float OccluderFraction(float3 origin, float3 direction, float maxT, uint rayFlags)
{
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0;
    ray.TMax = maxT;
    RayQuery<RAY_FLAG_NONE> query = TraceClosest(ray, rayFlags, 0x02, 0.001);
    return query.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? query.CommittedRayT() / maxT : 1;
}

// --- Materials ----------------------------------------------------------------

// Toksvig: the gloss lowered by the normal map's filtered length.
float ToksvigGloss(float gloss, float normalLength)
{
    float power = 2 / pow(max(1 - gloss, 0.001), 2) - 2;
    float factor = max(normalLength / lerp(power, 1, normalLength), 0.001);
    return 1 - sqrt(2) * sqrt(1 / (factor * power + 2));
}

struct MaterialSample
{
    float3 baseColor;  // linear
    float metalness;
    float3 normal;     // world shading normal
    float gloss;
    float ao;
    float emissive;
    float paintMask;   // the extensions texture's alpha
    float coloring;    // how much paint the texel took
};

// ShadeMaterial options: which of the game's adjustments it applies.
static const uint kShadeTextureOffset = 1;  // the pattern offset of deformed cube blocks
static const uint kShadeArmorFrame = 2;     // their per-pixel tangent frame
static const uint kShadeMultipliers = 4;    // TextureDebugMultipliers
static const uint kShadeAll = 7;

// The mesh's vertex layout has bones (MyVertexLayoutInfo.HasBonesInfo: bone
// indices and weights in VB0), as armor has.
bool HasBones(GeometryRecord record) { return (record.Flags & 0x10000) != 0; }

// Deformed cube instancing (USE_DEFORMED_CUBE_INSTANCING): the game draws a mesh
// with bones this way when it comes from cube-block records
// (MyRenderableComponent.RebuildLodProxy); such a mesh shifts its UVs by the
// record's pattern offset and builds its tangent frame per pixel
// (Template.hlsli: BUILD_TANGENT_IN_PIXEL).
bool Deformed(GeometryRecord record, uint source)
{
    return source == kSourceCube && HasBones(record);
}

// The per-pixel tangent frame of deformed cubes from position and UV
// derivatives (PixelTangentSpace, the cotangent frame); per triangle the
// derivatives are its edges, signed as the screen's x/y derivatives of a front
// face would be.
float3 ArmorNormal(Surface surface, float3 n, float3 normalmap)
{
    float3 e1 = surface.edge1;
    float3 e2 = surface.edge2;
    float3 perp1 = cross(e2, n);
    float3 perp2 = cross(n, e1);
    float orientation = dot(n, cross(e1, e2)) >= 0 ? -1 : 1;
    float3 t = orientation * (perp1 * surface.uvDelta1.x + perp2 * surface.uvDelta2.x);
    float3 b = orientation * (perp1 * surface.uvDelta1.y + perp2 * surface.uvDelta2.y);
    float scale = rsqrt(max(max(dot(t, t), dot(b, b)), 1e-20));
    return normalize(-normalmap.x * t * scale + normalmap.y * b * scale + normalmap.z * n);
}

// ApplyMultipliers (Geometry/Materials/PixelUtilsMaterials.hlsli).
MaterialSample ApplyGameMultipliers(MaterialSample m)
{
    m.gloss = m.gloss * Multipliers0.z + Multipliers2.x;
    m.ao = 1 - (1 - m.ao) * Multipliers0.w - Multipliers2.y;
    m.emissive = m.emissive * Multipliers1.x + Multipliers2.z;
    m.baseColor = m.baseColor * Multipliers0.x + Multipliers1.z;
    m.metalness = m.metalness * Multipliers0.y + Multipliers1.w;
    return m;
}

// --- Voxels -------------------------------------------------------------------
//
// The game's triplanar voxel materials (Geometry/Materials/TriplanarMulti,
// Geometry/TriplanarSampling.hlsli; high shader quality: SampleTiled,
// StandardTiling, TRIPLANAR_ADD), written anew for rays: the screen
// derivatives become the ray cone and the triangle's edges.

struct VoxelSample
{
    float4 cm;
    float4 ng;
    float4 ext;
};

// rgb_to_hsv (Math/Color.hlsli).
float3 RgbToHsv(float3 rgb)
{
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = lerp(float4(rgb.bg, K.wz), float4(rgb.gb, K.xy), step(rgb.b, rgb.g));
    float4 q = lerp(float4(p.xyw, rgb.r), float4(rgb.r, p.yzx), step(p.x, rgb.r));
    float d = q.x - min(q.w, q.y);
    float e = 1.0e-10;
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

// ColorShift (Math/Color.hlsli) at the full rate voxels use.
float3 ColorShift(float3 rgb, float3 hsvShift)
{
    float3 hsv = RgbToHsv(rgb) + hsvShift;
    hsv.x = fmod(hsv.x, 1.0);
    if (hsv.x < 0)
        hsv.x += 1.0;
    hsv.yz = saturate(hsv.yz);
    return HsvToRgb(hsv);
}

// GetTriplanarWeights.
float3 TriplanarWeights(float3 n)
{
    float3 w = saturate(abs(n) - 0.55);
    w *= w;
    w *= w;
    return w / dot(w, 1);
}

// GetNearestDistanceAndScaleLoop: the distance band around `distance` and the
// texture scales at its two ends.
float4 DistanceBands(float distance, float4 settings)
{
    float curDistance = 0;
    float curScale = settings.x;
    float nextDistance = settings.y;
    float nextScale = settings.z;
    for (uint i = 0; i <= 16 && nextDistance < distance; ++i)
    {
        curDistance = nextDistance;
        curScale = nextScale;
        nextDistance *= settings.w;
        nextScale *= settings.z;
    }
    return float4(curDistance, nextDistance, curScale, nextScale);
}

// SampleTiled: two copies of the texture, shifted by offsets that change per
// cell of the game's random texture, blended (hides the tiling).
float4 SampleTiled(uint slot, float2 uv, float tilingScale, float lodBase)
{
    float k = SampleTexture(VoxelRandom, uv * tilingScale, lodBase + log2(max(tilingScale, 1e-6))).r;
    float l = k * 8.0;
    float i = floor(l);
    float f = frac(l);
    float2 offa = sin(float2(3.0, 7.0) * i);
    float2 offb = sin(float2(3.0, 7.0) * (i + 1.0));
    float4 a = SampleTexture(slot, uv + 0.4 * offa, lodBase);
    float4 b = SampleTexture(slot, uv + 0.4 * offb, lodBase);
    return lerp(a, b, smoothstep(0.2, 0.8, f));
}

// SampleTriplanarGrad: X and Z project onto the XZnY texture, Y onto the Y one;
// `forced` (0-2) takes one projection only.
float4 SampleTriplanar(uint slotXZnY, uint slotY, float3 uvw, float3 weights, int forced, float tilingScale,
                       float lodBase)
{
    if (forced == 0)
        return SampleTiled(slotXZnY, uvw.zy, tilingScale, lodBase);
    if (forced == 1)
        return SampleTiled(slotY, uvw.xz, tilingScale, lodBase);
    if (forced == 2)
        return SampleTiled(slotXZnY, uvw.xy, tilingScale, lodBase);
    float4 result = 0;
    if (weights.x > 0)
        result += SampleTiled(slotXZnY, uvw.zy, tilingScale, lodBase) * weights.x;
    if (weights.y > 0)
        result += SampleTiled(slotY, uvw.xz, tilingScale, lodBase) * weights.y;
    if (weights.z > 0)
        result += SampleTiled(slotXZnY, uvw.xy, tilingScale, lodBase) * weights.z;
    return result;
}

uint VoxelTexture(VoxelMaterial m, uint component, uint distance, uint axis)
{
    uint index = component * 6 + distance * 2 + axis;
    return m.Textures[index / 4][index % 4];
}

// SampleTriplanarDistance: one distance band's textures (or the far color).
VoxelSample SampleVoxelDistance(VoxelMaterial m, float3 texcoords, float3 weights, int forced, float scale,
                                float bandDistance, float3 offset, float lodBase)
{
    int textureIndex = 0;
    if (m.Far1.y > 0 && bandDistance >= m.Far1.y)
    {
        scale = m.Far1.x;
        textureIndex = (int)m.Far1.z;
    }
    if (m.Far2.y > 0 && bandDistance >= m.Far2.y)
    {
        scale = m.Far2.x;
        textureIndex = (int)m.Far2.z;
    }
    if (m.Far3.y > 0 && bandDistance >= m.Far3.y)
    {
        scale = m.Far3.x;
        textureIndex = (int)m.Far3.z;
    }
    VoxelSample result;
    result.cm = float4(m.ColorFar3.xyz, 0);
    result.ng = float4(0.5, 0.5, 1, 0);
    result.ext = float4(1, 0, 0, 0);
    if (textureIndex < 3)
    {
        uint d = (uint)min(2, textureIndex);
        float3 uvw = (texcoords + offset) / scale;
        float lod = lodBase - log2(scale);  // one texture repeat spans `scale` metres
        float tiling = m.Far1.w;
        result.cm = saturate(SampleTriplanar(VoxelTexture(m, 0, d, 0), VoxelTexture(m, 0, d, 1), uvw, weights, forced,
                                             tiling, lod));
        result.ng = SampleTriplanar(VoxelTexture(m, 1, d, 0), VoxelTexture(m, 1, d, 1), uvw, weights, forced, tiling, lod);
        result.ext = SampleTriplanar(VoxelTexture(m, 2, d, 0), VoxelTexture(m, 2, d, 1), uvw, weights, forced, tiling,
                                     lod);
    }
    return result;
}

// SampleTriplanar (TriplanarSampling.hlsli): the bands near and far of
// `distance`, blended over the far 75 % of the band (high quality); the
// cell's offset joins the coordinates beyond the first band.
VoxelSample SampleVoxelMaterial(VoxelMaterial m, float3 texcoords, float3 weights, int forced, float distance,
                                float3 voxelOffset, float lodBase)
{
    float4 settings = m.DistanceAndScale * float4(2, 2, 2, 4);
    float4 bands = DistanceBands(distance, settings);
    float scaleWeight = saturate(((distance - bands.x) / (bands.y - bands.x) - 0.25) / 0.75);
    float3 nearOffset = step(settings.y, distance) * voxelOffset;
    if (scaleWeight <= 0.005)
        return SampleVoxelDistance(m, texcoords, weights, forced, bands.z, bands.x, nearOffset, lodBase);
    if (scaleWeight >= 0.995)
        return SampleVoxelDistance(m, texcoords, weights, forced, bands.w, bands.y, voxelOffset, lodBase);
    VoxelSample nearSample = SampleVoxelDistance(m, texcoords, weights, forced, bands.z, bands.x, nearOffset, lodBase);
    VoxelSample farSample = SampleVoxelDistance(m, texcoords, weights, forced, bands.w, bands.y, voxelOffset, lodBase);
    VoxelSample result;
    result.cm = lerp(nearSample.cm, farSample.cm, scaleWeight);
    result.ng = lerp(nearSample.ng, farSample.ng, scaleWeight);
    result.ext = lerp(nearSample.ext, farSample.ext, scaleWeight);
    return result;
}

// TriplanarTangentSpace for one projection: the cotangent frame of its
// coordinates across the triangle (object space), signed as for a front face
// (as ArmorNormal); `tangentNormal`'s x is already flipped.
float3 TriplanarAxisNormal(float3 e1, float3 e2, float3 n, float2 d1, float2 d2, float3 tangentNormal)
{
    float3 perp1 = cross(e2, n);
    float3 perp2 = cross(n, e1);
    float orientation = dot(n, cross(e1, e2)) >= 0 ? -1 : 1;
    float3 t = orientation * (perp1 * d1.x + perp2 * d2.x);
    float3 b = orientation * (perp1 * d1.y + perp2 * d2.y);
    float invmax = rsqrt(max(max(dot(t, t), dot(b, b)), 1e-20));
    return tangentNormal.x * t * invmax + tangentNormal.y * b * invmax + tangentNormal.z * n;
}

// The triangle's materials blended by its vertex weights, then FeedOutputTriplanar.
// A voxel cell's instance data: VoxelOffset in the paint slots, VoxelScale in extra.z.
MaterialSample ShadeVoxel(Surface surface, float4 instance, float4 extra)
{
    float3 voxelOffset = instance.xyz;
    float voxelScale = extra.z > 0 ? extra.z : 1;
    float3 n = surface.objectNormal;
    float3 weights = TriplanarWeights(n);
    int forced = weights.x >= 0.995 ? 0 : weights.y >= 0.995 ? 1 : weights.z >= 0.995 ? 2 : -1;
    float3 texcoords = surface.objectPosition * voxelScale;
    VoxelSample total;
    total.cm = 0;
    total.ng = 0;
    total.ext = 0;
    [unroll]
    for (uint t = 0; t < 3; ++t)
    {
        float weight = surface.voxelWeights[t];
        if (weight >= 0.0005)
        {
            VoxelMaterial m = VoxelMaterials[min(surface.voxelMaterials[t], VoxelMaterialCount - 1)];
            VoxelSample s = SampleVoxelMaterial(m, texcoords, weights, forced, surface.cameraDistance, voxelOffset,
                                                surface.footprint);
            total.cm += s.cm * weight;
            total.ng += s.ng * weight;
            total.ext += s.ext * weight;
        }
    }
    float3 tangentNormal = total.ng.xyz * 2 - 1;
    tangentNormal.x = -tangentNormal.x;
    float3 e1 = surface.objectEdge1;
    float3 e2 = surface.objectEdge2;
    float3 objectNormal = TriplanarAxisNormal(e1, e2, n, e1.zy, e2.zy, tangentNormal) * weights.x +
                          TriplanarAxisNormal(e1, e2, n, e1.xz, e2.xz, tangentNormal) * weights.y +
                          TriplanarAxisNormal(e1, e2, n, e1.xy, e2.xy, tangentNormal) * weights.z;
    float3 worldNormal = mul(surface.toWorld, objectNormal);
    MaterialSample result = (MaterialSample)0;
    result.normal = dot(worldNormal, worldNormal) > 0 ? normalize(worldNormal) : surface.normal;
    result.baseColor = ColorShift(total.cm.rgb, surface.colorShift);
    result.metalness = total.cm.w;
    result.gloss = total.ng.w;
    result.ao = total.ext.x;
    result.emissive = total.ext.y;
    return result;
}

// `instance` and `extra`: the hit instance's two InstanceData entries.
MaterialSample ShadeMaterial(Surface surface, float4 instance, float4 extra, uint options)
{
    MaterialSample result = (MaterialSample)0;
    result.normal = surface.normal;
    if (Kind(surface.record) == 2 && VoxelMaterialCount != 0 && surface.hasAttributes)
    {
        result = ShadeVoxel(surface, instance, extra);
        if ((options & kShadeMultipliers) != 0)
            result = ApplyGameMultipliers(result);
        return result;
    }
    if (Kind(surface.record) == 2 || !surface.hasAttributes)
    {
        // Voxels: triplanar materials come later.
        result.baseColor = 0.5;
        result.metalness = 0;
        result.gloss = 0.3;
        result.ao = 1;
        result.emissive = 0;
        return result;
    }
    MaterialRecord material = Materials[surface.record.Material];
    bool deformed = Deformed(surface.record, SourceOf(extra));
    float2 uv = deformed && (options & kShadeTextureOffset) != 0 ? surface.uv + extra.xy : surface.uv;
    float4 cm = SampleTexture(material.Textures.x, uv, surface.lodBase);
    float4 ng = SampleTexture(material.Textures.y, uv, surface.lodBase);
    float4 ext = SampleTexture(material.Textures.z, uv, surface.lodBase);
    float3 n = surface.normal;
    float3 nm = ng.xyz * 2 - 1;
    if (deformed && (options & kShadeArmorFrame) != 0)
    {
        result.normal = ArmorNormal(surface, n, nm);
    }
    else if (dot(surface.tangent.xyz, surface.tangent.xyz) > 1e-8)
    {
        float3 t = surface.tangent.xyz;
        float3 b = cross(t, n) * surface.tangent.w;
        result.normal = normalize(nm.x * t - nm.y * b + nm.z * n);  // the game flips the map's y
    }
    result.gloss = ToksvigGloss(ng.w, min(length(nm), 1));
    result.metalness = cm.a;
    float coloring = ext.w;
    bool colorable = (material.Flags & 2) != 0 || instance.w > 0.5;
    if (!colorable)
        coloring *= saturate(1 - saturate(result.metalness - 0.4) / 0.3);
    // The object's own color multiplier and emissivity for this part (FeedOutputInternal).
    float4 part = PartOverride(extra, surface.geometryIndex);
    result.baseColor = Paint(cm.rgb, instance.xyz, coloring) * part.rgb;
    result.ao = ext.x;
    result.emissive = saturate(ext.y - 1 / 255.0) * part.a;
    result.paintMask = ext.w;
    result.coloring = coloring;
    if ((options & kShadeMultipliers) != 0)
        result = ApplyGameMultipliers(result);
    return result;
}

// --- Decals ---------------------------------------------------------------------

static const float kNormalDotFactor = 4;  // PixelUtils.hlsli NORMAL_DOT_FACTOR
static const float kAoBumpPow = 4;        // PixelUtils.hlsli AO_BUMP_POW
// How far from the hit surface's plane a decal the ray met still lies on it (the game's decals sit a few
// millimetres above theirs).
static const float kDecalReach = 0.03;

// One blended decal over the hit's material, as the game's G-buffer pass blends it
// (Geometry/Passes/GBuffer/PixelStage.hlsli STATIC_DECAL into GBuffer/GBufferWrite.hlsli GbufferWriteBlend, with the
// BlendDecal* state MyGBufferRenderPass.GetAlphamaskBlendState picks by the maps the decal has): its alpha mask `a`
// mixes in its color and metalness (with a color-metal map), its gloss (a normal-gloss map) and its emissive (an
// extensions map); premultiplied, the decal's values are added and the surface's kept by 1 - a, else the two mix by
// a. Its normal and AO mix by alphaN: the mask, or how far the decal's normal map turns from its own surface
// (ComputeNormalMapAlphaBestEffort); without an extensions map its AO is that turn's
// (ComputeAmbientOcclusionBestEffort). Whether it lies on the hit surface: within kDecalReach of its plane and not
// behind it by more than the game's decal depth bias lets it (DecalDepthBias, scene_common.hlsli TraceClosestDecals).
// The decal is a part of the hit's instance: its transform (`toWorld`) and instance data are the hit's.
bool ApplyDecal(inout MaterialSample m, DecalHits h, uint hitInstance, float3x3 toWorld, float4 instance,
                float4 extra, float3 direction, float hitT, float3 hitNormal, float coneSpread)
{
    if (h.count == 0 || h.instanceIndex != hitInstance || h.t > hitT * (1.0001 + DecalDepthBias) + 0.001 ||
        abs(dot(direction * (h.t - hitT), hitNormal)) > kDecalReach)
        return false;
    GeometryRecord record = Records[h.record];
    MaterialRecord material = Materials[record.Material];
    Surface ds = LoadSurface(record, h.primitive, h.barycentrics, toWorld, direction, coneSpread * h.t);
    if (!ds.hasAttributes || material.Textures.w == kAlphamaskSlotNone)
        return false;
    ds.geometryIndex = h.geometryIndex;
    float a = SampleTexture(material.Textures.w, ds.uv, ds.lodBase).r;
    MaterialSample d = ShadeMaterial(ds, instance, extra, kShadeAll);
    float give = (record.Flags & kPartDecalPremultiplied) != 0 ? 1 : a;
    float keep = 1 - a;
    bool colorMap = material.Textures.x >= kFallbackSlots;
    bool normalMap = material.Textures.y >= kFallbackSlots;
    bool extensions = material.Textures.z >= kFallbackSlots;
    if (colorMap)
    {
        m.baseColor = d.baseColor * give + m.baseColor * keep;
        m.metalness = d.metalness * give + m.metalness * keep;
    }
    float normalAlpha = 0;
    if (normalMap)
    {
        float bend = dot(d.normal, ds.normal);
        normalAlpha = max(a, saturate((1 - bend) * kNormalDotFactor));
        float ao = extensions ? d.ao : pow(saturate(bend), kAoBumpPow);
        m.gloss = d.gloss * give + m.gloss * keep;
        m.normal = normalize(lerp(m.normal, d.normal, normalAlpha));
        m.ao = lerp(m.ao, ao, normalAlpha);
    }
    if (extensions)
        m.emissive = d.emissive * give + m.emissive * keep;
    return a > 0 || normalAlpha > 0;
}

// --- Projected decals ------------------------------------------------------------
//
// The game's projected decals (MyScreenDecals: bullet holes, impact marks, missile scorches, tire trails; scene.h
// RtDecal): boxes whose textures it projects onto whatever surface lies inside them, after the G-buffer pass
// (Decals/Decals.hlsl). Here their boxes are the procedural primitives of an acceleration structure of their own
// (scene.cpp UploadDecals): a ray of almost no length from a camera ray's hit meets those around it.

static const uint kDecalColor = 1;       // the game's MyFileTextureEnum COLOR_METAL
static const uint kDecalNormal = 2;      // NORMALMAP_GLOSS
static const uint kDecalExtensions = 4;  // EXTENSIONS
static const uint kMaxPointDecals = 8;   // per point; beyond them the earliest drawn are left out
static const float kDecalFadeThreshold = 0.3;  // Decals.hlsl FADE_THRESHOLD: |z| where the normal starts to fade
static const float kAoDistanceBoost = 14.0;    // Decals.hlsl AO_DISTANCE_BOOST
static const float kNormalAoBoostRatio = 0.01; // Decals.hlsl NORMAL_AO_BOOST_RATIO

float3 DecalBoxPoint(ProjectedDecal d, float3 p)
{
    float4 h = float4(p, 1);
    return float3(dot(d.toBox[0], h), dot(d.toBox[1], h), dot(d.toBox[2], h));
}

// The projected decals over material `m` at a camera ray's hit `position` (camera-relative), in the game's draw
// order (their index), as Decals.hlsl blends them into its G-buffer (GbufferWriteBlend under the premultiplied
// BlendDecal* state of the decal's maps, MyMeshMaterials1.GetMaterialTextureBlendState): where the box holds the
// point and the surface faces its axis (|cos| >= 0.33), the alpha mask a times the fade f lays the color and
// metalness over the surface's (added, the surface kept by 1 - a f: the textures are premultiplied), the gloss
// with a normal map, the emissive with an extensions map; the normal (the map turned into the box's frame across
// the surface, PixelTangentSpace with the x flip of adjust_normalmap_no_precomputed_tangents) and AO mix in by
// alphaN f: the mask, or how far the map turns from flat (ComputeNormalMapAlphaBestEffort), faded toward the box's
// depth ends; the AO is the extensions map's, else the turn's (ComputeAmbientOcclusionBestEffort), both boosted
// beyond kAoDistanceBoost of view depth (`viewDepth`). The surface's own normal (the game's G-buffer copy) is the
// one before any of them. `footprint`: the ray cone's width across the surface (log2 metres; the mip).
// Returns how many changed the material; `met`: the boxes holding the point; `dropped`: more than kMaxPointDecals.
uint ApplyProjectedDecals(inout MaterialSample m, float3 position, float viewDepth, float footprint, out uint met,
                          out bool dropped)
{
    met = 0;
    dropped = false;
    if (DecalCount == 0)
        return 0;
    RayDesc ray;
    ray.Origin = position;
    ray.Direction = float3(0, 0, 1);
    ray.TMin = 0;
    ray.TMax = 1e-4;
    RayQuery<RAY_FLAG_SKIP_TRIANGLES> query;
    query.TraceRayInline(DecalScene, RAY_FLAG_NONE, 0xFF, ray);
    // The boxes holding the point, by index; 0xFFFFFFFF: free (sorted last).
    uint found[kMaxPointDecals];
    [unroll]
    for (uint f = 0; f < kMaxPointDecals; ++f)
        found[f] = 0xFFFFFFFF;
    uint count = 0;
    while (query.Proceed())
    {
        if (query.CandidateType() != CANDIDATE_PROCEDURAL_PRIMITIVE)
            continue;
        uint index = query.CandidatePrimitiveIndex();
        if (index >= DecalCount || any(abs(DecalBoxPoint(ProjectedDecals[index], position)) > 0.5))
            continue;
        // The slots stay in registers: indexed by loop counters only.
        bool known = false;
        uint earliest = 0;               // the slot of the earliest drawn
        uint earliestIndex = found[0];
        [unroll]
        for (uint k = 0; k < kMaxPointDecals; ++k)
        {
            known = known || found[k] == index;
            if (found[k] < earliestIndex)
            {
                earliest = k;
                earliestIndex = found[k];
            }
        }
        if (known)
            continue;  // reported again
        ++met;
        if (count < kMaxPointDecals)
        {
            [unroll]
            for (uint k = 0; k < kMaxPointDecals; ++k)
            {
                if (k == count)
                    found[k] = index;
            }
            ++count;
            continue;
        }
        dropped = true;
        [unroll]
        for (uint k = 0; k < kMaxPointDecals; ++k)
        {
            if (k == earliest && earliestIndex < index)
                found[k] = index;
        }
    }
    if (count == 0)
        return 0;
    // In draw order: an odd-even transposition sort of the slots.
    [unroll]
    for (uint pass = 0; pass < kMaxPointDecals; ++pass)
    {
        [unroll]
        for (uint k = pass & 1; k + 1 < kMaxPointDecals; k += 2)
        {
            uint low = min(found[k], found[k + 1]);
            uint high = max(found[k], found[k + 1]);
            found[k] = low;
            found[k + 1] = high;
        }
    }

    float3 N = m.normal;
    float boost = viewDepth > kAoDistanceBoost ? kAoDistanceBoost / viewDepth : 1;
    uint applied = 0;
    [unroll]
    for (uint i = 0; i < kMaxPointDecals; ++i)
    {
        if (i >= count)
            break;
        ProjectedDecal d = ProjectedDecals[found[i]];
        if (abs(dot(d.axis, N)) < 0.33)
            continue;
        float3 b = DecalBoxPoint(d, position);
        float2 uv = 0.5 - b.xy;
        // The gradients of u and v across the surface: the normal map's frame and the mip.
        float3 du = -d.toBox[0].xyz;
        float3 dv = -d.toBox[1].xyz;
        du -= N * dot(N, du);
        dv -= N * dot(N, dv);
        float lodBase = footprint + 0.5 * log2(max(length(du) * length(dv), 1e-12));
        MaterialRecord material = Materials[d.material];
        // Its mask (every decal of the game has one) and color map still loading: left out, not a dark square.
        if (material.Textures.w < kFallbackSlots || ((d.maps & kDecalColor) != 0 && material.Textures.x < kFallbackSlots))
            continue;
        float a = SampleTexture(material.Textures.w, uv, lodBase).r;
        float fade = d.fade;
        float keep = 1 - a * fade;
        if ((d.maps & kDecalColor) != 0)
        {
            float4 cm = SampleTexture(material.Textures.x, uv, lodBase);
            m.baseColor = cm.rgb * fade + m.baseColor * keep;
            m.metalness = cm.a * fade + m.metalness * keep;
        }
        float4 ext = 0;
        if ((d.maps & kDecalExtensions) != 0)
            ext = SampleTexture(material.Textures.z, uv, lodBase);
        float normalAlpha = 0;
        if ((d.maps & kDecalNormal) != 0)
        {
            float4 ng = SampleTexture(material.Textures.y, uv, lodBase);
            float3 nm = normalize(ng.xyz * 2 - 1);
            nm.z *= (1 - kNormalAoBoostRatio) + kNormalAoBoostRatio * boost;
            nm = normalize(nm);
            float scale = rsqrt(max(max(dot(du, du), dot(dv, dv)), 1e-12));
            float3 turned = normalize((nm.x * du - nm.y * dv) * scale + nm.z * N);
            float ao = (d.maps & kDecalExtensions) != 0 ? ext.r : pow(saturate(dot(N, turned)), kAoBumpPow);
            ao *= boost * boost;
            normalAlpha = max(a, saturate((1 - nm.z) * kNormalDotFactor));
            float depthEnd = abs(b.z);
            if (depthEnd > kDecalFadeThreshold)
                normalAlpha *= saturate((0.5 - depthEnd) / (0.5 - kDecalFadeThreshold));
            float n = normalAlpha * fade;
            m.normal = normalize(lerp(m.normal, turned, n));
            m.ao = lerp(m.ao, ao, n);
            m.gloss = ng.a * fade + m.gloss * keep;
        }
        if ((d.maps & kDecalExtensions) != 0)
            m.emissive = ext.g * fade + m.emissive * keep;
        applied += (a > 0 || normalAlpha > 0) && fade > 0 ? 1 : 0;
    }
    return applied;
}

// The shading normal for the light, on the side of the surface the ray sees (`geometricNormal`, facing the ray). A
// normal on the other side (a double-sided part's back face, a mesh whose normals point away from its visible side)
// would take no light at all: every direction toward a light, and every bounce, lies below one of the two. It is
// mirrored across the surface instead, keeping its tilt (the game lights such surfaces by its ambient alone).
float3 SeenSideNormal(float3 normal, float3 geometricNormal)
{
    float d = dot(normal, geometricNormal);
    return d < 0 ? normalize(normal - 2 * d * geometricNormal) : normal;
}

// A traced material against the G-buffer's at one pixel.
struct MaterialError
{
    float color;         // sRGB, worst channel
    float normalCosine;
    float roughness;
    float metalness;
};

MaterialError CompareMaterial(MaterialSample m, float4 rasterBase, float3 rasterNormal, float rasterRoughness)
{
    MaterialError e;
    float3 delta = abs(LinearToSrgb(m.baseColor) - LinearToSrgb(rasterBase.rgb));
    e.color = max(delta.r, max(delta.g, delta.b));
    e.normalCosine = dot(m.normal, rasterNormal);
    e.roughness = abs((1 - m.gloss) - rasterRoughness);
    e.metalness = abs(m.metalness - rasterBase.a);
    return e;
}

// --- The game's G-buffer ------------------------------------------------------

// The G-buffer texel under a point of this pass's pixel grid (`pixel`: in its pixels, continuous). The pass traces
// at the game's size, one texel per pixel, but for the lit view whose light Ray Reconstruction upscales from a
// smaller size (DLSS modes: Width x Height against GBufferWidth x GBufferHeight).
uint2 GBufferTexel(float2 pixel)
{
    float2 scale = float2(GBufferWidth, GBufferHeight) / float2(Width, Height);
    return min(uint2(max(pixel * scale, 0)), uint2(GBufferWidth - 1, GBufferHeight - 1));
}

bool Upscaled() { return Width != GBufferWidth || Height != GBufferHeight; }

// --- Probes -------------------------------------------------------------------

uint ProbeAt(uint2 pixel)
{
    [unroll]
    for (uint p = 0; p < kMaxProbes; ++p)
    {
        uint packed = Probes[p / 4][p % 4];
        if (packed != kNoProbe && pixel.x == (packed & 0xFFFF) && pixel.y == (packed >> 16))
            return p;
    }
    return kNoProbe;
}

// The triangles a probe pixel's camera ray crosses, the nearest kProbeCandidates in order (4 uints each after the
// probe's 24, at `base` + 96; rt_core.cpp LogProbes): its record, its distance, its part flags, the side the ray
// meets (bit 8: the front), whether the camera's trace takes it there (bit 9: an opaque part, culling aside, or a
// non-opaque one AlphaSolid keeps for the camera) and its alpha mask there (bits 16-23), and its material. Every
// triangle is traced as non-opaque, back faces too, so that none is left out unseen. Sorted in place in Stats (zeroed
// every frame: an empty slot's distance is 0; only this probe's thread writes its slots), not in local arrays that
// would weigh on the whole pass.
void RecordProbeCandidates(RayDesc ray, float coneSpread, uint base)
{
    uint slots = base + 96;
    RayQuery<RAY_FLAG_FORCE_NON_OPAQUE> query;
    query.TraceRayInline(Scene, RAY_FLAG_NONE, 0x01, ray);
    while (query.Proceed())
    {
        float t = query.CandidateTriangleRayT();
        uint at = 0;
        for (; at < kProbeCandidates; ++at)
        {
            uint stored = Stats.Load(slots + at * 16 + 4);
            if (stored == 0 || asfloat(stored) > t)
                break;
        }
        if (at == kProbeCandidates)
            continue;  // farther than the nearest kProbeCandidates
        uint recordIndex = query.CandidateInstanceID() + query.CandidateGeometryIndex();
        GeometryRecord record = Records[recordIndex];
        MaterialRecord material = Materials[record.Material];
        bool nonOpaque = (record.Flags & (1 | 4 | kPartDecal)) != 0;  // scene.h kPartNonOpaque
        bool taken = !nonOpaque ||
                     AlphaSolid(query.CandidateInstanceIndex(), query.CandidateInstanceID(),
                                query.CandidateGeometryIndex(), query.CandidatePrimitiveIndex(),
                                query.CandidateTriangleBarycentrics(), coneSpread * t,
                                query.CandidateInstanceContributionToHitGroupIndex() != 0, true);
        float mask = 1;
        if (material.Textures.w != kAlphamaskSlotNone)
        {
            uint3 corners = LoadTriangle(record, query.CandidatePrimitiveIndex());
            float2 b = query.CandidateTriangleBarycentrics();
            float2 uv = (1 - b.x - b.y) * LoadUv(record, corners.x) + b.x * LoadUv(record, corners.y) +
                        b.y * LoadUv(record, corners.z);
            mask = SampleTexture(material.Textures.w, uv, log2(max(coneSpread * t, 1e-9)) - 6).r;
        }
        uint4 entry = uint4(recordIndex, asuint(t),
                            (record.Flags & 0xFF) | (query.CandidateTriangleFrontFace() ? 0x100u : 0u) |
                                (taken ? 0x200u : 0u) | (uint(saturate(mask) * 255 + 0.5) << 16),
                            record.Material);
        // The farther ones move back by one (the last falls off).
        for (uint k = kProbeCandidates - 1; k > at; --k)
            Stats.Store4(slots + k * 16, Stats.Load4(slots + (k - 1) * 16));
        Stats.Store4(slots + at * 16, entry);
    }
}

uint PackUnorm4(float4 v)
{
    uint4 b = uint4(round(saturate(v) * 255));
    return b.x | (b.y << 8) | (b.z << 16) | (b.w << 24);
}

uint PackUnorm2x16(float a, float b)
{
    return uint(round(saturate(a) * 65535)) | (uint(round(saturate(b) * 65535)) << 16);
}

// sRGB base color and metalness, 8 bits each.
uint PackMaterialColor(float3 linearColor, float metalness)
{
    return PackUnorm4(float4(LinearToSrgb(linearColor), metalness));
}

// The paint HSV offset (each in -1..1, 10 bits) and the metalness-colorable flag.
uint PackKeyColor(float4 instance)
{
    uint3 k = uint3(round(saturate(instance.xyz * 0.5 + 0.5) * 1023));
    return k.x | (k.y << 10) | (k.z << 20) | (instance.w > 0.5 ? 1u << 30 : 0);
}

// --- Debug output -------------------------------------------------------------

float3 OctahedralDecode(float2 e)
{
    e = e * 2 - 1;
    float3 n = float3(e, 1 - abs(e.x) - abs(e.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0 ? -t : t, n.y >= 0 ? -t : t);
    return normalize(n);
}

float3 HashColor(uint value)
{
    value = value * 747796405u + 2891336453u;
    value = ((value >> ((value >> 28) + 4)) ^ value) * 277803737u;
    value = (value >> 22) ^ value;
    return float3(value & 0xFF, (value >> 8) & 0xFF, (value >> 16) & 0xFF) / 255.0 * 0.75 + 0.25;
}

// Rows: tangent, bitangent, n.
float3x3 Basis(float3 n)
{
    float3 t = abs(n.y) < 0.999 ? normalize(cross(float3(0, 1, 0), n)) : float3(1, 0, 0);
    return float3x3(t, cross(n, t), n);
}

float3 CosineDirection(float3 n, float u1, float u2)
{
    float r = sqrt(u1);
    float phi = 6.2831853 * u2;
    return mul(float3(r * cos(phi), r * sin(phi), sqrt(max(0, 1 - u1))), Basis(n));
}

// Uniform over the cone of half-angle `radius` around `axis`.
float3 ConeDirection(float3 axis, float radius, float u1, float u2)
{
    float cosTheta = lerp(1, cos(radius), u1);
    float sinTheta = sqrt(max(0, 1 - cosTheta * cosTheta));
    float phi = 6.2831853 * u2;
    return mul(float3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta), Basis(axis));
}

// --- Light (P2) -------------------------------------------------------------------
//
// The game's direct lighting model (Lighting/Brdf.hlsli, SpecularPhong.hlsli,
// LightingModel.hlsli): Lambert diffuse and a normalized Blinn-Phong specular
// with a horizon fade and Schlick's Fresnel at N.V, scaled by the light's
// diffuse, specular and gloss factors from the game's frame constants.

float GameSpecularExponent(float gloss)
{
    gloss = min(gloss, 0.995);
    float e = -10.0 / log2(gloss * 0.968 + 0.03);
    return e * e;
}

float3 GameSpecular(float ln, float nh, float vn, float3 f0, float gloss)
{
    gloss = min(gloss, 0.995);
    float specExp = GameSpecularExponent(gloss);
    float phong = (specExp + 4.0) / (8.0 * kPi) * pow(nh, specExp);
    float horizon = 1.0 - ln;
    horizon *= horizon;
    horizon *= horizon;
    phong -= phong * horizon;
    float f = saturate(1.0 - vn);
    float f2 = f * f;
    f *= f2 * f2;
    float3 fresnel = lerp(f0, 1.0, f * gloss * gloss);
    return min(fresnel * phong, 100);
}

// CalculateLight: what a light of `color` arriving along `L` sends toward V,
// in two parts: the diffuse light without the albedo (the caller multiplies it
// in) and the specular light.
void GameLight(float3 f0, float gloss, float3 N, float3 V, float3 L, float3 color, float3 specularColor,
               float glossFactor, float diffuseFactor, float specularFactor, out float3 diffuse, out float3 specular)
{
    float3 H = normalize(V + L);
    float ln = saturate(dot(L, N));
    float vn = saturate(dot(V, N));
    float nh = saturate(dot(N, H));
    diffuse = color * (diffuseFactor / kPi * ln);
    specular = color * specularColor * GameSpecular(ln, nh, vn, f0, gloss * glossFactor) * (specularFactor * ln);
}

// A direction's light in a cube atlas, by D3D's face selection and orientation.
float3 SampleCubeAtlas(Texture2D<float4> atlas, float3 d)
{
    uint width, height;
    atlas.GetDimensions(width, height);
    if (height == 0)
        return 0;
    float3 a = abs(d);
    uint face;
    float2 uv;
    if (a.x >= a.y && a.x >= a.z)
    {
        face = d.x > 0 ? 0 : 1;
        uv = float2(d.x > 0 ? -d.z : d.z, -d.y) / a.x;
    }
    else if (a.y >= a.z)
    {
        face = d.y > 0 ? 2 : 3;
        uv = float2(d.x, d.y > 0 ? d.z : -d.z) / a.y;
    }
    else
    {
        face = d.z > 0 ? 4 : 5;
        uv = float2(d.z > 0 ? d.x : -d.x, -d.y) / a.z;
    }
    float size = height;
    float2 texel = clamp((uv * 0.5 + 0.5) * size, 0.5, size - 0.5);
    return atlas.SampleLevel(LinearWrap, float2((face * size + texel.x) / width, texel.y / height), 0).rgb;
}

// Light from beyond the scene: the game's far environment probe (its IBL;
// looked up with x mirrored as its ambient shaders do).
float3 EnvironmentRadiance(float3 direction)
{
    direction.x = -direction.x;
    return SampleCubeAtlas(EnvironmentFar, direction);
}

// The split-sum environment BRDF, analytic (Karis' approximation of the game's LUT).
float3 EnvironmentBrdf(float3 f0, float roughness, float nv)
{
    const float4 c0 = float4(-1, -0.0275, -0.572, 0.022);
    const float4 c1 = float4(1, 0.0425, 1.04, -0.04);
    float4 r = roughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * nv)) * r.x + r.y;
    float2 ab = float2(-1.04, 1.04) * a004 + r.zw;
    return f0 * ab.x + ab.y;
}

// The sun's light (the game's sun term) on a surface at `position`, seen from
// V, if one ray toward a point of the sun's disc is free; AO as the game's.
// The diffuse part without the albedo, the specular part.
void SunLight(float3 position, float3 geometricNormal, MaterialSample m, float3 V, inout uint seed,
              out float3 diffuse, out float3 specular, uint self)
{
    float3 toSun = normalize(-Game.Light.directionalLightVec);
    float3 f0 = lerp(0.04, m.baseColor, m.metalness);
    GameLight(f0, m.gloss, m.normal, V, toSun, Game.Light.directionalLightColor,
              Game.Light.directionalLightSpecularColor, Game.Light.directionalLightGlossFactor,
              Game.Light.directionalLightDiffuseFactor, Game.Light.directionalLightSpecularFactor, diffuse, specular);
    if (all(diffuse + specular <= 0))
        return;
    float discRadius = clamp(acos(saturate(Game.Light.SunDiscInnerDot)), 0.002, 0.05);
    float3 toDisc = ConeDirection(toSun, discRadius, Random(seed), Random(seed));
    float shadow = dot(toDisc, geometricNormal) > 0 && !Occluded(position, toDisc, FarPlane, 0, self) ? 1 : 0;
    shadow = 1 - (1 - shadow) * (1 - Game.Light.shadowFadeout);
    float lightAO = saturate(1 - (1 - m.ao) * Game.Light.aoDirLight);
    diffuse *= shadow * lightAO;
    specular *= shadow * lightAO;
}

// A direction of the game's Blinn-Phong lobe: the half vector around N, reflected.
float3 SpecularDirection(float3 N, float3 V, float gloss, float u1, float u2)
{
    float exponent = GameSpecularExponent(gloss);
    float cosTheta = pow(max(u1, 1e-6), 1.0 / (exponent + 1.0));
    float sinTheta = sqrt(saturate(1 - cosTheta * cosTheta));
    float phi = 6.2831853 * u2;
    float3 H = mul(float3(sinTheta * cos(phi), sinTheta * sin(phi), cosTheta), Basis(N));
    return reflect(-V, H);
}

// --- Emissive surfaces and lamps as lights (§6.9) --------------------------------------
//
// Next-event estimation of the listed lights (lights.hlsli,
// emissive_lights.hlsl: emissive triangles and the game's lamps), resampled
// after ReSTIR DI, for points inside the light grid (farther ones leave
// emitters to the paths that hit them, and the game's lamp passes light them;
// with the light tree the grid's coarse levels reach every point):
// kLightCandidates new candidates from the point's light-grid cell (a uniform
// point of a triangle, a point of a lamp's disc), and the samples the previous
// lit frame kept near this point (at a pixel of its 4x4 tile there, permuted
// from frame to frame: RTXDI's permutation sampling) and at a neighbour of it
// (found again by their light's key, so a moving grid carries its lights), all
// weighed by their unshadowed diffuse contribution (LightTarget). One shadow
// ray to the chosen point; a triangle's emission is read from its textures, a
// lamp shaded with the game's falloff, AO factor and BRDF (diffuse and
// specular). The chosen sample is kept for the next frame, an occluded one
// without its weight but with the candidates it stands for (ReSTIR's
// visibility reuse; dropping it, as before, kept only visible samples and made
// partly shadowed points too bright).
//
// Ray Reconstruction assumes samples uncorrelated in space and time, so the
// reuse is short (kReservoirCap, kNeighbourCap) and permuted: with 20 frames of
// candidates per kept sample whole regions kept one light's sample for many
// frames (the lights of a room did not blend). The boiling filter (after RTXDI's)
// does not keep a sample whose contribution weight stands far above its wave's:
// a light that a lucky candidate found, which the reuse would spread as a
// blotch (RTXDI drops it; here it is still shaded this frame, unbiased).

static const uint kLightCandidates = 8;
static const uint kReservoirCap = 32;     // candidates a kept sample of this point stands for (4 frames of them)
static const uint kNeighbourCap = 16;     // those a neighbour's sample stands for
static const float kReuseRadius = 16;     // pixels: how far the neighbour whose sample is reused lies
static const float kBoilingFactor = 40;   // a sample's contribution weight over the average of its wave's others
static const bool kLightDiagnostics = false;  // where the shadow rays of light samples stop (statistics 32-35)

// What happened to a pixel's light sample, for the statistics.
struct LightSampleInfo
{
    bool inGrid;
    bool sampled;     // a sample with a contribution was chosen
    bool visible;     // its shadow ray is free
    float occluder;   // occluded (kLightDiagnostics): the nearest occluder's fraction of the way
    bool freeCulled;  // occluded (kLightDiagnostics): free with back faces culled
    float distance;   // the chosen point's distance
    bool twoSided;
    bool lamp;        // the chosen sample is a lamp's
    bool reusedHere;      // the previous frame's sample of this point was found again
    bool reusedNeighbour; // that of a neighbour
    bool fromReuse;       // the chosen sample is a reused one
    bool boiled;          // the boiling filter did not keep the chosen sample
};

// Where a point was in the previous recorded frame's image (pixels) and its view depth there; false outside
// it. `previousPoint`: its position then, relative to that frame's camera.
bool PreviousPixel(float3 previousPoint, out float2 pixel, out float depth)
{
    float4 clip = mul(float4(previousPoint, 1), PreviousViewProjectionAt0);
    pixel = 0;
    depth = clip.w;
    if (clip.w <= 0)
        return false;
    float2 uv = float2(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5);
    pixel = uv * float2(Width, Height);
    return all(uv >= 0) && all(uv < 1);
}

uint ReservoirIndex(uint set, uint2 pixel)
{
    return set * Width * Height + pixel.y * Width + pixel.x;
}

// A resampled choice among light samples (RIS): the chosen light, the point on it, its two numbers, its
// target, the sum of the candidates' weights and how many candidates they stand for.
struct LightChoice
{
    uint index;
    float3 lightPoint;
    float2 weights;
    float target;
    float weightSum;
    uint candidates;
    bool fromReuse;
};

LightChoice NoLightChoice()
{
    LightChoice choice = (LightChoice)0;
    choice.index = kNoLight;
    return choice;
}

// The light-grid cell around a shading point (clamped to the grid).
uint LightCell(float3 position)
{
    int3 coordinates = clamp(int3(floor(position / kLightCellSize)) + (int)(kLightGridSize / 2), 0,
                             (int)kLightGridSize - 1);
    return ((uint)coordinates.z * kLightGridSize + (uint)coordinates.y) * kLightGridSize + (uint)coordinates.x;
}

static uint g_levelDraws = 0;  // this thread's candidates from the light grid's coarse levels (statistic 120)
static uint g_levelWalks = 0;  // of them, those drawn from the light tree itself: their cell not filled (121)

// `count` new candidates (a uniform point of a triangle, a point of a lamp's disc), weighed by their unshadowed
// diffuse contribution at `position` (LightTarget) over their probability: from the slots of the light-grid
// cells around `position`, with the light tree (LightTreeOn) beyond the fine level those of its coarse levels
// (light_tree.hlsli), each candidate's cell marked as taken (the next frames fill it: emissive_lights.hlsl pass
// 20); where this frame did not fill it, from the tree itself (TreeLight, by importance at the point). Each
// candidate's cell is the one of a point up to half a cell from `position` on each axis (a stochastic blend of the
// cells: their borders do not show; emissive_lights.hlsl builds each cell for the points that far from it,
// kLightCellReach).
void LightCandidates(inout LightChoice choice, uint count, float3 position, float3 N, float3 geometricNormal,
                     inout uint seed)
{
    choice.candidates += count;
    uint level = LightTreeOn != 0 ? PointLevel(position) : 0;
    float levelCellSize = level != 0 ? LevelCellSize(level) : 0;
    uint markedCell = kNoLight;  // the coarse cell this point marked last (one mark per cell)
    for (uint c = 0; c < count; ++c)
    {
        uint index = kNoLight;
        float choiceWeight = 0;  // the light's unbiased contribution weight (about 1 / its probability)
        if (level != 0)
        {
            ++g_levelDraws;
            float3 jitter = (float3(Random(seed), Random(seed), Random(seed)) - 0.5) * levelCellSize;
            uint levelCell = LevelCell(level, position + jitter);
            uint slotPick = min((uint)(Random(seed) * kLightSlots), kLightSlots - 1);
            if (levelCell != markedCell)
            {
                LightTree.Store(kTreeLevelUsed + levelCell * 4, Frame);
                markedCell = levelCell;
            }
            if (LightTree.Load(kTreeLevelFilled + levelCell * 4) == Frame)
            {
                uint2 slot = LightTree.Load2(kTreeLevelSlots + (levelCell * kLightSlots + slotPick) * 8);
                index = slot.x;
                choiceWeight = asfloat(slot.y);
            }
            else
            {
                ++g_levelWalks;
                float probability;
                if (TreeLight(position, N, 0, 0.01, seed, index, probability))
                    choiceWeight = 1 / probability;
            }
        }
        else
        {
            float3 jitter = (float3(Random(seed), Random(seed), Random(seed)) - 0.5) * kLightCellSize;
            uint cell = LightCell(Mode != 14 ? position + jitter : position);  // mode 14: P2p's grid
            uint2 slot = LightGrid[cell * kLightSlots + min((uint)(Random(seed) * kLightSlots), kLightSlots - 1)];
            index = slot.x;
            choiceWeight = asfloat(slot.y);
        }
        float u1 = Random(seed);
        float u2 = Random(seed);
        float u3 = Random(seed);
        if (index == kNoLight || !(choiceWeight > 0))
            continue;
        LightTriangle light = Lights[index];
        bool lamp = IsLamp(light);
        // A triangle: uniform over it (the weights of edge1 and edge2); a lamp: its disc's polar numbers.
        float root = sqrt(u1);
        float2 weights = lamp ? float2(u1, u2) : float2(root * (1 - u2), root * u2);
        float3 lightPoint;
        float target = LightTarget(light, weights, position, N, geometricNormal, lightPoint);
        // A triangle's point has the density 1 / area on it; a lamp is chosen as a whole.
        float w = target * choiceWeight * (lamp ? 1 : LightArea(light));
        choice.weightSum += w;
        if (w > 0 && u3 * choice.weightSum < w)
        {
            choice.index = index;
            choice.lightPoint = lightPoint;
            choice.weights = weights;
            choice.target = target;
            choice.fromReuse = false;
        }
    }
}

// Whether the way from `position` to the chosen point is free, up to just before the light: a triangle's own
// surface, a lamp's fitting around it do not shadow it.
bool LightVisible(LightTriangle light, float3 position, float3 lightPoint, out float3 L, out float distance,
                  uint self)
{
    float3 toLight = lightPoint - position;
    distance = length(toLight);
    L = toLight / max(distance, 1e-6);
    float maxT = IsLamp(light) ? distance - kLampSkip : distance * 0.998;
    return !(maxT > 0.001 && Occluded(position, L, maxT, 0, self));
}

// What a visible light sample sends to the shading point, times its contribution weight W: the diffuse light
// without the albedo (returned; a triangle's weighted by `indirectAO` like the bounce, a lamp's by the game's
// AO factor for lamps) and a lamp's specular light. A lamp is the game's point or spot light (LightPoint.hlsl,
// LightSpot.hlsl) from its centre, a triangle's emission is read from its textures at the sample's point.
float3 ShadeLight(LightTriangle light, float2 weights, float3 L, float distance, float3 position, MaterialSample m,
                  float3 V, float indirectAO, float W, out float3 specular)
{
    specular = 0;
    float3 N = m.normal;
    if (IsLamp(light))
    {
        Lamp lamp = Lamps[asuint(light.corner.w)];
        float3 toLamp = lamp.position - position;
        float lampDistance = length(toLamp);
        float3 lampL = toLamp / max(lampDistance, 1e-4);
        float2 attenuation = LampAttenuation(lampDistance, lamp.range, lamp.falloff);
        float aoFactor = (lamp.flags & kLampSpot) != 0 ? Game.Light.aoSpotLight : Game.Light.aoPointLight;
        float ao = saturate(1 - (1 - m.ao) * aoFactor);
        float3 color = lamp.color * (attenuation.x * LampCone(lamp, -lampL) * ao);
        float3 f0 = lerp(0.04, m.baseColor, m.metalness);
        float3 diffuse, lampSpecular;
        GameLight(f0, m.gloss, N, V, lampL, color, 1, lamp.glossFactor, lamp.diffuseFactor * attenuation.y, 1,
                  diffuse, lampSpecular);
        specular = lampSpecular * W;
        return diffuse * W;
    }
    uint lightInstance = asuint(light.corner.w);
    float4 lightExtra = InstanceData[lightInstance * 2 + 1];
    float2 uv = light.uv01.xy + weights.x * (light.uv01.zw - light.uv01.xy) +
                weights.y * (light.uv2.xy - light.uv01.xy);
    float3 radiance = Emission(Records[asuint(light.edge1.w)], InstanceData[lightInstance * 2], lightExtra,
                               LightGeometryIndex(light), uv,
                               LightLodBase(light.uv01.xy, light.uv01.zw, light.uv2.xy));
    float cosLight = -dot(LightNormal(light), L);
    if ((asuint(light.uv2.z) & kLightTwoSided) != 0)
        cosLight = abs(cosLight);
    // Lambert: radiance x both cosines / (distance^2 pi), times the contribution weight.
    return indirectAO * radiance * (dot(N, L) * cosLight / (distance * distance * kPi)) * W;
}

// The light the listed emissive triangles and lamps send to the surface a pixel sees (`surfacePoint` the
// hit, `position` the shading point above it, `m` its material, V toward the eye): ShadeLight's diffuse light
// (returned) and specular light, from kLightCandidates new candidates and the samples the previous lit frame
// kept at this point and at a neighbour (found again by their light's key).
float3 SampledLight(uint2 pixel, float3 surfacePoint, float3 previousPoint, float3 position, MaterialSample m,
                    float3 geometricNormal, float3 V, float indirectAO, inout uint seed, out float3 specular,
                    out LightSampleInfo info, uint self)
{
    info = (LightSampleInfo)0;
    specular = 0;
    float3 N = m.normal;
    uint writeAt = ReservoirIndex(LightSet & 1, pixel);
    if (!LightsActive())
    {
        LightReservoirs[writeAt] = 0;
        return 0;
    }
    // Inside the grid by the surface point, as the patched lamp passes of the game test it (InLightGrid).
    bool inGrid = LightTreeOn != 0 || InLightGrid(surfacePoint);  // the tree has no extent
    info.inGrid = inGrid;
    if (!inGrid)
    {
        // Beyond the grid (32 m and more from the camera) emissive surfaces are left to the paths that hit
        // them (the caller's diffuse bounce keeps their emission): far, small and costly to trace to; the
        // game's lamp passes light such points.
        LightReservoirs[writeAt] = 0;
        return 0;
    }
    LightChoice choice = NoLightChoice();
    LightCandidates(choice, kLightCandidates, position, N, geometricNormal, seed);
    // The samples the previous lit frame kept near here and at a neighbour (their depth near this point's there);
    // none in mode 13 (emitters-reference: the new candidates alone).
    float2 previous;
    float previousDepth;
    if (Mode != 13 && HistoryValid != 0 && PreviousPixel(previousPoint, previous, previousDepth))
    {
        // Permutation sampling: this frame's offset of the 4x4 tiles, the same for every pixel.
        uint permutation = Pcg(Frame ^ 0x2C1B3C6Du);
        int2 tileOffset = int2(permutation & 3, (permutation >> 2) & 3);
        [unroll]
        for (uint k = 0; k < 2; ++k)
        {
            float2 offset = 0;
            if (k == 1)
            {
                float angle = 6.2831853 * Random(seed);
                offset = kReuseRadius * sqrt(Random(seed)) * float2(cos(angle), sin(angle));
            }
            float u = Random(seed);
            int2 q = int2(floor(previous + offset));
            if (k == 0)
                q = ((q + tileOffset) ^ 3) - tileOffset;
            if (any(q < 0) || q.x >= (int)Width || q.y >= (int)Height)
                continue;
            float stored = PreviousViewZ[GBufferTexel(q + 0.5)];
            if (!(stored > 0 && abs(stored - previousDepth) < 0.05 * previousDepth))
                continue;
            uint4 kept = LightReservoirs[ReservoirIndex(1 - (LightSet & 1), uint2(q))];
            uint candidatesKept = min(kept.z >> 24, k == 0 ? kReservoirCap : kNeighbourCap);
            float keptWeight = asfloat(kept.w);
            // Its candidates count even without a sample to reuse (an occluded one, a light no longer listed:
            // its target here is 0).
            choice.candidates += candidatesKept;
            if (candidatesKept == 0 || !(keptWeight > 0))
                continue;
            uint index = FindLightByKey(kept.xy);
            if (index == kNoLight)
                continue;
            if (k == 0)
                info.reusedHere = true;
            else
                info.reusedNeighbour = true;
            LightTriangle light = Lights[index];
            float2 weights = float2(kept.z & 0xFFF, (kept.z >> 12) & 0xFFF) / 4095.0;
            float3 lightPoint;
            float target = LightTarget(light, weights, position, N, geometricNormal, lightPoint);
            float w = target * keptWeight * candidatesKept;
            choice.weightSum += w;
            if (w > 0 && u * choice.weightSum < w)
            {
                choice.index = index;
                choice.lightPoint = lightPoint;
                choice.weights = weights;
                choice.target = target;
                choice.fromReuse = true;
            }
        }
    }
    info.fromReuse = choice.fromReuse;
    // Kept without a sample: the candidates it stands for.
    uint4 empty = uint4(0, 0, min(choice.candidates, kReservoirCap) << 24, 0);
    if (choice.index == kNoLight)
    {
        LightReservoirs[writeAt] = empty;
        return 0;
    }
    float W = choice.weightSum / (choice.candidates * choice.target);  // the chosen sample's contribution weight
    // The boiling filter, over the wave's pixels with a sample (8x4 of them on NVIDIA): with a contribution weight
    // kBoilingFactor times the average of the others' the sample is not kept (nor its history).
    float waveWeight = WaveActiveSum(W);
    uint waveSamples = WaveActiveCountBits(true);
    bool boiling = waveSamples >= 8 && W > kBoilingFactor * (waveWeight - W) / (waveSamples - 1);
    info.boiled = boiling;
    info.sampled = true;
    LightTriangle light = Lights[choice.index];
    bool lamp = IsLamp(light);
    info.lamp = lamp;
    info.twoSided = (asuint(light.uv2.z) & kLightTwoSided) != 0;
    float3 L;
    float distance;
    bool visible = LightVisible(light, position, choice.lightPoint, L, distance, self);
    info.distance = distance;
    if (!visible)
    {
        if (kLightDiagnostics)
        {
            float maxT = lamp ? distance - kLampSkip : distance * 0.998;
            info.occluder = OccluderFraction(position, L, maxT, RAY_FLAG_NONE);
            info.freeCulled = OccluderFraction(position, L, maxT, RAY_FLAG_CULL_BACK_FACING_TRIANGLES) >= 1;
        }
        LightReservoirs[writeAt] = boiling ? 0 : empty;  // no sample to reuse, but its candidates count
        return 0;
    }
    info.visible = true;
    uint2 packedWeights = uint2(round(saturate(choice.weights) * 4095));
    uint packed = packedWeights.x | (packedWeights.y << 12) | (min(choice.candidates, kReservoirCap) << 24);
    if (boiling)
    {
        LightReservoirs[writeAt] = 0;
    }
    else if (lamp)
    {
        LightReservoirs[writeAt] = uint4(LampKey(Lamps[asuint(light.corner.w)]), packed, asuint(W));
    }
    else
    {
        uint lightInstance = asuint(light.corner.w);
        uint2 key = LightKeyOf(InstanceData[lightInstance * 2 + 1], asuint(light.edge1.w),
                               LightGeometryIndex(light), asuint(light.uv2.w));
        LightReservoirs[writeAt] = uint4(key, packed, asuint(W));
    }
    return ShadeLight(light, choice.weights, L, distance, position, m, V, indirectAO, W, specular);
}

// One bounce of the listed lights' light (lamps' and emitters'): at a bounce's hit inside the light grid,
// kBounceCandidates new candidates from its cell, one shadow ray, ShadeLight's diffuse light (returned) and
// specular light. `status`: 1 a sample was chosen, 2 it was visible.
static const uint kBounceCandidates = 4;
// The specular bounce samples a light at its hit where its weight (the integrated BRDF) is above this.
static const float kSpecularLightWeight = 0.3;

float3 BounceLight(float3 surfacePoint, float3 position, MaterialSample m, float3 geometricNormal, float3 V,
                   float indirectAO, inout uint seed, out float3 specular, out uint status, uint self)
{
    specular = 0;
    status = 0;
    if (!LightsActive() || !(LightTreeOn != 0 || InLightGrid(surfacePoint)))
        return 0;
    LightChoice choice = NoLightChoice();
    LightCandidates(choice, kBounceCandidates, position, m.normal, geometricNormal, seed);
    if (choice.index == kNoLight)
        return 0;
    status = 1;
    float W = choice.weightSum / (choice.candidates * choice.target);
    LightTriangle light = Lights[choice.index];
    float3 L;
    float distance;
    if (!LightVisible(light, position, choice.lightPoint, L, distance, self))
        return 0;
    status = 2;
    return ShadeLight(light, choice.weights, L, distance, position, m, V, indirectAO, W, specular);
}

// The length a bounce that leaves the scene is given (Ray Reconstruction's specular hit distance).
static const float kEscapedDistance = 10000;

// The light arriving at `origin` from `direction`: what the first surface
// there emits and reflects of the sun toward it (and, with `sampleLights`, of
// the listed lamps and emitters: BounceLight), or the environment (scaled as
// the game scales its ambient light) when the ray leaves the scene.
// Emission comes from the front of single-sided parts only, as the game draws
// them; with `lightsSampled`, listed emissive triangles (lights.hlsli) emit
// nothing here: their light comes through SampledLight. With the
// CharacterLights setting off, characters' glowing suit parts emit nothing
// here either (scene_common.hlsli GlowsOnly). `openInstance`: the object
// whose open faces let light through to a hit on it (the camera sees the
// pixel's surface through one of them: main's openSelf), or kNoInstance.
float3 IncomingLight(float3 origin, float3 direction, float pathLength, float coneSpread, float environmentFactor,
                     bool lightsSampled, bool sampleLights, uint openInstance, inout uint seed, inout uint lightStatus,
                     out float hitDistance)
{
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0;
    ray.TMax = FarPlane;
    RayQuery<RAY_FLAG_NONE> query = TraceClosest(ray, RAY_FLAG_NONE, 0x01, coneSpread);
    hitDistance = kEscapedDistance;
    if (query.CommittedStatus() != COMMITTED_TRIANGLE_HIT)
        return EnvironmentRadiance(direction) * environmentFactor;
    float t = query.CommittedRayT();
    hitDistance = t * length(direction);
    uint instanceIndex = query.CommittedInstanceIndex();
    Surface s = LoadSurface(Records[query.CommittedInstanceID() + query.CommittedGeometryIndex()],
                            query.CommittedPrimitiveIndex(), query.CommittedTriangleBarycentrics(),
                            (float3x3)query.CommittedObjectToWorld3x4(), direction, coneSpread * (pathLength + t));
    s.cameraDistance = pathLength + t;
    s.geometryIndex = query.CommittedGeometryIndex();
    float4 instance = InstanceData[instanceIndex * 2];
    float4 extra = InstanceData[instanceIndex * 2 + 1];
    MaterialSample m = ShadeMaterial(s, instance, extra, kShadeAll);
    m.normal = SeenSideNormal(m.normal, s.geometricNormal);
    float3 position = origin + direction * t;
    position += s.geometricNormal * (0.002 + 0.0005 * (pathLength + t));
    float3 albedo = m.baseColor * (1 - m.metalness);
    float emission = m.emissive * Game.Post.BloomEmissiveness * EmissiveIntensity;  // the light it casts here
    if (emission > 0 &&
        ((!query.CommittedTriangleFrontFace() && (s.record.Flags & 2) == 0) || GlowsOnly(s.record) ||
         (lightsSampled && ListedLight(s.record, instance, extra, s.geometryIndex, query.CommittedPrimitiveIndex()))))
        emission = 0;
    float3 diffuse, specular;
    uint self = instanceIndex == openInstance ? instanceIndex : kNoInstance;
    SunLight(position, s.geometricNormal, m, -direction, seed, diffuse, specular, self);
    if (sampleLights)
    {
        // The lamps' and emitters' light at the hit: their light's first bounce.
        float hitAO = saturate(1 - (1 - m.ao) * Game.Light.aoIndirectLight);
        float3 lightSpecular;
        diffuse += BounceLight(origin + direction * t, position, m, s.geometricNormal, -direction, hitAO, seed,
                               lightSpecular, lightStatus, self);
        specular += lightSpecular;
    }
    return albedo * (emission + diffuse) + specular;
}

// --- Light history -------------------------------------------------------------------
//
// The lit view keeps each pixel's traced light averaged over time, apart from
// the surface's own detail: the diffuse light without the albedo (multiplied
// in afresh every frame, so textures stay sharp) and the specular light. The
// point a pixel sees now is projected into the previous recorded frame's camera
// and the four texels around it are blended (bilinear weights) where that
// frame's depth matches within 2 %; the others are disocclusions or other
// surfaces. A pixel without a matching texel starts anew.

// `previousPoint`: where the point was in the previous recorded frame, relative to that frame's camera (its
// object's motion included).
bool ReprojectHistory(float3 previousPoint, out float4 diffuse, out float4 specular)
{
    diffuse = 0;
    specular = 0;
    if (HistoryValid == 0)
        return false;
    float4 clip = mul(float4(previousPoint, 1), PreviousViewProjectionAt0);
    if (clip.w <= 0)
        return false;
    float2 previousUv = float2(clip.x / clip.w * 0.5 + 0.5, 0.5 - clip.y / clip.w * 0.5);
    // The history holds each pixel's light (accumulated from its jittered samples): it is read where the pixel's
    // centre was, the jittered sample's previous position less this frame's jitter. Read at the sample's own
    // position, a still history was resampled at another sub-pixel offset every frame, and over a long history
    // (256 frames with the camera still) the light spread by several pixels: smears and ghosts around edges, the
    // larger the fewer pixels the light is traced at (FSR's modes).
    float2 pixel = previousUv * float2(Width, Height) - 0.5 - Jitter;
    int2 corner = (int2)floor(pixel);
    float2 f = pixel - corner;
    float weightSum = 0;
    [unroll]
    for (uint tap = 0; tap < 4; ++tap)
    {
        int2 offset = int2(tap & 1, tap >> 1);
        int2 texel = corner + offset;
        if (any(texel < 0) || texel.x >= (int)Width || texel.y >= (int)Height)
            continue;
        // With a perspective projection, clip.w is the view distance (PreviousViewZ's).
        float stored = PreviousViewZ[GBufferTexel(texel + 0.5)];
        if (!(stored > 0 && abs(clip.w - stored) < 0.02 * stored))
            continue;
        float4 d = PreviousDiffuse[texel];
        if (d.a <= 0)
            continue;
        float w = (offset.x != 0 ? f.x : 1 - f.x) * (offset.y != 0 ? f.y : 1 - f.y);
        diffuse += d * w;
        specular += PreviousSpecular[texel] * w;
        weightSum += w;
    }
    if (weightSum < 1e-3)
        return false;
    diffuse /= weightSum;
    specular /= weightSum;
    return true;
}

// One atomic per wave and counter.
void Count(uint index, bool condition)
{
    uint n = WaveActiveCountBits(condition);
    if (WaveIsFirstLane() && n != 0)
        Stats.InterlockedAdd(index * 4, n);
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
        return;
    float2 uv = (id.xy + 0.5 + Jitter) / float2(Width, Height);
    float3 direction = mul(ScreenRay(uv), (float3x3)InvViewAt0);
    // The ray cone: the angle of the game's pixel, per unit of the ray parameter. Traced smaller (DLSS modes), the
    // cone still takes the output's pixel: textures as sharp as the upscaled image shows them (DLSS's mip bias of
    // log2(render / output)).
    float coneSpread = 2 * ScreenRayTerms.y / GBufferHeight * length(direction);

    RayDesc ray;
    ray.Origin = 0;
    ray.Direction = direction;
    ray.TMin = NearPlane;
    ray.TMax = FarPlane;
    DecalHits decals;
    RayQuery<RAY_FLAG_NONE> query = TraceClosestDecals(ray, RayFlags, 0x01, coneSpread, decals);

    bool hit = query.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
    float rayZ = hit ? query.CommittedRayT() : 0;
    // The game's G-buffer under the ray's sample (the pixel itself at the game's size).
    uint2 gbuffer = GBufferTexel(id.xy + 0.5 + Jitter);
    float rasterZ = ViewZ[gbuffer];
    // Grass pixels (pack_gbuffer.hlsl) are left out of the comparison.
    float rasterKind = NormalRoughness[gbuffer].w;  // pack_gbuffer.hlsl: 1/3 grass, 2/3 emissive
    bool foliage = rasterZ > 0 && abs(rasterKind - 1.0 / 3) < 0.1;
    bool rasterEmissive = abs(rasterKind - 2.0 / 3) < 0.1;
    bool surface = rasterZ > 0 && !foliage;
    float error = hit && surface ? (rayZ - rasterZ) / rasterZ : 0;

    Surface s = (Surface)0;
    uint kind = 0xFF;
    bool front = false;
    uint recordIndex = 0;
    float4 instance = 0;
    float4 blockData = 0;  // cube and generic records: texture offset, pattern bytes, source
    uint hitInstance = 0;  // the hit's TLAS instance (ApplyDecal)
    // Where the surface point was in the previous recorded frame, relative to that frame's camera
    // (research/docs/space-engineers-rt-plan.md §6.1): its object-space position through the instance's
    // transform then (moving grids, rotors, doors; at rest: the camera's motion alone), on a posed skinned mesh
    // its position in the previous pose.
    float3 previousPoint = 0;
    if (hit)
    {
        float3 objectPoint = mul(query.CommittedWorldToObject3x4(), float4(direction * rayZ, 1));
        uint transform = query.CommittedInstanceIndex() * 3;
        float3x4 previousTransform = float3x4(PreviousTransforms[transform], PreviousTransforms[transform + 1],
                                              PreviousTransforms[transform + 2]);
        recordIndex = query.CommittedInstanceID() + query.CommittedGeometryIndex();
        GeometryRecord hitRecord = Records[recordIndex];
        uint previousDistance = PreviousPositionsDistance(hitRecord);
        if (previousDistance != 0)
            objectPoint = PreviousPosePoint(hitRecord, query.CommittedPrimitiveIndex(),
                                            query.CommittedTriangleBarycentrics(), previousDistance);
        previousPoint = mul(previousTransform, float4(objectPoint, 1));
        s = LoadSurface(hitRecord, query.CommittedPrimitiveIndex(), query.CommittedTriangleBarycentrics(),
                        (float3x3)query.CommittedObjectToWorld3x4(), direction, coneSpread * rayZ);
        s.cameraDistance = rayZ * length(direction);
        s.geometryIndex = query.CommittedGeometryIndex();
        kind = Kind(s.record);
        front = query.CommittedTriangleFrontFace();
        hitInstance = query.CommittedInstanceIndex();
        instance = InstanceData[hitInstance * 2];
        blockData = InstanceData[hitInstance * 2 + 1];
    }
    // Whether the camera sees the hit through a one-sided face of its own object met from behind: a cavity modelled
    // as an inward-facing box shows through its lid this way (the Collector's funnel throat), and its walls would
    // get no light past the lid. Then such faces are open to the light of the hit's shadow rays and of its bounces'
    // hits on that object (openSelf: scene_common.hlsli Occluded's `self`). The pixels on records seen so before
    // (SeenThrough) look, and to find more one 8x8 tile in 16 per frame (whole tiles: the other waves skip it).
    uint openSelf = kNoInstance;
    bool seenBefore = hit && SeenThrough.Load(recordIndex * 4) == OpenGeneration;
    if (hit && front && (seenBefore || ((Pcg((id.x >> 3) + Pcg(id.y >> 3)) + Frame) & 15) == 0))
    {
        RayDesc behind = ray;
        behind.TMax = rayZ * 0.9999;
        RayQuery<RAY_FLAG_CULL_FRONT_FACING_TRIANGLES | RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH |
                 RAY_FLAG_FORCE_OPAQUE> seen;
        seen.TraceRayInline(Scene, RAY_FLAG_NONE, 0x02, behind);
        seen.Proceed();
        if (seen.CommittedStatus() == COMMITTED_TRIANGLE_HIT && seen.CommittedInstanceIndex() == hitInstance &&
            !seen.CommittedTriangleFrontFace())
        {
            GeometryRecord seenRecord = Records[seen.CommittedInstanceID() + seen.CommittedGeometryIndex()];
            if ((seenRecord.Flags & 2) == 0 && !IsDecal(seenRecord))
            {
                openSelf = hitInstance;
                if (!seenBefore)
                    SeenThrough.Store(recordIndex * 4, OpenGeneration);
            }
        }
    }
    uint source = SourceOf(blockData);
    bool deformed = hit && Deformed(s.record, source);

    bool fine = hit && surface && abs(error) < 0.001;
    bool close = hit && surface && abs(error) >= 0.001 && abs(error) < 0.01;
    bool nearer = hit && surface && error <= -0.01;
    bool farther = hit && surface && error >= 0.01;
    bool missing = !hit && surface;
    bool extra = hit && rasterZ <= 0;
    Count(0, true);
    Count(1, surface);
    Count(2, fine);
    Count(3, close);
    Count(4, nearer);
    Count(5, farther);
    Count(6, missing);
    Count(7, extra);
    Count(8, hit);
    Count(9, hit && front);
    Count(10, kind == 0);
    Count(11, kind == 1);
    Count(12, kind == 2);
    Count(13, fine && front);
    Count(14, foliage);

    // The pixel's motion (research/docs/space-engineers-rt-plan.md §6.1): through the hit's object motion where
    // the ray found the game's surface, else the camera's motion at the G-buffer's depth, for the sky its
    // direction alone (at infinity); zero without a previous frame. With full coverage the traced surface is
    // what the pixel shows: its motion wherever the ray hit, the sky's where it missed; the jittered sample's
    // own position is the one that moved.
    bool tracedSurface = hit && (FullCoverage != 0 || fine || close);
    float4 previousClip = tracedSurface ? mul(float4(previousPoint, 1), PreviousViewProjectionAt0)
                        : rasterZ > 0 && FullCoverage == 0
                            ? mul(float4(direction * rasterZ + CameraDelta, 1), PreviousViewProjectionAt0)
                            : mul(float4(direction, 0), PreviousViewProjectionAt0);
    float2 motion = 0;
    if (HasPrevious != 0 && previousClip.w > 0)
        motion = float2(previousClip.x / previousClip.w * 0.5 + 0.5, 0.5 - previousClip.y / previousClip.w * 0.5) *
                 float2(Width, Height) - (id.xy + 0.5 + Jitter);
    Motion[id.xy] = motion;

    float3 view = normalize(direction);
    float shade = 0.25 + 0.75 * abs(dot(s.geometricNormal, view));
    float3 color = float3(0.02, 0.02, 0.05);
    float alpha = Strength;
    float upscaleZ = 0;  // the view depth UpscaleDepth takes (0: the sky)
    if (Mode == 0)
    {
        if (hit)
            color = HashColor(recordIndex) * shade;
    }
    else if (Mode == 1)
    {
        if (foliage)
            color = float3(0.05, 0.3, 0.05);
        else if (fine || close)
            color = (fine ? float3(0.8, 0.8, 0.8) : float3(0.85, 0.85, 0.55)) * shade;
        else if (nearer)
            color = float3(1, 0, 1);
        else if (farther)
            color = float3(1, 0.5, 0);
        else if (missing)
            color = float3(1, 0, 0);
        else if (extra)
            color = float3(0, 0.8, 1);
    }
    else if (Mode == 2)
    {
        if (hit)
            color = (front ? float3(0.2, 0.9, 0.2) : float3(0.9, 0.2, 0.2)) * shade;
    }
    else if (Mode == 3)
    {
        if (hit)
        {
            float3 n = s.geometricNormal;
            float3 origin = direction * rayZ + n * (0.002 + rayZ * 0.0005);
            uint seed = Pcg(id.x + Pcg(id.y + Pcg(Frame)));
            float3 toSun = ConeDirection(SunToward, SunRadius, Random(seed), Random(seed));
            float lit = saturate(dot(n, toSun));
            if (lit > 0 && Occluded(origin, toSun, FarPlane, 0, openSelf))
                lit = 0;
            float open = 0;
            [unroll]
            for (uint sample = 0; sample < 2; ++sample)
                open += Occluded(origin, CosineDirection(n, Random(seed), Random(seed)), 5.0, 0, openSelf) ? 0 : 0.5;
            color = 0.7 * (SunColor * lit + Ambient * open);
            // Probes: the first shadow caster (instance mask 0x02) between the point and the camera, which sees it
            // (rt_core.cpp LogProbes prints it as a hit: "ray depth" its distance, "G-buffer" the point's): what
            // keeps the light from a point the camera's rays reach.
            uint probe = ProbeAt(id.xy);
            if (probe != kNoProbe)
            {
                RayDesc back;
                back.Origin = origin;
                back.Direction = -direction;
                back.TMin = 0;
                back.TMax = rayZ;
                RayQuery<RAY_FLAG_NONE> caster = TraceClosest(back, RAY_FLAG_NONE, 0x02, coneSpread);
                bool blocked = caster.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
                GeometryRecord br = (GeometryRecord)0;
                if (blocked)
                    br = Records[caster.CommittedInstanceID() + caster.CommittedGeometryIndex()];
                uint base = kProbeOffset + probe * kProbeBytes;
                Stats.Store4(base, uint4(1 | (blocked ? 2 : 0) | ((Kind(br) & 0xFF) << 8),
                                         blocked ? caster.CommittedInstanceIndex() : 0,
                                         blocked ? caster.CommittedInstanceID() : 0,
                                         blocked ? caster.CommittedGeometryIndex() : 0));
                Stats.Store4(base + 16, uint4(blocked ? caster.CommittedPrimitiveIndex() : 0, br.Layout, br.Material, 0));
                Stats.Store4(base + 32, uint4(0, 0, 0, 0));
                Stats.Store4(base + 48, uint4(0, 0, 0, 0));
                Stats.Store4(base + 64, uint4(0, 0, 0, 0));
                Stats.Store4(base + 80, uint4(0, asuint(blocked ? caster.CommittedRayT() : 0), asuint(rayZ),
                                              (br.Flags & 0xFF) | (s.record.Flags << 8)));
            }
        }
    }
    else if (Mode == 10)
    {
        // Motion: grey at rest, red/cyan along x and green/magenta along y, full at 16 pixels per frame.
        float2 m = clamp(motion / 16, -1, 1);
        color = float3(0.5 + 0.5 * m.x, 0.5 + 0.5 * m.y, 0.5 - 0.25 * (m.x + m.y)) * (hit || rasterZ > 0 ? shade : 0.6);
    }
    else
    {
        uint probe = ProbeAt(id.xy);
        // The material as the game's G-buffer pass computes it, and without each
        // of its adjustments (material compare and probes only).
        bool variants = Mode == 5 || probe != kNoProbe;
        MaterialSample material = (MaterialSample)0;
        MaterialSample noOffset = (MaterialSample)0;
        MaterialSample vertexFrame = (MaterialSample)0;
        MaterialSample raw = (MaterialSample)0;
        bool decalApplied = false;
        uint projectedMet = 0;
        uint projectedApplied = 0;
        bool projectedDropped = false;
        if (hit)
        {
            material = ShadeMaterial(s, instance, blockData, kShadeAll);
            decalApplied = ApplyDecal(material, decals, hitInstance, s.toWorld, instance, blockData, direction,
                                      rayZ, s.geometricNormal, coneSpread);
            projectedApplied = ApplyProjectedDecals(material, direction * rayZ, rayZ, s.footprint, projectedMet,
                                                    projectedDropped);
            if (variants)
            {
                noOffset = ShadeMaterial(s, instance, blockData, kShadeAll & ~kShadeTextureOffset);
                vertexFrame = ShadeMaterial(s, instance, blockData, kShadeAll & ~kShadeArmorFrame);
                raw = ShadeMaterial(s, instance, blockData, kShadeAll & ~kShadeMultipliers);
            }
        }
        // Compared where the ray found the game's surface, on textured meshes.
        bool compared = (fine || close) && s.hasAttributes && (kind != 2 || VoxelMaterialCount != 0);
        float4 rasterBase = BaseColorMetalness[gbuffer];
        float4 rasterNormalRoughness = NormalRoughness[gbuffer];
        float3 rasterNormal = OctahedralDecode(rasterNormalRoughness.xy);
        MaterialError e = CompareMaterial(material, rasterBase, rasterNormal, rasterNormalRoughness.z);
        Count(16, compared);
        Count(17, compared && e.color < 0.02);
        Count(18, compared && e.color < 0.05);
        Count(19, compared && e.color < 0.15);
        Count(20, compared && e.normalCosine > 0.9962);  // 5 degrees
        Count(21, compared && e.normalCosine > 0.9659);  // 15 degrees
        Count(22, compared && e.roughness < 0.05);
        Count(23, compared && e.metalness < 0.1);
        // Decals: hits on cutout decals; hits whose ray met blended decals, whose material one changed
        // (ApplyDecal), whose ray met more than one.
        Count(100, hit && (s.record.Flags & kPartDecalCutout) != 0);
        Count(101, hit && decals.count > 0);
        Count(102, hit && decalApplied);
        Count(103, hit && decals.dropped > 0);
        // Hits on a cutout decal that lay within the game's decal depth bias behind the closest surface.
        Count(122, hit && decals.behind != 0);
        // Projected decals (ApplyProjectedDecals): hits inside one's box, changed by one, inside more than
        // kMaxPointDecals; compared pixels one changed, of them those whose base color is within 0.05 and 0.02 of
        // the G-buffer's (which the game's decal pass blended them into), whose normal is within 15 and 5 degrees.
        bool projectedCompared = compared && projectedApplied > 0;
        Count(104, hit && projectedMet > 0);
        Count(105, hit && projectedApplied > 0);
        Count(106, hit && projectedDropped);
        Count(107, projectedCompared);
        Count(108, projectedCompared && e.color < 0.05);
        Count(109, projectedCompared && e.color < 0.02);
        Count(110, projectedCompared && e.normalCosine > 0.9659);
        Count(111, projectedCompared && e.normalCosine > 0.9962);
        bool fractional = deformed && any(frac(blockData.xy) != 0);
        MaterialError eNoOffset = (MaterialError)0;
        MaterialError eVertexFrame = (MaterialError)0;
        if (variants)
        {
            eNoOffset = CompareMaterial(noOffset, rasterBase, rasterNormal, rasterNormalRoughness.z);
            eVertexFrame = CompareMaterial(vertexFrame, rasterBase, rasterNormal, rasterNormalRoughness.z);
        }
        if (Mode == 5)
        {
            MaterialError eRaw = CompareMaterial(raw, rasterBase, rasterNormal, rasterNormalRoughness.z);
            // 24-41: deformed cube blocks with and without the pattern offset and
            // the per-pixel tangent frame; everything without the multipliers.
            bool d = compared && deformed;
            Count(24, d);
            Count(25, d && e.color < 0.02);
            Count(26, d && eNoOffset.color < 0.02);
            Count(27, d && e.normalCosine > 0.9962);
            Count(28, d && eVertexFrame.normalCosine > 0.9962);
            Count(29, d && e.roughness < 0.05);
            Count(30, d && eNoOffset.roughness < 0.05);
            Count(31, d && e.metalness < 0.1);
            Count(32, d && eNoOffset.metalness < 0.1);
            Count(33, d && fractional);
            Count(34, d && fractional && e.color < 0.02);
            Count(35, d && fractional && eNoOffset.color < 0.02);
            Count(36, compared && eRaw.color < 0.02);
            Count(37, compared && eRaw.roughness < 0.05);
            Count(38, compared && eRaw.metalness < 0.1);
            Count(39, compared && eRaw.normalCosine > 0.9962);
            Count(40, d && e.normalCosine > 0.9659);
            Count(41, d && eVertexFrame.normalCosine > 0.9659);
            // 42-52: by how the object reaches the mirror.
            bool model = compared && kind == 0;
            bool cube = compared && kind == 1 && source == kSourceCube;
            bool placed = compared && kind == 1 && source == kSourceObject;
            Count(42, model);
            Count(43, model && e.color < 0.02);
            Count(44, cube);
            Count(45, cube && e.color < 0.02);
            Count(46, placed);
            Count(47, placed && e.color < 0.02);
            Count(48, placed && HasBones(s.record));  // bones without cube records
            Count(49, cube && !HasBones(s.record));   // cube records without bones
            Count(50, model && e.metalness < 0.1);
            Count(51, cube && e.metalness < 0.1);
            Count(52, placed && e.metalness < 0.1);
            Count(53, compared);  // the denominator of 36-59 (16 also counts the other material views)
            // 54-59: voxel cells.
            bool voxel = compared && kind == 2;
            Count(54, voxel);
            Count(55, voxel && e.color < 0.02);
            Count(56, voxel && e.normalCosine > 0.9962);
            Count(57, voxel && e.roughness < 0.05);
            Count(58, voxel && e.color < 0.05);
            Count(59, voxel && e.color < 0.15);
        }
        if (probe != kNoProbe)
        {
            // rt_core.cpp (LogProbes) decodes these 24 uints.
            MaterialRecord record = Materials[s.record.Material];
            uint flags = 1 | (hit ? 2 : 0) | (front ? 4 : 0) | (deformed ? 8 : 0) | (compared ? 16 : 0) |
                         (rasterEmissive ? 32 : 0) | ((kind & 0xFF) << 8) | ((source & 0xFF) << 16);
            uint base = kProbeOffset + probe * kProbeBytes;
            Stats.Store4(base, uint4(flags, hit ? query.CommittedInstanceIndex() : 0,
                                     hit ? query.CommittedInstanceID() : 0, hit ? query.CommittedGeometryIndex() : 0));
            Stats.Store4(base + 16, uint4(hit ? query.CommittedPrimitiveIndex() : 0, s.record.Layout, s.record.Material,
                                          asuint(blockData.z)));
            Stats.Store4(base + 32, uint4(asuint(blockData.x), asuint(blockData.y), asuint(s.uv.x), asuint(s.uv.y)));
            Stats.Store4(base + 48, uint4(PackMaterialColor(material.baseColor, material.metalness),
                                          PackMaterialColor(rasterBase.rgb, rasterBase.a),
                                          PackMaterialColor(noOffset.baseColor, noOffset.metalness),
                                          PackKeyColor(instance)));
            Stats.Store4(base + 64, uint4(PackUnorm2x16(1 - material.gloss, rasterNormalRoughness.z),
                                          PackUnorm2x16(e.normalCosine * 0.5 + 0.5, eVertexFrame.normalCosine * 0.5 + 0.5),
                                          record.Textures.x | (record.Textures.y << 16),
                                          record.Textures.z | (record.Textures.w << 16)));
            // The traced emissive (overrides and multipliers applied), not the roughness without the multipliers.
            Stats.Store4(base + 80, uint4(PackUnorm4(float4(material.paintMask, material.coloring, material.ao,
                                                            material.emissive)),
                                          asuint(rayZ), asuint(rasterZ), (record.Flags & 0xFF) | (s.record.Flags << 8)));
            RecordProbeCandidates(ray, coneSpread, base);
        }
        if (Mode == 4)
        {
            if (hit)
                color = material.baseColor;
        }
        else if (Mode == 6)
        {
            if (hit)
            {
                float3 path = kind == 2 ? float3(0.3, 0.3, 0.3)
                            : kind == 0 ? float3(0.2, 0.4, 1.0)
                            : source == kSourceGeneric ? float3(0.7, 0.3, 0.9)
                            : source == kSourceObject ? (HasBones(s.record) ? float3(0.0, 0.9, 0.9)
                                                                            : float3(0.8, 0.8, 0.8))
                            : !deformed ? float3(0.2, 0.8, 0.2)
                            : fractional ? float3(1.0, 0.1, 0.1)
                                         : float3(1.0, 0.85, 0.1);
                color = path * shade;
            }
        }
        else if (Mode >= 7)  // 7-9, 11-14
        {
            // Traced light where the ray found the game's surface (depth within 1 %); elsewhere, also on
            // what the mirror lacks, the game's own light stays. Full coverage: every hit is lit (also where
            // the game drew sky: Ray Reconstruction sees the edge a jittered ray found) and every surface
            // pixel the game drew is composited, the environment where the ray missed (beyond an edge).
            bool litPixel = FullCoverage != 0 ? hit && !foliage : fine || close;
            bool background = FullCoverage != 0 && surface && !hit;
            alpha = (FullCoverage != 0 ? surface : litPixel) ? 1 : 0;
            float4 diffuseHistory = 0;  // zero: no history here for the next frame
            float4 specularHistory = 0;
            float reusedFrames = 0;
            LightSampleInfo lightInfo = (LightSampleInfo)0;
            uint bounceStatus = 0;    // the diffuse bounce's light sample at its hit (BounceLight)
            bool bounceOutlier = false;  // the diffuse bounce's light far above its wave's (statistic 82)
            uint sampledSum = 0;         // statistic 83: the sampled light's luminance x 16, dithered
            bool sampledLimit = false;   // statistic 84: at its limit
            bool noiseValid = false;     // statistics 90-93: this frame's diffuse light against its history
            float noise = 0;
            float noiseHistory = 0;
            uint specularStatus = 0;  // the specular bounce's light sample at its hit
            float specularHitDistance = kEscapedDistance;  // its length (Ray Reconstruction's guide)
            if (litPixel)
            {
                material.normal = SeenSideNormal(material.normal, s.geometricNormal);
                uint seed = Pcg(id.x + Pcg(id.y + Pcg(Frame)));
                float distance = rayZ * length(direction);
                float3 position = direction * rayZ + s.geometricNormal * (0.002 + 0.0005 * distance);
                float3 V = -view;
                float3 albedo = material.baseColor * (1 - material.metalness);
                float3 f0 = lerp(0.04, material.baseColor, material.metalness);
                float indirectAO = saturate(1 - (1 - material.ao) * Game.Light.aoIndirectLight);
                bool everything = Mode != 9 && Mode != 13 && Mode != 14;  // 9, 13, 14: the emitters' light alone
                float3 diffuseLight = 0;
                float3 specularLight = 0;
                if (everything)
                    SunLight(position, s.geometricNormal, material, V, seed, diffuseLight, specularLight, openSelf);
                // Emissive surfaces and lamps: next-event estimation (the diffuse bounce below leaves the
                // listed emitters' light out; the game's lamp passes skip these pixels).
                float3 sampledSpecular;
                float3 sampledDiffuse = SampledLight(id.xy, direction * rayZ, previousPoint, position, material,
                                                     s.geometricNormal, V, indirectAO, seed, sampledSpecular,
                                                     lightInfo, openSelf);
                diffuseLight += sampledDiffuse;
                float sampledLuminance = Luminance(sampledDiffuse);
                sampledLimit = sampledLuminance > 64;
                sampledSum = (uint)(min(sampledLuminance, 64) * 16 + Random(seed));
                specularLight += sampledSpecular;
                if (everything)
                {
                    // Diffuse: one cosine-distributed bounce (Lambert: the albedo, multiplied in below, times
                    // what arrives).
                    float3 diffuseDirection = CosineDirection(material.normal, Random(seed), Random(seed));
                    // (Listed emitters' light came through SampledLight where the point lies in the light grid.)
                    float diffuseHitDistance;
                    float3 bounce = 0;
                    if (dot(diffuseDirection, s.geometricNormal) > 0)
                        bounce = indirectAO * IncomingLight(position, diffuseDirection, distance, coneSpread * 8,
                                                            Game.Light.ambientDiffuseFactor, lightInfo.inGrid, true,
                                                            openSelf, seed, bounceStatus, diffuseHitDistance);
                    diffuseLight += bounce;
                    float bounceLuminance = Luminance(bounce);
                    float waveBounce = WaveActiveSum(bounceLuminance);
                    uint waveLanes = WaveActiveCountBits(true);
                    bounceOutlier = waveLanes >= 8 &&
                                    bounceLuminance > kBoilingFactor * (waveBounce - bounceLuminance) / (waveLanes - 1);
                    // Specular: one direction of the lobe, weighted by the integrated BRDF; it sees emissive
                    // surfaces itself (glossy reflections of small lights).
                    float3 specularDirection = SpecularDirection(material.normal, V, material.gloss, Random(seed),
                                                                 Random(seed));
                    // Its hit is lit by a sampled lamp or emitter too where the reflection matters (metals: a
                    // specular weight above kSpecularLightWeight), so that lamp-lit surfaces reflect lit. (At 0.1,
                    // glossy dielectrics at grazing angles too: 13 % of RedShip's lit pixels, +0.5 ms.)
                    float3 specularWeight = EnvironmentBrdf(f0, 1 - material.gloss, saturate(dot(material.normal, V)));
                    if (dot(specularDirection, s.geometricNormal) > 0)
                        specularLight += specularWeight * indirectAO *
                                         IncomingLight(position, specularDirection, distance, coneSpread,
                                                       Game.Light.ambientSpecularFactor, false,
                                                       Luminance(specularWeight) > kSpecularLightWeight, openSelf,
                                                       seed, specularStatus, specularHitDistance);
                }
                // This frame's sample joins the history of the point it sees; glossy reflections, which move
                // with the camera unlike the surface, keep a shorter one. Mode 11 hands this frame's light,
                // before the accumulation, to Ray Reconstruction (which accumulates itself).
                upscaleZ = FullCoverage != 0 ? rayZ : rasterZ;
                if (Mode == 11)
                {
                    float emissionLight = material.emissive * Game.Post.BloomEmissiveness;
                    // Alpha: whether its output is composed here (denoise.hlsl ComposeRayReconstruction).
                    RrColor[id.xy] = float4(albedo * (emissionLight + diffuseLight) + specularLight, alpha);
                    RrDiffuseAlbedo[id.xy] = float4(albedo, 1);
                    RrSpecularAlbedo[id.xy] = float4(EnvironmentBrdf(f0, 1 - material.gloss,
                                                                     saturate(dot(material.normal, V))), 1);
                    RrNormalRoughness[id.xy] = float4(material.normal, 1 - material.gloss);
                    RrDepth[id.xy] = FullCoverage != 0 ? rayZ : rasterZ;
                    RrSpecularHitDistance[id.xy] = specularHitDistance;
                }
                float luminance = Luminance(diffuseLight);
                float moment = luminance * luminance;
                float frames = 1;
                float4 previousDiffuse, previousSpecular;
                // Statistics 74-76: pixels on objects that moved since then, their history found, and found by
                // the camera's motion alone (the reprojection before objects carried their own motion).
                bool moving = length(previousPoint - (direction * rayZ + CameraDelta)) > 0.001;
                bool reprojected = ReprojectHistory(previousPoint, previousDiffuse, previousSpecular);
                Count(74, moving);
                Count(75, moving && reprojected);
                if (moving)
                {
                    float4 unused0, unused1;
                    Count(76, ReprojectHistory(direction * rayZ + CameraDelta, unused0, unused1));
                }
                if (reprojected)
                {
                    noiseValid = previousDiffuse.a >= 8;
                    noiseHistory = Luminance(previousDiffuse.rgb);
                    noise = luminance - noiseHistory;
                    frames = min(previousDiffuse.a + 1, (float)HistoryCap);
                    float specularFrames = min(frames, max(2, HistoryCap * (1 - 0.9 * material.gloss * material.gloss)));
                    diffuseLight = lerp(previousDiffuse.rgb, diffuseLight, 1 / frames);
                    moment = lerp(previousSpecular.a, moment, 1 / frames);
                    specularLight = lerp(previousSpecular.rgb, specularLight, 1 / specularFrames);
                    reusedFrames = frames;
                }
                diffuseHistory = float4(diffuseLight, frames);
                AlbedoEmission[id.xy] = float4(albedo, material.emissive * Game.Post.BloomEmissiveness);
                specularHistory = float4(specularLight, min(moment, 60000));
                color = albedo * (material.emissive * Game.Post.BloomEmissiveness + diffuseLight) + specularLight;
                // 15, 63: this pixel's shadow rays that took the open-face pass (scene_common.hlsli Occluded); lit
                // pixels seen through a face of their own object (openSelf; one atomic per wave).
                uint2 openWave = WaveActiveSum(uint2(g_openFacePasses, openSelf != kNoInstance ? 1u : 0u));
                if (WaveIsFirstLane() && (openWave.x | openWave.y) != 0)
                {
                    Stats.InterlockedAdd(15 * 4, openWave.x);
                    Stats.InterlockedAdd(63 * 4, openWave.y);
                }
                // 120, 121: this pixel's candidates from the grid's coarse levels, of them those drawn from the
                // light tree itself (their cell not filled this frame).
                uint2 levelWave = WaveActiveSum(uint2(g_levelDraws, g_levelWalks));
                if (WaveIsFirstLane() && levelWave.x != 0)
                {
                    Stats.InterlockedAdd(120 * 4, levelWave.x);
                    Stats.InterlockedAdd(121 * 4, levelWave.y);
                }
            }
            else
            {
                LightReservoirs[ReservoirIndex(LightSet & 1, id.xy)] = 0;  // no light sample kept here
                // Where the ray left the scene: the environment (the game's far probe), shown on a surface
                // pixel with full coverage (no history: denoise.hlsl leaves it as it is).
                float3 environment = hit ? float3(0, 0, 0) : EnvironmentRadiance(direction);
                if (background)
                    color = environment;
                upscaleZ = hit ? (rasterZ > 0 ? rasterZ : rayZ) : 0;
                if (Mode == 11)
                {
                    // The sky as the DLSS-RR Integration Guide wants it (3.4): mid-grey diffuse albedo, no
                    // specular albedo, normal or roughness, the farthest hit distance; its colour as the ray saw
                    // it, composed only with full coverage (alpha 0: the game's light stays here).
                    RrColor[id.xy] = float4(environment, alpha);
                    RrDiffuseAlbedo[id.xy] = hit ? float4(0, 0, 0, 0) : float4(0.5, 0.5, 0.5, 1);
                    RrSpecularAlbedo[id.xy] = 0;
                    RrNormalRoughness[id.xy] = hit ? float4(-view, 1) : float4(0, 0, 0, 0);
                    RrDepth[id.xy] = hit ? (rasterZ > 0 ? rasterZ : rayZ) : 1e6;
                    RrSpecularHitDistance[id.xy] = hit ? kEscapedDistance : 65504;
                }
            }
            HistoryDiffuse[id.xy] = diffuseHistory;
            HistorySpecular[id.xy] = specularHistory;
            // 27-29 (emissive_lights.hlsl has 24-26, 31): lit pixels with a light sample, with an unshadowed
            // one, inside the light grid. 32-39 (kLightDiagnostics): occluded samples by where the nearest
            // occluder stops the ray (< 50 %, 50-95 %, >= 95 % of the way), those free with back faces culled;
            // samples within 0.5, 2, 8 m; two-sided ones. 60-62: lit pixels, those that reused their history,
            // its length summed (one atomic per wave).
            bool occluded = lightInfo.sampled && !lightInfo.visible;
            Count(27, lightInfo.sampled);
            Count(28, lightInfo.visible);
            Count(29, lightInfo.inGrid);
            Count(32, kLightDiagnostics && occluded && lightInfo.occluder < 0.5);
            Count(33, kLightDiagnostics && occluded && lightInfo.occluder >= 0.5 && lightInfo.occluder < 0.95);
            Count(34, kLightDiagnostics && occluded && lightInfo.occluder >= 0.95);
            Count(35, kLightDiagnostics && occluded && lightInfo.freeCulled);
            Count(36, lightInfo.sampled && lightInfo.distance < 0.5);
            Count(37, lightInfo.sampled && lightInfo.distance < 2);
            Count(38, lightInfo.sampled && lightInfo.distance < 8);
            Count(39, lightInfo.sampled && lightInfo.twoSided);
            // 48-50: lit pixels that found the previous frame's sample near them again, a neighbour's; chose a
            // reused one.
            // 52, 54: lit pixels whose sample is a lamp's, unshadowed (emissive_lights.hlsl has 51).
            Count(48, lightInfo.reusedHere);
            Count(49, lightInfo.reusedNeighbour);
            Count(50, lightInfo.sampled && lightInfo.fromReuse);
            // 81: lit pixels whose chosen sample the boiling filter did not keep. 82: whose diffuse bounce
            // brought kBoilingFactor times the average of the wave's others (fireflies for the denoiser).
            Count(81, lightInfo.boiled);
            Count(82, bounceOutlier);
            // 83: the sampled lamps' and emitters' diffuse light (SampledLight, without the albedo) summed over the
            // lit pixels, its luminance x 16 (dithered; at most 64, 84: the pixels beyond), linear: estimators of
            // it compared (emitters against emitters-reference), the exposure of the game's frame aside.
            uint sampledWave = WaveActiveSum(sampledSum);
            if (WaveIsFirstLane() && sampledWave != 0)
                Stats.InterlockedAdd(83 * 4, sampledWave);
            Count(84, sampledLimit);
            // 90-93: this frame's diffuse light against the history it joins (a still camera: the average of the
            // frames before), on pixels with 8 frames of it or more: |difference| x 256 (at most 4), the 8x4
            // tile's (the wave's) |summed difference| x 256 (per pixel 0.18 of the pixels' for independent noise,
            // as much for patches: the flicker the denoiser cannot remove), the pixels, the history's luminance
            // x 16 (at most 64); dithered.
            float dither = (Pcg(id.x + Pcg(id.y + Pcg(Frame ^ 0x51ED270Bu))) & 0xFFFF) / 65536.0;
            float noiseClamped = noiseValid ? clamp(noise, -4, 4) : 0;
            uint noisePixels = WaveActiveCountBits(noiseValid);
            uint noiseSum = WaveActiveSum((uint)(abs(noiseClamped) * 256 + dither));
            float waveNoise = WaveActiveSum(noiseClamped);
            uint historySum = WaveActiveSum(noiseValid ? (uint)(min(noiseHistory, 64) * 16 + dither) : 0u);
            if (WaveIsFirstLane() && noisePixels != 0)
            {
                Stats.InterlockedAdd(90 * 4, noiseSum);
                Stats.InterlockedAdd(91 * 4, (uint)(abs(waveNoise) * 256 + dither));
                Stats.InterlockedAdd(92 * 4, noisePixels);
                Stats.InterlockedAdd(93 * 4, historySum);
            }
            Count(52, lightInfo.sampled && lightInfo.lamp);
            Count(54, lightInfo.visible && lightInfo.lamp);
            // 77-78: lit pixels whose diffuse bounce sampled a light at its hit (BounceLight), unshadowed.
            Count(77, bounceStatus >= 1);
            Count(78, bounceStatus == 2);
            // 79-80: those whose specular bounce did (glossy and metal surfaces).
            Count(79, specularStatus >= 1);
            Count(80, specularStatus == 2);
            Count(60, litPixel);
            Count(61, reusedFrames > 0);
            uint lengthSum = WaveActiveSum((uint)reusedFrames);
            if (WaveIsFirstLane() && lengthSum != 0)
                Stats.InterlockedAdd(62 * 4, lengthSum);
        }
        else if (compared)
        {
            color = e.color < 0.02 ? float3(0.1, 0.8, 0.1)
                  : e.color < 0.05 ? float3(0.9, 0.9, 0.1)
                  : e.color < 0.15 ? float3(1.0, 0.5, 0.0)
                                   : float3(1.0, 0.0, 0.0);
            color *= shade;
        }
        else if (hit)
        {
            color = 0.15 * shade;
        }
    }
    // Traced smaller than the game's size (DLSS and FSR modes), the output is denoise.hlsl pass 3's: Ray
    // Reconstruction's or FSR's light, upscaled.
    if (!Upscaled())
        Output[id.xy] = float4(color, alpha);
    // FSR's inputs after our filter (UpscaleInputs bit 1): every pixel's light (pass 2 puts the filtered light over
    // the lit pixels'); for FSR and frame generation (bit 2) and Ray Reconstruction's view, the game's reverse-Z depth
    // of what the pixel shows (rt_core.cpp RecordFrame).
    if ((UpscaleInputs & 1) != 0)
        RrColor[id.xy] = float4(color, alpha);
    if (UpscaleInputs != 0 || Mode == 11)
        UpscaleDepth[id.xy] = upscaleZ > 0 ? saturate(ProjM43 / upscaleZ - ProjM33) : 0;
}

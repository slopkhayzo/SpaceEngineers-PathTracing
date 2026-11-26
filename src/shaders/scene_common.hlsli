// Declarations the traced passes share (primary_view.hlsl, emissive_lights.hlsl;
// one root signature, rt_core.cpp CreatePipelines): the scene mirror's
// geometry records, materials, instance data, geometry pages and textures,
// the frame's constants, and the helpers that read them.

#ifndef SCENE_COMMON_HLSLI
#define SCENE_COMMON_HLSLI

#include "game_frame.hlsli"
#include "vertex_packing.hlsli"  // DecodeNormal, DecodeTangent

struct GeometryRecord
{
    uint Page;             // ByteAddressBuffer in Pages (15: skinned objects' poses, scene.cpp PoseSkins)
    uint IndexOffset;      // bytes: the part's first index
    uint VertexOffset;     // bytes: VB0 vertex 0 of the part
    uint AttributeOffset;  // bytes: VB1 vertex 0, or 0xFFFFFFFF
    uint Layout;           // bits 0-7 VB0 stride, 8-15 VB1 stride, 16 32-bit indices, 17 float3 positions,
                           // 18-31 PreviousPositionsDistance
    uint Material;         // index into Materials
    uint Flags;            // bits 0-7 part flags, 8-15 mesh kind (0 model, 1 old pipeline, 2 voxel), 16 bones
    uint Counts;           // bits 0-23 the part's triangles, 24-31 the mesh's records (scene.cpp PublishRecords)
};

struct MaterialRecord
{
    uint4 Textures;  // descriptor slots: ColorMetal, NormalGloss, Extensions, Alphamask
    uint Flags;      // 1 alpha masked, 2 metalness colorable
    uint3 Padding;
};

StructuredBuffer<GeometryRecord> Records : register(t5);
StructuredBuffer<MaterialRecord> Materials : register(t6);
// By TLAS instance index, two float4: paint HSV offset and flags; the armor
// texture offset (objects: asuint, the part overrides' slot, see PartOverride,
// and the game actor's id), the w bytes of bones 5-7 (asuint) and the source
// (asuint: kSource*, see SourceOf; cube and generic records: | their place in
// the block buffer << kSourceShift, which lasts from frame to frame unlike the
// instance's index, see LightKeyOf).
StructuredBuffer<float4> InstanceData : register(t7);
// Per part of the objects the game gives their own color and emissivity
// (MyRenderProxy.UpdateColorEmissivity: status lights, screens, lights switched
// off): xyz the color multiplier, w the emissivity (scene.cpp BuildTopLevel).
StructuredBuffer<float4> PartOverrides : register(t14);
ByteAddressBuffer Pages[16] : register(t0, space1);
Texture2D<float4> Textures[16384] : register(t0, space2);
SamplerState LinearWrap : register(s0);

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
    uint RayFlags;          // added to the primary ray (culling)
    float Strength;
    float3 SunToward;       // unit vector toward the sun
    float SunRadius;        // angular radius of the sun disc (radians)
    float3 SunColor;
    float Ambient;          // constant sky light of the clay view
    // The game's TextureDebugMultipliers (ApplyMultipliers in its G-buffer shaders).
    float4 Multipliers0;    // albedo, metalness, gloss, AO multipliers
    float4 Multipliers1;    // emissive, color mask multipliers; albedo, metalness shifts
    float4 Multipliers2;    // gloss, AO, emissive, color mask shifts
    float4 ColorizeHsv;     // the frame's paint offset
    uint4 Probes[2];        // probe pixels, x | y << 16; kNoProbe = unused
    uint VoxelRandom;       // descriptor slot of the game's random texture (tiled triplanar sampling)
    uint VoxelMaterialCount;  // 0: no voxel material table yet (voxels stay grey)
    uint HistoryCap;        // lit view: the most frames a pixel's light history averages
    uint HistoryValid;      // PreviousDiffuse/PreviousSpecular hold the previous recorded frame's light
    row_major float4x4 PreviousViewProjectionAt0;  // that frame's camera (and PreviousViewZ's)
    float3 CameraDelta;     // camera position now minus then (m)
    uint LightSet;          // lit views: the set of LightReservoirs written this frame (the other: read)
    uint HasPrevious;       // PreviousViewProjectionAt0 and CameraDelta hold a previous recorded frame's camera
    uint CharacterLights;   // characters' glowing suit parts light their surroundings (the CharacterLights setting)
    float2 Jitter;          // the primary rays' sub-pixel offset (pixels, y down; full coverage only, else 0)
    uint FullCoverage;      // lit views in replace mode: every surface pixel the game drew takes the traced light
    float EmissiveIntensity;  // the EmissiveIntensity setting: the light emissive surfaces cast (not their glow)
    uint DecalCount;        // the frame's projected decals (primary_view.hlsl ApplyProjectedDecals); 0: none
    uint DecalLights;       // glowing decals light their surroundings (the DecalLights setting)
    uint OpenGeneration;    // primary_view.hlsl SeenThrough: the entries holding it count (from 1, now and then on)
    uint LightTreeOn;       // 1: light candidates from the light tree (light_tree.hlsli), everywhere; 0: the grid
    float DecalDepthBias;   // the game's decal depth bias, a share of the view depth (TraceClosestDecals)
    // The game's G-buffer size: Width and Height but where Ray Reconstruction upscales the lit view's light from a
    // smaller size (DLSS modes; primary_view.hlsl GBufferTexel).
    uint GBufferWidth;
    uint GBufferHeight;
    // Bit 1 where an upscaler after our filter takes this frame's light (FSR): primary_view.hlsl writes every
    // pixel's light into RrColor (denoise.hlsl pass 2 puts the filtered light over the lit pixels'). Bit 2 where
    // frame generation takes its inputs. Either, and Ray Reconstruction's view: the game's reverse-Z depth of what
    // each pixel shows (UpscaleDepth).
    uint UpscaleInputs;
    float ProjM33;  // the game's projection: depth = ProjM43 / z - ProjM33 (1 at the near plane, 0 at infinity)
    float ProjM43;
};

// The game's own frame constants (MyCommon.FrameConstantsData), as its shaders read them.
cbuffer GameFrame : register(b1)
{
    GameFrameConstants Game;
};

static const uint kNoAttributes = 0xFFFFFFFF;
static const float kPi = 3.14159265;

// Where a TLAS instance came from (InstanceData).
static const uint kSourceObject = 0;   // an instance desc the CPU wrote (models, renderables, voxel cells)
static const uint kSourceCube = 1;     // a cube-block record (expand_blocks.hlsl)
static const uint kSourceGeneric = 2;  // a generic instance record (trees)
static const uint kSourceMask = 3;
static const uint kSourceShift = 2;    // above the source: a record's byte offset / 32 (expand_blocks.hlsl)

// Where a TLAS instance came from (`extra`: its second InstanceData entry): kSource*.
uint SourceOf(float4 extra)
{
    return asuint(extra.w) & kSourceMask;
}

// The color multiplier (xyz) and emissivity (w) of part `geometryIndex` of a TLAS
// instance (`extra`: its second InstanceData entry), which the game multiplies
// into the base color and emission of its G-buffer (FeedOutputInternal); 1 by default.
float4 PartOverride(float4 extra, uint geometryIndex)
{
    uint slot = SourceOf(extra) == kSourceObject ? asuint(extra.x) : 0;
    return slot != 0 ? PartOverrides[slot - 1 + geometryIndex] : 1;
}

// --- Geometry ---------------------------------------------------------------

uint VertexStride(GeometryRecord r) { return r.Layout & 0xFF; }
uint AttributeStride(GeometryRecord r) { return (r.Layout >> 8) & 0xFF; }
uint Kind(GeometryRecord r) { return (r.Flags >> 8) & 0xFF; }

// Part flags (GeometryRecord.Flags bits 0-7; scene.h): blended decals, the game's STATIC_DECAL parts. Never a hit
// themselves: the rays look through them and blend them into the surface beneath (TraceClosestDecals).
static const uint kPartDecal = 8;
static const uint kPartDecalPremultiplied = 16;
// Cutout decals (also alpha-masked): solid for the camera's rays only (scene.h).
static const uint kPartDecalCutout = 32;
// Record flag: the record's mesh has decal parts (scene.h kRecordMeshDecals; TraceClosestDecals).
static const uint kRecordMeshDecals = 1u << 17;

bool IsDecal(GeometryRecord r) { return (r.Flags & kPartDecal) != 0; }

uint3 LoadTriangle(GeometryRecord record, uint primitive)
{
    ByteAddressBuffer page = Pages[NonUniformResourceIndex(record.Page)];
    if ((record.Layout & 0x10000) != 0)
        return page.Load3(record.IndexOffset + primitive * 12);
    uint offset = record.IndexOffset + primitive * 6;
    uint2 words = page.Load2(offset & ~3u);
    return (offset & 2) != 0 ? uint3(words.x >> 16, words.y & 0xFFFF, words.y >> 16)
                             : uint3(words.x & 0xFFFF, words.x >> 16, words.y & 0xFFFF);
}

float3 LoadPosition(GeometryRecord record, uint index)
{
    ByteAddressBuffer page = Pages[NonUniformResourceIndex(record.Page)];
    uint address = record.VertexOffset + index * VertexStride(record);
    if ((record.Layout & 0x20000) == 0)
    {
        uint2 raw = page.Load2(address);
        float4 packed = float4(f16tof32(raw.x), f16tof32(raw.x >> 16), f16tof32(raw.y), f16tof32(raw.y >> 16));
        return packed.xyz * packed.w;  // the game's packed position: w scales xyz
    }
    return asfloat(page.Load3(address));
}

// The records of posed skinned meshes (characters; scene.cpp PoseSkins) point into the last geometry page.
static const uint kPosedPage = 15;
bool IsPosed(GeometryRecord r) { return r.Page == kPosedPage; }

// With the CharacterLights setting off, characters' glowing suit parts glow where they are seen but light
// nothing, as in the game: not listed as lights (emissive_lights.hlsl, lights.hlsli ListedLight), no emission
// at a bounce's hit (primary_view.hlsl IncomingLight); with DecalLights off, decals too. (Otherwise glowing
// decals light their surroundings like any other emissive part, where their alpha mask covers them:
// lights.hlsli Coverage.)
bool GlowsOnly(GeometryRecord r)
{
    return (CharacterLights == 0 && IsPosed(r)) || (DecalLights == 0 && (r.Flags & (kPartDecal | kPartDecalCutout)) != 0);
}

// A posed skinned mesh keeps its previous frame's positions this many bytes after its own (layout bits
// 18-31, 256-byte units); 0: none (every other mesh).
uint PreviousPositionsDistance(GeometryRecord r) { return (r.Layout >> 18) * 256; }

// The object-space point at `barycentrics` of triangle `primitive` in a posed mesh's previous pose
// (`distance`: its PreviousPositionsDistance, not 0): the animation's part of the point's motion.
float3 PreviousPosePoint(GeometryRecord record, uint primitive, float2 barycentrics, uint distance)
{
    uint3 corners = LoadTriangle(record, primitive);
    ByteAddressBuffer page = Pages[NonUniformResourceIndex(record.Page)];
    uint base = record.VertexOffset + distance;
    uint stride = VertexStride(record);
    float3 weights = float3(1 - barycentrics.x - barycentrics.y, barycentrics.x, barycentrics.y);
    return weights.x * asfloat(page.Load3(base + corners.x * stride)) +
           weights.y * asfloat(page.Load3(base + corners.y * stride)) +
           weights.z * asfloat(page.Load3(base + corners.z * stride));
}

// Models keep the UV in VB0 (after the half4 position), old-pipeline meshes in VB1.
float2 LoadUv(GeometryRecord record, uint index)
{
    ByteAddressBuffer page = Pages[NonUniformResourceIndex(record.Page)];
    uint kind = Kind(record);
    uint raw;
    if (kind == 0)
        raw = page.Load(record.VertexOffset + index * VertexStride(record) + 8);
    else if (kind == 1 && record.AttributeOffset != kNoAttributes)
        raw = page.Load(record.AttributeOffset + index * AttributeStride(record) + 8);
    else
        return 0;
    return float2(f16tof32(raw), f16tof32(raw >> 16));
}

float TextureLod(uint slot, float lodBase)
{
    uint width, height, levels;
    Textures[NonUniformResourceIndex(slot)].GetDimensions(0, width, height, levels);
    return max(lodBase + 0.5 * log2(float(width) * float(height)), 0);
}

float4 SampleTexture(uint slot, float2 uv, float lodBase)
{
    return Textures[NonUniformResourceIndex(slot)].SampleLevel(LinearWrap, uv, TextureLod(slot, lodBase));
}

// --- Colors, random numbers ----------------------------------------------------

float3 SrgbToLinear(float3 c)
{
    return select(c <= 0.04045, c / 12.92, pow((c + 0.055) / 1.055, 2.4));
}

float3 LinearToSrgb(float3 c)
{
    c = max(c, 0);
    return select(c <= 0.0031308, c * 12.92, 1.055 * pow(c, 1 / 2.4) - 0.055);
}

float3 HsvToRgb(float3 hsv)
{
    float3 k = saturate(abs(frac(hsv.xxx + float3(1, 2.0 / 3, 1.0 / 3)) * 6 - 3) - 1);
    return hsv.z * lerp(1, k, hsv.y);
}

// Paint: the texel's value (sRGB) with the key color's hue and saturation,
// blended by the paint mask.
float3 Paint(float3 texel, float3 keyHsv, float factor)
{
    if (factor <= 0)
        return texel;
    float3 encoded = LinearToSrgb(texel);
    float value = max(encoded.r, max(encoded.g, encoded.b));
    float3 painted = SrgbToLinear(HsvToRgb(saturate(float3(0, 0, value) + keyHsv + ColorizeHsv.xyz)));
    return lerp(texel, painted, factor);
}

uint Pcg(uint value)
{
    uint state = value * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float Random(inout uint seed)
{
    seed = Pcg(seed);
    return seed * (1.0 / 4294967296.0);
}

float Luminance(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

// --- Traversal ----------------------------------------------------------------

RaytracingAccelerationStructure Scene : register(t4);

static const uint kAlphamaskSlotNone = 0;  // the white fallback: no mask
static const uint kFallbackSlots = 4;      // textures.h TextureFallback: slots below it are fallbacks, not a file's


// Whether a candidate triangle is solid at its hit point (the game clips
// alpha-masked parts below 0.5). `hiddenParts`: a main view's ray (the camera's
// or a bounce's, instance mask 0x01) on an instance with parts the game hides
// there (InstanceContributionToHitGroupIndex 1; scene.h kInstanceHiddenParts):
// such a part (its proxy's SkipInMainView, as for a screen without content; a
// negative emissivity in its part override; registered non-opaque,
// kPartHideable) is not there for it. It still casts shadows, as in the game.
bool AlphaSolid(uint instanceIndex, uint instanceId, uint geometryIndex, uint primitive, float2 barycentrics,
                float coneWidth, bool hiddenParts, bool cameraRay = false)
{
    GeometryRecord record = Records[instanceId + geometryIndex];
    if (IsDecal(record) || (!cameraRay && (record.Flags & kPartDecalCutout) != 0))
        return false;  // a layer over a surface (cutout decals: but for the camera's rays), not in a ray's way
    if (hiddenParts && PartOverride(InstanceData[instanceIndex * 2 + 1], geometryIndex).a < 0)
        return false;
    MaterialRecord material = Materials[record.Material];
    if (material.Textures.w == kAlphamaskSlotNone)
        return true;
    uint3 corners = LoadTriangle(record, primitive);
    float3 weights = float3(1 - barycentrics.x - barycentrics.y, barycentrics.x, barycentrics.y);
    float2 uv = weights.x * LoadUv(record, corners.x) + weights.y * LoadUv(record, corners.y) +
                weights.z * LoadUv(record, corners.z);
    // A coarse mip for the mask: the cone over a guessed 64-texel-per-unit density.
    float lodBase = log2(max(coneWidth, 1e-9)) - 0.5 * log2(64.0 * 64.0);
    return SampleTexture(material.Textures.w, uv, lodBase).r >= 0.5;
}

// Closest hit of `ray`; alpha-masked candidates are tested.
RayQuery<RAY_FLAG_NONE> TraceClosest(RayDesc ray, uint flags, uint mask, float coneSpread)
{
    RayQuery<RAY_FLAG_NONE> query;
    query.TraceRayInline(Scene, flags, mask, ray);
    while (query.Proceed())
    {
        if (query.CandidateType() == CANDIDATE_NON_OPAQUE_TRIANGLE &&
            AlphaSolid(query.CandidateInstanceIndex(), query.CandidateInstanceID(), query.CandidateGeometryIndex(),
                       query.CandidatePrimitiveIndex(), query.CandidateTriangleBarycentrics(),
                       coneSpread * query.CandidateTriangleRayT(),
                       (mask & 0x01) != 0 && query.CandidateInstanceContributionToHitGroupIndex() != 0))
            query.CommitNonOpaqueTriangleHit();
    }
    return query;
}

// The blended decal a ray met on its way to its closest hit (TraceClosestDecals): the farthest one, which is the one
// nearest the surface it stops at (the game's decals lie on their surfaces or up to a few millimetres above them);
// `dropped` counts the others. Its instance must be the hit's (a model's decals are parts of it): the caller uses the
// hit's transform.
struct DecalHits
{
    uint count;          // 0 or 1
    uint dropped;
    uint record;         // index into Records
    uint geometryIndex;  // the part in its mesh (PartOverride)
    uint instanceIndex;
    uint primitive;
    float2 barycentrics;
    float t;
    uint behind;         // 1: the hit is a cutout decal within the game's depth bias behind the closest surface
};

// A blended decal candidate of a TraceClosestDecals query: kept in `decals` if it is the farthest met so far (the
// game draws single-sided decals from the front only).
void KeepBlendedDecal(uint recordIndex, uint flags, bool frontFace, float t, uint geometryIndex, uint instanceIndex,
                      uint primitive, float2 barycentrics, inout DecalHits decals)
{
    if (!frontFace && (flags & 2) == 0)
        return;
    if (decals.count != 0)
    {
        ++decals.dropped;
        if (t <= decals.t)
            return;  // the farther one stays
    }
    decals.count = 1;
    decals.record = recordIndex;
    decals.geometryIndex = geometryIndex;
    decals.instanceIndex = instanceIndex;
    decals.primitive = primitive;
    decals.barycentrics = barycentrics;
    decals.t = t;
}

// TraceClosest that also keeps the blended decals the ray met; which of them lie on the surface it stops at, the
// caller decides (primary_view.hlsl ApplyDecals). The camera's rays only.
// The game draws its decals, cutout and blended, a little nearer than they lie (clip z + 0.0001 under its reversed
// depth, Geometry/Passes/GBuffer/VertexStage.hlsli), and many lie exactly on their surface once its half-float
// positions rounded both alike (RedShip's inset bookshelf: its vent grille, keyhole and labels): the game shows them,
// a ray meeting both at the same distance would take either. So where the ray stops on a mesh with decals
// (kRecordMeshDecals), a second query of the non-opaque triangles alone looks from there to DecalDepthBias of the
// distance behind (the ray's parameter is the view depth): a cutout decal there is the hit (the nearest solid one),
// a blended one is kept as in the first.
RayQuery<RAY_FLAG_NONE> TraceClosestDecals(RayDesc ray, uint flags, uint mask, float coneSpread, out DecalHits decals)
{
    decals = (DecalHits)0;
    RayQuery<RAY_FLAG_NONE> query;
    query.TraceRayInline(Scene, flags, mask, ray);
    while (query.Proceed())
    {
        if (query.CandidateType() != CANDIDATE_NON_OPAQUE_TRIANGLE)
            continue;
        uint recordIndex = query.CandidateInstanceID() + query.CandidateGeometryIndex();
        GeometryRecord candidate = Records[recordIndex];
        if (IsDecal(candidate))
        {
            KeepBlendedDecal(recordIndex, candidate.Flags, query.CandidateTriangleFrontFace(),
                             query.CandidateTriangleRayT(), query.CandidateGeometryIndex(),
                             query.CandidateInstanceIndex(), query.CandidatePrimitiveIndex(),
                             query.CandidateTriangleBarycentrics(), decals);
            continue;
        }
        if (AlphaSolid(query.CandidateInstanceIndex(), query.CandidateInstanceID(), query.CandidateGeometryIndex(),
                       query.CandidatePrimitiveIndex(), query.CandidateTriangleBarycentrics(),
                       coneSpread * query.CandidateTriangleRayT(),
                       (mask & 0x01) != 0 && query.CandidateInstanceContributionToHitGroupIndex() != 0, true))
            query.CommitNonOpaqueTriangleHit();
    }
    if (query.CommittedStatus() != COMMITTED_TRIANGLE_HIT || DecalDepthBias <= 0)
        return query;
    uint hitFlags = Records[query.CommittedInstanceID() + query.CommittedGeometryIndex()].Flags;
    if ((hitFlags & kRecordMeshDecals) == 0 || (hitFlags & kPartDecalCutout) != 0)
        return query;
    RayDesc margin = ray;
    margin.TMin = query.CommittedRayT() * (1 - 1e-6);
    margin.TMax = query.CommittedRayT() * (1 + DecalDepthBias);
    RayQuery<RAY_FLAG_CULL_OPAQUE> nearer;
    nearer.TraceRayInline(Scene, flags, mask, margin);
    while (nearer.Proceed())
    {
        uint recordIndex = nearer.CandidateInstanceID() + nearer.CandidateGeometryIndex();
        GeometryRecord candidate = Records[recordIndex];
        if (IsDecal(candidate))
            KeepBlendedDecal(recordIndex, candidate.Flags, nearer.CandidateTriangleFrontFace(),
                             nearer.CandidateTriangleRayT(), nearer.CandidateGeometryIndex(),
                             nearer.CandidateInstanceIndex(), nearer.CandidatePrimitiveIndex(),
                             nearer.CandidateTriangleBarycentrics(), decals);
        else if ((candidate.Flags & kPartDecalCutout) != 0 &&
                 AlphaSolid(nearer.CandidateInstanceIndex(), nearer.CandidateInstanceID(),
                            nearer.CandidateGeometryIndex(), nearer.CandidatePrimitiveIndex(),
                            nearer.CandidateTriangleBarycentrics(), coneSpread * nearer.CandidateTriangleRayT(),
                            (mask & 0x01) != 0 && nearer.CandidateInstanceContributionToHitGroupIndex() != 0, true))
            nearer.CommitNonOpaqueTriangleHit();
    }
    if (nearer.CommittedStatus() != COMMITTED_TRIANGLE_HIT)
        return query;
    // The decal as the hit: the query again over the decal's distance alone (non-opaque triangles; the same ray
    // meets the same triangles at the same distances), so that the caller reads it from the query it gets.
    float decalT = nearer.CommittedRayT();
    RayDesc at = ray;
    at.TMin = decalT * (1 - 1e-5);
    at.TMax = decalT * (1 + 1e-5);
    query.TraceRayInline(Scene, flags | RAY_FLAG_CULL_OPAQUE, mask, at);
    while (query.Proceed())
    {
        GeometryRecord candidate = Records[query.CandidateInstanceID() + query.CandidateGeometryIndex()];
        if ((candidate.Flags & kPartDecalCutout) != 0 &&
            AlphaSolid(query.CandidateInstanceIndex(), query.CandidateInstanceID(), query.CandidateGeometryIndex(),
                       query.CandidatePrimitiveIndex(), query.CandidateTriangleBarycentrics(),
                       coneSpread * query.CandidateTriangleRayT(),
                       (mask & 0x01) != 0 && query.CandidateInstanceContributionToHitGroupIndex() != 0, true))
            query.CommitNonOpaqueTriangleHit();
    }
    decals.behind = query.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 1 : 0;
    return query;
}

static const uint kNoInstance = 0xFFFFFFFF;

// A one-sided triangle of the ray's own object (TLAS instance `self`) met on its front: not in the way of light
// leaving that object's surfaces. The camera sees through such a face from behind (the game culls back faces), so
// a cavity modelled as an inward-facing box, whose lid closes it on the inside (the Collector's funnel throat), is
// open to the camera but would be shut to all light. A closed shell still shadows its own surfaces: such a ray then
// meets the shell from inside, on a back face; other objects' faces are in the way as before.
bool OwnOpenFace(uint instanceIndex, uint instanceId, uint geometryIndex, bool frontFace, uint self)
{
    return instanceIndex == self && frontFace && (Records[instanceId + geometryIndex].Flags & 2) == 0;
}

static uint g_openFacePasses = 0;  // this thread's shadow rays that took the open-face pass (primary_view.hlsl 15)

// Shadow casters (instance mask 0x02) between origin + direction * minT and origin + direction * maxT. `self`: the
// TLAS instance of the surface the ray leaves when its open faces let light through (OwnOpenFace: the camera sees
// that surface through one of them, primary_view.hlsl SeenThrough), else kNoInstance. Taken for every surface, the
// rule cost RedShip's lit view ~1.1 ms and changed nothing there: about 3 % of its shadow rays met an own one-sided
// face first, nearly all with their object's solid behind it, and a wave waits for its slowest ray.
bool Occluded(float3 origin, float3 direction, float maxT, float minT = 0, uint self = kNoInstance)
{
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = minT;
    ray.TMax = maxT;
    // Pass 0 takes the faces opaque as built. When the caster it finds is an open face of the ray's own object, pass
    // 1 makes every face a candidate: such faces are passed, any other stops the ray (whichever is met first: the
    // answer is only whether there is one). The two passes share one query and one candidate loop: the lit view
    // inlines this four times over.
    uint forced = RAY_FLAG_NONE;
    [loop]
    for (uint pass = 0; pass < 2; ++pass)
    {
        RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH> query;
        query.TraceRayInline(Scene, forced, 0x02, ray);
        while (query.Proceed())
        {
            if (query.CandidateType() != CANDIDATE_NON_OPAQUE_TRIANGLE)
                continue;
            uint instanceIndex = query.CandidateInstanceIndex();
            uint instanceId = query.CandidateInstanceID();
            uint geometryIndex = query.CandidateGeometryIndex();
            if (pass != 0 &&
                OwnOpenFace(instanceIndex, instanceId, geometryIndex, query.CandidateTriangleFrontFace(), self))
                continue;
            if (AlphaSolid(instanceIndex, instanceId, geometryIndex, query.CandidatePrimitiveIndex(),
                           query.CandidateTriangleBarycentrics(), 0.001 * query.CandidateTriangleRayT(), false))
                query.CommitNonOpaqueTriangleHit();
        }
        if (query.CommittedStatus() == COMMITTED_NOTHING)
            return false;
        if (pass != 0 || self == kNoInstance ||
            !OwnOpenFace(query.CommittedInstanceIndex(), query.CommittedInstanceID(), query.CommittedGeometryIndex(),
                         query.CommittedTriangleFrontFace(), self))
            return true;
        ++g_openFacePasses;
        forced = RAY_FLAG_FORCE_NON_OPAQUE;
    }
    return true;
}

#endif

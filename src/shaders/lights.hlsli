// Emissive surfaces as lights (research/docs/space-engineers-rt-plan.md §6.9),
// shared by emissive_lights.hlsl, which lists the frame's emissive triangles
// and prepares their sampling, and primary_view.hlsl, which samples them
// (next-event estimation). Needs scene_common.hlsli.
//
// Every lit frame: the parts whose material can emit (MaterialLight, from the
// extensions texture's G channel) on the TLAS instances drawn in the main view
// go into LightParts; their triangles, placed around the camera, into Lights,
// each with an estimate of the radiance it emits; the triangles' power
// (radiance x area) into prefix sums for choosing one by power (LightCdf
// within blocks of kLightScanBlock, LightBlocks across them) and, per part,
// for choosing one of its triangles; the parts' and lamps' cells: every cell
// of a grid around the camera lists the parts and lamps whose light reaches it
// (kLightCellEntries of them, the strongest there when more reach it); and per
// cell, kLightSlots lights resampled by
// their light at the cell from candidates drawn from its list and from the
// whole list by power (after ReGIR, RTXDI), so that a point chooses among the
// lights near it. (Drawn by power alone, a small light, a keypad or a suit's
// glowing strip, entered a cell's slots on a few frames in a hundred, and
// then lit that cell's part of the surfaces strongly: patches the shape of
// the cells, often triangles, flickering under Ray Reconstruction.)
//
// The game's lamps (point and spot lights) are entries of the same list after
// the triangles (kLightLamp: corner.xyz the lamp's position, corner.w the
// index into Lamps), chosen, resampled and kept the same way and shaded with
// the game's falloff and BRDF (Lighting/LightPoint.hlsl, LightSpot.hlsl).

#ifndef LIGHTS_HLSLI
#define LIGHTS_HLSLI

struct LightTriangle
{
    float4 corner;  // xyz: the first corner, camera-relative; w: asfloat(TLAS instance index)
    float4 edge1;   // xyz: second corner - first; w: asfloat(geometry record index)
    float4 edge2;   // xyz: third corner - first; w: the estimated emitted radiance (luminance)
    float4 uv01;    // the first and second corners' UVs
    float4 uv2;     // xy: the third corner's UV; z: asuint: kLight* flags | the part in its mesh << 8 | its
                    // entry in LightParts + 1 << 16 (0: past kMaxNearParts, and lamps); w: asuint: the triangle
                    // in its part
};

static const uint kLightTwoSided = 1;  // a part the game draws without culling: both faces emit
static const uint kLightFlipped = 2;   // the front faces -(edge1 x edge2) (see LightNormal)
static const uint kLightLamp = 4;      // a lamp, not a triangle (Lamps[asuint(corner.w)])

uint LightGeometryIndex(LightTriangle light)
{
    return (asuint(light.uv2.z) >> 8) & 0xFF;
}

float LightArea(LightTriangle light)
{
    return 0.5 * length(cross(light.edge1.xyz, light.edge2.xyz));
}

bool IsLamp(LightTriangle light)
{
    return (asuint(light.uv2.z) & kLightLamp) != 0;
}

// Parts with an entry in LightParts below this are listed near the cells their light reaches (the entry + 1
// in uv2.z's bits 16-30: a finite float's pattern, as the buffer holds floats); later ones only by power.
static const uint kMaxNearParts = 32639;
static const uint kNoPart = 0xFFFFFFFF;

// A listed triangle's entry in LightParts, or kNoPart (a lamp, a part past kMaxNearParts).
uint LightPartOf(LightTriangle light)
{
    return (asuint(light.uv2.z) >> 16) - 1;
}

// A lamp of the frame (scene.cpp; the game's MyLightComponent: UpdateRenderLightData's point or spot light
// layout, placed by its actor as MyLightComponent.WritePointlightConstants/WriteSpotlightConstants do).
struct Lamp
{
    float3 position;     // camera-relative
    float range;         // its light ends there
    float3 color;        // intensity included
    float falloff;
    float3 direction;    // spot: the cone's axis (the actor's forward)
    float apertureCos;   // spot: the cone's cosine
    float3 up;           // spot: the mask's up (the actor's up)
    uint maskSlot;       // spot: descriptor slot of its reflector mask (0: none)
    float glossFactor;
    float diffuseFactor;
    uint key;            // the light actor's id (LampKey)
    uint flags;          // kLampSpot
};

static const uint kLampSpot = 1;

// A lamp's key in the key table (see LightKeyOf): its actor's id, and in place of a triangle's part and index
// 0xFFFFFFFF for its point light, 0xFFFFFFFE for its spot light (an actor can have both).
uint2 LampKey(Lamp lamp)
{
    return uint2(lamp.key, 0xFFFFFFFFu - (lamp.flags & kLampSpot));
}
StructuredBuffer<Lamp> Lamps : register(t15);
// The game's lamps are points; soft shadows assume a disc of this radius facing the surface.
static const float kLampRadius = 0.08;
// Shadow rays stop this short of a lamp: its own fitting around it does not shadow it.
static const float kLampSkip = 0.15;
// An emissive part with a lamp inside its bounds widened by this much is that lamp's bulb (a light block's
// glowing fixture): it glows for the eye but lights nothing, its lamp does (emissive_lights.hlsl).
static const float kBulbMargin = 0.3;

// GetAttenuation (Lighting/LightingModel.hlsli): x the hard falloff to 0 at the range, y the diffuse factor's.
float2 LampAttenuation(float distance, float range, float falloff)
{
    if (!(distance < range))
        return 0;
    float d = distance / range;
    float dq = d * d;
    float dq1 = 1 - dq * dq;
    return float2(dq1 * dq1 * dq1, 1 / (1 + falloff * dq));
}

// A spot light's cone and reflector mask along `toSurface` (unit, from the lamp): 0 outside the cone, else
// the mask's red channel where the light's projection puts the direction (LightSpot.hlsl; the projection is
// MyLightComponent's: CreateLookAt along the forward with the up, CreatePerspectiveFieldOfView of
// 2 acos(apertureCos), aspect 1; ClipspaceToTexture). 1 for point lights.
float LampCone(Lamp lamp, float3 toSurface)
{
    if ((lamp.flags & kLampSpot) == 0)
        return 1;
    float along = dot(toSurface, lamp.direction);
    if (!(along > lamp.apertureCos))
        return 0;
    if (lamp.maskSlot == 0)
        return 1;
    float3 xAxis = normalize(cross(lamp.direction, lamp.up));
    float3 yAxis = cross(xAxis, lamp.direction);
    float tanHalf = sqrt(saturate(1 - lamp.apertureCos * lamp.apertureCos)) / max(lamp.apertureCos, 1e-4);
    float2 ndc = float2(dot(toSurface, xAxis), dot(toSurface, yAxis)) / (along * tanHalf);
    float2 uv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    return Textures[NonUniformResourceIndex(lamp.maskSlot)].SampleLevel(LinearWrap, uv, 0).r;
}

// A lamp's diffuse strength without the surface's cosine and 1/pi: the luminance of its colour times the
// game's falloff, diffuse factor, cone and mask at `toSurface` (unit) and `distance`.
float LampStrength(Lamp lamp, float3 toSurface, float distance)
{
    float2 attenuation = LampAttenuation(distance, lamp.range, lamp.falloff);
    return Luminance(lamp.color) * lamp.diffuseFactor * attenuation.x * attenuation.y * LampCone(lamp, toSurface);
}

// A lamp's power, which the prefix sums choose it by (with the camera distance: LightWeight): the luminance of
// its colour x its diffuse factor x (range / 2)^2.
float LampPower(Lamp lamp)
{
    return Luminance(lamp.color) * lamp.diffuseFactor * 0.25 * lamp.range * lamp.range;
}

// A lamp's strength at a light-grid cell (the light it sends there without the surface's cosine): LampStrength
// from the cell's nearest point, `reach` from its centre, without the cone and mask; 0 where a spot light's cone,
// widened by the cell's angular size, misses the cell.
float LampCellStrength(Lamp lamp, float3 centre, float reach)
{
    float3 toCell = centre - lamp.position;
    float distance = length(toCell);
    if ((lamp.flags & kLampSpot) != 0 && distance > reach)
    {
        float coneAngle = acos(clamp(lamp.apertureCos, -1, 1));
        float cellAngle = asin(saturate(reach / distance));
        if (acos(clamp(dot(toCell / distance, lamp.direction), -1, 1)) > coneAngle + cellAngle)
            return 0;
    }
    float2 attenuation = LampAttenuation(max(distance - reach, 0), lamp.range, lamp.falloff);
    return Luminance(lamp.color) * lamp.diffuseFactor * attenuation.x * attenuation.y;
}

// Capacities (rt_core.cpp sizes the buffers alike; kLightKeySlots below).
static const uint kMaxLightParts = 65535;  // one ExecuteIndirect dispatch, a group per part
static const uint kMaxLightTriangles = 262144;
static const uint kLightScanBlock = 1024;                               // triangles per prefix-sum block
static const uint kLightBlocks = kMaxLightTriangles / kLightScanBlock;  // 256
// The light grid: kLightGridSize^3 cells of kLightCellSize metres centred on the camera, kLightSlots
// resampled lights each, and the list of the parts and lamps near each (kLightCellEntries).
static const uint kLightGridSize = 32;
static const float kLightCellSize = 2.0;
static const uint kLightSlots = 16;
static const uint kLightCells = kLightGridSize * kLightGridSize * kLightGridSize;
static const uint kLightGridSlots = kLightCells * kLightSlots;
static const float kLightCellHalfDiagonal = 0.8660254 * kLightCellSize;
// What a cell's slots serve: the points up to its half diagonal from its centre, and those up to half a cell
// beyond it on each axis (LightCandidates draws a point's candidates from the cells around it, jittered): what
// the grid leaves out of a cell must light nothing that far from its centre.
static const float kLightCellReach = 2 * kLightCellHalfDiagonal;
// Per cell, the parts and lamps whose light reaches it (their light there above kNearLight, at most kNearReach
// metres from them): kLightCellEntries of them, LightGrid's entries after the slots; when more reach it, the
// strongest there (by their weight's octave, kLightWeightBins: emissive_lights.hlsl ListNear, ThresholdPass).
static const uint kLightCellEntries = 32;
static const float kNearLight = 0.001;  // luminance of the diffuse light: power / (pi distance^2)
static const float kNearReach = 8.0;
// The share of a cell's candidates drawn from its list (the others from the whole list by power).
static const float kNearShare = 0.75;
static const uint kLightWeightBins = 32;  // per cell: the lights reaching it by weight, an octave each (WeightBin)
static const uint kLightBinBits = 5;      // log2(kLightWeightBins)
// LightGrid's layout: the slots; per cell its list's counters (.x: the lights listed above its threshold's
// bin, those of that bin << 16: ListedCount; .y: the threshold's bin | the lights above it << kLightBinBits |
// the lights reaching the cell << (kLightBinBits + 6)); the lists (kLightCellEntries per cell, from the
// strongest bins: a part's entry in LightParts or kNearLamp | a lamp's index in Lamps, asuint(its weight at
// the cell)); per cell, its lights counted by bin (kLightWeightBins / 2 entries: .x the even bin, .y the odd).
static const uint kLightCellCounts = kLightGridSlots;
static const uint kLightCellLists = kLightGridSlots + kLightCells;
static const uint kLightCellBins = kLightCellLists + kLightCells * kLightCellEntries;
static const uint kLightGridEntries = kLightCellBins + kLightCells * kLightWeightBins / 2;
static const uint kNearLamp = 0x80000000;

// A weight's bin: its octave (the float's exponent), from 2^-24 (bin 0, and all below) to 2^7 (bin 31, and all
// above).
uint WeightBin(float weight)
{
    return (uint)clamp((int)(asuint(weight) >> 23) - 127 + 24, 0, (int)kLightWeightBins - 1);
}

// A cell's list's length, from its counters (.x).
uint ListedCount(uint counters)
{
    return min((counters & 0xFFFF) + (counters >> 16), kLightCellEntries);
}
// LightParts' layout: the parts, then per part two entries (from kLightPartBounds): asuint(float4(its bounds'
// low corner, its power)), asuint(float4(its bounds' high corner, 0)), camera-relative.
static const uint kLightPartBounds = kMaxLightParts + 1;
// LightCdf's layout: the prefix sums of LightWeight within the blocks, then (from kLightPartCdf) per
// triangle the inclusive prefix sum of TrianglePower within its part.
static const uint kLightPartCdf = kMaxLightTriangles;

// A light-grid cell's coordinates, index and centre (camera-relative).
uint3 CellCoordinates(uint cell)
{
    return uint3(cell % kLightGridSize, (cell / kLightGridSize) % kLightGridSize,
                 cell / (kLightGridSize * kLightGridSize));
}

uint CellIndex(uint3 c)
{
    return (c.z * kLightGridSize + c.y) * kLightGridSize + c.x;
}

float3 CellCentre(uint3 c)
{
    return (float3(c) + 0.5 - 0.5 * kLightGridSize) * kLightCellSize;
}

// The cells (clamped to the grid) of the box from `low` to `high`; false when the box misses the grid.
bool CellRange(float3 low, float3 high, out uint3 first, out uint3 last)
{
    int3 a = int3(floor(low / kLightCellSize)) + (int)(kLightGridSize / 2);
    int3 b = int3(floor(high / kLightCellSize)) + (int)(kLightGridSize / 2);
    first = uint3(clamp(a, 0, (int)kLightGridSize - 1));
    last = uint3(clamp(b, 0, (int)kLightGridSize - 1));
    return all(b >= 0) && all(a < (int)kLightGridSize);
}

// Whether a camera-relative surface point lies in the light grid, where lamps are traced (beyond it the
// game's own lamp passes light it). The plugin's patch of those passes tests the same
// (src/managed/ShaderPatch.cs, RtLampShare): keep the two alike.
bool InLightGrid(float3 p)
{
    float3 cell = floor(p / kLightCellSize) + kLightGridSize / 2;
    return all(cell >= 0) && all(cell < kLightGridSize);
}
static const uint kNoLight = 0xFFFFFFFF;
// Texels a triangle's emission is averaged over (LightLodBase): its light, not its detail.
static const float kLightTexels = 64;
// Triangles emitting less (luminance of the estimate) are left to the paths that hit them: dim glows over
// large areas (texture noise above the game's 1/255 floor included) would take the power and light little.
static const float kLightMinRadiance = 1.0;

// A listed triangle's power, which the prefix sums choose it by (with the camera distance: LightWeight) and its
// part's sums within the part: its estimated radiance x area, twice for two-sided parts; 0 below
// kLightMinRadiance (a lamp's bulb's triangles hold 0 in the sums alone: they are never drawn).
float TrianglePower(LightTriangle light)
{
    float radiance = light.edge2.w;
    bool twoSided = (asuint(light.uv2.z) & kLightTwoSided) != 0;
    return radiance >= kLightMinRadiance ? radiance * LightArea(light) * (twoSided ? 2 : 1) : 0;
}

// A listed light's weight in the prefix sums (what FindLight chooses it by): its power (TrianglePower,
// LampPower) over its squared distance from the camera (at least 1 m^2), which favours the lights where the
// pixels are.
float LightWeight(LightTriangle light)
{
    if (IsLamp(light))
    {
        Lamp lamp = Lamps[asuint(light.corner.w)];
        return LampPower(lamp) / max(dot(lamp.position, lamp.position), 1);
    }
    float3 centre = light.corner.xyz + (light.edge1.xyz + light.edge2.xyz) / 3;
    return TrianglePower(light) / max(dot(centre, centre), 1);
}

// LightCounters words.
static const uint kLightCountParts = 0;      // parts appended (may exceed kMaxLightParts)
static const uint kLightCountTriangles = 1;  // triangles reserved (may exceed kMaxLightTriangles); with the
                                             // lamps after the arguments pass: the list's entries
static const uint kLightComplete = 2;        // 1: every part, triangle and lamp fit
static const uint kLightLampStart = 3;       // the list's first lamp entry
static const uint kLightArguments = 4;       // 3 words: the triangle pass's dispatch (ExecuteIndirect)
static const uint kLightTotalPower = 8;      // asfloat: the listed triangles' power summed
static const uint kLightActive = 9;          // 1: complete and with power: next-event estimation is on

RWStructuredBuffer<float4> MaterialLight : register(u9);  // per material: x the most emission, y the mean (luminance)
RWStructuredBuffer<uint4> LightParts : register(u10);     // TLAS instance, record, triangles | part << 24, first
                                                          // triangle; their bounds (kLightPartBounds)
RWByteAddressBuffer LightCounters : register(u11);
RWStructuredBuffer<LightTriangle> Lights : register(u12);
RWStructuredBuffer<float> LightCdf : register(u13);       // per triangle: the inclusive prefix sum of LightWeight in
                                                          // its block; of TrianglePower in its part (kLightPartCdf)
RWStructuredBuffer<float> LightBlocks : register(u14);    // per block: the weight of the blocks before it
RWStructuredBuffer<uint2> LightGrid : register(u15);      // per cell and slot: light, asuint(contribution weight);
                                                          // the cells' lists (kLightCellCounts, kLightCellLists,
                                                          // kLightCellBins)

// A listed triangle's key, which lasts from frame to frame (the list's order
// does not, nor do the TLAS's instance indices; see LightKeyOf). This frame's
// list is found by key through a hash table (open addressing, kLightKeyProbes
// probes): per slot a tag (0: empty), the key, the index.
static const uint kLightKeySlots = 1u << 19;
static const uint kLightKeyProbes = 32;
RWStructuredBuffer<uint4> LightKeys : register(u16);

// Per pixel, in two sets (the one written this frame, LightSet, and the previous
// lit frame's): the light sample its emissive light came from, kept for the next
// frame and its neighbours (temporal and spatial resampling after ReSTIR DI):
// the key, the point's weights of edge1 and edge2 (12 bits each) | the number
// of candidates it stands for << 24 (0: none), asuint(its contribution weight;
// 0: an occluded sample, or none, whose candidates still count).
RWStructuredBuffer<uint4> LightReservoirs : register(u17);

// A listed triangle's key (`extra`: its instance's second InstanceData entry; `record`: its part's geometry
// record): the instance's lasting identity, an object's game actor id or a cube block's or generic record's
// place in the block buffer (SourceOf, expand_blocks.hlsl), mixed with the geometry record (an actor can draw
// several meshes at once, as LODs; an odd multiplier: different records, different keys); the part << 24 | the
// triangle in its part. (Cube blocks' keys were their TLAS index once, which shifts whenever the instances before
// them change: a kept sample then found a lamp of the same model elsewhere, with the old sample's weight.)
uint2 LightKeyOf(float4 extra, uint record, uint geometryIndex, uint primitive)
{
    uint source = asuint(extra.w);
    uint identity = (source & kSourceMask) == kSourceObject ? asuint(extra.y)
                                                            : (0x80000000u | (source >> kSourceShift));
    return uint2(identity ^ (record * 0x9E3779B9u), (geometryIndex << 24) | (primitive & 0xFFFFFF));
}

uint LightKeyHash(uint2 key)
{
    return Pcg(key.x ^ Pcg(key.y + 0x632BE5AB));
}

// The index of the triangle with `key` in this frame's list, or kNoLight.
uint FindLightByKey(uint2 key)
{
    uint hash = LightKeyHash(key);
    uint tag = hash | 1;
    for (uint probe = 0; probe < kLightKeyProbes; ++probe)
    {
        uint4 entry = LightKeys[(hash + probe) & (kLightKeySlots - 1)];
        if (entry.x == 0)
            return kNoLight;
        if (entry.x == tag && entry.y == key.x && entry.z == key.y)
            return entry.w;
    }
    return kNoLight;
}

bool LightsActive()
{
    return LightCounters.Load(kLightActive * 4) != 0;
}

uint LightCount()
{
    return LightCounters.Load(kLightCountTriangles * 4);
}

float LightTotalPower()
{
    return asfloat(LightCounters.Load(kLightTotalPower * 4));
}

// The listed triangle whose power interval holds `target` (0 <= target < the total power; count > 0).
uint FindLight(float target, uint count)
{
    uint low = 0;
    uint high = (count + kLightScanBlock - 1) / kLightScanBlock - 1;
    while (low < high)
    {
        uint middle = (low + high + 1) / 2;
        if (LightBlocks[middle] <= target)
            low = middle;
        else
            high = middle - 1;
    }
    float local = target - LightBlocks[low];
    uint first = low * kLightScanBlock;
    high = min(first + kLightScanBlock, count) - 1;
    low = first;
    while (low < high)
    {
        uint middle = (low + high) / 2;
        if (LightCdf[middle] > local)
            high = middle;
        else
            low = middle + 1;
    }
    return low;
}

// A part's power (its LightParts entry): its last triangle's prefix sum within it.
float PartPower(uint part)
{
    uint4 entry = LightParts[part];
    return LightCdf[kLightPartCdf + entry.w + (entry.z & 0xFFFFFF) - 1];
}

// The triangle of a part (its LightParts entry) whose power interval within the part holds `u` x the part's
// power (0 <= u < 1; the part has power).
uint FindPartLight(uint part, float u)
{
    uint4 entry = LightParts[part];
    uint low = entry.w;
    uint high = entry.w + (entry.z & 0xFFFFFF) - 1;
    float target = u * LightCdf[kLightPartCdf + high];
    while (low < high)
    {
        uint middle = (low + high) / 2;
        if (LightCdf[kLightPartCdf + middle] > target)
            high = middle;
        else
            low = middle + 1;
    }
    return low;
}

// The direction a triangle emits toward: its front (the game's front faces,
// DXR's with TRIANGLE_FRONT_COUNTERCLOCKWISE: the side opposite to e1 x e2 in
// object space; the builder flips by the transform's handedness).
float3 LightNormal(LightTriangle light)
{
    float3 n = normalize(cross(light.edge1.xyz, light.edge2.xyz));
    return (asuint(light.uv2.z) & kLightFlipped) != 0 ? -n : n;
}

// The share of a part there at a point, at the coarse mip emission is read at (LightLodBase): an alpha-masked
// part's (cutout decals among them) and a blended decal's alpha mask (premultiplied decals carry it in their
// textures already); 1 for the others. The light of a glowing marking comes from the marking alone, not from the
// whole quad it is drawn on.
float Coverage(GeometryRecord record, MaterialRecord material, float2 uv, float lodBase)
{
    if (material.Textures.w == kAlphamaskSlotNone || (record.Flags & kPartDecalPremultiplied) != 0 ||
        ((material.Flags & 1) == 0 && !IsDecal(record)))
        return 1;
    return SampleTexture(material.Textures.w, uv, lodBase).r;
}

// What a point of an emissive part sends out: the game's emissive term
// (albedo x emissive x BloomEmissiveness; Lighting/LightDir.hlsl) with its
// paint, the object's color and emissivity for the part (PartOverride) and the
// material multipliers (FeedOutputInternal, ApplyMultipliers), as
// primary_view.hlsl's ShadeMaterial computes them, from the textures at
// `lodBase` (TextureLod), times the EmissiveIntensity setting and the share of
// the part there (Coverage): the light it casts (sampled here and in the list's
// estimate), not its glow where it is seen. `instance`, `extra`: the TLAS
// instance's two InstanceData entries.
// Not yet: deformed cube blocks' pattern offset.
float3 Emission(GeometryRecord record, float4 instance, float4 extra, uint geometryIndex, float2 uv, float lodBase)
{
    MaterialRecord material = Materials[record.Material];
    float coverage = Coverage(record, material, uv, lodBase);
    if (coverage <= 0)
        return 0;
    float4 cm = SampleTexture(material.Textures.x, uv, lodBase);
    float4 ext = SampleTexture(material.Textures.z, uv, lodBase);
    float4 part = PartOverride(extra, geometryIndex);
    float coloring = ext.w;
    bool colorable = (material.Flags & 2) != 0 || instance.w > 0.5;
    if (!colorable)
        coloring *= saturate(1 - saturate(cm.a - 0.4) / 0.3);
    float3 baseColor = Paint(cm.rgb, instance.xyz, coloring) * part.rgb * Multipliers0.x + Multipliers1.z;
    float metalness = cm.a * Multipliers0.y + Multipliers1.w;
    float emissive = saturate(ext.y - 1 / 255.0) * part.a * Multipliers1.x + Multipliers2.z;
    return max(baseColor * (1 - metalness) * emissive * Game.Post.BloomEmissiveness * EmissiveIntensity, 0) * coverage;
}

// The LOD base (TextureLod) at which a triangle spans about kLightTexels texels.
float LightLodBase(float2 uv0, float2 uv1, float2 uv2)
{
    float2 d1 = uv1 - uv0;
    float2 d2 = uv2 - uv0;
    return 0.5 * log2(max(0.5 * abs(d1.x * d2.y - d1.y * d2.x), 1e-12) / kLightTexels);
}

// The radiance (luminance) a triangle emits on average, from four points.
// Deterministic: the builder and a bounce that hits the triangle agree on
// whether it is a listed light (above zero).
float EmissionEstimate(GeometryRecord record, float4 instance, float4 extra, uint geometryIndex, float2 uv0, float2 uv1,
                       float2 uv2)
{
    float lodBase = LightLodBase(uv0, uv1, uv2);
    float2 centre = (uv0 + uv1 + uv2) / 3;
    float sum = Luminance(Emission(record, instance, extra, geometryIndex, centre, lodBase));
    sum += Luminance(Emission(record, instance, extra, geometryIndex, lerp(centre, uv0, 0.5), lodBase));
    sum += Luminance(Emission(record, instance, extra, geometryIndex, lerp(centre, uv1, 0.5), lodBase));
    sum += Luminance(Emission(record, instance, extra, geometryIndex, lerp(centre, uv2, 0.5), lodBase));
    return sum * 0.25;
}

// Whether a triangle's light comes through next-event estimation: it is in
// this frame's list (the builder's tests: its part's material can emit, the
// part is among the mesh's first 255, its estimate reaches kLightMinRadiance),
// so a path that hits it leaves its emission out. `instance`, `extra`: the hit
// instance's InstanceData entries; `geometryIndex`: the part in its mesh.
bool ListedLight(GeometryRecord record, float4 instance, float4 extra, uint geometryIndex, uint primitive)
{
    if (!LightsActive() || geometryIndex >= 255 || Kind(record) == 2 || record.AttributeOffset == kNoAttributes ||
        MaterialLight[record.Material].x <= 0 || GlowsOnly(record))
        return false;
    uint3 corners = LoadTriangle(record, primitive);
    return EmissionEstimate(record, instance, extra, geometryIndex, LoadUv(record, corners.x),
                            LoadUv(record, corners.y), LoadUv(record, corners.z)) >= kLightMinRadiance;
}

// The point a sample's two numbers pick on a listed light and its resampling target at a surface (the
// unshadowed diffuse contribution without 1/pi): a triangle's point at the weights of edge1 and edge2, its
// estimated radiance x both cosines / distance^2; a lamp's point on the disc of kLampRadius facing the
// surface at polar coordinates (sqrt(u), 2 pi v), its strength x the surface's cosine (the game's lamps fall
// off by their own curve, not by the distance squared).
float LightTarget(LightTriangle light, float2 weights, float3 position, float3 N, float3 geometricNormal,
                  out float3 lightPoint)
{
    if (IsLamp(light))
    {
        Lamp lamp = Lamps[asuint(light.corner.w)];
        float3 toLamp = lamp.position - position;
        float distance = length(toLamp);
        float3 L = toLamp / max(distance, 1e-4);
        float3 t = abs(L.y) < 0.999 ? normalize(cross(float3(0, 1, 0), L)) : float3(1, 0, 0);
        float3 b = cross(L, t);
        float radius = kLampRadius * sqrt(weights.x);
        float angle = 6.2831853 * weights.y;
        lightPoint = lamp.position + radius * (cos(angle) * t + sin(angle) * b);
        float cosSurface = dot(N, L);
        return cosSurface > 0 && dot(geometricNormal, L) > 0 ? LampStrength(lamp, -L, distance) * cosSurface : 0;
    }
    lightPoint = light.corner.xyz + weights.x * light.edge1.xyz + weights.y * light.edge2.xyz;
    float3 toLight = lightPoint - position;
    float distance2 = max(dot(toLight, toLight), 1e-6);
    float3 L = toLight * rsqrt(distance2);
    float cosLight = -dot(LightNormal(light), L);
    if ((asuint(light.uv2.z) & kLightTwoSided) != 0)
        cosLight = abs(cosLight);
    float cosSurface = dot(N, L);
    return cosLight > 0 && cosSurface > 0 && dot(geometricNormal, L) > 0
               ? light.edge2.w * cosSurface * cosLight / distance2
               : 0;
}

#endif

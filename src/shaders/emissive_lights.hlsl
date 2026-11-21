// Emissive surfaces as lights: the list of the frame's emissive triangles and
// what samples it (D3D12 compute, cs_6_5; research/docs/space-engineers-rt-plan.md
// §6.9; lights.hlsli describes the buffers). One dispatch per step
// (rt_core.cpp BuildLights), by Pass:
//   0 materials: whether each material can emit (the most emission over
//     32x32 points of its extensions texture at a coarse mip, an alpha-masked
//     one's within its mask) and its mean emitted luminance; only when the
//     material table changed
//   1 reset: the counters, the key table and the cells' lists
//   2 parts: per TLAS instance drawn in the main view, its parts whose
//     material can emit, each with its range of the triangle list
//   3 arguments: the triangle pass's dispatch (one group per part), the
//     lamps' range after the triangles, whether the list is complete, the
//     statistics
//   4 triangles (ExecuteIndirect): per part, its triangles placed around the
//     camera, their emitted radiance estimated, their weight, their keys, the
//     prefix sums of their power within the part; the part's bounds and
//     power; a part with a lamp inside its bounds (widened by kBulbMargin) is
//     that lamp's bulb: listed without power or keys, so that it is never
//     sampled and the paths that hit it leave its emission out
//     (ListedLight): the lamp alone lights the room
//   9 near (ExecuteIndirect, the same groups) and 10 near lamps (per lamp,
//     kEnclosureRays threads), twice (First 0, then 1; ListNear): the cells a
//     part's light reaches (lights.hlsli kNearLight, kNearReach), with its
//     power over the squared distance from the cell's centre to its bounds,
//     and those within a lamp's range (kNearReach at most), with its strength
//     there, counted by that weight's octave, then listed: all of them where
//     they fit, else the strongest
//   11 thresholds, between the two: per cell, the weakest bin its list takes
//   5 lamps: per lamp of the frame (Lamps), kEnclosureRays threads: its
//     entry, weight and key; how enclosed it is (a ray from it per thread,
//     statistics only)
//   6 blocks: prefix sums of the weights within blocks of kLightScanBlock
//   7 totals: the blocks' offsets, the total weight, whether the list is used
//   8 grid: per cell and slot, one light resampled from kGridCandidates
//     candidates, kNearShare of them drawn from the cell's list (an entry by
//     its weight there, a part's triangle by power within it), the others by
//     weight from the whole list, by its strength at the cell (triangles:
//     power over the squared distance, one-sided ones with the whole cell
//     behind them left out; lamps: the game's falloff from the cell's nearest
//     point, spot lights whose cone misses the cell left out), the cell's
//     reach widened for the points that draw from it (kLightCellReach)
//
// The weight of an entry in the prefix sums (LightWeight) is its power over its
// squared distance from the camera (at least 1 m^2): a triangle's power is its
// radiance x area (twice for two-sided parts), a lamp's the luminance of its
// colour x its diffuse factor x (range / 2)^2.

#include "scene_common.hlsli"
#include "lights.hlsli"

#define GROUP_SIZE 256

// The frame's TLAS instance descs (D3D12_RAYTRACING_INSTANCE_DESC, 64 bytes; scene.cpp BuildTopLevel).
ByteAddressBuffer InstanceDescs : register(t13);
// primary_view.hlsl's counters; 24-26 and 31 are the light list's (rt_core.cpp Report).
RWByteAddressBuffer Stats : register(u1);

cbuffer LightBuild : register(b2)
{
    uint Pass;
    uint Count;  // pass 0: materials; pass 2: TLAS instances; passes 3, 4, 5 and 10: lamps
    uint First;  // pass 0: the first material of this dispatch; passes 9 and 10: 0 counts, 1 lists (ListNear)
    uint GridByPower;  // pass 8: 1 for P2p's grid (lights drawn by power alone; the emitters-power-grid view)
};

static const uint kGridCandidates = 8;
static const uint kMaterialPoints = 32;  // per axis

groupshared float gsA[64];  // per wave (waves of at least 4 lanes)
groupshared float gsB[64];
groupshared float3 gsLow[64];  // per wave: a part's bounds (PartBounds)
groupshared float3 gsHigh[64];
groupshared uint gsBulb;
// GridPass: the lists of the group's cells (GROUP_SIZE / kLightSlots of them).
static const uint kGroupCells = GROUP_SIZE / kLightSlots;
groupshared uint2 gsNear[kGroupCells * kLightCellEntries];

// Diagnostics of the lamps (statistics 66-73): of kEnclosureRays rays from a lamp's centre (a spot light's
// within its cone), those a shadow caster stops between kLampSkip, where shadow rays toward the lamp end,
// and kLampSkip + kEnclosureReach: the directions its own fixture (or the surface it is mounted on) keeps its
// light from.
static const uint kEnclosureRays = 32;  // threads per lamp (rt_core.cpp kLampThreads)
static const float kEnclosureReach = 0.5;
groupshared uint gsBlocked[GROUP_SIZE / kEnclosureRays];  // per lamp of the group: its rays blocked

// The mip level at which a texture is about kMaterialPoints texels wide.
float CoarseLevel(uint slot)
{
    uint width, height, levels;
    Textures[NonUniformResourceIndex(slot)].GetDimensions(0, width, height, levels);
    return clamp(log2(max(width, height) / (float)kMaterialPoints), 0, levels - 1.0);
}

void MaterialPass(uint material, uint thread)
{
    if (material >= Count)
        return;
    MaterialRecord m = Materials[material];
    float colorLevel = CoarseLevel(m.Textures.x);
    float extensionsLevel = CoarseLevel(m.Textures.z);
    // An alpha-masked material (cutout decals among them) emits where its mask leaves it (lights.hlsli Coverage).
    bool masked = (m.Flags & 1) != 0 && m.Textures.w != kAlphamaskSlotNone;
    float maskLevel = masked ? CoarseLevel(m.Textures.w) : 0;
    float most = 0;
    float sum = 0;
    for (uint i = thread; i < kMaterialPoints * kMaterialPoints; i += GROUP_SIZE)
    {
        float2 uv = (float2(i % kMaterialPoints, i / kMaterialPoints) + 0.5) / kMaterialPoints;
        float4 cm = Textures[NonUniformResourceIndex(m.Textures.x)].SampleLevel(LinearWrap, uv, colorLevel);
        float4 ext = Textures[NonUniformResourceIndex(m.Textures.z)].SampleLevel(LinearWrap, uv, extensionsLevel);
        float emissive = saturate(ext.y - 1 / 255.0);
        if (masked)
            emissive *= Textures[NonUniformResourceIndex(m.Textures.w)].SampleLevel(LinearWrap, uv, maskLevel).r;
        most = max(most, emissive);
        sum += Luminance(cm.rgb * (1 - cm.a)) * emissive;
    }
    most = WaveActiveMax(most);
    sum = WaveActiveSum(sum);
    uint lanes = WaveGetLaneCount();
    if (WaveIsFirstLane())
    {
        gsA[thread / lanes] = most;
        gsB[thread / lanes] = sum;
    }
    GroupMemoryBarrierWithGroupSync();
    if (thread == 0)
    {
        for (uint w = 1; w < GROUP_SIZE / lanes; ++w)
        {
            most = max(most, gsA[w]);
            sum += gsB[w];
        }
        MaterialLight[material] = float4(most, sum / (kMaterialPoints * kMaterialPoints), 0, 0);
    }
}

// The counters, the key table's tags, the cells' lists' counters and bins (one thread per slot).
void ResetPass(uint slot)
{
    if (slot < 16)
        LightCounters.Store(slot * 4, 0);
    if (slot < kLightKeySlots)
        LightKeys[slot].x = 0;
    if (slot < kLightCells)
        LightGrid[kLightCellCounts + slot] = 0;
    if (slot < kLightCells * kLightWeightBins / 2)
        LightGrid[kLightCellBins + slot] = 0;
}

// Passes 9 and 10: a part or lamp (`id`: its LightParts entry, or kNearLamp | its index in Lamps) whose light
// reaches a cell, with its weight there. Counting (First 0) puts it in the cell's bin of its weight; listing
// (First 1, after ThresholdPass) lists it unless its bin lies below the cell's threshold: those above it all
// fit, from the list's start; those of the threshold's own bin follow as they come, while room remains. The
// two passes reach the same cells with the same weights (the same code on the same data).
void ListNear(uint cell, uint id, float weight)
{
    uint bin = WeightBin(weight);
    if (First == 0)
    {
        uint element = kLightCellBins + cell * (kLightWeightBins / 2) + bin / 2;
        if ((bin & 1) != 0)
            InterlockedAdd(LightGrid[element].y, 1u);
        else
            InterlockedAdd(LightGrid[element].x, 1u);
        return;
    }
    uint threshold = LightGrid[kLightCellCounts + cell].y;
    uint above = (threshold >> kLightBinBits) & 63;
    threshold &= kLightWeightBins - 1;
    if (bin < threshold)
        return;  // left to the draw by power
    uint at;
    if (bin > threshold)
    {
        InterlockedAdd(LightGrid[kLightCellCounts + cell].x, 1u, at);
        at &= 0xFFFF;
    }
    else
    {
        InterlockedAdd(LightGrid[kLightCellCounts + cell].x, 1u << 16, at);
        at = above + (at >> 16);
    }
    if (at < kLightCellEntries)
        LightGrid[kLightCellLists + cell * kLightCellEntries + at] = uint2(id, asuint(weight));
}

// Pass 11, per cell (after counting): the lights reaching it; the weakest bin its list takes: when more reach
// it than the list holds, the bin where the count from the strongest down passes kLightCellEntries (its lights
// listed as they come, after those above it), else bin 0 (all of them); and the lights above that bin, where
// that bin's start in the list.
void ThresholdPass(uint cell)
{
    if (cell >= kLightCells)
        return;
    uint base = kLightCellBins + cell * (kLightWeightBins / 2);
    uint reaching = 0;
    for (uint p = 0; p < kLightWeightBins / 2; ++p)
    {
        uint2 n = LightGrid[base + p];
        reaching += n.x + n.y;
    }
    uint threshold = 0;
    uint above = 0;
    for (uint b = kLightWeightBins - 1; b > 0; --b)
    {
        uint2 n = LightGrid[base + b / 2];
        uint inBin = (b & 1) != 0 ? n.y : n.x;
        if (above + inBin > kLightCellEntries)
        {
            threshold = b;
            break;
        }
        above += inBin;
    }
    LightGrid[kLightCellCounts + cell].y =
        threshold | (above << kLightBinBits) | (min(reaching, 0x1FFFFFu) << (kLightBinBits + 6));
}

void PartsPass(uint instance)
{
    if (instance >= Count)
        return;
    uint2 ids = InstanceDescs.Load2(instance * 64 + 48);  // InstanceID | InstanceMask << 24, ... | Flags << 24
    if (((ids.x >> 24) & 0x01) == 0)
        return;  // not drawn in the main view
    uint first = ids.x & 0xFFFFFF;
    uint records = Records[first].Counts >> 24;
    for (uint p = 0; p < records; ++p)
    {
        GeometryRecord record = Records[first + p];
        uint triangles = record.Counts & 0xFFFFFF;
        if (triangles == 0 || Kind(record) == 2 || record.AttributeOffset == kNoAttributes ||
            MaterialLight[record.Material].x <= 0 || GlowsOnly(record))
            continue;
        uint slot;
        LightCounters.InterlockedAdd(kLightCountParts * 4, 1, slot);
        uint start;
        LightCounters.InterlockedAdd(kLightCountTriangles * 4, triangles, start);
        if (slot < kMaxLightParts)
            LightParts[slot] = uint4(instance, first + p, triangles | (p << 24), start);
    }
}

void ArgumentsPass(uint thread)
{
    if (thread != 0)
        return;
    uint parts = LightCounters.Load(kLightCountParts * 4);
    uint triangles = LightCounters.Load(kLightCountTriangles * 4);
    // The lamps (Count) follow the triangles.
    uint entries = triangles + Count;
    bool complete = parts <= kMaxLightParts && entries <= kMaxLightTriangles;
    LightCounters.Store3(kLightArguments * 4, uint3(complete ? parts : 0, 1, 1));
    LightCounters.Store(kLightComplete * 4, complete ? 1u : 0u);
    LightCounters.Store(kLightLampStart * 4, triangles);
    LightCounters.Store(kLightCountTriangles * 4, entries);
    Stats.InterlockedAdd(24 * 4, parts);
    Stats.InterlockedAdd(25 * 4, triangles);
    Stats.InterlockedAdd(26 * 4, complete ? 0u : 1u);
    Stats.InterlockedAdd(31 * 4, 1u);
    Stats.InterlockedAdd(51 * 4, Count);
}

// Inserts a listed light's key into the key table (the first free slot from its hash on).
void InsertKey(uint2 key, uint index)
{
    uint hash = LightKeyHash(key);
    for (uint probe = 0; probe < kLightKeyProbes; ++probe)
    {
        uint slot = (hash + probe) & (kLightKeySlots - 1);
        uint previous;
        InterlockedCompareExchange(LightKeys[slot].x, 0, hash | 1, previous);
        if (previous == 0)
        {
            LightKeys[slot] = uint4(hash | 1, key, index);  // the slot is this thread's now
            return;
        }
    }
}

// Whether ray `r` of kEnclosureRays from a lamp's centre (spread evenly over the sphere, a spot light's over
// its cone: cos theta uniform, the golden angle around) meets a shadow caster between kLampSkip, where the
// shadow rays toward the lamp end, and kLampSkip + kEnclosureReach.
bool EnclosureRayBlocked(Lamp lamp, uint r)
{
    bool spot = (lamp.flags & kLampSpot) != 0;
    float cosMax = spot ? clamp(lamp.apertureCos, -1, 1) : -1;
    float3 forward = spot ? lamp.direction : float3(0, 0, 1);
    float3 side = spot ? normalize(cross(lamp.direction, lamp.up)) : float3(1, 0, 0);
    float3 upward = cross(side, forward);
    float z = 1 - (1 - cosMax) * (r + 0.5) / kEnclosureRays;
    float radius = sqrt(saturate(1 - z * z));
    float phi = 2.39996323 * r;
    float3 direction = normalize(radius * cos(phi) * side + radius * sin(phi) * upward + z * forward);
    return Occluded(lamp.position, direction, kLampSkip + kEnclosureReach, kLampSkip);
}

void LampsPass(uint id, uint thread)
{
    uint lampIndex = id / kEnclosureRays;
    uint ray = id % kEnclosureRays;
    uint slot = thread / kEnclosureRays;
    if (ray == 0)
        gsBlocked[slot] = 0;
    GroupMemoryBarrierWithGroupSync();
    bool valid = lampIndex < Count && LightCounters.Load(kLightComplete * 4) != 0;
    Lamp lamp = (Lamp)0;
    if (valid)
    {
        lamp = Lamps[lampIndex];
        uint index = LightCounters.Load(kLightLampStart * 4) + lampIndex;
        float power = LampPower(lamp);
        if (ray == 0)
        {
            LightTriangle light;
            light.corner = float4(lamp.position, asfloat(lampIndex));
            light.edge1 = 0;
            light.edge2 = float4(0, 0, 0, Luminance(lamp.color) * lamp.diffuseFactor);
            light.uv01 = 0;
            light.uv2 = float4(0, 0, asfloat(kLightLamp), asfloat(0xFFFFFFu));  // no part (LightPartOf)
            Lights[index] = light;
            LightCdf[index] = power / max(dot(lamp.position, lamp.position), 1);  // LightWeight
            if (power > 0)
                InsertKey(LampKey(lamp), index);
        }
        if (EnclosureRayBlocked(lamp, ray))
            InterlockedAdd(gsBlocked[slot], 1u);
    }
    GroupMemoryBarrierWithGroupSync();
    if (!valid || ray != 0)
        return;
    // Statistics 66-73: the lamps' enclosure (the share of their directions blocked just beyond the end of the
    // shadow rays toward them).
    bool spot = (lamp.flags & kLampSpot) != 0;
    uint blocked = gsBlocked[slot];
    float share = blocked / (float)kEnclosureRays;
    Stats.InterlockedAdd(66 * 4, 1u);
    Stats.InterlockedAdd(67 * 4, blocked);
    Stats.InterlockedAdd(68 * 4, share > 0.25 ? 1u : 0u);
    Stats.InterlockedAdd(69 * 4, share > 0.5 ? 1u : 0u);
    Stats.InterlockedAdd(70 * 4, share > 0.75 ? 1u : 0u);
    Stats.InterlockedAdd(71 * 4, blocked == kEnclosureRays ? 1u : 0u);
    Stats.InterlockedAdd(72 * 4, spot ? 1u : 0u);
    Stats.InterlockedAdd(73 * 4, spot && share > 0.5 ? 1u : 0u);
}

// An emissive part's bounds (camera-relative, from all its triangles), and whether it is a lamp's bulb: one of
// the frame's lamps (Count of them) lies within the bounds widened by kBulbMargin. Called by the whole group.
bool PartBounds(GeometryRecord record, uint triangles, float3x3 rotation, float3 translation, uint thread,
                out float3 low, out float3 high)
{
    low = 1e30;
    high = -1e30;
    for (uint t = thread; t < triangles; t += GROUP_SIZE)
    {
        uint3 corners = LoadTriangle(record, t);
        float3 p0 = mul(rotation, LoadPosition(record, corners.x));
        float3 p1 = mul(rotation, LoadPosition(record, corners.y));
        float3 p2 = mul(rotation, LoadPosition(record, corners.z));
        low = min(low, min(p0, min(p1, p2)));
        high = max(high, max(p0, max(p1, p2)));
    }
    low = WaveActiveMin(low);
    high = WaveActiveMax(high);
    uint lanes = WaveGetLaneCount();
    if (WaveIsFirstLane())
    {
        gsLow[thread / lanes] = low;
        gsHigh[thread / lanes] = high;
    }
    if (thread == 0)
        gsBulb = 0;
    GroupMemoryBarrierWithGroupSync();
    low = gsLow[0];
    high = gsHigh[0];
    for (uint w = 1; w < GROUP_SIZE / lanes; ++w)
    {
        low = min(low, gsLow[w]);
        high = max(high, gsHigh[w]);
    }
    low += translation;
    high += translation;
    for (uint l = thread; l < Count; l += GROUP_SIZE)
    {
        float3 lamp = Lamps[l].position;
        if (all(lamp >= low - kBulbMargin) && all(lamp <= high + kBulbMargin))
            InterlockedOr(gsBulb, 1u);
    }
    GroupMemoryBarrierWithGroupSync();
    return gsBulb != 0;
}

void TrianglesPass(uint part, uint thread)
{
    uint4 entry = LightParts[part];
    uint instance = entry.x;
    GeometryRecord record = Records[entry.y];
    uint triangles = entry.z & 0xFFFFFF;
    uint geometryIndex = entry.z >> 24;
    float4 row0 = asfloat(InstanceDescs.Load4(instance * 64));
    float4 row1 = asfloat(InstanceDescs.Load4(instance * 64 + 16));
    float4 row2 = asfloat(InstanceDescs.Load4(instance * 64 + 32));
    float3x3 rotation = float3x3(row0.xyz, row1.xyz, row2.xyz);
    float3 translation = float3(row0.w, row1.w, row2.w);
    // The front faces -(e1 x e2) in object space (LightNormal); a mirroring transform turns the
    // world cross product around.
    uint flags = (determinant(rotation) > 0 ? kLightFlipped : 0) | ((record.Flags & 2) != 0 ? kLightTwoSided : 0);
    float4 paint = InstanceData[instance * 2];
    float4 extra = InstanceData[instance * 2 + 1];
    // Its bounds (the cells near it: NearPass); a lamp's bulb glows but lights nothing (statistics 64-65).
    float3 low, high;
    bool bulb = PartBounds(record, triangles, rotation, translation, thread, low, high);
    if (bulb && thread == 0)
        Stats.InterlockedAdd(64 * 4, 1u);
    // Its entry in the triangles' uv2.z (LightPartOf), for the cells' lists below kMaxNearParts.
    uint partBits = part < kMaxNearParts ? (part + 1) << 16 : 0;
    uint lanes = WaveGetLaneCount();
    float partPower = 0;  // the power of the part's triangles before this round's
    for (uint first = 0; first < triangles; first += GROUP_SIZE)
    {
        uint t = first + thread;
        float power = 0;
        if (t < triangles)
        {
            uint3 corners = LoadTriangle(record, t);
            float3 o0 = LoadPosition(record, corners.x);
            float3 e1 = mul(rotation, LoadPosition(record, corners.y) - o0);
            float3 e2 = mul(rotation, LoadPosition(record, corners.z) - o0);
            float2 uv0 = LoadUv(record, corners.x);
            float2 uv1 = LoadUv(record, corners.y);
            float2 uv2 = LoadUv(record, corners.z);
            float area = 0.5 * length(cross(e1, e2));
            float radiance = EmissionEstimate(record, paint, extra, geometryIndex, uv0, uv1, uv2);
            LightTriangle light;
            light.corner = float4(mul(rotation, o0) + translation, asfloat(instance));
            light.edge1 = float4(e1, asfloat(entry.y));
            light.edge2 = float4(e2, radiance);
            light.uv01 = float4(uv0, uv1);
            light.uv2 = float4(uv2, asfloat(flags | (geometryIndex << 8) | partBits), asfloat(t));
            uint index = entry.w + t;
            Lights[index] = light;
            // The weight the scan sums (LightWeight): the power of the triangles bright enough to be listed
            // (TrianglePower), over the squared distance from the camera; none for a bulb's.
            bool listed = radiance >= kLightMinRadiance;
            uint bulbTriangles = WaveActiveCountBits(listed && bulb);
            if (WaveIsFirstLane() && bulbTriangles != 0)
                Stats.InterlockedAdd(65 * 4, bulbTriangles);
            listed = listed && !bulb;
            power = listed ? radiance * area * ((flags & kLightTwoSided) != 0 ? 2 : 1) : 0;
            float3 centre = light.corner.xyz + (e1 + e2) / 3;
            LightCdf[index] = power / max(dot(centre, centre), 1);
            // Statistics 40-47 (rt_core.cpp Report): triangles and their power by radiance (< 1, 1-10, 10-100,
            // more).
            uint bucket = radiance < 1 ? 0 : radiance < 10 ? 1 : radiance < 100 ? 2 : 3;
            float bucketPower = radiance * area;
            [unroll]
            for (uint b = 0; b < 4; ++b)
            {
                uint triangleCount = WaveActiveCountBits(bucket == b);
                uint powerSum = WaveActiveSum(bucket == b ? (uint)min(bucketPower, 1e6) : 0u);
                if (WaveIsFirstLane())
                {
                    if (triangleCount != 0)
                        Stats.InterlockedAdd((40 + b) * 4, triangleCount);
                    if (powerSum != 0)
                        Stats.InterlockedAdd((44 + b) * 4, powerSum);
                }
            }
            // The key table, listed triangles only (a kept sample of one that dimmed below the threshold is not
            // found again: its light is the paths' then).
            if (listed)
                InsertKey(LightKeyOf(extra, entry.y, geometryIndex, t), index);
        }
        // The prefix sums of power within the part (a cell draws a part's triangle by power: FindPartLight).
        float before = WavePrefixSum(power);
        if (WaveGetLaneIndex() == lanes - 1)
            gsA[thread / lanes] = before + power;
        GroupMemoryBarrierWithGroupSync();
        float waveBefore = 0;
        float roundPower = 0;
        for (uint w = 0; w < GROUP_SIZE / lanes; ++w)
        {
            if (w < thread / lanes)
                waveBefore += gsA[w];
            roundPower += gsA[w];
        }
        if (t < triangles)
            LightCdf[kLightPartCdf + entry.w + t] = partPower + waveBefore + before + power;
        partPower += roundPower;
        GroupMemoryBarrierWithGroupSync();  // gsA is the next round's
    }
    if (thread == 0)
    {
        LightParts[kLightPartBounds + 2 * part] = asuint(float4(low, partPower));
        LightParts[kLightPartBounds + 2 * part + 1] = asuint(float4(high, 0));
    }
}

// Pass 10 (ListNear; reads no LightCounters, which are the dispatch's arguments then): per lamp (Count of
// them), kEnclosureRays threads: the cells within its range (at most kNearReach), widened by what their slots
// serve (kLightCellReach), with its strength there (the grid's target).
void NearLampsPass(uint id)
{
    uint lampIndex = id / kEnclosureRays;
    if (lampIndex >= Count)
        return;
    Lamp lamp = Lamps[lampIndex];
    float reach = min(lamp.range, kNearReach) + kLightCellReach;
    uint3 first, last;
    if (!(LampPower(lamp) > 0) || !CellRange(lamp.position - reach, lamp.position + reach, first, last))
        return;
    uint3 extent = last - first + 1;
    uint cells = extent.x * extent.y * extent.z;
    for (uint i = id % kEnclosureRays; i < cells; i += kEnclosureRays)
    {
        uint3 c = first + uint3(i % extent.x, (i / extent.x) % extent.y, i / (extent.x * extent.y));
        float3 centre = CellCentre(c);
        float strength = LampCellStrength(lamp, centre, kLightCellReach);
        if (strength > 0 && length(centre - lamp.position) <= reach)
            ListNear(CellIndex(c), kNearLamp | lampIndex, strength);
    }
}

// Pass 9 (ListNear): a part's cells: those its light reaches (its power's diffuse light above kNearLight, at
// most kNearReach metres from its bounds), widened by what their slots serve (kLightCellReach), with its power
// over the squared distance from the cell's centre to its bounds (at least the cell's half diagonal).
void NearPass(uint part, uint thread)
{
    if (part >= kMaxNearParts)
        return;
    float4 low = asfloat(LightParts[kLightPartBounds + 2 * part]);
    float4 high = asfloat(LightParts[kLightPartBounds + 2 * part + 1]);
    float power = low.w;
    if (!(power > 0))
        return;
    float reach = min(sqrt(power / (kPi * kNearLight)), kNearReach) + kLightCellReach;
    uint3 first, last;
    if (!CellRange(low.xyz - reach, high.xyz + reach, first, last))
        return;
    uint3 extent = last - first + 1;
    uint cells = extent.x * extent.y * extent.z;
    float halfDiagonal2 = kLightCellHalfDiagonal * kLightCellHalfDiagonal;
    for (uint i = thread; i < cells; i += GROUP_SIZE)
    {
        uint3 c = first + uint3(i % extent.x, (i / extent.x) % extent.y, i / (extent.x * extent.y));
        float3 centre = CellCentre(c);
        float3 toBounds = centre - clamp(centre, low.xyz, high.xyz);
        float distance2 = dot(toBounds, toBounds);
        if (distance2 <= reach * reach)
            ListNear(CellIndex(c), part, power / max(distance2, halfDiagonal2));
    }
}

// The power prefix sums of one block of kLightScanBlock triangles (GROUP_SIZE threads, four each).
void ScanBlocksPass(uint block, uint thread)
{
    uint count = LightCounters.Load(kLightComplete * 4) != 0 ? LightCount() : 0;
    uint first = block * kLightScanBlock;
    if (first >= count)
    {
        if (thread == 0)
            LightBlocks[block] = 0;
        return;
    }
    uint base = first + thread * 4;
    float4 v;
    v.x = base < count ? LightCdf[base] : 0;
    v.y = base + 1 < count ? LightCdf[base + 1] : 0;
    v.z = base + 2 < count ? LightCdf[base + 2] : 0;
    v.w = base + 3 < count ? LightCdf[base + 3] : 0;
    v.y += v.x;
    v.z += v.y;
    v.w += v.z;
    float before = WavePrefixSum(v.w);
    uint lanes = WaveGetLaneCount();
    if (WaveGetLaneIndex() == lanes - 1)
        gsA[thread / lanes] = before + v.w;
    GroupMemoryBarrierWithGroupSync();
    if (thread == 0)
    {
        float run = 0;
        for (uint w = 0; w < GROUP_SIZE / lanes; ++w)
        {
            float waveTotal = gsA[w];
            gsA[w] = run;
            run += waveTotal;
        }
        LightBlocks[block] = run;  // the block's total; the next pass makes it an offset
    }
    GroupMemoryBarrierWithGroupSync();
    v += gsA[thread / lanes] + before;
    if (base < count)
        LightCdf[base] = v.x;
    if (base + 1 < count)
        LightCdf[base + 1] = v.y;
    if (base + 2 < count)
        LightCdf[base + 2] = v.z;
    if (base + 3 < count)
        LightCdf[base + 3] = v.w;
}

// The blocks' totals into offsets (one group: kLightBlocks == GROUP_SIZE), the total power.
void ScanTotalsPass(uint thread)
{
    float blockTotal = LightBlocks[thread];
    float before = WavePrefixSum(blockTotal);
    uint lanes = WaveGetLaneCount();
    if (WaveGetLaneIndex() == lanes - 1)
        gsA[thread / lanes] = before + blockTotal;
    GroupMemoryBarrierWithGroupSync();
    if (thread == 0)
    {
        float run = 0;
        for (uint w = 0; w < GROUP_SIZE / lanes; ++w)
        {
            float waveTotal = gsA[w];
            gsA[w] = run;
            run += waveTotal;
        }
        gsB[0] = run;
    }
    GroupMemoryBarrierWithGroupSync();
    LightBlocks[thread] = gsA[thread / lanes] + before;
    if (thread == 0)
    {
        float power = gsB[0];
        bool complete = LightCounters.Load(kLightComplete * 4) != 0;
        LightCounters.Store(kLightTotalPower * 4, asuint(power));
        LightCounters.Store(kLightActive * 4, complete && LightCount() > 0 && power > 0 ? 1u : 0u);
    }
}

// Pass 8: per cell and slot (a group: kGroupCells cells), one light resampled from kGridCandidates candidates
// by its strength at the cell: kNearShare of them drawn from the cell's list (an entry by its weight there, a
// part's triangle by power within it: FindPartLight), the others by weight from the whole list (FindLight);
// each weighed by its probability under both (the list's share of it times a part's triangle's power within the
// part, and its weight over the total), so that what the list lacks (a light past its reach, one too weak there
// for a full list) is still found by weight. The dispatch covers the slots exactly (the group's barrier).
void GridPass(uint slot, uint thread)
{
    if (!LightsActive())
    {
        LightGrid[slot] = uint2(kNoLight, 0);
        return;
    }
    // The lists of the group's cells.
    uint cell = slot / kLightSlots;
    uint groupCell = cell - thread / kLightSlots;
    for (uint k = thread; k < kGroupCells * kLightCellEntries; k += GROUP_SIZE)
    {
        uint c = groupCell + k / kLightCellEntries;
        uint e = k % kLightCellEntries;
        uint listed = ListedCount(LightGrid[kLightCellCounts + c].x);
        gsNear[k] = e < listed ? LightGrid[kLightCellLists + c * kLightCellEntries + e] : uint2(kNoPart, 0);
    }
    GroupMemoryBarrierWithGroupSync();
    uint listBase = (thread / kLightSlots) * kLightCellEntries;
    uint2 cellCounters = LightGrid[kLightCellCounts + cell];
    uint nearCount = ListedCount(cellCounters.x);
    uint reaching = cellCounters.y >> (kLightBinBits + 6);  // the lights whose light reaches the cell
    uint lampStart = LightCounters.Load(kLightLampStart * 4);
    float nearSum = 0;
    for (uint n = 0; n < nearCount; ++n)
        nearSum += asfloat(gsNear[listBase + n].y);
    float nearShare = nearSum > 0 && GridByPower == 0 ? kNearShare : 0;
    // What the cell's slots serve: the points of the cell, and (LightCandidates' jitter) as far again.
    float reach = GridByPower == 0 ? kLightCellReach : kLightCellHalfDiagonal;
    uint count = LightCount();
    float total = LightTotalPower();
    float3 centre = CellCentre(CellCoordinates(cell));
    float halfDiagonal2 = kLightCellHalfDiagonal * kLightCellHalfDiagonal;
    uint seed = Pcg(slot ^ Pcg(Frame * 0x9E3779B9u + 7));
    uint chosen = kNoLight;
    float chosenTarget = 0;
    float weightSum = 0;
    bool chosenNear = false;
    // Checks (statistics 95-98): candidates drawn from the list, those whose light is not the entry drawn; lamp
    // candidates, those whose entry's lamp (corner.w) is not their place after the triangles.
    uint drawnNear = 0;
    uint drawnAmiss = 0;
    uint lampCandidates = 0;
    uint lampAmiss = 0;
    for (uint i = 0; i < kGridCandidates; ++i)
    {
        bool fromNear = Random(seed) < nearShare;
        float u = Random(seed);
        float v = Random(seed);
        float s = Random(seed);
        uint index;
        uint drawn = kNoPart;
        if (fromNear)
        {
            // An entry by its weight at the cell; a lamp's is the lamp, a part's one of its triangles.
            float mark = v * nearSum;
            uint pick = 0;
            float run = asfloat(gsNear[listBase].y);
            while (pick + 1 < nearCount && run <= mark)
            {
                ++pick;
                run += asfloat(gsNear[listBase + pick].y);
            }
            drawn = gsNear[listBase + pick].x;
            if ((drawn & kNearLamp) != 0)
                index = lampStart + (drawn & ~kNearLamp);
            else
                index = FindPartLight(drawn, u);  // (an if: a ternary may evaluate both sides)
        }
        else
        {
            index = FindLight(u * total, count);
        }
        LightTriangle light = Lights[index];
        bool lamp = IsLamp(light);
        float power = lamp ? 0 : TrianglePower(light);
        // A lamp's place in Lamps: its place in the list after the triangles (as its entry in the cells' lists).
        // Not asuint(corner.w): that small integer is a denormal float's pattern, and read through it here the
        // lamps went unfound in their lists and were weighed several times over (P2q: 4-16x the light; the
        // check below, statistic 98, has found corner.w intact since).
        uint lampIndex = lamp ? index - lampStart : 0;
        uint id = lamp ? (kNearLamp | lampIndex) : LightPartOf(light);
        drawnNear += fromNear ? 1u : 0u;
        drawnAmiss += fromNear && id != drawn ? 1u : 0u;
        lampCandidates += lamp ? 1u : 0u;
        lampAmiss += lamp && asuint(light.corner.w) != lampIndex ? 1u : 0u;
        // Its probability: drawn from the list (its entry's share of the weights, a part's triangle's share of
        // the part's power) or by weight.
        float nearProbability = 0;
        if (nearShare > 0)
        {
            for (uint m = 0; m < nearCount; ++m)
            {
                if (gsNear[listBase + m].x == id)
                {
                    nearProbability = asfloat(gsNear[listBase + m].y) / nearSum;
                    break;
                }
            }
            if (!lamp && nearProbability > 0)
                nearProbability *= power / PartPower(id);
        }
        float weight;
        if (lamp)
        {
            Lamp lampData = Lamps[lampIndex];
            weight = LampPower(lampData) / max(dot(lampData.position, lampData.position), 1);  // LightWeight
        }
        else
        {
            weight = LightWeight(light);
        }
        float probability = nearShare * nearProbability + (1 - nearShare) * weight / total;
        if (!(probability > 0))
            continue;
        float target;
        if (lamp)
        {
            // The game's falloff from the nearest point the cell serves; spot lights whose cone misses it: 0.
            target = LampCellStrength(Lamps[lampIndex], centre, reach);
        }
        else
        {
            float3 lightCentre = light.corner.xyz + (light.edge1.xyz + light.edge2.xyz) / 3;
            float3 toCell = centre - lightCentre;
            // A one-sided triangle with the whole cell (and the points it serves) behind it lights nothing there.
            if ((asuint(light.uv2.z) & kLightTwoSided) == 0 && dot(LightNormal(light), toCell) < -reach)
                continue;
            // Its power over the squared distance.
            target = power / max(dot(toCell, toCell), halfDiagonal2);
        }
        if (!(target > 0))
            continue;
        float w = target / probability;  // the target over the candidate's probability
        weightSum += w;
        if (s * weightSum < w)
        {
            chosen = index;
            chosenTarget = target;
            chosenNear = fromNear;
        }
    }
    LightGrid[slot] = chosenTarget > 0 ? uint2(chosen, asuint(weightSum / (kGridCandidates * chosenTarget)))
                                       : uint2(kNoLight, 0);
    // Statistics 85-89 and 94 (rt_core.cpp Report): cells with a list, the entries listed, cells more lights
    // reach than their list holds; slots holding a light, one drawn from the list; the lights reaching the cells
    // with a list.
    bool cellFirst = slot % kLightSlots == 0;
    uint lists = WaveActiveCountBits(cellFirst && nearCount > 0);
    uint entries = WaveActiveSum(cellFirst ? nearCount : 0u);
    uint overflowed = WaveActiveCountBits(cellFirst && reaching > kLightCellEntries);
    uint lit = WaveActiveCountBits(chosenTarget > 0);
    uint litNear = WaveActiveCountBits(chosenTarget > 0 && chosenNear);
    uint reached = WaveActiveSum(cellFirst && nearCount > 0 ? reaching : 0u);
    // Check (statistic 99): listing cells whose lights above the threshold's bin are not as many as it counted
    // (that bin's lights would then start elsewhere than where the list is read).
    uint above = (cellCounters.y >> kLightBinBits) & 63;
    bool misplaced = cellFirst && nearCount > 0 && (cellCounters.x & 0xFFFF) != above;
    drawnNear = WaveActiveSum(drawnNear);
    drawnAmiss = WaveActiveSum(drawnAmiss);
    lampCandidates = WaveActiveSum(lampCandidates);
    lampAmiss = WaveActiveSum(lampAmiss);
    uint misplacedCells = WaveActiveCountBits(misplaced);
    if (WaveIsFirstLane())
    {
        if (drawnNear != 0)
            Stats.InterlockedAdd(95 * 4, drawnNear);
        if (drawnAmiss != 0)
            Stats.InterlockedAdd(96 * 4, drawnAmiss);
        if (lampCandidates != 0)
            Stats.InterlockedAdd(97 * 4, lampCandidates);
        if (lampAmiss != 0)
            Stats.InterlockedAdd(98 * 4, lampAmiss);
        if (misplacedCells != 0)
            Stats.InterlockedAdd(99 * 4, misplacedCells);
        if (lists != 0)
            Stats.InterlockedAdd(85 * 4, lists);
        if (entries != 0)
            Stats.InterlockedAdd(86 * 4, entries);
        if (overflowed != 0)
            Stats.InterlockedAdd(87 * 4, overflowed);
        if (lit != 0)
            Stats.InterlockedAdd(88 * 4, lit);
        if (litNear != 0)
            Stats.InterlockedAdd(89 * 4, litNear);
        if (reached != 0)
            Stats.InterlockedAdd(94 * 4, reached);
    }
}

[numthreads(GROUP_SIZE, 1, 1)]
void main(uint3 id : SV_DispatchThreadID, uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    if (Pass == 0)
        MaterialPass(First + group.x, thread);
    else if (Pass == 1)
        ResetPass(id.x);
    else if (Pass == 2)
        PartsPass(id.x);
    else if (Pass == 3)
        ArgumentsPass(thread);
    else if (Pass == 4)
        TrianglesPass(group.x, thread);
    else if (Pass == 5)
        LampsPass(id.x, thread);
    else if (Pass == 6)
        ScanBlocksPass(group.x, thread);
    else if (Pass == 7)
        ScanTotalsPass(thread);
    else if (Pass == 8)
        GridPass(id.x, thread);
    else if (Pass == 9)
        NearPass(group.x, thread);
    else if (Pass == 10)
        NearLampsPass(id.x);
    else
        ThresholdPass(id.x);
}

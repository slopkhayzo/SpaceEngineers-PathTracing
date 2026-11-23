// The light tree (research/docs/space-engineers-rt-plan.md P2z): a bounding
// volume hierarchy over the frame's listed lights, the emissive parts (by
// their LightParts entry) and then the lamps, as its leaves. Built every frame
// (emissive_lights.hlsl passes 13-19: the leaves' records and the bounds of
// their centres, a Morton key per leaf, a bitonic sort of the keys, the
// internal nodes from the sorted keys after Karras 2012, the nodes' child
// slots filled from the leaves up) and walked from the root, each child taken
// by its importance where the light is wanted (TreeLight). Unlike the light
// grid around the camera it has no extent: every listed light can be drawn at
// every point.
//
// Walked for every candidate of every lit pixel it cost 3-5 ms a frame (P2z
// v1). Instead it fills the slots of the light grid's coarser levels around
// its fine one (passes 20 and 21: only the cells that points took in the
// latest frames), from which the points beyond the fine level draw their
// candidates as the points inside it draw from its cells; a point walks the
// tree itself only where its cell was not filled this frame.
//
// Needs scene_common.hlsli and lights.hlsli, and `LightTree`
// (RWByteAddressBuffer, u27) declared by the including shader: globallycoherent
// where the tree is built, plain where it is walked. The walks read the nodes
// through LIGHT_TREE_WALK (LightTree unless defined: where LightTree is
// coherent, a plain view of the same buffer, whose reads the caches keep).

#ifndef LIGHT_TREE_HLSLI
#define LIGHT_TREE_HLSLI

#ifndef LIGHT_TREE_WALK
#define LIGHT_TREE_WALK LightTree
#endif

static const uint kTreeMaxLeaves = 73728;  // kMaxLightParts + kMaxLamps (scene.h 8192), rounded up
static const uint kTreeMaxKeys = 131072;   // the sort's largest size: a power of two above kTreeMaxLeaves
static const uint kTreeSortChunk = 1024;   // keys a group sorts in shared memory

// The light grid's coarser levels (lights.hlsli has its fine level: 2 m cells out to 32 m): kLightLevels of
// kLightGridSize^3 cells centred on the camera, each level's cells 4 times as wide as the level's inside it (8,
// 32, 128 and 512 m: out to 128 m, 512 m, 2 km and 8 km from the camera; farther points take the outermost level's
// nearest cells), kLightSlots resampled lights each, like the fine level's. A cell is filled in a frame where a
// point's candidate came from it in the latest kLevelUseFrames frames (each candidate's cell is the one of a
// point up to half a cell from the point: primary_view.hlsl LightCandidates marks it).
static const uint kLightLevels = 4;
static const uint kLightLevelCells = kLightLevels * kLightCells;
static const uint kLevelUseFrames = 4;

// LightTree's layout (bytes; rt_core.cpp kTreeBytes): the keys being sorted (TreeKey); the leaves'
// records; the internal nodes, two child slots each; the leaves' parents (by sorted place), the nodes' parents;
// the nodes' visit counters (the build from the leaves up). A record or slot is two float4 and a uint4: the low
// corner and the parts' power; the high corner and the lamps' intensity (luminance x diffuse factor); asuint of
// the lamps' largest range and of their smallest falloff, the child (kTreeLeaf | a leaf, or a node), 0. (The
// child stays a uint: as a float its pattern is a denormal, which float moves may flush; P2q's lost lamps.)
// Then the coarse levels: their cells' slots (as the fine level's: light, asuint(contribution weight)); per
// cell the frame a point last took it (primary_view.hlsl) and the frame it was last filled; the cells to fill
// this frame (pass 20's list).
static const uint kTreeKeys = 0;
static const uint kTreeLeaves = kTreeKeys + kTreeMaxKeys * 8;
static const uint kTreeNodes = kTreeLeaves + kTreeMaxLeaves * 48;
static const uint kTreeLeafParents = kTreeNodes + kTreeMaxLeaves * 96;
static const uint kTreeNodeParents = kTreeLeafParents + kTreeMaxLeaves * 4;
static const uint kTreeVisits = kTreeNodeParents + kTreeMaxLeaves * 4;
static const uint kTreeLevelSlots = kTreeVisits + kTreeMaxLeaves * 4;
static const uint kTreeLevelUsed = kTreeLevelSlots + kLightLevelCells * kLightSlots * 8;
static const uint kTreeLevelFilled = kTreeLevelUsed + kLightLevelCells * 4;
static const uint kTreeLevelList = kTreeLevelFilled + kLightLevelCells * 4;
static const uint kTreeBytes = kTreeLevelList + kLightLevelCells * 4;
static const uint kTreeLeafBits = 17;  // a key's low bits: its leaf (kTreeMaxLeaves fits)
static const uint kTreeLeafMask = (1u << kTreeLeafBits) - 1;
static const uint kTreeLeaf = 0x80000000;
static const uint kTreeNone = 0xFFFFFFFF;
static const float kTreeNoFalloff = 1e30;  // a record without lamps: no falloff bound from it

// A float as a uint ordered like the floats (InterlockedMax over both signs), and back.
uint OrderedFloat(float f)
{
    uint u = asuint(f);
    return (u & 0x80000000) != 0 ? ~u : (u | 0x80000000);
}

float FromOrderedFloat(uint u)
{
    return asfloat((u & 0x80000000) != 0 ? (u & 0x7FFFFFFF) : ~u);
}

// 10 bits spread to every third bit of 30.
uint ExpandBits10(uint v)
{
    v &= 0x3FF;
    v = (v | (v << 16)) & 0x030000FF;
    v = (v | (v << 8)) & 0x0300F00F;
    v = (v | (v << 4)) & 0x030C30C3;
    v = (v | (v << 2)) & 0x09249249;
    return v;
}

// A leaf's key in the sort: the Morton code of its centre in the unit cube of the leaves' centres, 15 bits per
// axis (a 45-bit code: about 3 m apart over 100 km, so that the lights of a room keep their places when the
// tree also takes a lamp kilometres away), then the leaf (kTreeLeafBits), which makes every key unique. As a
// uint2 ordered by x, then y: x the 30-bit code of the axes' high 10 bits; y that of their low 5 bits (15 bits)
// above the leaf.
uint2 TreeKey(float3 unit, uint leaf)
{
    uint3 q = (uint3)clamp(unit * 32768, 0, 32767);
    uint high = (ExpandBits10(q.x >> 5) << 2) | (ExpandBits10(q.y >> 5) << 1) | ExpandBits10(q.z >> 5);
    uint low = (ExpandBits10(q.x & 31) << 2) | (ExpandBits10(q.y & 31) << 1) | ExpandBits10(q.z & 31);
    return uint2(high, (low << kTreeLeafBits) | (leaf & kTreeLeafMask));
}

// The tree's leaves this frame (those the sort took: kLightTreeSorted).
uint TreeLeafCount()
{
    return LightCounters.Load(kLightTreeSorted * 4);
}

// A coarse level's cell size (level 1 to kLightLevels): 4^level times the fine level's.
float LevelCellSize(uint level)
{
    return kLightCellSize * (float)(1u << (2 * level));
}

// The level whose cells a camera-relative point takes: 0 inside the fine level's box (InLightGrid), else the
// first coarse level whose box holds it, the outermost beyond them all.
uint PointLevel(float3 p)
{
    if (InLightGrid(p))
        return 0;
    float extent = max(max(abs(p.x), abs(p.y)), abs(p.z));
    float halfWidth = 0.5 * kLightGridSize * kLightCellSize;
    uint level = 1;
    for (; level < kLightLevels; ++level)
    {
        halfWidth *= 4;
        if (extent < halfWidth)
            break;
    }
    return level;
}

// A coarse level's cell holding a camera-relative point (clamped to the level's box): its index among the coarse
// levels' cells (the first level's first).
uint LevelCell(uint level, float3 p)
{
    int3 c = clamp(int3(floor(p / LevelCellSize(level))) + (int)(kLightGridSize / 2), 0, (int)kLightGridSize - 1);
    return (level - 1) * kLightCells + CellIndex(uint3(c));
}

// A coarse cell's level and centre (camera-relative).
uint LevelOfCell(uint levelCell)
{
    return levelCell / kLightCells + 1;
}

float3 LevelCellCentre(uint levelCell)
{
    return (float3(CellCoordinates(levelCell % kLightCells)) + 0.5 - 0.5 * kLightGridSize) *
           LevelCellSize(LevelOfCell(levelCell));
}

// How much a record or slot (r0, r1, r2: see the layout) may light the points within `reach` of `position` that
// face `N` (a unit normal; 0: any way, a cell's points): zero where it cannot (its bounding sphere wholly below
// the surface, its lamps all out of range); else its parts' power over the squared distance from `position` to
// its centre (at least its half diagonal's square and `nearest2`) plus its lamps' intensity at the game's falloff
// from its nearest point less `reach` (their largest range, smallest falloff), times the best cosine the surface
// can have toward it. The same units as the cells' targets (emissive_lights.hlsl CellTarget): a triangle's power
// over the squared distance, a lamp's strength.
float TreeImportance(float4 r0, float4 r1, uint4 r2, float3 position, float3 N, float reach, float nearest2)
{
    float lampRange = asfloat(r2.x);
    float lampFalloff = asfloat(r2.y);
    float3 centre = 0.5 * (r0.xyz + r1.xyz);
    float3 halfExtent = 0.5 * (r1.xyz - r0.xyz);
    float radius2 = dot(halfExtent, halfExtent);
    float3 toCentre = centre - position;
    float distance2 = dot(toCentre, toCentre);
    float cosBound = 1;
    if (any(N != 0) && distance2 > radius2)
    {
        float distance = sqrt(distance2);
        float cosCentre = dot(N, toCentre) / distance;
        float sinRadius = sqrt(radius2 / distance2);
        float cosRadius = sqrt(saturate(1 - sinRadius * sinRadius));
        float sinCentre = sqrt(saturate(1 - cosCentre * cosCentre));
        cosBound = cosCentre >= cosRadius ? 1 : cosCentre * cosRadius + sinCentre * sinRadius;
    }
    if (!(cosBound > 0))
        return 0;
    float importance = 0;
    if (r0.w > 0)
        importance += r0.w / max(distance2, max(radius2, nearest2));
    if (r1.w > 0)
    {
        float3 outside = max(abs(toCentre) - halfExtent, 0);
        float nearest = max(length(outside) - reach, 0);
        if (nearest < lampRange)
        {
            float q = nearest / lampRange;
            float q2 = q * q;
            float a = 1 - q2 * q2;
            importance += r1.w * a * a * a / (1 + min(lampFalloff, 1e6) * q2);
        }
    }
    return cosBound * importance;
}

// One listed light drawn from the tree for the points within `reach` of camera-relative `position` that face `N`
// (TreeImportance; a shading point: its normal, reach 0; a coarse cell's points: N 0): its index in Lights (a
// part's triangle drawn by power within the part: FindPartLight) and the probability of that draw; false when no
// light can reach there, or the tree is empty.
bool TreeLight(float3 position, float3 N, float reach, float nearest2, inout uint seed, out uint index,
               out float probability)
{
    index = kNoLight;
    probability = 1;
    uint child = LightCounters.Load(kLightTreeRoot * 4);
    if (child == kTreeNone)
        return false;
    [loop]
    for (uint depth = 0; depth < 64 && (child & kTreeLeaf) == 0; ++depth)
    {
        uint base = kTreeNodes + child * 96;
        float4 a0 = asfloat(LIGHT_TREE_WALK.Load4(base));
        float4 a1 = asfloat(LIGHT_TREE_WALK.Load4(base + 16));
        uint4 a2 = LIGHT_TREE_WALK.Load4(base + 32);
        float4 b0 = asfloat(LIGHT_TREE_WALK.Load4(base + 48));
        float4 b1 = asfloat(LIGHT_TREE_WALK.Load4(base + 64));
        uint4 b2 = LIGHT_TREE_WALK.Load4(base + 80);
        float importanceA = TreeImportance(a0, a1, a2, position, N, reach, nearest2);
        float importanceB = TreeImportance(b0, b1, b2, position, N, reach, nearest2);
        float sum = importanceA + importanceB;
        if (!(sum > 0))
            return false;
        bool takeA = Random(seed) * sum < importanceA;
        probability *= (takeA ? importanceA : importanceB) / sum;
        child = takeA ? a2.z : b2.z;
    }
    if ((child & kTreeLeaf) == 0 || child == kTreeNone)
        return false;
    uint leaf = child & ~kTreeLeaf;
    uint parts = min(LightCounters.Load(kLightCountParts * 4), kMaxLightParts);
    if (leaf < parts)
    {
        float partPower = PartPower(leaf);
        if (!(partPower > 0))
            return false;
        index = FindPartLight(leaf, Random(seed));
        probability *= TrianglePower(Lights[index]) / partPower;
    }
    else
    {
        index = LightCounters.Load(kLightLampStart * 4) + (leaf - parts);
    }
    return probability > 0;
}

#endif

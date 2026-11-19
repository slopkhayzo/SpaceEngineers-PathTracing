// Instancing records -> TLAS instance descs (D3D12 compute, cs_6_0;
// src/native/rt/scene.cpp). One group per bucket: the instances of one model
// under one renderable, records of one of the game's instancing buffers.
//
// Cube blocks (stride 64, MyVertexFormatCubeInstance: 8 packed deformation
// bones, then float4 translationRotation, then the HSV color mask). The game
// places a block vertex p as p * B * C (VRageMath row vectors), B the block
// matrix of construct_cube_instance_matrix (VertexTransformations.hlsli): rows
// right, up, -forward, translation; C the cell's world matrix.
//
// Generic instances such as trees (stride 32, MyVertexFormatGenericInstance:
// half4 rows 0-2, then the HSV color mask). The vertex shader uses
// construct_matrix_43(row0, row1, row2) = transpose(rows): the rows are
// already a column-vector 3x4 transform R, so p goes to C^T * R * p.
//
// A DXR instance transform T is column-vector: the bucket holds C in that
// layout, camera-relative, row i = (C[0][i], C[1][i], C[2][i], C[3][i]), and
// T = Ccol * R with R = B^T for blocks.

struct Bucket
{
    float4 Row0;
    float4 Row1;
    float4 Row2;
    uint RecordOffset;    // bytes: the first record in Records
    uint BlockCount;
    uint OutputOffset;    // first instance, after FirstInstance
    uint InstanceIdMask;  // InstanceID | InstanceMask << 24
    uint2 Blas;
    uint Flags;           // InstanceFlags << 24
    uint Stride;          // 64 cube blocks, 32 generic instances
    float4 PreviousRow0;  // C in the previous recorded frame, around that frame's camera
    float4 PreviousRow1;
    float4 PreviousRow2;
};

StructuredBuffer<Bucket> Buckets : register(t0);
ByteAddressBuffer Records : register(t1);
RWByteAddressBuffer Instances : register(u0);     // D3D12_RAYTRACING_INSTANCE_DESC, 64 bytes
// Two float4 per instance: the paint HSV offset and flags; the armor texture
// offset, the w bytes of bones 5-7 and the source (scene_common.hlsli: 1
// kSourceCube, 2 kSourceGeneric) | the record's byte offset / 32 << 2
// (kSourceShift; records are 32 or 64 bytes, 32-byte aligned, in at most
// 64 MB), which lasts from frame to frame unlike the instance's index: the key
// of the record's lights (lights.hlsli LightKeyOf).
RWByteAddressBuffer InstanceData : register(u1);
// Three float4 per instance: its transform in the previous recorded frame (motion vectors, primary_view.hlsl).
RWByteAddressBuffer PreviousTransforms : register(u2);

cbuffer Expand : register(b0)
{
    uint FirstInstance;  // instance descs written by the CPU come first
    uint FirstBucket;    // of this dispatch
};

// Base6Directions: Forward, Backward, Left, Right, Up, Down.
static const float3 kDirections[6] = {
    float3(0, 0, -1), float3(0, 0, 1), float3(-1, 0, 0), float3(1, 0, 0), float3(0, 1, 0), float3(0, -1, 0)
};

float4 HalfRow(uint address)
{
    uint2 raw = Records.Load2(address);
    return float4(f16tof32(raw.x), f16tof32(raw.x >> 16), f16tof32(raw.y), f16tof32(raw.y >> 16));
}

[numthreads(64, 1, 1)]
void main(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    Bucket bucket = Buckets[FirstBucket + group.x];
    float4 cell[3] = { bucket.Row0, bucket.Row1, bucket.Row2 };
    float4 previousCell[3] = { bucket.PreviousRow0, bucket.PreviousRow1, bucket.PreviousRow2 };
    for (uint instance = lane; instance < bucket.BlockCount; instance += 64)
    {
        uint record = bucket.RecordOffset + instance * bucket.Stride;
        // R: the instance's column-vector 3x4 transform (implicit last row 0, 0, 0, 1).
        float4 r[3];
        float3 keyColor;  // colorMaskHSV: the paint the vertex shader passes on (key_color)
        float2 textureOffset = 0;
        uint patternBytes = 0;
        if (bucket.Stride == 64)
        {
            keyColor = asfloat(Records.Load3(record + 48));
            // Deformed armor shifts its UVs by a pattern offset kept in the w
            // bytes of bones 5-7 (VertexTemplateBase.hlsli, USE_DEFORMED_CUBE_INSTANCING).
            uint2 bones = Records.Load2(record + 20);  // bytes 20-27: bone 5, bone 6
            uint bone7 = Records.Load(record + 28);
            uint w5 = bones.x >> 24, w6 = bones.y >> 24, w7 = bone7 >> 24;
            textureOffset = float2(w5 / (1.0 + ((w7 >> 4) & 0xF)), w6 / (1.0 + (w7 & 0xF)));
            patternBytes = w5 | (w6 << 8) | (w7 << 16);
            float4 translationRotation = asfloat(Records.Load4(record + 32));
            uint orientation = (uint)translationRotation.w;
            float3 forward = kDirections[min(orientation / 6, 5)];
            float3 up = kDirections[orientation % 6];
            float3 right = cross(up, -forward);
            r[0] = float4(right.x, up.x, -forward.x, translationRotation.x);
            r[1] = float4(right.y, up.y, -forward.y, translationRotation.y);
            r[2] = float4(right.z, up.z, -forward.z, translationRotation.z);
        }
        else
        {
            r[0] = HalfRow(record);
            r[1] = HalfRow(record + 8);
            r[2] = HalfRow(record + 16);
            keyColor = HalfRow(record + 24).xyz;
        }
        uint index = FirstInstance + bucket.OutputOffset + instance;
        InstanceData.Store4(index * 32, asuint(float4(keyColor, 0)));
        uint source = (bucket.Stride == 64 ? 1 : 2) | ((record / 32) << 2);
        InstanceData.Store4(index * 32 + 16, uint4(asuint(textureOffset), patternBytes, source));
        uint address = index * 64;
        [unroll]
        for (uint i = 0; i < 3; ++i)
        {
            float4 c = cell[i];
            float4 row = c.x * r[0] + c.y * r[1] + c.z * r[2] + float4(0, 0, 0, c.w);
            Instances.Store4(address + i * 16, asuint(row));
            float4 p = previousCell[i];
            float4 previousRow = p.x * r[0] + p.y * r[1] + p.z * r[2] + float4(0, 0, 0, p.w);
            PreviousTransforms.Store4(index * 48 + i * 16, asuint(previousRow));
        }
        Instances.Store4(address + 48, uint4(bucket.InstanceIdMask, bucket.Flags, bucket.Blas.x, bucket.Blas.y));
    }
}

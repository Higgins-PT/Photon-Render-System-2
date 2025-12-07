#ifndef PHOTON_GEOMETRY_HLSL
#define PHOTON_GEOMETRY_HLSL



struct PhotonMeshSlot
{
    uint vertexBase;
    uint vertexCount;
    uint triangleBase;
    uint triangleCount;
};

struct PhotonInstanceData
{
    float4 row0;
    float4 row1;
    float4 row2;
    float4 row3;
    float4 normalRow0;
    float4 normalRow1;
    float4 normalRow2;
};

struct PhotonInstanceLookup
{
    int slotToken;
    uint subMeshTriangleOffset;
    uint padding0;
    uint padding1;
};

StructuredBuffer<float3> _PhotonStaticNormals;
StructuredBuffer<float2> _PhotonStaticUVs;
StructuredBuffer<uint3> _PhotonStaticIndices;

StructuredBuffer<float3> _PhotonDynamicNormals;
StructuredBuffer<float2> _PhotonDynamicUVs;
StructuredBuffer<uint3> _PhotonDynamicIndices;

StructuredBuffer<PhotonInstanceLookup> _PhotonInstanceLookup;
StructuredBuffer<PhotonMeshSlot> _PhotonStaticSlots;
StructuredBuffer<PhotonMeshSlot> _PhotonDynamicSlots;
StructuredBuffer<PhotonInstanceData> _PhotonInstanceData;

inline PhotonInstanceData GetPhotonInstance(uint instanceIndex)
{
    return _PhotonInstanceData[instanceIndex];
}

inline float4x4 GetInstanceMatrix(PhotonInstanceData data)
{
    return float4x4(data.row0, data.row1, data.row2, data.row3);
}

inline PhotonMeshSlot DecodeMeshSlot(int slotToken, out bool isDynamic)
{
    PhotonMeshSlot slot;
    int slotIndex = abs(slotToken) - 1;
    if (slotToken < 0)
    {
        isDynamic = true;
        slot = _PhotonDynamicSlots[slotIndex];
    }
    else
    {
        isDynamic = false;
        slot = _PhotonStaticSlots[slotIndex];
    }
    return slot;
}

inline PhotonInstanceLookup GetInstanceLookup(uint instanceIndex)
{
    return _PhotonInstanceLookup[instanceIndex];
}

inline float3 LoadNormal(bool isDynamic, uint index)
{
    return isDynamic ? _PhotonDynamicNormals[index] : _PhotonStaticNormals[index];
}

inline float2 LoadUv(bool isDynamic, uint index)
{
    return isDynamic ? _PhotonDynamicUVs[index] : _PhotonStaticUVs[index];
}

inline uint3 LoadTriangle(bool isDynamic, uint index)
{
    return isDynamic ? _PhotonDynamicIndices[index] : _PhotonStaticIndices[index];
}

inline float3 SampleInterpolatedNormal(
    uint instanceIndex,
    uint primitiveIndex,
    float2 barycentrics,
    out float2 uvOut,
    out float4x4 instanceMatrix,
    out float3x3 normalMatrix)
{
    PhotonInstanceData inst = GetPhotonInstance(instanceIndex);
    instanceMatrix = GetInstanceMatrix(inst);
    normalMatrix = float3x3(inst.normalRow0.xyz, inst.normalRow1.xyz, inst.normalRow2.xyz);
    PhotonInstanceLookup lookup = GetInstanceLookup(instanceIndex);
    int slotToken = lookup.slotToken;
    bool isDynamic;
    PhotonMeshSlot slot = DecodeMeshSlot(slotToken, isDynamic);

    uint triIndex = slot.triangleBase + lookup.subMeshTriangleOffset + primitiveIndex;
    uint3 tri = LoadTriangle(isDynamic, triIndex) + uint3(slot.vertexBase, slot.vertexBase, slot.vertexBase);

    float3 n0 = LoadNormal(isDynamic, tri.x);
    float3 n1 = LoadNormal(isDynamic, tri.y);
    float3 n2 = LoadNormal(isDynamic, tri.z);

    float2 uv0 = LoadUv(isDynamic, tri.x);
    float2 uv1 = LoadUv(isDynamic, tri.y);
    float2 uv2 = LoadUv(isDynamic, tri.z);

    float b1 = saturate(barycentrics.x);
    float b2 = saturate(barycentrics.y);
    float b0 = saturate(1.0 - b1 - b2);
    float3 localNormal = normalize(n0 * b0 + n1 * b1 + n2 * b2);
    uvOut = uv0 * b0 + uv1 * b1 + uv2 * b2;
    return localNormal;
}

inline float3 TransformNormal(float3 localNormal, float3x3 normalMatrix)
{
    return normalize(mul(normalMatrix, localNormal));
}

#endif // PHOTON_GEOMETRY_HLSL


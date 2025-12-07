#ifndef PROBE_SAMPLING_HLSL
#define PROBE_SAMPLING_HLSL

#include "SH_Lite.hlsl"

struct ProbePosition
{
    float3 position;
    float  padding;
};

StructuredBuffer<ProbePosition> g_ProbePositions;
StructuredBuffer<L2> g_ProbeSHCoefficients;

cbuffer ProbeMetadata
{
    uint  g_ProbeCount;
    uint  g_ProbesPerAxis;
    uint  g_CascadeCount;
    float g_SmallestCellSize;
    float3 g_ProbeCameraPosition;
    float  g_ProbePadding0;
};

uint GetProbeIndex(uint cascadeIndex, uint x, uint y, uint z)
{
    uint axis = max(1u, g_ProbesPerAxis);
    uint axisSq = axis * axis;
    uint probesPerCascade = axis * axisSq;
    return cascadeIndex * probesPerCascade + x * axisSq + y * axis + z;
}

bool WorldToCascadeCoords(float3 worldPos, out uint cascadeIndex, out float3 cellCoords)
{
    cascadeIndex = 0;
    cellCoords = 0;

    if (g_ProbeCount == 0 || g_ProbesPerAxis == 0 || g_CascadeCount == 0)
        return false;

    float3 offset = worldPos - g_ProbeCameraPosition;
    uint selectedCascade = g_CascadeCount - 1;

    [loop]
    for (uint c = 0; c < g_CascadeCount; ++c)
    {
        float cellSize = g_SmallestCellSize * exp2((float)c);
        float halfExtent = (max(1u, g_ProbesPerAxis) - 1u) * 0.5f * cellSize;
        float3 extent = float3(halfExtent, halfExtent, halfExtent);
        if (all(abs(offset) <= extent + cellSize))
        {
            selectedCascade = c;
            break;
        }
    }

    float selectedCellSize = g_SmallestCellSize * exp2((float)selectedCascade);
    float halfExtentSel = (max(1u, g_ProbesPerAxis) - 1u) * 0.5f * selectedCellSize;
    float3 normalized = (offset + halfExtentSel) / selectedCellSize;

    float maxCoord = max(1u, g_ProbesPerAxis) - 1u;
    cellCoords = clamp(normalized, 0.0f, (float)maxCoord);
    cascadeIndex = selectedCascade;
    return true;
}

bool SampleInterpolatedProbeSH(float3 worldPos, out L2 interpolatedSH)
{
    interpolatedSH = L2::Zero();
    uint cascadeIndex;
    float3 cellCoords;
    if (!WorldToCascadeCoords(worldPos, cascadeIndex, cellCoords))
        return false;

    uint axis = max(1u, g_ProbesPerAxis);
    float3 clamped = clamp(cellCoords, 0.0f, (float)(axis - 1u));
    uint3 minIdx = (uint3)clamped;
    uint3 maxIdx = min(minIdx + 1, axis - 1u);
    float3 frac = clamped - (float3)minIdx;
    float3 oneMinusFrac = 1.0f - frac;

    float totalWeight = 0.0f;
    L2 accum = L2::Zero();

    [unroll]
    for (uint ix = 0; ix < 2; ++ix)
    {
        uint px = ix == 0 ? minIdx.x : maxIdx.x;
        float wx = ix == 0 ? oneMinusFrac.x : frac.x;
        if (wx <= 0.0f)
            continue;

        [unroll]
        for (uint iy = 0; iy < 2; ++iy)
        {
            uint py = iy == 0 ? minIdx.y : maxIdx.y;
            float wy = iy == 0 ? oneMinusFrac.y : frac.y;
            if (wy <= 0.0f)
                continue;

            [unroll]
            for (uint iz = 0; iz < 2; ++iz)
            {
                uint pz = iz == 0 ? minIdx.z : maxIdx.z;
                float wz = iz == 0 ? oneMinusFrac.z : frac.z;
                float weight = wx * wy * wz;
                if (weight <= 0.0f)
                    continue;

                uint probeIndex = GetProbeIndex(cascadeIndex, px, py, pz);
                if (probeIndex >= g_ProbeCount)
                    continue;

                L2 sample = g_ProbeSHCoefficients[probeIndex];
                accum = Add(accum, Multiply(sample, weight));
                totalWeight += weight;
            }
        }
    }

    if (totalWeight <= 0.0f)
        return false;

    interpolatedSH = Multiply(accum, 1.0f / totalWeight);
    return true;
}

float3 SampleUniformSphere(float2 xi)
{
    float z = 1.0f - 2.0f * xi.x;
    float r = sqrt(saturate(1.0f - z * z));
    float phi = 6.28318530f * xi.y;
    float x = r * cos(phi);
    float y = r * sin(phi);
    return float3(x, y, z);
}

bool SampleDirectionFromProbeSH(
    float3 worldPos,
    float3 surfaceNormal,
    uint baseSeed,
    out float3 sampledDir,
    out float pdf)
{
    sampledDir = surfaceNormal;
    pdf = 0.0f;

    L2 interpolatedSH;
    if (!SampleInterpolatedProbeSH(worldPos, interpolatedSH))
        return false;

    const uint maxAttempts = 128u;
    for (uint attempt = 0u; attempt < maxAttempts; ++attempt)
    {
        uint seed = Hash(baseSeed + attempt * 1664525u);
        float u1 = RandomValue(seed);
        float u2 = RandomValue(seed + 1u);
        float3 dir = SampleUniformSphere(float2(u1, u2));
        if (dot(dir, surfaceNormal) < 0.0f)
            dir = -dir;

        float value = max(0.01f, Evaluate(interpolatedSH, dir));
        if (value <= 0.0f)
            continue;

        float accept = RandomValue(seed + 2u);
        if (accept <= value)
        {
            sampledDir = dir;
            pdf = value;
            return true;
        }
    }

    return false;
}

#endif // PROBE_SAMPLING_HLSL


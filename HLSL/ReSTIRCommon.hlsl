#ifndef RESTIR_COMMON_HLSL
#define RESTIR_COMMON_HLSL

static const float3 RESTIR_LUMINANCE = float3(0.2126f, 0.7152f, 0.0722f);

struct ReSTIRCandidate
{
    float3 radiance;
    float  weight;
    float  depth;
    float3 normal;
    float  pdf;
    float  padding0;
    float2 padding1;
};

struct ReSTIRReservoir
{
    float3 radiance;
    float  weight;
    float  depth;
    float3 normal;
    float  pdf;
    float  weightSum;
    uint   m;
    float  padding;
};

uint Hash(uint state)
{
    state ^= 2747636419u;
    state *= 2654435769u;
    state ^= state >> 16;
    state *= 2654435769u;
    state ^= state >> 16;
    state *= 2654435769u;
    return state;
}

float RandomValue(uint state)
{
    return (Hash(state) & 0x00FFFFFFu) / 16777215.0f;
}

float ComputeCandidateWeight(float3 radiance, float pdf)
{
    float luminance = dot(radiance, RESTIR_LUMINANCE);
    return luminance / max(pdf, 1.0e-4f);
}

float3 NormalizeSafe(float3 value, float3 fallback)
{
    float lenSq = dot(value, value);
    if (lenSq <= 1.0e-6f)
        return fallback;
    return value * rsqrt(lenSq);
}

ReSTIRReservoir CreateReservoirFromCandidate(ReSTIRCandidate candidate)
{
    ReSTIRReservoir reservoir;
    reservoir.radiance = candidate.radiance;
    reservoir.weight = candidate.weight;
    reservoir.depth = candidate.depth;
    reservoir.normal = NormalizeSafe(candidate.normal, float3(0, 1, 0));
    reservoir.pdf = max(candidate.pdf, 1.0e-4f);
    reservoir.weightSum = candidate.weight;
    reservoir.m = 1u;
    reservoir.padding = 0.0f;
    return reservoir;
}

bool ReservoirsCompatible(ReSTIRReservoir a, ReSTIRReservoir b, float depthThreshold, float normalThreshold)
{
    float depthDiff = abs(a.depth - b.depth);
    if (depthDiff > depthThreshold)
        return false;
    float normalDot = dot(a.normal, b.normal);
    return normalDot >= normalThreshold;
}

void MergeReservoirWithCandidate(
    inout ReSTIRReservoir reservoir,
    ReSTIRReservoir candidate,
    float candidateWeight,
    uint randomSeed)
{
    candidateWeight = max(candidateWeight, 0.0f);
    float totalWeight = reservoir.weightSum + candidateWeight;
    if (totalWeight <= 0.0f)
        return;

    float acceptProbability = candidateWeight / totalWeight;
    float randValue = RandomValue(randomSeed);
    if (randValue < acceptProbability)
    {
        reservoir.radiance = candidate.radiance;
        reservoir.weight = candidate.weight;
        reservoir.depth = candidate.depth;
        reservoir.normal = candidate.normal;
        reservoir.pdf = candidate.pdf;
    }

    reservoir.weightSum = totalWeight;
    reservoir.m = max(reservoir.m + candidate.m, 1u);
}

#endif // RESTIR_COMMON_HLSL



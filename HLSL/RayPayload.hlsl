#ifndef PHOTON_RAY_PAYLOAD_HLSL
#define PHOTON_RAY_PAYLOAD_HLSL

struct RayPayload
{
    float3 baseColor;
    float3 emission;
    float3 throughput;
    float3 hitPosition;
    float3 hitNormal;
    float3 rayDir;
    float3 specularColor;
    float  metallic;
    float  smoothness;
    float  anisotropy;
    float  transparency;
    float  ior;
    int    hit;
    int    mainLight;
    int    remainingDepth;
};

#endif // PHOTON_RAY_PAYLOAD_HLSL



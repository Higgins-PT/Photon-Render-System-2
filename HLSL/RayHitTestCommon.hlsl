#ifndef RAY_HIT_TEST_COMMON_INCLUDED
#define RAY_HIT_TEST_COMMON_INCLUDED

#define PI 3.14159265f

inline float DistributionGGX(float3 N, float3 halfDir, float r)
{
    float a = r * r;
    float a2 = a * a;
    float NdotH = saturate(dot(N, halfDir));
    float denom = NdotH * NdotH * (a2 - 1.0f) + 1.0f;
    denom = PI * denom * denom + 1e-5f;
    return a2 / denom;
}

inline float GeometrySchlickGGX(float NdotX, float r)
{
    float k = ((r + 1.0f) * (r + 1.0f)) * 0.125f;
    return NdotX / (NdotX * (1.0f - k) + k);
}

inline float GeometrySmith(float3 N, float3 V, float3 L, float r)
{
    float ggx1 = GeometrySchlickGGX(saturate(dot(N, V)), r);
    float ggx2 = GeometrySchlickGGX(saturate(dot(N, L)), r);
    return ggx1 * ggx2;
}

inline float3 FresnelSchlick(float cosTheta, float3 F0)
{
    return F0 + (1.0f - F0) * pow(1.0f - cosTheta, 5.0f);
}

inline float3 EvaluatePBR(
    float3 normalDir,
    float3 viewDir,
    float3 lightDir,
    float3 baseColor,
    float metallic,
    float roughness,
    float3 lightColor,
    float3 emissionColor
)
{
    float NdotL = saturate(dot(normalDir, lightDir));
    float NdotV = saturate(dot(normalDir, viewDir));
    if (NdotL <= 0.0 || NdotV <= 0.0)
    {
        return saturate(emissionColor);
    }

    float3 H = normalize(viewDir + lightDir);

    float D = DistributionGGX(normalDir, H, roughness);
    float G = GeometrySmith(normalDir, viewDir, lightDir, roughness);
    float3 F0 = lerp(float3(0.04f, 0.04f, 0.04f), baseColor, metallic);
    float3 F = FresnelSchlick(saturate(dot(H, viewDir)), F0);
    float3 specular = (D * G * F) / max(4.0f * NdotV * NdotL, 1e-4f);

    float3 kS = F;
    float3 kD = (1.0f - kS) * (1.0f - metallic);
    float3 diffuse = (kD * baseColor / PI);

    float3 color = (diffuse + specular) * lightColor * NdotL + emissionColor;
    return saturate(color);
}

#endif // RAY_HIT_TEST_COMMON_INCLUDED


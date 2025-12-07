
#ifndef BRDF_SAMPLER_HLSL
#define BRDF_SAMPLER_HLSL
#define PI 3.14159265
// ──────────────────────────────────────────────────────────────
// Math helpers
// ──────────────────────────────────────────────────────────────
float3 ToWorld(float3 v, float3 N)
{
    float3 B, T;
    if (abs(N.z) < 0.999f)
    {
        B = normalize(cross(float3(0, 0, 1), N));
    }
    else
    {
        B = normalize(cross(float3(0, 1, 0), N));
    }
    T = cross(B, N);
    return v.x * T + v.y * B + v.z * N;
}

float3 CosineSampleHemisphere(float2 u)
{
    float phi = 2.0 * PI * u.x;
    float cosT = sqrt(1.0 - u.y);
    float sinT = sqrt(u.y);
    return float3(cos(phi) * sinT, sin(phi) * sinT, cosT);
}
// Sample a direction within a cone around a center direction
// centerDir: normalized center direction
// coneAngle: half-angle of the cone in radians
// u: random values in [0,1]
float3 SampleCone(float3 centerDir, float coneAngle, float2 u)
{
    // Build orthonormal basis around centerDir
    float3 up = abs(centerDir.y) < 0.999f ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 right = normalize(cross(up, centerDir));
    float3 forward = normalize(cross(centerDir, right));
    
    // Sample angle from center (uniform in solid angle)
    // cosTheta ranges from cos(coneAngle) to 1.0
    float cosTheta = lerp(cos(coneAngle), 1.0f, u.y);
    float sinTheta = sqrt(max(0.0f, 1.0f - cosTheta * cosTheta));
    
    // Sample azimuth angle uniformly
    float phi = 2.0f * PI * u.x;
    
    // Construct direction in local space (spherical coordinates)
    // x = sinTheta * cos(phi) along right
    // y = sinTheta * sin(phi) along forward
    // z = cosTheta along centerDir
    float3 localDir = float3(
        sinTheta * cos(phi),
        sinTheta * sin(phi),
        cosTheta
    );
    
    // Transform to world space
    return normalize(localDir.x * right + localDir.y * forward + localDir.z * centerDir);
}

float2 ConcentricSampleDisk(float2 u)
{
    float2 offset = 2.0f * u - 1.0f;
    if (offset.x == 0.0f && offset.y == 0.0f)
    {
        return float2(0.0f, 0.0f);
    }

    float r;
    float theta;
    if (abs(offset.x) > abs(offset.y))
    {
        r = offset.x;
        theta = (PI * 0.25f) * (offset.y / offset.x);
    }
    else
    {
        r = offset.y;
        theta = (PI * 0.5f) - (PI * 0.25f) * (offset.x / offset.y);
    }

    return float2(r * cos(theta), r * sin(theta));
}

float3 SampleEllipticalCone(float3 centerDir, float2 coneAngles, float2 u)
{
    float3 up = abs(centerDir.y) < 0.999f ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 right = normalize(cross(up, centerDir));
    float3 forward = normalize(cross(centerDir, right));

    float2 disk = ConcentricSampleDisk(u);
    float slopeX = tan(coneAngles.x);
    float slopeY = tan(coneAngles.y);
    float3 localDir = float3(disk.x * slopeX, disk.y * slopeY, 1.0f);

    return normalize(localDir.x * right + localDir.y * forward + localDir.z * centerDir);
}

float2 ComputeAnisotropicConeAngles(float coneAngle, float anisotropy)
{
    float clampedAniso = clamp(anisotropy, -1.0f, 1.0f);
    float minorScale = 1.0f + clampedAniso;
    float majorScale = 1.0f - clampedAniso;
    float maxScale = 2.0f - 1.0e-4f;
    minorScale = clamp(minorScale, 0.0f, maxScale);
    majorScale = clamp(majorScale, 0.0f, maxScale);
    float2 angles = float2(coneAngle * majorScale, coneAngle * minorScale);
    float maxAngle = radians(180.0f);
    return clamp(angles, float2(0.0f, 0.0f), float2(maxAngle, maxAngle));
}

float ComputeEllipticalConePdf(float2 coneAngles, float eps)
{
    float cosX = cos(coneAngles.x);
    float cosY = cos(coneAngles.y);
    float cosEquivalent = clamp(0.5f * (cosX + cosY), -0.9999f, 0.9999f);
    float solidAngle = max(2.0f * PI * (1.0f - cosEquivalent), eps);
    return 1.0f / solidAngle;
}
float ComputeLuminanceBRDF(float3 color)
{
    float3 luminanceCoeff = float3(0.2126, 0.7152, 0.0722);
    float luminance = dot(color, luminanceCoeff);
    return luminance;
}
static const float PHOTON_DEFAULT_IOR = 1.5f;
float3 NormalizeToUnitRadiance(float3 inColor)
{
    float luminance = ComputeLuminanceBRDF(inColor);

    if (luminance <= 0.0)
    {
        return float3(0.0, 0.0, 0.0);
    }

    float invLum = 1.0 / luminance;

    return inColor * invLum;
}
// Smith‑Schlick GGX  (single direction)
float SmithG1_GGX(float NdotV, float alpha)
{
    float a2 = alpha * alpha;
    float b = sqrt(a2 + (1.0 - a2) * NdotV * NdotV);
    return 2.0 * NdotV / (NdotV + b);
}

// Heitz 2018 – VNDF hemispherical GGX sampling
float3 SampleGGXVNDF(float3 V, float3 N, float alpha, float2 u)
{
    // stretch view
    float3 Vh = normalize(float3(alpha * V.x, alpha * V.y, V.z));
    // orthonormal basis
    float lensq = Vh.x * Vh.x + Vh.y * Vh.y;
    float3 T1 = lensq > 0 ? float3(-Vh.y, Vh.x, 0) / sqrt(lensq) : float3(1, 0, 0);
    float3 T2 = cross(Vh, T1);
    // sample polar coords
    float r = sqrt(u.x);
    float phi = 2.0 * PI * u.y;
    float t1 = r * cos(phi);
    float t2 = r * sin(phi);
    float s = 0.5 * (1.0 + Vh.z);
    t2 = (1.0 - s) * sqrt(1.0 - t1 * t1) + s * t2;
    // transform
    float3 Nh = t1 * T1 + t2 * T2 + sqrt(max(0.0, 1.0 - t1 * t1 - t2 * t2)) * Vh;
    // unstretch
    float3 H = normalize(float3(alpha * Nh.x, alpha * Nh.y, max(0.0, Nh.z)));
    return H;
}

// NDF: Trowbridge‑Reitz GGX
float D_GGX(float NdotH, float alpha)
{
    float a2 = alpha * alpha;
    float d = (NdotH * NdotH) * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

void BuildAnisotropyBasis(float3 N, float3 V, out float3 T, out float3 B)
{
    float3 up = abs(N.z) < 0.999f ? float3(0, 0, 1) : float3(0, 1, 0);
    T = normalize(cross(up, N));
    float3 viewProjected = V - N * dot(V, N);
    float viewLenSq = dot(viewProjected, viewProjected);
    if (viewLenSq > 1.0e-4f)
    {
        T = normalize(viewProjected);
    }
    B = normalize(cross(N, T));
}

float D_GGX_Aniso(float3 H, float3 N, float3 T, float3 B, float alphaX, float alphaY)
{
    float3 Ht = float3(dot(H, T), dot(H, B), max(dot(H, N), 1.0e-4f));
    float invAlphaX = 1.0f / max(alphaX, 1.0e-4f);
    float invAlphaY = 1.0f / max(alphaY, 1.0e-4f);
    float denominator = Ht.x * Ht.x * invAlphaX * invAlphaX +
                        Ht.y * Ht.y * invAlphaY * invAlphaY +
                        Ht.z * Ht.z;
    float normalization = PI * max(alphaX * alphaY, 1.0e-4f);
    return 1.0f / (normalization * denominator * denominator);
}

float Lambda_GGX_Aniso(float3 W, float3 N, float3 T, float3 B, float alphaX, float alphaY)
{
    float3 Wt = float3(dot(W, T), dot(W, B), max(dot(W, N), 1.0e-4f));
    float absWtZ = max(abs(Wt.z), 1.0e-4f);
    float alpha = sqrt(Wt.x * Wt.x * alphaX * alphaX + Wt.y * Wt.y * alphaY * alphaY);
    float a = alpha / absWtZ;
    return (-1.0f + sqrt(1.0f + a * a)) * 0.5f;
}

float SmithG_GGX_Aniso(float3 V, float3 L, float3 N, float3 T, float3 B, float alphaX, float alphaY)
{
    float lambdaV = Lambda_GGX_Aniso(V, N, T, B, alphaX, alphaY);
    float lambdaL = Lambda_GGX_Aniso(L, N, T, B, alphaX, alphaY);
    return 1.0f / max(1.0f + lambdaV + lambdaL, 1.0e-3f);
}

// Fresnel Schlick
float3 FresnelSchlick(float3 F0, float HdotV)
{
    return F0 + (1.0 - F0) * pow(1.0 - HdotV, 5.0);
}

// Cook‑Torrance BRDF

float3 CookTorranceBRDF(
    float3 N, float3 V, float3 L,
    float3 baseColor, float metallic, float3 specularColor, float roughness, float anisotropy)
{
    float3 H = normalize(V + L);
    float NdotL = max(dot(N, L), 0.0);
    float NdotV = max(dot(N, V), 0.0);
    float NdotH = max(dot(N, H), 0.0);
    float VdotH = max(dot(V, H), 0.0);
    float3 metallicColor = clamp(metallic, 0, 0.995);
    roughness = clamp(roughness, 0.05, 1);
    float3 F0 = lerp(0.04f, specularColor, metallic.xxx);
    float3 F = FresnelSchlick(F0, VdotH);

    float alpha = roughness * roughness;
    float clampedAniso = clamp(anisotropy, -1.0f, 1.0f);
    float aspect = sqrt(saturate(1.0f - 0.9f * abs(clampedAniso)));
    aspect = max(aspect, 1.0e-2f);
    float alphaX = max(0.0025f, alpha / aspect);
    float alphaY = max(0.0025f, alpha * aspect);
    if (clampedAniso < 0.0f)
    {
        float tmp = alphaX;
        alphaX = alphaY;
        alphaY = tmp;
    }

    float3 T, B;
    BuildAnisotropyBasis(N, V, T, B);

    float D = D_GGX_Aniso(H, N, T, B, alphaX, alphaY);
    float G = SmithG_GGX_Aniso(V, L, N, T, B, alphaX, alphaY);

    float3 spec = (D * G * F) / max(4.0 * NdotV * NdotL, 1e-1);
    float3 diff = ((1.0 - metallicColor.xyz) * baseColor / PI) * NdotL;

    return (diff + spec);
}
// ──────────────────────────────────────────────────────────────
// BRDF importance sampler
// ──────────────────────────────────────────────────────────────
// Inputs:
//   N, V           ‑ shading normal & view dir (both normalized, V points OUT of surface)
//   baseColor      ‑ albedo in linear space [0,1]
//   metallic       ‑ [0,1]
//   roughness      ‑ perceptual roughness [0,1]
//   transparency   ‑ [0,1] controls transmission weight
//   ior            ‑ index of refraction for transmission
//   randBrdf       ‑ float4 random set (xyz for BRDF sampling, w for transmission choice)
//   randTransmission ‑ float2 random set for transmission cone jittering
// Outputs (by reference):
//   L              ‑ sampled light direction, normalized
//   f              ‑ BRDF value for that (N,V,L)
//   pdf            ‑ combined pdf(N,V,L)
//   isTransmission ‑ whether the sampled event was a transmission
// Returns: true if sample is valid (N·L > 0)
bool SampleBRDF(
    float3 N, float3 V,
    float3 baseColor, float metallic, float3 specularColor, float roughness, float anisotropy,
    float transparency, float ior,
    float4 randBrdf, float2 randTransmission,
    out float3 L, out float3 f, out float pdf, out bool isTransmission)
{
    float3 normalizedSpecularColor = saturate(NormalizeToUnitRadiance(specularColor));

    float eps = 1.0e-2f;
    metallic = clamp(metallic, 0, 1);
    roughness = clamp(roughness, 0.0, 1.0);
    float anisotropyParam = clamp(anisotropy, -1.0f, 1.0f);
    float3 viewDir = NormalizeSafe(V, N);
    float smoothness = clamp(1.0f - roughness, 0.001f, 1.0f);
    float3 incidentDir = -viewDir;
    float cosTheta = saturate(dot(N, viewDir));

    isTransmission = false;

    // Transmission branch (handled before reflection sampling)
    float transparentAmount = saturate(transparency);
    float transmissionProb = 0.0f;
    if (transparentAmount > 1.0e-3f)
    {
        transmissionProb = transparentAmount;
    }

    if (transmissionProb > 1.0e-4f && randBrdf.w < transmissionProb)
    {
        float materialIor = ior > 0.0f ? ior : PHOTON_DEFAULT_IOR;
        bool entering = cosTheta > 0.0f;
        float eta = entering ? (1.0f / materialIor) : materialIor;
        //float eta = 1 / materialIor;
        float3 refractNormal = entering ? N : -N;
        float3 refractedDir = refract(incidentDir, refractNormal, eta);

        float baseConeAngle = radians(90.0f) * (1.0f - smoothness);
        float2 coneAngles = ComputeAnisotropicConeAngles(baseConeAngle, anisotropyParam);
        float3 jitteredDir = SampleEllipticalCone(normalize(refractedDir), coneAngles, randTransmission);
        L = normalize(jitteredDir);

        float basePdf = ComputeEllipticalConePdf(coneAngles, eps);
        if (max(coneAngles.x, coneAngles.y) >= radians(90.0f))
        {
            basePdf *= 2.0f;
        }

        pdf = 1;
        f = specularColor;
        isTransmission = true;
        return true;
    }

    float remainingProb = max(1.0f - transmissionProb, eps);

    // Mirror reflection for smooth surfaces (smoothness >= 0)
    // At 0.0: 180 degree cone spread
    // At 1.0: perfect mirror (0 degree spread)
    float3 reflectNormal = N;
    if (dot(N, incidentDir) > 0.0f)
    {
        reflectNormal = -N;
    }
    float3 perfectReflect = reflect(incidentDir, reflectNormal);

    float baseReflectionCone = radians(90.0f) * (1.0f - smoothness);
    float2 reflectionConeAngles = ComputeAnisotropicConeAngles(baseReflectionCone, anisotropyParam);
    L = SampleEllipticalCone(perfectReflect, reflectionConeAngles, float2(randBrdf.x, randBrdf.y));
    float3 F0 = lerp(0.04f, specularColor, metallic.xxx);
    F0 = max(F0, 0.04f);
    float3 H = normalize(V + L);
    float VdotH = max(dot(V, H), 0.0);
    float F = ComputeLuminanceBRDF(FresnelSchlick(F0, VdotH));
    float mirrorProb = saturate(F);
    float uMirror = randBrdf.z;
    if (mirrorProb > 0.0f && uMirror < mirrorProb)
    {

        bool wasFlipped = false;
        if (dot(reflectNormal, L) < 0.0f)
        {
            L = normalize(L - 2.0f * dot(L, reflectNormal) * reflectNormal);
            wasFlipped = true;
        }

        if (dot(reflectNormal, L) <= 0.0f)
        {
            f = 0;
            pdf = 0;
            return false;
        }

        float basePdf = ComputeEllipticalConePdf(reflectionConeAngles, eps);
        if (wasFlipped && max(reflectionConeAngles.x, reflectionConeAngles.y) >= radians(90.0f))
        {
            basePdf *= 2.0f;
        }
        basePdf = saturate(basePdf);
        pdf = max(remainingProb * basePdf, eps);
        f = normalizedSpecularColor;
        return true;
    }

    float residual = max(1.0f - mirrorProb, eps);

    float pDiffuse = saturate(1.0f - metallic);
    float pSpec = 1.0f - pDiffuse;
    pDiffuse = saturate(pDiffuse / residual);
    pSpec = saturate(pSpec / residual);
    float renorm = max(pDiffuse + pSpec, eps);
    pDiffuse /= renorm;
    pDiffuse = 1;
    pSpec = 1.0f - pDiffuse;

    float u1 = randBrdf.x;
    float u2 = randBrdf.y;
    bool chooseDiff = (u1 < pDiffuse);
    float xi1 = chooseDiff ? (u1 / max(pDiffuse, eps)) : ((u1 - pDiffuse) / max(1.0f - pDiffuse, eps));
    float2 rand2 = float2(xi1, u2);

    float pdf_diff = 0.0f, pdf_spec = 0.0f;

    if (chooseDiff)
    {
        float3 localDir = CosineSampleHemisphere(rand2);
        L = normalize(ToWorld(localDir, N));
        float NdotL = max(dot(N, L), 0.0f);
        pdf_diff = NdotL / PI;
    }
    else
    {
        float alpha = max(roughness * roughness, 0.0025f);
        float3 H = SampleGGXVNDF(viewDir, N, alpha, rand2);
        L = reflect(-viewDir, H);
        if (dot(N, L) <= 0.0f)
        {
            f = 0;
            pdf = 0;
            return false;
        }
        float NdotH = max(dot(N, H), 0.0f);
        float VdotH = max(dot(viewDir, H), 0.0f);
        pdf_spec = D_GGX(NdotH, alpha) * NdotH / max(4.0f * VdotH, eps);
    }

    float pdfMix = pDiffuse * pdf_diff + pSpec * pdf_spec;
    pdf = max(remainingProb * residual * pdfMix, eps);
    if (pdf <= eps)
    {
        f = 0;
        return false;
    }

    f = CookTorranceBRDF(N, viewDir, L, baseColor, metallic, specularColor, roughness, anisotropyParam);
    return true;
}
// ──────────────────────────────────────────────────────────────

float EvaluateBRDFPdf(
    float3 N, float3 V, float3 L,
    float metallic,
    float roughness)
{
    float NdotL = dot(N, L);
    if (NdotL <= 0.0)
        return 0.0;

   
    float pdf_diff = NdotL / PI; 

    float3 H = normalize(V + L);
    float NdotH = max(dot(N, H), 0.0);
    float VdotH = max(dot(V, H), 0.0);
    float alpha = roughness * roughness;

    float a2 = alpha * alpha;
    float D = a2 / (PI * pow((NdotH * NdotH) * (a2 - 1.0) + 1.0, 2.0));

    float pdf_spec = D * NdotH / max(4.0 * VdotH, 1e-6);

    float pDiffuse = saturate(1.0 - metallic); 
    float pdf_mix = pDiffuse * pdf_diff 
                    + (1.0 - pDiffuse) * pdf_spec;

    return pdf_mix;
}
#endif 

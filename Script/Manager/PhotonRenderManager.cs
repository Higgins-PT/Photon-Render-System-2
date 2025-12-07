using PhotonSystem;
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace PhotonGISystem2
{
    /// <summary>
    /// Central rendering manager that orchestrates the overall rendering pipeline per camera.
    /// Coordinates probe management and ray tracing dispatch.
    /// </summary>
    public class PhotonRenderManager : PGSingleton<PhotonRenderManager>
    {
        #region Serialized Fields

        [Serializable]
        private struct MainLightSettings
        {
            public bool enabled;
            public Light overrideLight;
            [Range(0.01f, 10f)] public float angularDiameter;
            [Min(0)] public int transparentIterations;
            [Min(0f)] public float directLightMultiplier;

            public static MainLightSettings Default => new MainLightSettings
            {
                enabled = true,
                angularDiameter = 0.53f,
                transparentIterations = 4,
                directLightMultiplier = 1.0f
            };

            public void Clamp()
            {
                angularDiameter = Mathf.Clamp(angularDiameter, 0.01f, 10f);
                transparentIterations = Mathf.Max(0, transparentIterations);
                directLightMultiplier = Mathf.Max(0f, directLightMultiplier);
            }
        }

        [Serializable]
        public struct MainLightConfig
        {
            public bool enabled;
            [Range(0.01f, 10f)] public float angularDiameter;
            [Min(0)] public int transparentIterations;
            [Min(0f)] public float directLightMultiplier;

            public void Clamp()
            {
                angularDiameter = Mathf.Clamp(angularDiameter, 0.01f, 10f);
                transparentIterations = Mathf.Max(0, transparentIterations);
                directLightMultiplier = Mathf.Max(0f, directLightMultiplier);
            }

            public static MainLightConfig Default => new MainLightConfig
            {
                enabled = true,
                angularDiameter = 0.53f,
                transparentIterations = 4,
                directLightMultiplier = 1f
            };
        }

        [Serializable]
        public struct QualitySettings
        {
            public bool enableRayTracing;
            public BrdfRayTracer.Settings brdfSettings;
            [Range(1, 20)] public int maxIterations;
            [Range(0f, 10f)] public float skyboxExposure;
            public MainLightConfig mainLight;

            public void Clamp()
            {
                brdfSettings.Clamp();
                maxIterations = Mathf.Clamp(maxIterations, 1, 20);
                skyboxExposure = Mathf.Max(0f, skyboxExposure);
                mainLight.Clamp();
            }

            public static QualitySettings Default => new QualitySettings
            {
                enableRayTracing = true,
                brdfSettings = BrdfRayTracer.Settings.Default,
                maxIterations = 5,
                skyboxExposure = 1f,
                mainLight = MainLightConfig.Default
            };
        }

        [SerializeField] private bool enableRayTracing = true;
        [FormerlySerializedAs("diffuseSettings")]
        [SerializeField] private BrdfRayTracer.Settings brdfSettings = BrdfRayTracer.Settings.Default;
        [SerializeField, Range(1, 20)] private int maxIterations = 5;
        [SerializeField, Range(0f, 10f)] private float skyboxExposure = 1.0f;
        [SerializeField] private MainLightSettings mainLightSettings = MainLightSettings.Default;

        #endregion

        #region Private Fields

        private BrdfRayTracer _brdfRayTracer = new BrdfRayTracer();
        private PrimaryRayTracer _primaryRayTracer = new PrimaryRayTracer();
        private ParticleRenderer _particleRenderer = new ParticleRenderer();

        #endregion

        public QualitySettings CaptureQualitySettings()
        {
            return new QualitySettings
            {
                enableRayTracing = enableRayTracing,
                brdfSettings = brdfSettings,
                maxIterations = maxIterations,
                skyboxExposure = skyboxExposure,
                mainLight = new MainLightConfig
                {
                    enabled = mainLightSettings.enabled,
                    angularDiameter = mainLightSettings.angularDiameter,
                    transparentIterations = mainLightSettings.transparentIterations,
                    directLightMultiplier = mainLightSettings.directLightMultiplier
                }
            };
        }

        public void ApplyQualitySettings(QualitySettings settings)
        {
            settings.Clamp();
            enableRayTracing = settings.enableRayTracing;
            brdfSettings = settings.brdfSettings;
            maxIterations = settings.maxIterations;
            skyboxExposure = settings.skyboxExposure;
            mainLightSettings.enabled = settings.mainLight.enabled;
            mainLightSettings.angularDiameter = settings.mainLight.angularDiameter;
            mainLightSettings.transparentIterations = settings.mainLight.transparentIterations;
            mainLightSettings.directLightMultiplier = settings.mainLight.directLightMultiplier;
        }

        #region Unity Lifecycle

        /// <summary>
        /// Initializes the render manager.
        /// </summary>
        protected override void OnAwake()
        {
            base.OnAwake();
        }

        /// <summary>
        /// Cleans up cached references on system destruction.
        /// </summary>
        public override void DestroySystem()
        {
            base.DestroySystem();
            _brdfRayTracer?.Dispose();
            _brdfRayTracer = null;
            _primaryRayTracer?.Dispose();
            _primaryRayTracer = null;
            _particleRenderer = null;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Executes the rendering pipeline for the given rendering data.
        /// Retrieves probe buffers and dispatches ray tracing if enabled.
        /// </summary>
        /// <param name="renderingData">The rendering context containing camera, command buffer, and render textures.</param>
        public void ExecuteRendering(PhotonRenderingData renderingData)
        {
            if (renderingData == null || renderingData.camera == null || renderingData.cmd == null)
                return;

            renderingData.originalTargetRT ??= renderingData.targetRT;
            renderingData.originalNormalRT ??= renderingData.normalRT;
            renderingData.originalDepthRT ??= renderingData.depthRT;
            renderingData.originalMotionVectorRT ??= renderingData.motionVectorRT;
            renderingData.originalAlbedoRT ??= renderingData.albedoRT;
            renderingData.originalDiffuseAlbedoRT ??= renderingData.diffuseAlbedoRT;
            renderingData.originalSpecularAlbedoRT ??= renderingData.specularAlbedoRT;
            renderingData.originalRoughnessRT ??= renderingData.roughnessRT;
            renderingData.specularAccumOriginalRT ??= renderingData.specularAccumRT;

            renderingData.scaledTargetRT = renderingData.targetRT;
            renderingData.scaledNormalRT = renderingData.normalRT;
            renderingData.scaledDepthRT = renderingData.depthRT;
            renderingData.scaledMotionVectorRT = renderingData.motionVectorRT;
            renderingData.scaledAlbedoRT = renderingData.albedoRT;
            renderingData.scaledDiffuseAlbedoRT = renderingData.diffuseAlbedoRT;
            renderingData.scaledSpecularAlbedoRT = renderingData.specularAlbedoRT;
            renderingData.scaledRoughnessRT = renderingData.roughnessRT;

            var probeBuffers = CascadedProbeManager.ProbeBuffers.Empty;

            if (!enableRayTracing)
            {
                renderingData.cmd.Blit(renderingData.targetRT, renderingData.activeRT);
                return;
            }

            if (_brdfRayTracer == null)
            {
                renderingData.cmd.Blit(renderingData.targetRT, renderingData.activeRT);
                return;
            }

            PrimaryRayTracer.PrimaryRayData primaryData = _primaryRayTracer != null
                ? _primaryRayTracer.Render(renderingData, brdfSettings.maxBounces, maxIterations)
                : default;

            if (!primaryData.IsValid)
            {
                renderingData.cmd.Blit(renderingData.targetRT, renderingData.activeRT);
                return;
            }

            ApplyDlssHalfResolution(renderingData, ref primaryData);

            _brdfRayTracer.Render(
                renderingData,
                brdfSettings,
                probeBuffers,
                primaryData,
                skyboxExposure,
                ResolveMainLight());

            _particleRenderer?.Render(renderingData);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Ensures serialized ranges remain valid.
        /// </summary>
        private void OnValidate()
        {
            brdfSettings.Clamp();
            mainLightSettings.Clamp();
        }

        private BrdfRayTracer.MainLightData ResolveMainLight()
        {
            if (!mainLightSettings.enabled)
                return BrdfRayTracer.MainLightData.Disabled;

            Light target = mainLightSettings.overrideLight;
            if (target == null || target.type != LightType.Directional || !target.enabled)
            {
                target = FindActiveDirectionalLight();
            }

            if (target == null || !target.enabled)
                return BrdfRayTracer.MainLightData.Disabled;

            Vector3 direction = -target.transform.forward;
            if (direction.sqrMagnitude < 1.0e-6f)
                direction = Vector3.down;
            else
                direction.Normalize();

            Color lightColor = target.color;
            if (target.useColorTemperature)
            {
                lightColor *= Mathf.CorrelatedColorTemperatureToRGB(target.colorTemperature);
            }
            Color linear = lightColor.linear * target.intensity;
            Vector3 colorVec = new Vector3(linear.r, linear.g, linear.b);

            float diameter = Mathf.Max(0.01f, mainLightSettings.angularDiameter);
            float angularRadius = Mathf.Clamp(diameter * Mathf.Deg2Rad * 0.5f, 0.0001f, Mathf.PI * 0.5f);

            float multiplier = Mathf.Max(0f, mainLightSettings.directLightMultiplier);
            return new BrdfRayTracer.MainLightData(true, direction, colorVec, angularRadius, mainLightSettings.transparentIterations, multiplier);
        }

        private bool ApplyDlssHalfResolution(PhotonRenderingData renderingData, ref PrimaryRayTracer.PrimaryRayData primaryData)
        {
            DlssDenoiserManager dlss = DlssDenoiserManager.Instance;
            if (dlss == null || !dlss.IsHalfResolutionInputEnabled)
                return false;

            if (renderingData.targetRT == null || renderingData.activeRT == null)
                return false;

            RTManager rtManager = RTManager.Instance;
            if (rtManager == null)
            {
                Debug.LogWarning("RTManager is required for DLSS half-resolution mode.");
                return false;
            }

            CommandBuffer cmd = renderingData.cmd;
            if (cmd == null)
                return false;

            int originalWidth = Mathf.Max(1, renderingData.targetRT.width);
            int originalHeight = Mathf.Max(1, renderingData.targetRT.height);
            int halfWidth = Mathf.Max(1, originalWidth / 2);
            int halfHeight = Mathf.Max(1, originalHeight / 2);
            int cameraId = renderingData.camera != null ? renderingData.camera.GetInstanceID() : 0;

            RenderTexture scaledColor = CreateScaledCopy(
                renderingData.targetRT,
                $"Photon_DLSS_Color_{cameraId}",
                halfWidth,
                halfHeight,
                cmd,
                rtManager,
                clearIfSourceMissing: false);
            if (scaledColor == null)
                return false;

            renderingData.targetRT = scaledColor;
            renderingData.scaledTargetRT = scaledColor;
            renderingData.normalRT = CreateScaledCopy(
                renderingData.normalRT,
                $"Photon_DLSS_Normal_{cameraId}",
                halfWidth,
                halfHeight,
                cmd,
                rtManager);
            renderingData.scaledNormalRT = renderingData.normalRT;
            renderingData.depthRT = CreateScaledCopy(
                renderingData.depthRT,
                $"Photon_DLSS_Depth_{cameraId}",
                halfWidth,
                halfHeight,
                cmd,
                rtManager);
            renderingData.scaledDepthRT = renderingData.depthRT;
            renderingData.motionVectorRT = CreateScaledCopy(
                renderingData.motionVectorRT,
                $"Photon_DLSS_MV_{cameraId}",
                halfWidth,
                halfHeight,
                cmd,
                rtManager,
                clearIfSourceMissing: false);
            renderingData.scaledMotionVectorRT = renderingData.motionVectorRT;

            renderingData.albedoRT = CreateScaledCopy(
                renderingData.albedoRT,
                $"Photon_DLSS_Albedo_{cameraId}",
                halfWidth,
                halfHeight,
                cmd,
                rtManager,
                clearIfSourceMissing: false);
            renderingData.scaledAlbedoRT = renderingData.albedoRT;

            renderingData.diffuseAlbedoRT = CreateScaledCopy(
                renderingData.diffuseAlbedoRT,
                $"Photon_DLSS_DiffuseAlbedo_{cameraId}",
                halfWidth,
                halfHeight,
                cmd,
                rtManager,
                clearIfSourceMissing: false);
            renderingData.scaledDiffuseAlbedoRT = renderingData.diffuseAlbedoRT;

            renderingData.specularAlbedoRT = CreateScaledCopy(
                renderingData.specularAlbedoRT,
                $"Photon_DLSS_SpecularAlbedo_{cameraId}",
                halfWidth,
                halfHeight,
                cmd,
                rtManager,
                clearIfSourceMissing: false);
            renderingData.scaledSpecularAlbedoRT = renderingData.specularAlbedoRT;

            renderingData.roughnessRT = CreateScaledCopy(
                renderingData.roughnessRT,
                $"Photon_DLSS_Roughness_{cameraId}",
                halfWidth,
                halfHeight,
                cmd,
                rtManager,
                clearIfSourceMissing: false);
            renderingData.scaledRoughnessRT = renderingData.roughnessRT;

            if (primaryData.SpecularAccumFull != null)
            {
                RenderTexture specHalf = rtManager.GetAdjustableRT(
                    $"Photon_DLSS_SpecAccum_{cameraId}",
                    halfWidth,
                    halfHeight,
                    primaryData.SpecularAccumFull.format,
                    TextureWrapMode.Clamp,
                    FilterMode.Bilinear,
                    useMipMap: false,
                    autoGenerateMips: false,
                    enableRandomWrite: true);
                cmd.Blit(primaryData.SpecularAccumFull, specHalf);
                primaryData.SpecularAccumHalf = specHalf;
                primaryData.SpecularAccum = specHalf;
                renderingData.specularAccumOriginalRT = primaryData.SpecularAccumFull;
                renderingData.specularAccumRT = specHalf;
            }
            else
            {
                renderingData.specularAccumOriginalRT = null;
                renderingData.specularAccumRT = null;
            }

            renderingData.usesHalfResolutionInput = true;
            renderingData.deferFullResolutionResolve = true;
            return true;
        }

        private RenderTexture CreateScaledCopy(
            RenderTexture source,
            string cacheKey,
            int width,
            int height,
            CommandBuffer cmd,
            RTManager rtManager,
            bool clearIfSourceMissing = true)
        {
            if (source == null)
            {
                if (clearIfSourceMissing)
                {
                    RenderTextureDescriptor fallbackDesc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBHalf, 0)
                    {
                        msaaSamples = 1,
                        useMipMap = false,
                        enableRandomWrite = true
                    };
                    RenderTexture fallback = rtManager.GetAdjustableRT(cacheKey, fallbackDesc);
                    cmd.SetRenderTarget(fallback);
                    cmd.ClearRenderTarget(false, true, Color.clear);
                    return fallback;
                }
                return null;
            }

            RenderTextureDescriptor descriptor = source.descriptor;
            descriptor.width = width;
            descriptor.height = height;
            descriptor.msaaSamples = 1;
            descriptor.useMipMap = false;

            RenderTexture scaled = rtManager.GetAdjustableRT(cacheKey, descriptor);
            cmd.Blit(source, scaled);
            return scaled;
        }

        private static Light FindActiveDirectionalLight()
        {
            Light sun = RenderSettings.sun;
            if (sun != null && sun.type == LightType.Directional && sun.enabled)
                return sun;

            Light[] lights = GameObject.FindObjectsOfType<Light>();
            foreach (var light in lights)
            {
                if (light == null)
                    continue;

                if (light.type == LightType.Directional && light.enabled)
                    return light;
            }

            return null;
        }


        #endregion
    }
}

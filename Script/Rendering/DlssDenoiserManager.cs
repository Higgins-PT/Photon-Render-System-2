using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace PhotonGISystem2
{
    /// <summary>
    /// High-level DLSS denoiser integration that consumes PhotonRenderingData and manages GPU interop.
    /// </summary>
    [DefaultExecutionOrder(-70)]
    public sealed class DlssDenoiserManager : PGSingleton<DlssDenoiserManager>
    {
        [Serializable]
        public struct DlssDenoiseParameters
        {
            [Tooltip("Master toggle that decides whether DLSS executes at all.")]
            public bool enableDlss;
            [Tooltip("Controls whether the DLSS denoiser dispatch runs once DLSS is enabled.")]
            public bool enableDlssDenoise;
            [Tooltip("DLSS internal performance/quality preset.")]
            public DlssQuality quality;
            [Tooltip("When enabled, DLSS consumes half-resolution inputs and outputs directly to the full-resolution target.")]
            public bool useHalfResolutionInput;
            [Tooltip("Fine grained DLSS render configuration, e.g. jitter/motion scale.")]
            public DLSSRenderSettings renderSettings;

            public static DlssDenoiseParameters Default => new DlssDenoiseParameters
            {
                enableDlss = true,
                enableDlssDenoise = true,
                quality = DlssQuality.MaxQuality,
                useHalfResolutionInput = false,
                renderSettings = DLSSRenderSettings.Default
            };
        }

        private DLSSDenoiser _dlssDenoiser;
        private DLSSSuperResolution _dlssSuperResolution;

        [SerializeField]
        private DlssDenoiseParameters _defaultParameters = DlssDenoiseParameters.Default;

        public DlssDenoiseParameters DefaultParameters
        {
            get => _defaultParameters;
            set => _defaultParameters = value;
        }

        /// <summary>
        /// Executes DLSS denoising on the provided rendering data.
        /// </summary>
        public void Execute(PhotonRenderingData renderingData)
        {
            Execute(renderingData, _defaultParameters);
        }

        public void Execute(PhotonRenderingData renderingData, DlssDenoiseParameters parameters)
        {
            if (renderingData == null || renderingData.cmd == null || renderingData.targetRT == null)
                return;

            if (!parameters.enableDlss || !parameters.enableDlssDenoise)
                return;

            RenderTexture colorRt = renderingData.targetRT;
            RTManager rtManager = RTManager.Instance;
            if (rtManager == null)
            {
                Debug.LogWarning("RTManager instance is required for DLSS.");
                return;
            }

            bool useHalfResolutionInput = parameters.useHalfResolutionInput || renderingData.usesHalfResolutionInput;

            int cameraId = renderingData.camera != null ? renderingData.camera.GetInstanceID() : 0;
            RenderTexture denoiseOutput = rtManager.GetAdjustableRT(
                cameraId + "_DLSSDenoise",
                colorRt.width,
                colorRt.height,
                RenderTextureFormat.ARGBFloat);

            RenderTexture motionRt = renderingData.motionVectorRT;
            RenderTexture depthRt = renderingData.depthRT;

            if (motionRt == null || depthRt == null)
            {
                Debug.LogWarning("DLSS requires motion vector and depth render textures.");
                return;
            }

            RenderTexture albedoRt = renderingData.albedoRT;
            RenderTexture diffuseAlbedo = renderingData.diffuseAlbedoRT ?? albedoRt;
            RenderTexture specularAlbedo = renderingData.specularAlbedoRT;
            RenderTexture roughnessRt = renderingData.roughnessRT;
            RenderTexture normalRt = renderingData.normalRT;

            bool guideAlbedo = diffuseAlbedo != null || specularAlbedo != null || albedoRt != null;
            bool guideNormal = normalRt != null;
            DlssQuality quality = Enum.IsDefined(typeof(DlssQuality), parameters.quality)
                ? parameters.quality
                : DlssQuality.MaxQuality;
            EnsureDenoiser(
                colorRt.width,
                colorRt.height,
                colorRt.width,
                colorRt.height,
                guideAlbedo,
                guideNormal,
                quality,
                useHalfResolutionInput);

            CommandBuffer cmd = renderingData.cmd;

            var preFence = cmd.CreateAsyncGraphicsFence();
            cmd.WaitOnAsyncGraphicsFence(preFence);
            _dlssDenoiser.Render(cmd,
                                 colorRt,
                                 denoiseOutput,
                                 motionRt,
                                 depthRt,
                                 diffuseAlbedo,
                                 specularAlbedo,
                                 roughnessRt,
                                 albedoRt,
                                 normalRt,
                                 parameters.renderSettings);

            var postFence = cmd.CreateAsyncGraphicsFence();
            cmd.WaitOnAsyncGraphicsFence(postFence);
            cmd.Blit(denoiseOutput, colorRt, null);

            if (useHalfResolutionInput)
            {
                RenderTexture finalDestination = renderingData.originalTargetRT ?? renderingData.activeRT;
                if (finalDestination == null)
                {
                    Debug.LogWarning("DLSS half-resolution mode requires a valid full-resolution render target.");
                    return;
                }

                EnsureSuperResolution(
                    colorRt.width,
                    colorRt.height,
                    finalDestination.width,
                    finalDestination.height,
                    quality);

                var srPreFence = cmd.CreateAsyncGraphicsFence();
                cmd.WaitOnAsyncGraphicsFence(srPreFence);
                _dlssSuperResolution.Render(cmd,
                                            colorRt,
                                            finalDestination,
                                            motionRt,
                                            depthRt,
                                            diffuseAlbedo,
                                            specularAlbedo,
                                            roughnessRt,
                                            albedoRt,
                                            normalRt,
                                            parameters.renderSettings);
                var srPostFence = cmd.CreateAsyncGraphicsFence();
                cmd.WaitOnAsyncGraphicsFence(srPostFence);

                renderingData.targetRT = finalDestination;
                renderingData.scaledTargetRT = finalDestination;
                if (renderingData.originalNormalRT != null)
                    renderingData.normalRT = renderingData.originalNormalRT;
                if (renderingData.originalDepthRT != null)
                    renderingData.depthRT = renderingData.originalDepthRT;
                if (renderingData.originalMotionVectorRT != null)
                    renderingData.motionVectorRT = renderingData.originalMotionVectorRT;
                renderingData.scaledNormalRT = renderingData.normalRT;
                renderingData.scaledDepthRT = renderingData.depthRT;
                renderingData.scaledMotionVectorRT = renderingData.motionVectorRT;
                if (renderingData.originalDiffuseAlbedoRT != null)
                    renderingData.diffuseAlbedoRT = renderingData.originalDiffuseAlbedoRT;
                if (renderingData.originalSpecularAlbedoRT != null)
                    renderingData.specularAlbedoRT = renderingData.originalSpecularAlbedoRT;
                if (renderingData.originalRoughnessRT != null)
                    renderingData.roughnessRT = renderingData.originalRoughnessRT;
                if (renderingData.originalAlbedoRT != null)
                    renderingData.albedoRT = renderingData.originalAlbedoRT;
                renderingData.scaledDiffuseAlbedoRT = renderingData.diffuseAlbedoRT;
                renderingData.scaledSpecularAlbedoRT = renderingData.specularAlbedoRT;
                renderingData.scaledRoughnessRT = renderingData.roughnessRT;
                renderingData.scaledAlbedoRT = renderingData.albedoRT;
                if (renderingData.specularAccumOriginalRT != null)
                    renderingData.specularAccumRT = renderingData.specularAccumOriginalRT;
                renderingData.usesHalfResolutionInput = false;
                renderingData.deferFullResolutionResolve = false;
            }
        }

        public override void DestroySystem()
        {
            base.DestroySystem();
            _dlssDenoiser?.Dispose();
            _dlssDenoiser = null;
            _dlssSuperResolution?.Dispose();
            _dlssSuperResolution = null;

        }

        private void EnsureDenoiser(
            int inputWidth,
            int inputHeight,
            int outputWidth,
            int outputHeight,
            bool guideAlbedo,
            bool guideNormal,
            DlssQuality quality,
            bool halfResolutionInput)
        {
            _dlssDenoiser ??= new DLSSDenoiser();

            bool needsReinit = !_dlssDenoiser.IsInitialized ||
                               _dlssDenoiser.CurrentConfig.imageWidth != inputWidth ||
                               _dlssDenoiser.CurrentConfig.imageHeight != inputHeight ||
                               _dlssDenoiser.CurrentConfig.outputWidth != outputWidth ||
                               _dlssDenoiser.CurrentConfig.outputHeight != outputHeight ||
                               (_dlssDenoiser.CurrentConfig.guideAlbedo != (guideAlbedo ? 1 : 0)) ||
                               (_dlssDenoiser.CurrentConfig.guideNormal != (guideNormal ? 1 : 0)) ||
                               (_dlssDenoiser.CurrentConfig.perfQuality != (int)quality) ||
                               (_dlssDenoiser.CurrentConfig.halfResolutionInput != (halfResolutionInput ? 1 : 0));

            if (needsReinit)
            {
                _dlssDenoiser.Initialize(
                    inputWidth,
                    inputHeight,
                    outputWidth,
                    outputHeight,
                    guideAlbedo,
                    guideNormal,
                    quality,
                    halfResolutionInput);
            }
        }

        private void EnsureSuperResolution(
            int inputWidth,
            int inputHeight,
            int outputWidth,
            int outputHeight,
            DlssQuality quality)
        {
            _dlssSuperResolution ??= new DLSSSuperResolution();

            bool needsReinit = !_dlssSuperResolution.IsInitialized ||
                               _dlssSuperResolution.CurrentConfig.imageWidth != inputWidth ||
                               _dlssSuperResolution.CurrentConfig.imageHeight != inputHeight ||
                               _dlssSuperResolution.CurrentConfig.outputWidth != outputWidth ||
                               _dlssSuperResolution.CurrentConfig.outputHeight != outputHeight ||
                               (_dlssSuperResolution.CurrentConfig.perfQuality != (int)quality);

            if (needsReinit)
            {
                _dlssSuperResolution.Initialize(
                    inputWidth,
                    inputHeight,
                    outputWidth,
                    outputHeight,
                    quality);
            }
        }

        public DlssDenoiseParameters CaptureQualitySettings()
        {
            return _defaultParameters;
        }

        public void ApplyQualitySettings(DlssDenoiseParameters parameters)
        {
            _defaultParameters = parameters;
        }

        public bool IsHalfResolutionInputEnabled => _defaultParameters.useHalfResolutionInput;
    }
}



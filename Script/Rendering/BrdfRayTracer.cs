using System;
using System.Collections.Generic;
using PhotonSystem;
using UnityEngine;
using UnityEngine.Rendering;

namespace PhotonGISystem2
{
    /// <summary>
    /// Executes the BRDF ray tracing pass using the shared RayTraceManager infrastructure.
    /// </summary>
    public class BrdfRayTracer
    {
        [Serializable]
        public struct Settings
        {
            public enum ResolutionDownscale
            {
                [InspectorName("1x")] Full = 1,
                [InspectorName("2x")] Half = 2,
                [InspectorName("4x")] Quarter = 4,
                [InspectorName("8x")] Eighth = 8
            }

            [Range(1, 256)] public int samplesPerPixel;
            [Range(1, 16)] public int maxBounces;
            public bool enableImportanceSampling;
            public bool enableTemporalResampling;
            public bool enableSpatialResampling;
            [Range(0f, 1f)] public float restirNormalThreshold;
            [Min(0f)] public float restirDepthThreshold;
            [Range(1, 4)] public int restirSpatialRadius;
            [Range(0.25f, 4f)] public float restirTemporalWeightBoost;
            public ResolutionDownscale resolutionDownscale;

            public static Settings Default => new Settings
            {
                samplesPerPixel = 8,
                maxBounces = 5,
                enableImportanceSampling = true,
                enableTemporalResampling = true,
                enableSpatialResampling = true,
                restirNormalThreshold = 0.85f,
                restirDepthThreshold = 0.5f,
                restirSpatialRadius = 1,
                restirTemporalWeightBoost = 1.0f,
                resolutionDownscale = ResolutionDownscale.Full
            };

            public void Clamp()
            {
                samplesPerPixel = Mathf.Max(1, samplesPerPixel);
                maxBounces = Mathf.Max(1, maxBounces);
                restirNormalThreshold = Mathf.Clamp01(restirNormalThreshold);
                restirDepthThreshold = Mathf.Max(0f, restirDepthThreshold);
                restirSpatialRadius = Mathf.Max(1, restirSpatialRadius);
                restirTemporalWeightBoost = Mathf.Max(0.01f, restirTemporalWeightBoost);
                if (!Enum.IsDefined(typeof(ResolutionDownscale), resolutionDownscale))
                {
                    resolutionDownscale = ResolutionDownscale.Full;
                }
            }
        }

        public readonly struct MainLightData
        {
            public readonly bool Enabled;
            public readonly Vector3 Direction;
            public readonly Vector3 Color;
            public readonly float AngularRadius;
            public readonly int TransparentIterations;
            public readonly float Multiplier;

            public MainLightData(bool enabled, Vector3 direction, Vector3 color, float angularRadius, int transparentIterations, float multiplier)
            {
                Enabled = enabled;
                Direction = direction;
                Color = color;
                AngularRadius = angularRadius;
                TransparentIterations = Mathf.Max(0, transparentIterations);
                Multiplier = Mathf.Max(0f, multiplier);
            }

            public static MainLightData Disabled => new MainLightData(false, Vector3.up, Vector3.zero, 0f, 0, 0f);
        }

        private const int CandidateStride = 48;
        private const int ReservoirStride = 48;
        private const int ReservoirFloatCount = 12;

        private readonly Dictionary<Camera, ReSTIRContext> _restirContexts = new();
        private ComputeBuffer _emptyProbePositionBuffer;
        private ComputeBuffer _emptyProbeSHBuffer;

        private sealed class ReSTIRContext
        {
            public ComputeBuffer CandidateBuffer;
            public ComputeBuffer TemporalBuffer;
            public ComputeBuffer HistoryBuffer;
            public int Width;
            public int Height;
            public int PixelCount;
            public uint FrameIndex;
            public bool NeedsClear;

            public void Dispose(RTManager manager)
            {
                ReleaseBuffer(manager, ref CandidateBuffer);
                ReleaseBuffer(manager, ref TemporalBuffer);
                ReleaseBuffer(manager, ref HistoryBuffer);
            }

            private static void ReleaseBuffer(RTManager manager, ref ComputeBuffer buffer)
            {
                if (buffer == null)
                    return;

                if (manager != null)
                {
                    manager.ReleaseCB(buffer);
                }
                else
                {
                    buffer.Release();
                }

                buffer = null;
            }
        }

        /// <summary>
        /// Renders the BRDF pass into the provided rendering targets.
        /// </summary>
        /// <param name="renderingData">Active rendering context.</param>
        /// <param name="settings">Per-pass sampling settings.</param>
        /// <param name="probeBuffers">Probe data buffers for the current camera.</param>
        /// <param name="primaryData">Primary ray payloads rendered at full resolution.</param>
        public void Render(
            PhotonRenderingData renderingData,
            Settings settings,
            CascadedProbeManager.ProbeBuffers probeBuffers,
            PrimaryRayTracer.PrimaryRayData primaryData,
            float skyboxExposure,
            MainLightData mainLight)
        {
            if (renderingData == null || renderingData.cmd == null || renderingData.camera == null)
                return;
            if (!primaryData.IsValid)
            {
                renderingData.cmd.Blit(renderingData.targetRT, renderingData.activeRT);
                return;
            }

            RayTraceManager rayTraceManager = RayTraceManager.Instance;
            ResourceManager resourceManager = ResourceManager.Instance;
            CascadedProbeManager cascadedManager = CascadedProbeManager.Instance;
            if (rayTraceManager == null || resourceManager == null)
            {
                renderingData.cmd.Blit(renderingData.targetRT, renderingData.activeRT);
                return;
            }

            RayTracingShader shader = resourceManager.BrdfTracingShader;
            if (shader == null)
            {
                renderingData.cmd.Blit(renderingData.targetRT, renderingData.activeRT);
                return;
            }

            RTManager bufferManager = RTManager.Instance;
            if (bufferManager == null)
            {
                renderingData.cmd.Blit(renderingData.targetRT, renderingData.activeRT);
                return;
            }

            RenderTexture fullTarget = renderingData.targetRT;
            RenderTexture destination = renderingData.activeRT;
            if (fullTarget == null || destination == null)
            {
                renderingData.cmd.Blit(renderingData.targetRT, renderingData.activeRT);
                return;
            }

            int fullWidth = fullTarget.width;
            int fullHeight = fullTarget.height;
            if (fullWidth <= 0 || fullHeight <= 0)
            {
                renderingData.cmd.Blit(renderingData.targetRT, renderingData.activeRT);
                return;
            }

            int downscaleFactor = Mathf.Clamp((int)settings.resolutionDownscale, 1, 8);
            if (renderingData.usesHalfResolutionInput)
            {
                downscaleFactor = 1;
            }
            bool useDownscale = downscaleFactor > 1;
            int workingWidth = fullWidth;
            int workingHeight = fullHeight;
            RenderTexture workingTarget = fullTarget;

            if (useDownscale)
            {
                workingWidth = Mathf.Max(1, Mathf.CeilToInt(fullWidth / (float)downscaleFactor));
                workingHeight = Mathf.Max(1, Mathf.CeilToInt(fullHeight / (float)downscaleFactor));
                string downscaleKey = $"BrdfDownscale_{renderingData.camera.GetInstanceID()}";
                RenderTexture downscaleRT = bufferManager.GetAdjustableRT(
                    downscaleKey,
                    workingWidth,
                    workingHeight,
                    fullTarget.format,
                    TextureWrapMode.Clamp,
                    FilterMode.Bilinear,
                    useMipMap: false,
                    autoGenerateMips: false,
                    enableRandomWrite: true);

                if (downscaleRT != null)
                {
                    workingTarget = downscaleRT;
                }
                else
                {
                    useDownscale = false;
                    workingWidth = fullWidth;
                    workingHeight = fullHeight;
                    workingTarget = fullTarget;
                }
            }

            ReSTIRContext context = GetOrCreateContext(renderingData.camera, workingWidth, workingHeight, bufferManager);
            if (context == null)
            {
                renderingData.cmd.Blit(renderingData.targetRT, renderingData.activeRT);
                return;
            }

            settings.Clamp();
            uint frameIndex = ++context.FrameIndex;
            int spp = settings.samplesPerPixel;
            int bounces = settings.maxBounces;
            float zoom = Mathf.Tan(renderingData.camera.fieldOfView * Mathf.Deg2Rad * 0.5f);
            bool importanceOn = settings.enableImportanceSampling && cascadedManager != null && probeBuffers.IsValid;

            if (importanceOn)
            {
                if (!cascadedManager.SetProbeShaderData(renderingData, shader, probeBuffers))
                {
                    importanceOn = false;
                }
            }
            if (!importanceOn)
            {
                BindEmptyProbeData(renderingData, shader);
            }
            
            rayTraceManager.ExecuteRayTracingJob(
                renderingData,
                shader,
                "MainRayGenShader",
                (uint)workingWidth,
                (uint)workingHeight,
                1u,
                (cmd, cam) =>
                {
                    cmd.SetRayTracingFloatParam(shader, "g_Zoom", zoom);
                    cmd.SetRayTracingIntParam(shader, "g_SamplesPerPixel", spp);
                    cmd.SetRayTracingIntParam(shader, "g_MaxBounces", bounces);
                    cmd.SetRayTracingIntParam(shader, "g_EnableImportanceSampling", importanceOn ? 1 : 0);
                    cmd.SetRayTracingFloatParam(shader, "g_SkyboxExposure", Mathf.Max(0.0f, skyboxExposure));
                    cmd.SetRayTracingIntParam(shader, "g_FrameIndex", unchecked((int)frameIndex));
                    cmd.SetRayTracingFloatParam(shader, "g_Time", Time.time);
                    cmd.SetRayTracingIntParam(shader, "g_PrimaryWidth", primaryData.Width);
                    cmd.SetRayTracingIntParam(shader, "g_PrimaryHeight", primaryData.Height);
                    cmd.SetRayTracingIntParam(shader, "g_WorkingWidth", workingWidth);
                    cmd.SetRayTracingIntParam(shader, "g_WorkingHeight", workingHeight);
                    Vector3 mainDir = mainLight.Direction.sqrMagnitude > 1.0e-6f ? mainLight.Direction.normalized : Vector3.down;
                    Vector3 lightColor = mainLight.Enabled ? mainLight.Color : Vector3.zero;
                    cmd.SetRayTracingVectorParam(shader, "g_MainLightDirection", new Vector4(mainDir.x, mainDir.y, mainDir.z, 0f));
                    cmd.SetRayTracingVectorParam(shader, "g_MainLightColor", new Vector4(lightColor.x, lightColor.y, lightColor.z, 0f));
                    cmd.SetRayTracingFloatParam(shader, "g_MainLightAngularRadius", mainLight.Enabled ? Mathf.Max(0f, mainLight.AngularRadius) : 0f);
                    cmd.SetRayTracingFloatParam(shader, "g_MainLightEnabled", mainLight.Enabled ? 1f : 0f);
                    cmd.SetRayTracingIntParam(shader, "g_MainLightTransparentIterations", Mathf.Max(0, mainLight.TransparentIterations));
                    cmd.SetRayTracingFloatParam(shader, "g_MainLightMultiplier", mainLight.Multiplier);
                    cmd.SetRayTracingTextureParam(shader, "g_Output", workingTarget);
                    cmd.SetRayTracingBufferParam(shader, "g_PrimaryRayPayloads", primaryData.Buffer);
                    if (context.CandidateBuffer != null)
                    {
                        cmd.SetRayTracingBufferParam(shader, "g_ReSTIRCandidates", context.CandidateBuffer);
                    }
                });

            DispatchReSTIR(renderingData, settings, resourceManager, context, workingWidth, workingHeight, workingTarget, frameIndex);

            if (useDownscale && workingTarget != fullTarget)
            {
                renderingData.cmd.Blit(workingTarget, fullTarget);
            }

            DlssDenoiserManager dlss = DlssDenoiserManager.Instance;
            bool runDlssBeforeComposite = !useDownscale && dlss != null;
            bool wasHalfResolutionBeforeDlss = renderingData.usesHalfResolutionInput;

            if (runDlssBeforeComposite)
            {
                dlss.Execute(renderingData);
                if (renderingData.targetRT != null && renderingData.targetRT != fullTarget)
                {
                    fullTarget = renderingData.targetRT;
                    fullWidth = fullTarget.width;
                    fullHeight = fullTarget.height;
                }
                if (wasHalfResolutionBeforeDlss)
                {
                    primaryData.SpecularAccum = primaryData.SpecularAccumFull ?? primaryData.SpecularAccum;
                    renderingData.specularAccumRT = renderingData.specularAccumOriginalRT ?? renderingData.specularAccumRT;
                }
            }

            SvgfDenoiserManager denoiser = SvgfDenoiserManager.Instance;
            if (denoiser != null)
            {
                denoiser.Execute(renderingData);
            }

            CompositeWithPrimaryData(renderingData, resourceManager, primaryData, fullTarget, fullWidth, fullHeight);

            renderingData.cmd.Blit(fullTarget, destination);
        }

        private void DispatchReSTIR(
            PhotonRenderingData renderingData,
            Settings settings,
            ResourceManager resourceManager,
            ReSTIRContext context,
            int width,
            int height,
            RenderTexture target,
            uint frameIndex)
        {
            if (renderingData?.cmd == null || resourceManager == null || context == null || target == null)
                return;

            ComputeShader restirCS = resourceManager.ReSTIRCompute;
            if (restirCS == null)
                return;

            if (context.CandidateBuffer == null || context.TemporalBuffer == null || context.HistoryBuffer == null)
                return;

            int temporalKernel = restirCS.FindKernel("TemporalResample");
            int spatialKernel = restirCS.FindKernel("SpatialResample");

            int groupsX = Mathf.CeilToInt(width / 8.0f);
            int groupsY = Mathf.CeilToInt(height / 8.0f);

            var cmd = renderingData.cmd;
            RenderTexture motionVectorRT = renderingData.motionVectorRT;
            bool hasMotionVectors = motionVectorRT != null;

            cmd.SetComputeIntParam(restirCS, "g_ImageWidth", width);
            cmd.SetComputeIntParam(restirCS, "g_ImageHeight", height);
            cmd.SetComputeIntParam(restirCS, "g_FrameIndex", unchecked((int)frameIndex));
            cmd.SetComputeIntParam(restirCS, "g_TemporalEnabled", settings.enableTemporalResampling ? 1 : 0);
            cmd.SetComputeIntParam(restirCS, "g_TemporalHasMotionVectors", hasMotionVectors ? 1 : 0);
            cmd.SetComputeFloatParam(restirCS, "g_TemporalDepthThreshold", settings.restirDepthThreshold);
            cmd.SetComputeFloatParam(restirCS, "g_TemporalNormalThreshold", settings.restirNormalThreshold);
            cmd.SetComputeFloatParam(restirCS, "g_TemporalWeightBoost", Mathf.Max(0.0f, settings.restirTemporalWeightBoost));
            cmd.SetComputeBufferParam(restirCS, temporalKernel, "g_ReSTIRCandidates", context.CandidateBuffer);
            cmd.SetComputeBufferParam(restirCS, temporalKernel, "g_ReSTIRHistory", context.HistoryBuffer);
            cmd.SetComputeBufferParam(restirCS, temporalKernel, "g_ReSTIRTemporal", context.TemporalBuffer);
            cmd.SetComputeTextureParam(restirCS, temporalKernel, "g_MotionVectorTexture", hasMotionVectors ? (Texture)motionVectorRT : Texture2D.blackTexture);
            cmd.SetComputeTextureParam(restirCS, temporalKernel, "g_Output", target);
            cmd.DispatchCompute(restirCS, temporalKernel, groupsX, groupsY, 1);

            cmd.SetComputeIntParam(restirCS, "g_SpatialImageWidth", width);
            cmd.SetComputeIntParam(restirCS, "g_SpatialImageHeight", height);
            cmd.SetComputeIntParam(restirCS, "g_SpatialFrameIndex", unchecked((int)frameIndex));
            cmd.SetComputeIntParam(restirCS, "g_SpatialEnabled", settings.enableSpatialResampling ? 1 : 0);
            cmd.SetComputeIntParam(restirCS, "g_SpatialRadius", Mathf.Max(1, settings.restirSpatialRadius));
            cmd.SetComputeFloatParam(restirCS, "g_SpatialDepthThreshold", settings.restirDepthThreshold);
            cmd.SetComputeFloatParam(restirCS, "g_SpatialNormalThreshold", settings.restirNormalThreshold);
            cmd.SetComputeBufferParam(restirCS, spatialKernel, "g_ReSTIRTemporal", context.TemporalBuffer);
            cmd.SetComputeBufferParam(restirCS, spatialKernel, "g_ReSTIRHistory", context.HistoryBuffer);
            cmd.SetComputeTextureParam(restirCS, spatialKernel, "g_Output", target);
            cmd.DispatchCompute(restirCS, spatialKernel, groupsX, groupsY, 1);
        }

        private ReSTIRContext GetOrCreateContext(Camera camera, int width, int height, RTManager bufferManager)
        {
            if (camera == null || bufferManager == null || width <= 0 || height <= 0)
                return null;

            if (!_restirContexts.TryGetValue(camera, out var context))
            {
                context = new ReSTIRContext();
                _restirContexts[camera] = context;
            }

            long pixelCountLong = (long)width * height;
            int pixelCount = (int)Math.Max(1L, Math.Min(pixelCountLong, int.MaxValue));
            bool needsResize =
                context.CandidateBuffer == null ||
                context.TemporalBuffer == null ||
                context.HistoryBuffer == null ||
                context.Width != width ||
                context.Height != height ||
                context.PixelCount != pixelCount;

            if (needsResize)
            {
                context.Width = width;
                context.Height = height;
                context.PixelCount = pixelCount;
                context.FrameIndex = 0;
                context.CandidateBuffer = RequestBuffer(bufferManager, camera, "ReSTIR_Candidates", pixelCount, CandidateStride);
                context.TemporalBuffer = RequestBuffer(bufferManager, camera, "ReSTIR_Temporal", pixelCount, ReservoirStride);
                context.HistoryBuffer = RequestBuffer(bufferManager, camera, "ReSTIR_History", pixelCount, ReservoirStride);
                context.NeedsClear = true;
            }

            if (context.NeedsClear)
            {
                ClearBuffer(context.HistoryBuffer, context.PixelCount, ReservoirFloatCount);
                context.NeedsClear = false;
            }

            return context;
        }

        private void CompositeWithPrimaryData(
            PhotonRenderingData renderingData,
            ResourceManager resourceManager,
            PrimaryRayTracer.PrimaryRayData primaryData,
            RenderTexture target,
            int width,
            int height)
        {
            if (renderingData?.cmd == null ||
                resourceManager == null ||
                !primaryData.IsValid ||
                target == null ||
                width <= 0 ||
                height <= 0)
            {
                return;
            }

            ComputeShader compositeCS = resourceManager.BrdfCompositeCompute;
            if (compositeCS == null)
                return;

            RTManager bufferManager = RTManager.Instance;
            if (bufferManager == null)
                return;

            string copyKey = $"BrdfCompositeInput_{renderingData.camera.GetInstanceID()}";
            RenderTexture radianceCopy = bufferManager.GetAdjustableRT(
                copyKey,
                width,
                height,
                target.format,
                TextureWrapMode.Clamp,
                FilterMode.Bilinear,
                useMipMap: false,
                autoGenerateMips: false,
                enableRandomWrite: true);

            if (radianceCopy == null)
                return;

            CommandBuffer cmd = renderingData.cmd;
            cmd.Blit(target, radianceCopy);

            int kernel = compositeCS.FindKernel("Composite");
            cmd.SetComputeIntParam(compositeCS, "g_ImageWidth", width);
            cmd.SetComputeIntParam(compositeCS, "g_ImageHeight", height);
            cmd.SetComputeTextureParam(compositeCS, kernel, "g_RadianceInput", radianceCopy);
            Texture specAccumTex = primaryData.SpecularAccum != null ? (Texture)primaryData.SpecularAccum : Texture2D.whiteTexture;
            cmd.SetComputeTextureParam(compositeCS, kernel, "g_SpecularAccumInput", specAccumTex);
            cmd.SetComputeTextureParam(compositeCS, kernel, "g_FinalOutput", target);
            cmd.SetComputeBufferParam(compositeCS, kernel, "g_PrimaryRayPayloads", primaryData.Buffer);

            int groupsX = Mathf.CeilToInt(width / 8.0f);
            int groupsY = Mathf.CeilToInt(height / 8.0f);
            cmd.DispatchCompute(compositeCS, kernel, groupsX, groupsY, 1);
        }

        private static ComputeBuffer RequestBuffer(RTManager manager, Camera camera, string suffix, int count, int stride)
        {
            if (manager == null || camera == null || count <= 0)
                return null;

            string key = $"{camera.GetInstanceID()}_{suffix}";
            return manager.GetAdjustableCB(key, count, stride, ComputeBufferType.Structured);
        }

        private static void ClearBuffer(ComputeBuffer buffer, int elementCount, int floatsPerElement)
        {
            if (buffer == null || elementCount <= 0 || floatsPerElement <= 0)
                return;

            int totalFloats = elementCount * floatsPerElement;
            var zeroData = new float[totalFloats];
            buffer.SetData(zeroData);
        }

        /// <summary>
        /// Releases all cached ReSTIR buffers.
        /// </summary>
        public void Dispose()
        {
            var manager = RTManager.Instance;
            foreach (var context in _restirContexts.Values)
            {
                context.Dispose(manager);
            }
            _restirContexts.Clear();

            ReleaseEmptyBuffers();
        }

        private void BindEmptyProbeData(PhotonRenderingData renderingData, RayTracingShader shader)
        {
            if (renderingData?.cmd == null || shader == null)
                return;

            EnsureEmptyProbeBuffers();

            CommandBuffer cmd = renderingData.cmd;
            cmd.SetRayTracingBufferParam(shader, "g_ProbePositions", _emptyProbePositionBuffer);
            cmd.SetRayTracingBufferParam(shader, "g_ProbeSHCoefficients", _emptyProbeSHBuffer);
            cmd.SetRayTracingIntParam(shader, "g_ProbeCount", 0);
            cmd.SetRayTracingIntParam(shader, "g_ProbesPerAxis", 1);
            cmd.SetRayTracingIntParam(shader, "g_CascadeCount", 1);
            cmd.SetRayTracingFloatParam(shader, "g_SmallestCellSize", 1f);
            Vector3 camPos = renderingData.camera.transform.position;
            cmd.SetRayTracingVectorParam(shader, "g_ProbeCameraPosition", new Vector4(camPos.x, camPos.y, camPos.z, 1f));
        }

        private void EnsureEmptyProbeBuffers()
        {
            if (_emptyProbePositionBuffer == null)
            {
                _emptyProbePositionBuffer = new ComputeBuffer(1, CascadedProbeManager.ProbePosition.Stride, ComputeBufferType.Structured);
                var positions = new CascadedProbeManager.ProbePosition[1];
                _emptyProbePositionBuffer.SetData(positions);
            }

            if (_emptyProbeSHBuffer == null)
            {
                _emptyProbeSHBuffer = new ComputeBuffer(1, CascadedProbeManager.ProbeSHL2.Stride, ComputeBufferType.Structured);
                var sh = new CascadedProbeManager.ProbeSHL2[1];
                _emptyProbeSHBuffer.SetData(sh);
            }
        }

        private void ReleaseEmptyBuffers()
        {
            _emptyProbePositionBuffer?.Release();
            _emptyProbePositionBuffer = null;
            _emptyProbeSHBuffer?.Release();
            _emptyProbeSHBuffer = null;
        }
    }
}


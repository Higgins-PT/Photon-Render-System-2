using System;
using System.Collections.Generic;
using PhotonSystem;
using UnityEngine;
using UnityEngine.Rendering;

namespace PhotonGISystem2
{
    /// <summary>
    /// Applies a lightweight SVGF-inspired denoising pass using temporal accumulation plus a bilateral atrous filter.
    /// Maintains per-camera history buffers keyed by the camera instance.
    /// </summary>
    [DefaultExecutionOrder(-80)]
    public class SvgfDenoiserManager : PGSingleton<SvgfDenoiserManager>
    {
        [SerializeField, Range(0.05f, 1f)] private float temporalBlend = 0.2f;
        [SerializeField, Min(0.0001f)] private float depthSigma = 2.0f;
        [SerializeField, Min(0.0001f)] private float normalSigma = 32.0f;
        [SerializeField, Range(1, 6)] private int atrousIterations = 3;
        [SerializeField] private bool enableTemporalAccumulation = true;
        [SerializeField] private bool enableSpatialFiltering = true;
        [Header("History Clamping")]
        [SerializeField] private bool enableHistoryClamping = true;
        [SerializeField, Range(0.0f, 1.0f)] private float normalHistoryThreshold = 0.8f;
        [SerializeField, Min(0.001f)] private float worldPositionHistoryThreshold = 0.3f;

        private readonly Dictionary<Camera, HistoryState> _history = new();

        [Serializable]
        public struct QualitySettings
        {
            public float temporalBlend;
            public float depthSigma;
            public float normalSigma;
            public int atrousIterations;
            public bool enableTemporalAccumulation;
            public bool enableSpatialFiltering;
            public bool enableHistoryClamping;
            public float normalHistoryThreshold;
            public float worldPositionHistoryThreshold;

            public void Clamp()
            {
                temporalBlend = Mathf.Clamp(temporalBlend, 0.05f, 1f);
                depthSigma = Mathf.Max(0.0001f, depthSigma);
                normalSigma = Mathf.Max(0.0001f, normalSigma);
                atrousIterations = Mathf.Clamp(atrousIterations, 1, 6);
                normalHistoryThreshold = Mathf.Clamp01(normalHistoryThreshold);
                worldPositionHistoryThreshold = Mathf.Max(0.001f, worldPositionHistoryThreshold);
            }

            public static QualitySettings Default => new QualitySettings
            {
                temporalBlend = 0.2f,
                depthSigma = 2.0f,
                normalSigma = 32.0f,
                atrousIterations = 3,
                enableTemporalAccumulation = true,
                enableSpatialFiltering = true,
                enableHistoryClamping = true,
                normalHistoryThreshold = 0.8f,
                worldPositionHistoryThreshold = 0.3f
            };
        }

        public QualitySettings CaptureQualitySettings()
        {
            return new QualitySettings
            {
                temporalBlend = temporalBlend,
                depthSigma = depthSigma,
                normalSigma = normalSigma,
                atrousIterations = atrousIterations,
                enableTemporalAccumulation = enableTemporalAccumulation,
                enableSpatialFiltering = enableSpatialFiltering,
                enableHistoryClamping = enableHistoryClamping,
                normalHistoryThreshold = normalHistoryThreshold,
                worldPositionHistoryThreshold = worldPositionHistoryThreshold
            };
        }

        public void ApplyQualitySettings(QualitySettings settings)
        {
            settings.Clamp();
            temporalBlend = settings.temporalBlend;
            depthSigma = settings.depthSigma;
            normalSigma = settings.normalSigma;
            atrousIterations = settings.atrousIterations;
            enableTemporalAccumulation = settings.enableTemporalAccumulation;
            enableSpatialFiltering = settings.enableSpatialFiltering;
            enableHistoryClamping = settings.enableHistoryClamping;
            normalHistoryThreshold = settings.normalHistoryThreshold;
            worldPositionHistoryThreshold = settings.worldPositionHistoryThreshold;
        }

        private class HistoryState
        {
            public RenderTexture HistoryColor;
            public RenderTexture AccumulationColor;
            public RenderTexture Ping;
            public RenderTexture Pong;
            public RenderTexture HistoryNormal;
            public RenderTexture HistoryDepth;
            public Matrix4x4 PrevInvViewProj;
            public int Width;
            public int Height;
            public bool HasHistory;
            public bool HasHistoryNormals;
            public bool HasHistoryDepth;
        }

        /// <summary>
        /// Executes the denoising pass for the provided rendering data.
        /// </summary>
        public void Execute(PhotonRenderingData renderingData)
        {
            if (renderingData == null || renderingData.cmd == null || renderingData.camera == null)
                return;

            if (renderingData.usesHalfResolutionInput)
                return;

            ResourceManager resourceManager = ResourceManager.Instance;
            ComputeShader svgfCS = resourceManager != null ? resourceManager.SvgfDenoiseCompute : null;
            if (svgfCS == null)
                return;

            RenderTexture currentColor = renderingData.targetRT;
            if (currentColor == null)
                return;

            RenderTexture motionVectors = renderingData.motionVectorRT;
            RenderTexture depthTexture = renderingData.depthRT;
            RenderTexture normalTexture = renderingData.normalRT;

            if (!TryGetHistoryState(renderingData.camera, currentColor.width, currentColor.height, out HistoryState state))
                return;

            CommandBuffer cmd = renderingData.cmd;

            int temporalKernel = svgfCS.FindKernel("TemporalAccumulation");
            int atrousKernel = svgfCS.FindKernel("AtrousFilter");

            int groupsX = Mathf.CeilToInt(currentColor.width / 8.0f);
            int groupsY = Mathf.CeilToInt(currentColor.height / 8.0f);

            cmd.SetComputeIntParam(svgfCS, "g_Width", currentColor.width);
            cmd.SetComputeIntParam(svgfCS, "g_Height", currentColor.height);
            RenderTexture src;
            RenderTexture dst;

            Matrix4x4 currProj = renderingData.camera.projectionMatrix;
            Matrix4x4 currView = renderingData.camera.worldToCameraMatrix;
            Matrix4x4 currInvViewProj = (currProj * currView).inverse;

            if (enableTemporalAccumulation)
            {
                cmd.SetComputeFloatParam(svgfCS, "g_TemporalAlpha", Mathf.Clamp01(temporalBlend));
                cmd.SetComputeFloatParam(svgfCS, "g_DepthSigma", depthSigma);
                cmd.SetComputeFloatParam(svgfCS, "g_NormalSigma", normalSigma);
                cmd.SetComputeIntParam(svgfCS, "g_HasHistory", state.HasHistory ? 1 : 0);
                cmd.SetComputeIntParam(svgfCS, "g_HasMotionVectors", motionVectors != null ? 1 : 0);
                cmd.SetComputeIntParam(svgfCS, "g_HasNormals", normalTexture != null ? 1 : 0);
                cmd.SetComputeIntParam(svgfCS, "g_HasDepth", depthTexture != null ? 1 : 0);
                cmd.SetComputeIntParam(svgfCS, "g_HasHistoryNormals", state.HasHistory && state.HasHistoryNormals ? 1 : 0);
                cmd.SetComputeIntParam(svgfCS, "g_HasHistoryDepthTex", state.HasHistory && state.HasHistoryDepth ? 1 : 0);
                cmd.SetComputeIntParam(svgfCS, "g_EnableHistoryClamp", enableHistoryClamping ? 1 : 0);
                cmd.SetComputeFloatParam(svgfCS, "g_NormalHistoryThreshold", normalHistoryThreshold);
                cmd.SetComputeFloatParam(svgfCS, "g_WorldPosHistoryThreshold", worldPositionHistoryThreshold);
                cmd.SetComputeMatrixParam(svgfCS, "g_CurrInvViewProj", currInvViewProj);
                Matrix4x4 prevInvViewProj = state.HasHistory ? state.PrevInvViewProj : currInvViewProj;
                cmd.SetComputeMatrixParam(svgfCS, "g_PrevInvViewProj", prevInvViewProj);

                cmd.SetComputeTextureParam(svgfCS, temporalKernel, "g_CurrentColor", currentColor);
                cmd.SetComputeTextureParam(svgfCS, temporalKernel, "g_PreviousColor", state.HistoryColor);
                cmd.SetComputeTextureParam(svgfCS, temporalKernel, "g_TemporalOutput", state.AccumulationColor);
                cmd.SetComputeTextureParam(svgfCS, temporalKernel, "g_MotionVectorTexture", motionVectors != null ? motionVectors : Texture2D.blackTexture);
                cmd.SetComputeTextureParam(svgfCS, temporalKernel, "g_NormalTexture", normalTexture != null ? normalTexture : Texture2D.blackTexture);
                cmd.SetComputeTextureParam(svgfCS, temporalKernel, "g_DepthTexture", depthTexture != null ? depthTexture : Texture2D.blackTexture);
                cmd.SetComputeTextureParam(svgfCS, temporalKernel, "g_PreviousNormalTexture", state.HistoryNormal != null ? state.HistoryNormal : Texture2D.blackTexture);
                cmd.SetComputeTextureParam(svgfCS, temporalKernel, "g_PreviousDepthTexture", state.HistoryDepth != null ? state.HistoryDepth : Texture2D.blackTexture);
                cmd.DispatchCompute(svgfCS, temporalKernel, groupsX, groupsY, 1);
                src = state.AccumulationColor;
                state.PrevInvViewProj = currInvViewProj;
            }
            else
            {
                cmd.Blit(currentColor, state.AccumulationColor);
                src = state.AccumulationColor;
                state.HasHistory = false;
            }

            dst = state.Ping;

            if (enableSpatialFiltering)
            {
                for (int i = 0; i < Mathf.Max(1, atrousIterations); i++)
                {
                    cmd.SetComputeIntParam(svgfCS, "g_AtrousIteration", i);
                    cmd.SetComputeTextureParam(svgfCS, atrousKernel, "g_TemporalColor", src);
                    cmd.SetComputeTextureParam(svgfCS, atrousKernel, "g_NormalTexture", normalTexture != null ? normalTexture : Texture2D.blackTexture);
                    cmd.SetComputeTextureParam(svgfCS, atrousKernel, "g_DepthTexture", depthTexture != null ? depthTexture : Texture2D.blackTexture);
                    cmd.SetComputeTextureParam(svgfCS, atrousKernel, "g_DenoisedOutput", dst);
                    cmd.DispatchCompute(svgfCS, atrousKernel, groupsX, groupsY, 1);

                    (src, dst) = (dst, src);
                }
            }

            if (src != currentColor)
            {
                cmd.Blit(src, currentColor);
            }

            if (enableTemporalAccumulation)
            {
                cmd.Blit(state.AccumulationColor, state.HistoryColor);
                state.HasHistory = true;

                if (state.HistoryNormal != null && normalTexture != null)
                {
                    cmd.Blit(normalTexture, state.HistoryNormal);
                    state.HasHistoryNormals = true;
                }
                else
                {
                    state.HasHistoryNormals = false;
                }

                if (state.HistoryDepth != null && depthTexture != null)
                {
                    cmd.Blit(depthTexture, state.HistoryDepth);
                    state.HasHistoryDepth = true;
                }
                else
                {
                    state.HasHistoryDepth = false;
                }

                state.PrevInvViewProj = currInvViewProj;
            }
            else
            {
                state.HasHistory = false;
                state.HasHistoryNormals = false;
                state.HasHistoryDepth = false;
            }
        }

        private bool TryGetHistoryState(Camera camera, int width, int height, out HistoryState state)
        {
            if (!_history.TryGetValue(camera, out state))
            {
                state = new HistoryState();
                _history[camera] = state;
            }

            if (state.HistoryColor == null || state.Width != width || state.Height != height)
            {
                ReleaseRT(state.HistoryColor);
                ReleaseRT(state.AccumulationColor);
                ReleaseRT(state.Ping);
                ReleaseRT(state.Pong);
                ReleaseRT(state.HistoryNormal);
                ReleaseRT(state.HistoryDepth);

                state.Width = width;
                state.Height = height;
                state.HasHistory = false;
                state.HasHistoryNormals = false;
                state.HasHistoryDepth = false;
                state.HistoryColor = CreateHistoryRT($"SvgfHistory_{camera.GetInstanceID()}", width, height);
                state.AccumulationColor = CreateHistoryRT($"SvgfAccum_{camera.GetInstanceID()}", width, height);
                state.Ping = CreateHistoryRT($"SvgfPing_{camera.GetInstanceID()}", width, height);
                state.Pong = CreateHistoryRT($"SvgfPong_{camera.GetInstanceID()}", width, height);
                state.HistoryNormal = CreateHistoryRT($"SvgfHistoryNormal_{camera.GetInstanceID()}", width, height, RenderTextureFormat.ARGBHalf);
                state.HistoryDepth = CreateHistoryRT($"SvgfHistoryDepth_{camera.GetInstanceID()}", width, height, RenderTextureFormat.RFloat);
                state.PrevInvViewProj = Matrix4x4.identity;
            }

            return state.HistoryColor != null && state.AccumulationColor != null;
        }

        private static RenderTexture CreateHistoryRT(string name, int width, int height, RenderTextureFormat format = RenderTextureFormat.ARGBHalf)
        {
            return RTManager.Instance != null
                ? RTManager.Instance.GetRT(name, width, height, format)
                : null;
        }

        /// <summary>
        /// Releases all cached history textures.
        /// </summary>
        public override void DestroySystem()
        {
            base.DestroySystem();
            foreach (var kv in _history)
            {
                ReleaseRT(kv.Value.HistoryColor);
                ReleaseRT(kv.Value.AccumulationColor);
                ReleaseRT(kv.Value.Ping);
                ReleaseRT(kv.Value.Pong);
                ReleaseRT(kv.Value.HistoryNormal);
                ReleaseRT(kv.Value.HistoryDepth);
            }
            _history.Clear();
        }

        private static void ReleaseRT(RenderTexture rt)
        {
            if (rt == null)
                return;
            if (RTManager.Instance != null)
            {
                RTManager.Instance.ReleaseRT(rt);
            }
            else
            {
                rt.Release();
            }
        }
    }
}


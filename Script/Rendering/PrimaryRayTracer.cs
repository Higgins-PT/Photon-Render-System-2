using System;
using System.Collections.Generic;
using PhotonSystem;
using UnityEngine;
using UnityEngine.Rendering;

namespace PhotonGISystem2
{
    /// <summary>
    /// Executes the primary ray G-buffer pass at full resolution and caches its payload buffer per camera.
    /// </summary>
    public sealed class PrimaryRayTracer
    {
        private const int PrimaryPayloadStride = 116;

        private sealed class PrimaryContext
        {
            public ComputeBuffer PayloadBuffer;
            public RenderTexture SpecularAccum;
            public RenderTexture DiffuseAlbedo;
            public RenderTexture SpecularAlbedo;
            public RenderTexture Roughness;
            public int Width;
            public int Height;
            public uint FrameIndex;

            public void Dispose(RTManager manager)
            {
                if (PayloadBuffer != null)
                {
                    if (manager != null)
                    {
                        manager.ReleaseCB(PayloadBuffer);
                    }
                    else
                    {
                        PayloadBuffer.Release();
                    }

                    PayloadBuffer = null;
                }

                ReleaseRT(manager, ref SpecularAccum);
                ReleaseRT(manager, ref DiffuseAlbedo);
                ReleaseRT(manager, ref SpecularAlbedo);
                ReleaseRT(manager, ref Roughness);
            }

            private static void ReleaseRT(RTManager manager, ref RenderTexture rt)
            {
                if (rt == null)
                    return;

                    if (manager != null)
                    manager.ReleaseRT(rt);
                    else
                    rt.Release();

                rt = null;
            }
        }

        public struct PrimaryRayData
        {
            public ComputeBuffer Buffer;
            public int Width;
            public int Height;
            public uint FrameIndex;
            public RenderTexture SpecularAccum;
            public RenderTexture SpecularAccumFull;
            public RenderTexture SpecularAccumHalf;

            public bool IsValid => Buffer != null && Width > 0 && Height > 0;
        }

        private readonly Dictionary<Camera, PrimaryContext> _contexts = new();

        /// <summary>
        /// Renders the primary ray pass for the provided camera at full resolution.
        /// </summary>
        /// <param name="renderingData">Rendering context for the active camera.</param>
        /// <param name="maxBounces">Maximum bounce count to record in the payload.</param>
        /// <param name="maxIterations">Maximum iteration count for perfect mirror reflections.</param>
        public PrimaryRayData Render(PhotonRenderingData renderingData, int maxBounces, int maxIterations = 5)
        {
            PrimaryRayData invalid = default;
            if (renderingData == null || renderingData.camera == null || renderingData.cmd == null)
                return invalid;

            ResourceManager resourceManager = ResourceManager.Instance;
            RayTraceManager rayTraceManager = RayTraceManager.Instance;
            RTManager bufferManager = RTManager.Instance;
            if (resourceManager == null || rayTraceManager == null || bufferManager == null)
                return invalid;

            RayTracingShader primaryShader = resourceManager.PrimaryRayGBufferShader;
            if (primaryShader == null)
                return invalid;

            RenderTexture target = renderingData.targetRT;
            if (target == null)
                return invalid;

            int width = target.width;
            int height = target.height;
            if (width <= 0 || height <= 0)
                return invalid;

            PrimaryContext context = GetOrCreateContext(renderingData.camera, width, height, bufferManager);
            if (context == null || context.PayloadBuffer == null)
                return invalid;

            uint frameIndex = ++context.FrameIndex;
            int clampedBounces = Mathf.Max(1, maxBounces);
            int clampedIterations = Mathf.Max(1, maxIterations);
            float zoom = Mathf.Tan(renderingData.camera.fieldOfView * Mathf.Deg2Rad * 0.5f);

            rayTraceManager.ExecuteRayTracingJob(
                renderingData,
                primaryShader,
                "MainRayGenShader",
                (uint)width,
                (uint)height,
                1u,
                (cmd, cam) =>
                {
                    cmd.SetRayTracingFloatParam(primaryShader, "g_Zoom", zoom);
                    cmd.SetRayTracingIntParam(primaryShader, "g_MaxBounces", clampedBounces);
                    cmd.SetRayTracingIntParam(primaryShader, "g_MaxIterations", clampedIterations);
                    cmd.SetRayTracingIntParam(primaryShader, "g_FrameIndex", unchecked((int)frameIndex));
                    cmd.SetRayTracingFloatParam(primaryShader, "g_Time", Time.time);
                    cmd.SetRayTracingBufferParam(primaryShader, "g_PrimaryRayPayloads", context.PayloadBuffer);
                    if (context.SpecularAccum != null)
                    {
                        cmd.SetRayTracingTextureParam(primaryShader, "g_SpecularAccumOutput", context.SpecularAccum);
                    }
                    if (context.DiffuseAlbedo != null)
                    {
                        cmd.SetRayTracingTextureParam(primaryShader, "g_DiffuseAlbedoOutput", context.DiffuseAlbedo);
                    }
                    if (context.SpecularAlbedo != null)
                    {
                        cmd.SetRayTracingTextureParam(primaryShader, "g_SpecularAlbedoOutput", context.SpecularAlbedo);
                    }
                    if (context.Roughness != null)
                    {
                        cmd.SetRayTracingTextureParam(primaryShader, "g_RoughnessOutput", context.Roughness);
                    }
                });

            renderingData.diffuseAlbedoRT = context.DiffuseAlbedo;
            renderingData.originalDiffuseAlbedoRT = context.DiffuseAlbedo;
            renderingData.scaledDiffuseAlbedoRT = context.DiffuseAlbedo;

            renderingData.specularAlbedoRT = context.SpecularAlbedo;
            renderingData.originalSpecularAlbedoRT = context.SpecularAlbedo;
            renderingData.scaledSpecularAlbedoRT = context.SpecularAlbedo;

            renderingData.roughnessRT = context.Roughness;
            renderingData.originalRoughnessRT = context.Roughness;
            renderingData.scaledRoughnessRT = context.Roughness;

            renderingData.albedoRT = context.DiffuseAlbedo;
            renderingData.originalAlbedoRT = context.DiffuseAlbedo;
            renderingData.scaledAlbedoRT = context.DiffuseAlbedo;

            renderingData.specularAccumRT = context.SpecularAccum;
            renderingData.specularAccumOriginalRT = context.SpecularAccum;

            return new PrimaryRayData
            {
                Buffer = context.PayloadBuffer,
                Width = width,
                Height = height,
                FrameIndex = frameIndex,
                SpecularAccum = context.SpecularAccum,
                SpecularAccumFull = context.SpecularAccum,
                SpecularAccumHalf = null
            };
        }

        /// <summary>
        /// Releases all cached payload buffers.
        /// </summary>
        public void Dispose()
        {
            RTManager manager = RTManager.Instance;
            foreach (var pair in _contexts)
            {
                pair.Value.Dispose(manager);
            }
            _contexts.Clear();
        }

        private PrimaryContext GetOrCreateContext(Camera camera, int width, int height, RTManager bufferManager)
        {
            if (camera == null || bufferManager == null || width <= 0 || height <= 0)
                return null;

            if (!_contexts.TryGetValue(camera, out var context))
            {
                context = new PrimaryContext();
                _contexts[camera] = context;
            }

            long pixelCountLong = (long)width * height;
            int pixelCount = (int)Math.Max(1L, Math.Min(pixelCountLong, int.MaxValue));
            bool needsResize =
                context.PayloadBuffer == null ||
                context.Width != width ||
                context.Height != height ||
                context.PayloadBuffer.count != pixelCount;

            if (needsResize)
            {
                context.PayloadBuffer = bufferManager.GetAdjustableCB(
                    $"{camera.GetInstanceID()}_PrimaryPayload",
                    pixelCount,
                    PrimaryPayloadStride,
                    ComputeBufferType.Structured);
                context.Width = width;
                context.Height = height;
                context.FrameIndex = 0;
            }

            context.SpecularAccum = bufferManager.GetAdjustableRT(
                $"{camera.GetInstanceID()}_PrimarySpecAccum",
                width,
                height,
                RenderTextureFormat.ARGBFloat,
                TextureWrapMode.Clamp,
                FilterMode.Bilinear,
                useMipMap: false,
                autoGenerateMips: false,
                enableRandomWrite: true);

            context.DiffuseAlbedo = bufferManager.GetAdjustableRT(
                $"{camera.GetInstanceID()}_PrimaryDiffuseAlbedo",
                width,
                height,
                RenderTextureFormat.ARGBFloat,
                TextureWrapMode.Clamp,
                FilterMode.Point,
                useMipMap: false,
                autoGenerateMips: false,
                enableRandomWrite: true);

            context.SpecularAlbedo = bufferManager.GetAdjustableRT(
                $"{camera.GetInstanceID()}_PrimarySpecularAlbedo",
                width,
                height,
                RenderTextureFormat.ARGBFloat,
                TextureWrapMode.Clamp,
                FilterMode.Point,
                useMipMap: false,
                autoGenerateMips: false,
                enableRandomWrite: true);

            context.Roughness = bufferManager.GetAdjustableRT(
                $"{camera.GetInstanceID()}_PrimaryRoughness",
                width,
                height,
                RenderTextureFormat.RFloat,
                TextureWrapMode.Clamp,
                FilterMode.Point,
                useMipMap: false,
                autoGenerateMips: false,
                enableRandomWrite: true);

            return context;
        }
    }
}



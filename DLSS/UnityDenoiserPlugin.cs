using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace PhotonGISystem2
{
    public enum DenoiserType
    {
        DLSS = 0,
        DLSSSuperResolution = 1,
    }

    public enum DlssQuality
    {
        MaxPerformance = 0,
        Balanced = 1,
        MaxQuality = 2,
        UltraPerformance = 3,
        UltraQuality = 4,
        Dlaa = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DenoiserConfig
    {
        public int imageWidth;
        public int imageHeight;
        public int outputWidth;
        public int outputHeight;
        public int guideAlbedo;
        public int guideNormal;
        public int temporalMode;
        public int cleanAux;
        public int prefilterAux;
        public int perfQuality;
        public int halfResolutionInput;

        public bool Equals(DenoiserConfig cfg)
        {
            return imageWidth == cfg.imageWidth &&
                   imageHeight == cfg.imageHeight &&
                   outputWidth == cfg.outputWidth &&
                   outputHeight == cfg.outputHeight &&
                   guideAlbedo == cfg.guideAlbedo &&
                   guideNormal == cfg.guideNormal &&
                   temporalMode == cfg.temporalMode &&
                   cleanAux == cfg.cleanAux &&
                   prefilterAux == cfg.prefilterAux &&
                   perfQuality == cfg.perfQuality &&
                   halfResolutionInput == cfg.halfResolutionInput;
        }
    }

    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct DLSSRenderSettings
    {
        public float jitterX;
        public float jitterY;
        public float motionScaleX;
        public float motionScaleY;
        public float preExposure;
        public float exposureScale;
        public bool resetHistory;

        public static DLSSRenderSettings Default => new DLSSRenderSettings
        {
            motionScaleX = 1.0f,
            motionScaleY = 1.0f,
            preExposure = 1.0f,
            exposureScale = 1.0f,
            jitterX = 0.0f,
            jitterY = 0.0f,
            resetHistory = false
        };
    }

    class DenoiserPluginWrapper : IDisposable
    {
        public DenoiserType Type;
        public DenoiserConfig Config;

        IntPtr m_ptr;
        RenderEventDataArray m_eventData;

        public DenoiserPluginWrapper(DenoiserType type, DenoiserConfig cfg)
        {
            Type = type;
            Config = cfg;

            m_ptr = CreateDenoiser(type, ref cfg);
            m_eventData = new RenderEventDataArray();
        }

        public void Render(CommandBuffer commands,
                           RenderTexture color,
                           RenderTexture output,
                           RenderTexture motion,
                           RenderTexture depth,
                           RenderTexture diffuseAlbedo = null,
                           RenderTexture specularAlbedo = null,
                           RenderTexture roughness = null,
                           RenderTexture albedo = null,
                           RenderTexture normal = null,
                           DLSSRenderSettings? dlssSettings = null)
        {
            DLSSRenderSettings settings = dlssSettings ?? DLSSRenderSettings.Default;

            RenderEventData eventData;
            eventData.denoiser = m_ptr;
            eventData.diffuseAlbedo = diffuseAlbedo != null ? diffuseAlbedo.GetNativeTexturePtr() : IntPtr.Zero;
            eventData.specularAlbedo = specularAlbedo != null ? specularAlbedo.GetNativeTexturePtr() : IntPtr.Zero;
            eventData.roughness = roughness != null ? roughness.GetNativeTexturePtr() : IntPtr.Zero;
            eventData.albedo = Config.guideAlbedo != 0 && albedo != null ? albedo.GetNativeTexturePtr() : IntPtr.Zero;
            eventData.normal = Config.guideNormal != 0 && normal != null ? normal.GetNativeTexturePtr() : IntPtr.Zero;
            bool needsMotion = Config.temporalMode != 0 || Type == DenoiserType.DLSS;
            eventData.flow = needsMotion && motion != null ? motion.GetNativeTexturePtr() : IntPtr.Zero;
            eventData.color = color != null ? color.GetNativeTexturePtr() : IntPtr.Zero;
            eventData.output = output != null ? output.GetNativeTexturePtr() : IntPtr.Zero;
            eventData.depth = depth != null ? depth.GetNativeTexturePtr() : IntPtr.Zero;
            eventData.jitterX = settings.jitterX;
            eventData.jitterY = settings.jitterY;
            eventData.motionScaleX = -(settings.motionScaleX == 0.0f ? 1.0f : settings.motionScaleX) * color.width;
            eventData.motionScaleY = -(settings.motionScaleY == 0.0f ? 1.0f : settings.motionScaleY) * color.height;
            eventData.preExposure = settings.preExposure == 0.0f ? 1.0f : settings.preExposure;
            eventData.exposureScale = settings.exposureScale == 0.0f ? 1.0f : settings.exposureScale;
            eventData.reset = settings.resetHistory ? 1 : 0;

            if (Type == DenoiserType.DLSS)
            {
                if (motion == null)
                    Debug.LogWarning("DLSS denoiser expects motion vector render texture.");
                if (depth == null)
                    Debug.LogWarning("DLSS denoiser expects a depth render texture.");
            }

            commands.IssuePluginEventAndData(GetRenderEventFunc(), (int) Type, m_eventData.SetData(eventData));
        }

        public void Dispose()
        {
            DestroyDenoiser(Type, m_ptr);
            m_eventData.Dispose();

            GC.SuppressFinalize(this);
        }

        [DllImport("UnityDenoiserPlugin")]
        static extern IntPtr CreateDenoiser(DenoiserType type, ref DenoiserConfig cfg);

        [DllImport("UnityDenoiserPlugin")]
        static extern void DestroyDenoiser(DenoiserType type, IntPtr ptr);

        [DllImport("UnityDenoiserPlugin")]
        static extern IntPtr GetRenderEventFunc();

        [StructLayout(LayoutKind.Sequential)]
        private struct RenderEventData
        {
            public IntPtr denoiser;
            public IntPtr diffuseAlbedo;
            public IntPtr specularAlbedo;
            public IntPtr roughness;
            public IntPtr albedo;
            public IntPtr normal;
            public IntPtr flow;
            public IntPtr color;
            public IntPtr output;
            public IntPtr depth;
            public float jitterX;
            public float jitterY;
            public float motionScaleX;
            public float motionScaleY;
            public float preExposure;
            public float exposureScale;
            public int reset;
        }

        private class RenderEventDataArray : IDisposable
        {
            const int ElementCount = 8;

            int m_index;
            IntPtr[] m_array;

            public RenderEventDataArray()
            {
                m_index = 0;

                m_array = new IntPtr[ElementCount];
                for (int i = 0; i < ElementCount; ++i)
                {
                    int maxElementSize = Marshal.SizeOf<RenderEventData>();
                    m_array[i] = Marshal.AllocHGlobal(maxElementSize);
                }
            }

            public IntPtr SetData<T>(T data)
            {
                m_index = (m_index + 1) % ElementCount;

                IntPtr ptr = m_array[m_index];
                Marshal.StructureToPtr(data, ptr, true);
                return ptr;
            }

            public void Dispose()
            {
                if (m_array != null)
                {
                    for (int i = 0; i < m_array.Length; ++i)
                    {
                        Marshal.FreeHGlobal(m_array[i]);
                    }

                    m_array = null;
                }

                GC.SuppressFinalize(this);
            }
        }
    }

    /// <summary>
    /// Unity-facing convenience wrapper for creating and driving the DLSS denoiser.
    /// </summary>
    public sealed class DLSSDenoiser : IDisposable
    {
        public bool IsInitialized => m_wrapper != null;
        public DenoiserConfig CurrentConfig { get; private set; }

        DenoiserPluginWrapper m_wrapper;

        /// <summary>
        /// Creates or recreates the DLSS denoiser with the provided frame size and guide options.
        /// </summary>
        public void Initialize(
            int inputWidth,
            int inputHeight,
            int outputWidth,
            int outputHeight,
            bool guideAlbedo = false,
            bool guideNormal = false,
            DlssQuality quality = DlssQuality.MaxQuality,
            bool halfResolutionInput = false)
        {
            DisposeWrapper();

            DenoiserConfig cfg = new DenoiserConfig
            {
                imageWidth = inputWidth,
                imageHeight = inputHeight,
                outputWidth = outputWidth,
                outputHeight = outputHeight,
                guideAlbedo = guideAlbedo ? 1 : 0,
                guideNormal = guideNormal ? 1 : 0,
                temporalMode = 1,
                cleanAux = 0,
                prefilterAux = 0,
                perfQuality = (int)quality,
                halfResolutionInput = halfResolutionInput ? 1 : 0
            };

            m_wrapper = new DenoiserPluginWrapper(DenoiserType.DLSS, cfg);
            CurrentConfig = cfg;
        }

        /// <summary>
        /// Enqueues a DLSS denoise pass on the native plugin.
        /// </summary>
        public void Render(CommandBuffer commands,
                           RenderTexture color,
                           RenderTexture output,
                           RenderTexture motion,
                           RenderTexture depth,
                           RenderTexture diffuseAlbedo = null,
                           RenderTexture specularAlbedo = null,
                           RenderTexture roughness = null,
                           RenderTexture albedo = null,
                           RenderTexture normal = null,
                           DLSSRenderSettings? settings = null)
        {
            if (m_wrapper == null)
                throw new InvalidOperationException("DLSS denoiser has not been initialized.");

            if (commands == null) throw new ArgumentNullException(nameof(commands));
            if (color == null) throw new ArgumentNullException(nameof(color));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (motion == null) throw new ArgumentNullException(nameof(motion));
            if (depth == null) throw new ArgumentNullException(nameof(depth));

            m_wrapper.Render(commands,
                             color,
                             output,
                             motion,
                             depth,
                             diffuseAlbedo,
                             specularAlbedo,
                             roughness,
                             albedo,
                             normal,
                             settings);
        }

        public void Dispose()
        {
            DisposeWrapper();
            GC.SuppressFinalize(this);
        }

        void DisposeWrapper()
        {
            if (m_wrapper != null)
            {
                m_wrapper.Dispose();
                m_wrapper = null;
            }
        }
    }

    public sealed class DLSSSuperResolution : IDisposable
    {
        public bool IsInitialized => m_wrapper != null;
        public DenoiserConfig CurrentConfig { get; private set; }

        DenoiserPluginWrapper m_wrapper;

        public void Initialize(int renderWidth, int renderHeight, int outputWidth, int outputHeight, DlssQuality quality = DlssQuality.MaxQuality)
        {
            DisposeWrapper();

            DenoiserConfig cfg = new DenoiserConfig
            {
                imageWidth = renderWidth,
                imageHeight = renderHeight,
                outputWidth = outputWidth,
                outputHeight = outputHeight,
                guideAlbedo = 0,
                guideNormal = 0,
                temporalMode = 1,
                cleanAux = 0,
                prefilterAux = 0,
                perfQuality = (int)quality,
                halfResolutionInput = 0
            };

            m_wrapper = new DenoiserPluginWrapper(DenoiserType.DLSSSuperResolution, cfg);
            CurrentConfig = cfg;
        }

        public void Render(CommandBuffer commands,
                           RenderTexture color,
                           RenderTexture output,
                           RenderTexture motion,
                           RenderTexture depth,
                           RenderTexture diffuseAlbedo = null,
                           RenderTexture specularAlbedo = null,
                           RenderTexture roughness = null,
                           RenderTexture albedo = null,
                           RenderTexture normal = null,
                           DLSSRenderSettings? settings = null)
        {
            if (m_wrapper == null)
                throw new InvalidOperationException("DLSS super resolution has not been initialized.");

            if (commands == null) throw new ArgumentNullException(nameof(commands));
            if (color == null) throw new ArgumentNullException(nameof(color));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (motion == null) throw new ArgumentNullException(nameof(motion));
            if (depth == null) throw new ArgumentNullException(nameof(depth));

            m_wrapper.Render(commands,
                             color,
                             output,
                             motion,
                             depth,
                             diffuseAlbedo,
                             specularAlbedo,
                             roughness,
                             albedo,
                             normal,
                             settings);
        }

        public void Dispose()
        {
            DisposeWrapper();
            GC.SuppressFinalize(this);
        }

        void DisposeWrapper()
        {
            if (m_wrapper != null)
            {
                m_wrapper.Dispose();
                m_wrapper = null;
            }
        }
    }
}


using PhotonSystem;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.UI;
using UnityEngine.Rendering.RendererUtils;
using Unity.Mathematics;

namespace PhotonGISystem2 {
    public class PhotonRendererFeature : ScriptableRendererFeature
    {
        public const string EnvironmentCameraName = "PhotonEnvironmentCaptureCamera";

        class PhotonRenderPass : ScriptableRenderPass
        {
            private readonly Material _motionVectorMaterial;

            private class PassData
            {
                public TextureHandle renderingTexture;
                public TextureHandle activeColorTexture;
                public TextureHandle normalTexture;
                public TextureHandle depthTexture;
                public RenderTexture motionVectorTexture;
                public Camera camera;
                public RenderGraph renderGraph;
                public ContextContainer frameData;
                public Material motionVectorMaterial;
            }
            public static bool IsSceneCamera(Camera cam)
            {
                if (cam == null)
                    return false;

                if (cam.name == EnvironmentCameraName)
                    return false;

                switch (cam.cameraType)
                {
                    case CameraType.Preview:
                    case CameraType.Reflection:
                    case CameraType.VR:
                        return false;
                }

                return cam.cameraType == CameraType.Game;
            }
            public PhotonRenderPass(Material motionVectorMaterial)
            {
                _motionVectorMaterial = motionVectorMaterial;
            }

            static void ExecutePass(PassData data, UnsafeGraphContext context)
            {
                if (IsSceneCamera(data.camera))
                {
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    cmd.Blit(data.activeColorTexture, data.renderingTexture);
                    RenderTexture renderingTexture = ((RenderTexture)data.renderingTexture);
                    if (data.camera == null)
                    {
                        return;
                    }
                    RenderTexture motionVectorRT = null;
                    if (data.motionVectorMaterial != null && RTManager.Instance != null && renderingTexture != null)
                    {
                        motionVectorRT = RTManager.Instance.GetRT($"MotionVectorRT_{data.camera.GetInstanceID()}",
                            renderingTexture.width,
                            renderingTexture.height,
                            RenderTextureFormat.RGFloat);
                        cmd.Blit(null, motionVectorRT, data.motionVectorMaterial);
                    }

                    PhotonRenderingData photonRenderingData = new PhotonRenderingData(
                        data.renderingTexture,
                        data.normalTexture,
                        data.depthTexture,
                        data.activeColorTexture,
                        cmd,
                        data.camera,
                        motionVectorRT);
                    if(PhotonRenderManager.Instance != null)
                    {
                        PhotonRenderManager.Instance.ExecuteRendering(photonRenderingData);
                    }
                    
                }
            }
            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                const string passName = "Photon Render Pass";
                using (var builder = renderGraph.AddUnsafePass<PassData>(passName, out var passData))
                {
                    UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                    UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                    TextureDesc rgDesc = new TextureDesc(cameraData.cameraTargetDescriptor.width, cameraData.cameraTargetDescriptor.height);
                    rgDesc.name = "_CameraDepthTexture";
                    rgDesc.dimension = cameraData.cameraTargetDescriptor.dimension;
                    rgDesc.clearBuffer = false;
                    rgDesc.autoGenerateMips = true;
                    rgDesc.useMipMap = true;
                    rgDesc.msaaSamples = MSAASamples.None;
                    rgDesc.filterMode = FilterMode.Bilinear;
                    rgDesc.wrapMode = TextureWrapMode.Clamp;
                    rgDesc.enableRandomWrite = true;
                    rgDesc.bindTextureMS = cameraData.cameraTargetDescriptor.bindMS;
                    rgDesc.colorFormat = cameraData.cameraTargetDescriptor.graphicsFormat;
                    rgDesc.depthBufferBits = 0;
                    rgDesc.isShadowMap = false;
                    passData.renderingTexture = renderGraph.CreateTexture(rgDesc);
                    passData.activeColorTexture = resourceData.activeColorTexture;
                    passData.camera = cameraData.camera;
                    passData.normalTexture = resourceData.cameraNormalsTexture;
                    passData.depthTexture = resourceData.cameraDepthTexture;
                    passData.renderGraph = renderGraph;
                    passData.frameData = frameData;
                    passData.motionVectorMaterial = _motionVectorMaterial;
                    builder.UseTexture(passData.activeColorTexture, AccessFlags.ReadWrite);
                    builder.UseTexture(passData.renderingTexture, AccessFlags.ReadWrite);
                    builder.UseTexture(passData.normalTexture, AccessFlags.Read);
                    builder.UseTexture(passData.depthTexture, AccessFlags.ReadWrite);
                    builder.AllowPassCulling(false);
                    ConfigureInput(ScriptableRenderPassInput.Motion | ScriptableRenderPassInput.Normal | ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color);
                    builder.SetRenderFunc((PassData data, UnsafeGraphContext context) => ExecutePass(data, context));
                }
            }

            public override void OnCameraCleanup(CommandBuffer cmd)
            {
            }
        }

        private Material _motionVectorMaterial;
        PhotonRenderPass m_ScriptablePass;
        public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        /// <inheritdoc/>
        public override void Create()
        {
            Shader motionVectorShader = null;
            if (ResourceManager.Instance != null)
            {
                motionVectorShader = ResourceManager.Instance.MotionVectorShader;
            }

            if (motionVectorShader == null)
            {
                motionVectorShader = Shader.Find("PhotonSystem/MotionVector");
            }

            if (motionVectorShader != null)
            {
                _motionVectorMaterial = new Material(motionVectorShader);
            }

            m_ScriptablePass = new PhotonRenderPass(_motionVectorMaterial);
            m_ScriptablePass.renderPassEvent = renderPassEvent;
        }


        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            renderer.EnqueuePass(m_ScriptablePass);
        }
    }
    public class PhotonRenderingData
    {
        public PhotonRenderingData(RenderTexture targetRT, RenderTexture normalRT, RenderTexture depthRT, RenderTexture activeRT, CommandBuffer cmd, Camera camera, RenderTexture motionVectorRT = null)
        {
            this.targetRT = targetRT;
            this.normalRT = normalRT;
            this.depthRT = depthRT;
            this.activeRT = activeRT;
            this.motionVectorRT = motionVectorRT;
            this.cmd = cmd;
            this.camera = camera;

            originalTargetRT = targetRT;
            originalNormalRT = normalRT;
            originalDepthRT = depthRT;
            originalMotionVectorRT = motionVectorRT;
            originalAlbedoRT = albedoRT;
            originalDiffuseAlbedoRT = diffuseAlbedoRT;
            originalSpecularAlbedoRT = specularAlbedoRT;
            originalRoughnessRT = roughnessRT;
            specularAccumOriginalRT = specularAccumRT;

            scaledTargetRT = targetRT;
            scaledNormalRT = normalRT;
            scaledDepthRT = depthRT;
            scaledMotionVectorRT = motionVectorRT;
            scaledAlbedoRT = albedoRT;
            scaledDiffuseAlbedoRT = diffuseAlbedoRT;
            scaledSpecularAlbedoRT = specularAlbedoRT;
            scaledRoughnessRT = roughnessRT;
            specularAccumRT = specularAccumOriginalRT;
        }

        public RenderTexture targetRT;
        public RenderTexture originalTargetRT;
        public RenderTexture scaledTargetRT;

        public RenderTexture normalRT;
        public RenderTexture originalNormalRT;
        public RenderTexture scaledNormalRT;

        public RenderTexture depthRT;
        public RenderTexture originalDepthRT;
        public RenderTexture scaledDepthRT;

        public RenderTexture activeRT;
        public RenderTexture motionVectorRT;
        public RenderTexture originalMotionVectorRT;
        public RenderTexture scaledMotionVectorRT;

        public RenderTexture albedoRT;
        public RenderTexture originalAlbedoRT;
        public RenderTexture scaledAlbedoRT;

        public RenderTexture metallicRT;
        public RenderTexture sparseLightLevelRT;

        public RenderTexture diffuseAlbedoRT;
        public RenderTexture originalDiffuseAlbedoRT;
        public RenderTexture scaledDiffuseAlbedoRT;

        public RenderTexture specularAlbedoRT;
        public RenderTexture originalSpecularAlbedoRT;
        public RenderTexture scaledSpecularAlbedoRT;

        public RenderTexture roughnessRT;
        public RenderTexture originalRoughnessRT;
        public RenderTexture scaledRoughnessRT;
        public RenderTexture specularAccumRT;
        public RenderTexture specularAccumOriginalRT;
        public CommandBuffer cmd;
        public Camera camera;
        public bool usesHalfResolutionInput;
        public bool deferFullResolutionResolve;
    }
}
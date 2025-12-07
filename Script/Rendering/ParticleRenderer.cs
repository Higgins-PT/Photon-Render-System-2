using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PhotonGISystem2
{
    /// <summary>
    /// Renders all visible particle systems back into the active camera target after the ray traced result.
    /// </summary>
    public class ParticleRenderer
    {
        private readonly List<ParticleSystemRenderer> _rendererCache = new(64);
        private readonly List<RendererSortData> _visibleRenderers = new(64);
        private int _lastCacheFrame = -1;
        private static readonly int WorldSpaceCameraPosId = Shader.PropertyToID("_WorldSpaceCameraPos");
        private static readonly string[] ForwardPassNames =
        {
            "UniversalForward",
            "ForwardBase",
            "ForwardLit",
            "ForwardOnly",
            "SRPDefaultUnlit",
            "Sprite Unlit"
        };

        private struct RendererSortData
        {
            public ParticleSystemRenderer Renderer;
            public int SortingLayerValue;
            public int SortingOrder;
            public float DistanceToCamera;
        }

        /// <summary>
        /// Draws the particle systems that pass the camera filters using the command buffer supplied by PhotonRenderingData.
        /// </summary>
        public void Render(PhotonRenderingData renderingData)
        {
            if (renderingData == null || renderingData.cmd == null)
                return;

            Camera camera = renderingData.camera;
            if (camera == null)
                return;

            RenderTexture colorTarget = renderingData.activeRT ?? renderingData.targetRT;
            if (colorTarget == null)
                return;

            CommandBuffer cmd = renderingData.cmd;
            RenderTexture depthTarget = renderingData.originalDepthRT ?? renderingData.depthRT;

            cmd.BeginSample("Photon Particle Renderer");
            cmd.SetupCameraProperties(camera);
            cmd.SetViewProjectionMatrices(camera.cameraToWorldMatrix, camera.projectionMatrix);
            SetRenderTarget(cmd, colorTarget, depthTarget);
            SetCameraMatrices(cmd, camera, colorTarget);

            RefreshRendererCache();
            CollectVisibleRenderers(camera);
            IssueDrawCommands(cmd);

            _visibleRenderers.Clear();
            cmd.EndSample("Photon Particle Renderer");
        }

        private static void SetRenderTarget(CommandBuffer cmd, RenderTexture colorTarget, RenderTexture depthTarget)
        {
            if (depthTarget != null)
            {

                cmd.SetRenderTarget(
                    colorTarget,
                    depthTarget);
                
            }
            else
            {
                cmd.SetRenderTarget(
                    colorTarget);
            }
        }

        private static void SetCameraMatrices(CommandBuffer cmd, Camera camera, RenderTexture colorTarget)
        {
            Rect viewport = new Rect(0f, 0f, colorTarget.width, colorTarget.height);
            cmd.SetViewport(viewport);

            Matrix4x4 view = camera.worldToCameraMatrix;

            Matrix4x4 projection = camera.projectionMatrix;
            cmd.SetViewProjectionMatrices(view, projection);
            Vector3 camPos = camera.transform.position;
            cmd.SetGlobalVector(WorldSpaceCameraPosId, new Vector4(camPos.x, camPos.y, camPos.z, 1f));
        }

        private void RefreshRendererCache()
        {
            if (_lastCacheFrame == Time.frameCount)
                return;

            _rendererCache.Clear();
            ParticleSystemRenderer[] allRenderers = Object.FindObjectsOfType<ParticleSystemRenderer>(includeInactive: false);
            for (int i = 0; i < allRenderers.Length; i++)
            {
                ParticleSystemRenderer renderer = allRenderers[i];
                if (renderer == null)
                    continue;

                _rendererCache.Add(renderer);
            }

            _lastCacheFrame = Time.frameCount;
        }

        private void CollectVisibleRenderers(Camera camera)
        {
            _visibleRenderers.Clear();
            Vector3 cameraPosition = camera.transform.position;
            Vector3 cameraForward = camera.transform.forward;
            uint cameraRenderingLayers = uint.MaxValue;
            int cullingMask = camera.cullingMask;

            for (int i = 0; i < _rendererCache.Count; i++)
            {
                ParticleSystemRenderer renderer = _rendererCache[i];
                if (!IsRenderable(renderer, camera, cullingMask, cameraRenderingLayers))
                    continue;

                Bounds bounds = renderer.bounds;
                float distance = Vector3.Dot(cameraForward, bounds.center - cameraPosition);
                _visibleRenderers.Add(new RendererSortData
                {
                    Renderer = renderer,
                    SortingLayerValue = SortingLayer.GetLayerValueFromID(renderer.sortingLayerID),
                    SortingOrder = renderer.sortingOrder,
                    DistanceToCamera = distance
                });
            }

            _visibleRenderers.Sort(SortVisibleRenderers);
        }

        private static bool IsRenderable(ParticleSystemRenderer renderer, Camera camera, int cameraCullingMask, uint cameraRenderingLayers)
        {
            if (renderer == null || !renderer.enabled || renderer.forceRenderingOff)
                return false;
            if (!renderer.gameObject.activeInHierarchy)
                return false;
            ParticleSystem particleSystem = renderer.GetComponent<ParticleSystem>();
            if (particleSystem == null || !particleSystem.IsAlive(true))
                return false;

            int rendererLayerMask = 1 << renderer.gameObject.layer;
            if ((cameraCullingMask & rendererLayerMask) == 0)
                return false;

            if ((cameraRenderingLayers & renderer.renderingLayerMask) == 0)
                return false;

            return renderer.bounds.size.sqrMagnitude > 0f;
        }

        private void IssueDrawCommands(CommandBuffer cmd)
        {
            for (int i = 0; i < _visibleRenderers.Count; i++)
            {
                RendererSortData entry = _visibleRenderers[i];
                ParticleSystemRenderer renderer = entry.Renderer;

                Material[] materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0)
                    continue;

                for (int subMesh = 0; subMesh < materials.Length; subMesh++)
                {
                    Material material = materials[subMesh];
                    if (material == null)
                        continue;

                    int forwardPass = GetForwardPassIndex(material);
                    if (forwardPass < 0)
                        continue;

                    
                    cmd.DrawRenderer(renderer, material, subMesh, forwardPass);
                }
            }
        }

        private static int SortVisibleRenderers(RendererSortData a, RendererSortData b)
        {
            int layerCompare = a.SortingLayerValue.CompareTo(b.SortingLayerValue);
            if (layerCompare != 0)
                return layerCompare;

            int orderCompare = a.SortingOrder.CompareTo(b.SortingOrder);
            if (orderCompare != 0)
                return orderCompare;

            // Transparent content should render back-to-front.
            return -a.DistanceToCamera.CompareTo(b.DistanceToCamera);
        }

        private static int GetForwardPassIndex(Material material)
        {
            foreach (string pass in ForwardPassNames)
            {
                int index = material.FindPass(pass);
                if (index >= 0)
                    return index;
            }

            return -1;
        }
    }
}


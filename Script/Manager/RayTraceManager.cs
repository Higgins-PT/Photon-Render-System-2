using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace PhotonGISystem2
{
    /// <summary>
    /// Centralized manager that owns the shared RTAS, geometry buffers, and cubemap capture used by ray tracing features.
    /// </summary>
    public class RayTraceManager : PGSingleton<RayTraceManager>
    {
        #region Serialized Fields

        [Header("Environment Capture")]
        [SerializeField] private int environmentCubemapSize = 512;
        [SerializeField] private float maxRayDistance = 1000f;

        [Header("Mesh Buffer Settings")]
        [SerializeField] private MeshBufferSettings staticBufferSettings = MeshBufferSettings.CreateDefaultStatic();
        [SerializeField] private MeshBufferSettings dynamicBufferSettings = MeshBufferSettings.CreateDefaultDynamic();
        [SerializeField, Range(0.05f, 0.9f)] private float fragmentationThreshold = 0.3f;
        [SerializeField] private bool autoShrink = true;
        [SerializeField, Range(0.05f, 0.9f)] private float shrinkThreshold = 0.3f;
        [SerializeField] private bool showMemoryStats = true;

        [Serializable]
        public struct MeshPoolQualitySettings
        {
            public int initialVertexCapacity;
            public int initialTriangleCapacity;
            public float maxMemoryMB;

            public void Clamp()
            {
                initialVertexCapacity = Mathf.Max(128, initialVertexCapacity);
                initialTriangleCapacity = Mathf.Max(128, initialTriangleCapacity);
                maxMemoryMB = Mathf.Max(8f, maxMemoryMB);
            }

            internal static MeshPoolQualitySettings FromSettings(MeshBufferSettings settings)
            {
                return new MeshPoolQualitySettings
                {
                    initialVertexCapacity = settings.initialVertexCapacity,
                    initialTriangleCapacity = settings.initialTriangleCapacity,
                    maxMemoryMB = settings.maxMemoryMB
                };
            }

            public static MeshPoolQualitySettings DefaultStatic
            {
                get
                {
                    MeshBufferSettings defaults = MeshBufferSettings.CreateDefaultStatic();
                    return new MeshPoolQualitySettings
                    {
                        initialVertexCapacity = defaults.initialVertexCapacity,
                        initialTriangleCapacity = defaults.initialTriangleCapacity,
                        maxMemoryMB = defaults.maxMemoryMB
                    };
                }
            }

            public static MeshPoolQualitySettings DefaultDynamic
            {
                get
                {
                    MeshBufferSettings defaults = MeshBufferSettings.CreateDefaultDynamic();
                    return new MeshPoolQualitySettings
                    {
                        initialVertexCapacity = defaults.initialVertexCapacity,
                        initialTriangleCapacity = defaults.initialTriangleCapacity,
                        maxMemoryMB = defaults.maxMemoryMB
                    };
                }
            }
        }

        [Serializable]
        public struct QualitySettings
        {
            public int environmentCubemapSize;
            public float maxRayDistance;
            public MeshPoolQualitySettings staticPool;
            public MeshPoolQualitySettings dynamicPool;
            public float fragmentationThreshold;
            public bool autoShrink;
            public float shrinkThreshold;
            public bool showMemoryStats;

            public void Clamp()
            {
                environmentCubemapSize = Mathf.Max(16, environmentCubemapSize);
                maxRayDistance = Mathf.Max(0.1f, maxRayDistance);
                staticPool.Clamp();
                dynamicPool.Clamp();
                fragmentationThreshold = Mathf.Clamp(fragmentationThreshold, 0.05f, 0.9f);
                shrinkThreshold = Mathf.Clamp(shrinkThreshold, 0.05f, 0.9f);
            }

            public static QualitySettings Default => new QualitySettings
            {
                environmentCubemapSize = 512,
                maxRayDistance = 1000f,
                staticPool = MeshPoolQualitySettings.DefaultStatic,
                dynamicPool = MeshPoolQualitySettings.DefaultDynamic,
                fragmentationThreshold = 0.3f,
                autoShrink = true,
                shrinkThreshold = 0.3f,
                showMemoryStats = true
            };
        }

        #endregion

        #region Private Fields

        private RayTracingAccelerationStructure _rtas;
        private MeshBufferPool _staticPool;
        private MeshBufferPool _dynamicPool;

        private readonly Dictionary<int, MeshRecord> _meshRecords = new();
        private readonly List<InstanceRecord> _instanceRecords = new();
        private readonly List<PhotonInstanceData> _instanceDataCpu = new();
        private readonly List<PhotonInstanceLookup> _instanceLookupCpu = new();

        private ComputeBuffer _instanceDataBuffer;
        private ComputeBuffer _instanceLookupBuffer;
        private static ComputeBuffer _fallbackInstanceDataBuffer;
        private static ComputeBuffer _fallbackInstanceLookupBuffer;

        private ComputeBuffer _stagingFloat3;
        private ComputeBuffer _stagingFloat2;
        private ComputeBuffer _stagingUInt3;

        private int _kernelCopyFloat3 = -1;
        private int _kernelCopyFloat2 = -1;
        private int _kernelCopyUInt3 = -1;

        private GameObject _environmentCaptureGo;
        private Camera _environmentCamera;
        private RenderTexture _environmentCubemap;
        private static Cubemap _fallbackEnvironmentCubemap;
        private GameObject _environmentCaptureGO;

        private uint _frameIndex;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the maximum ray distance used for probe baking and ray dispatch settings.
        /// </summary>
        public float MaxRayDistance => maxRayDistance;

        public MeshMemoryStats StaticBufferStats => _staticPool?.Stats ?? MeshMemoryStats.Empty;
        public MeshMemoryStats DynamicBufferStats => _dynamicPool?.Stats ?? MeshMemoryStats.Empty;

        public bool AutoShrink
        {
            get => autoShrink;
            set => autoShrink = value;
        }

        public float AutoShrinkThreshold
        {
            get => shrinkThreshold;
            set => shrinkThreshold = Mathf.Clamp(value, 0.05f, 0.9f);
        }

        public QualitySettings CaptureQualitySettings()
        {
            return new QualitySettings
            {
                environmentCubemapSize = environmentCubemapSize,
                maxRayDistance = maxRayDistance,
                staticPool = MeshPoolQualitySettings.FromSettings(staticBufferSettings),
                dynamicPool = MeshPoolQualitySettings.FromSettings(dynamicBufferSettings),
                fragmentationThreshold = fragmentationThreshold,
                autoShrink = autoShrink,
                shrinkThreshold = shrinkThreshold,
                showMemoryStats = showMemoryStats
            };
        }

        public void ApplyQualitySettings(QualitySettings settings, bool rebuildBuffers = false)
        {
            settings.Clamp();

            bool cubemapChanged = environmentCubemapSize != settings.environmentCubemapSize;
            bool staticPoolChanged = ApplyMeshPoolSettings(staticBufferSettings, settings.staticPool);
            bool dynamicPoolChanged = ApplyMeshPoolSettings(dynamicBufferSettings, settings.dynamicPool);

            environmentCubemapSize = settings.environmentCubemapSize;
            maxRayDistance = settings.maxRayDistance;
            fragmentationThreshold = settings.fragmentationThreshold;
            autoShrink = settings.autoShrink;
            shrinkThreshold = settings.shrinkThreshold;
            showMemoryStats = settings.showMemoryStats;

            if (cubemapChanged)
            {
                DestroyEnvironmentCamera();
                EnsureEnvironmentCamera();
            }

            if (rebuildBuffers && (staticPoolChanged || dynamicPoolChanged))
            {
                ResetAllBuffers();
            }
            else
            {
                if (_staticPool != null)
                    _staticPool.SetMaxMemory(staticBufferSettings.maxMemoryMB);
                if (_dynamicPool != null)
                    _dynamicPool.SetMaxMemory(dynamicBufferSettings.maxMemoryMB);
            }
        }

        public bool ShowMemoryStats => showMemoryStats;
        
        /// <summary>
        /// Clears all cached geometry and rebuilds every GPU buffer from scratch on the next frame.
        /// </summary>
        public void ResetAllBuffers()
        {
            _staticPool?.Dispose();
            _dynamicPool?.Dispose();
            _staticPool = null;
            _dynamicPool = null;

            _meshRecords.Clear();
            _instanceRecords.Clear();
            _instanceDataCpu.Clear();
            _instanceLookupCpu.Clear();

            ReleaseInstanceBuffers();
            ReleaseStagingBuffers();

            InitializePools();
            BuildRTAS();
        }

        #endregion

        #region Unity Lifecycle

        protected override void OnAwake()
        {
            base.OnAwake();
            InitializeAccelerationStructure();
            InitializePools();
            EnsureEnvironmentCamera();
        }

        private void OnEnable()
        {
            InitializePools();
        }

        private void Update()
        {
            _frameIndex++;
            BuildRTAS();
            RenderSkyboxToCubemap();
        }

        public override void DestroySystem()
        {
            base.DestroySystem();
            ReleaseAccelerationStructure();
            _staticPool?.Dispose();
            _dynamicPool?.Dispose();
            _staticPool = null;
            _dynamicPool = null;
            ReleaseInstanceBuffers();
            ReleaseStagingBuffers();
            DestroyEnvironmentCamera();
            ReleaseFallbackBuffers();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Allows external systems to query current memory usage for a mesh pool.
        /// </summary>
        public MeshMemoryStats GetBufferStats(bool dynamicBuffer)
        {
            return dynamicBuffer ? DynamicBufferStats : StaticBufferStats;
        }

        /// <summary>
        /// Adjusts the maximum memory cap (in megabytes) for the specified pool.
        /// </summary>
        public void SetMaxMemoryMB(bool dynamicBuffer, float megabytes)
        {
            if (dynamicBuffer)
            {
                dynamicBufferSettings.maxMemoryMB = Mathf.Max(8f, megabytes);
                _dynamicPool?.SetMaxMemory(dynamicBufferSettings.maxMemoryMB);
            }
            else
            {
                staticBufferSettings.maxMemoryMB = Mathf.Max(16f, megabytes);
                _staticPool?.SetMaxMemory(staticBufferSettings.maxMemoryMB);
            }
        }

        /// <summary>
        /// Executes a ray tracing shader with the specified dispatch dimensions and shared setup.
        /// </summary>
        public void ExecuteRayTracingJob(
            PhotonRenderingData renderingData,
            RayTracingShader shader,
            string rayGenName,
            uint dispatchWidth,
            uint dispatchHeight,
            uint dispatchDepth,
            Action<CommandBuffer, Camera> setupAction)
        {
            if (renderingData == null || shader == null)
                return;

            CommandBuffer cmd = renderingData.cmd;
            Camera referenceCamera = renderingData.camera;

            if (cmd == null || referenceCamera == null)
                return;

            if (!SystemInfo.supportsRayTracingShaders)
                return;

            if (dispatchWidth == 0u || dispatchHeight == 0u || dispatchDepth == 0u)
                return;

            if (_rtas == null)
                InitializeAccelerationStructure();

            cmd.SetRayTracingShaderPass(shader, "MyRayPass");
            cmd.SetRayTracingAccelerationStructure(shader, "g_SceneAccelStruct", _rtas);

            BindGeometryBuffers(cmd);

            Texture envTexture = _environmentCubemap != null
                ? (Texture)_environmentCubemap
                : GetFallbackEnvironmentCubemap();
            cmd.SetRayTracingTextureParam(shader, "g_EnvironmentMap", envTexture);

            float nearClip = referenceCamera != null ? Mathf.Max(0.0001f, referenceCamera.nearClipPlane) : 0.01f;
            float farClip = referenceCamera != null ? referenceCamera.farClipPlane : maxRayDistance;
            if (float.IsInfinity(farClip) || float.IsNaN(farClip) || farClip <= 0f)
                farClip = maxRayDistance > 0f ? maxRayDistance : 1000f;
            farClip = Mathf.Max(nearClip + 0.001f, farClip);
            cmd.SetRayTracingFloatParam(shader, "g_CameraNearPlane", nearClip);
            cmd.SetRayTracingFloatParam(shader, "g_CameraFarPlane", farClip);

            setupAction?.Invoke(cmd, referenceCamera);

            cmd.DispatchRays(
                shader,
                string.IsNullOrEmpty(rayGenName) ? "MainRayGenShader" : rayGenName,
                dispatchWidth,
                dispatchHeight,
                dispatchDepth,
                referenceCamera);
        }

        #endregion

        #region Private Methods - RTAS / Geometry

        private void InitializeAccelerationStructure()
        {
            if (_rtas != null || !SystemInfo.supportsRayTracingShaders)
                return;

            var settings = new RayTracingAccelerationStructure.Settings(
                RayTracingAccelerationStructure.ManagementMode.Manual,
                RayTracingAccelerationStructure.RayTracingModeMask.Everything,
                -1);
            _rtas = new RayTracingAccelerationStructure(settings);
        }

        private void ReleaseAccelerationStructure()
        {
            if (_rtas != null)
            {
                _rtas.Release();
                _rtas = null;
            }
        }

        private void InitializePools()
        {
            if (_staticPool == null)
                _staticPool = new MeshBufferPool(this, PhotonMeshInfo.MeshResidency.Static, staticBufferSettings);

            if (_dynamicPool == null)
                _dynamicPool = new MeshBufferPool(this, PhotonMeshInfo.MeshResidency.Dynamic, dynamicBufferSettings);
        }

        private void BuildRTAS()
        {
            if (_rtas == null)
                return;

            PopulateRTASWithAllRenderers(_rtas);
            using (var cmd = CommandBufferPool.Get("RayTraceManager_BuildRTAS"))
            {
                cmd.BuildRayTracingAccelerationStructure(_rtas);
                Graphics.ExecuteCommandBuffer(cmd);
            }

            UploadInstanceBuffers();
        }

        private void PopulateRTASWithAllRenderers(RayTracingAccelerationStructure rtas)
        {
            if (rtas == null)
            {
                Debug.LogError("[RayTraceManager] PopulateRTASWithAllRenderers received a null RTAS instance.");
                return;
            }

            _instanceRecords.Clear();
            _instanceDataCpu.Clear();
            _instanceLookupCpu.Clear();

            rtas.ClearInstances();

            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            uint instanceId = 0;
            foreach (var renderer in renderers)
            {
                if (!renderer.enabled)
                    continue;

                Mesh mesh = ResolveMesh(renderer);
                if (mesh == null || CountMeshTriangles(mesh) == 0)
                    continue;

                PhotonMeshInfo meshInfo = renderer.GetComponent<PhotonMeshInfo>();
                PhotonMeshInfo.MeshResidency residency = meshInfo != null
                    ? meshInfo.Residency
                    : PhotonMeshInfo.MeshResidency.Static;

                MeshSlot slot = EnsureMeshSlot(mesh, residency, renderer);
                if (slot == null)
                    continue;

                Material[] sharedMaterials = renderer.sharedMaterials;
                int subMeshCount = mesh.subMeshCount > 0 ? mesh.subMeshCount : 1;

                for (int subMeshIndex = 0; subMeshIndex < subMeshCount; ++subMeshIndex)
                {
                    if (mesh.GetTopology(subMeshIndex) != MeshTopology.Triangles)
                        continue;

                    Material subMeshMaterial = ResolveSubMeshMaterial(renderer, sharedMaterials, subMeshIndex);
                    if (subMeshMaterial == null)
                        continue;

                    int subMeshTriangleOffset = ComputeSubMeshTriangleOffset(mesh, subMeshIndex);

                    var config = new RayTracingMeshInstanceConfig
                    {
                        mesh = mesh,
                        material = subMeshMaterial,
                        subMeshFlags = RayTracingSubMeshFlags.Enabled,
                        layer = renderer.gameObject.layer,
                        renderingLayerMask = renderer.renderingLayerMask,
                        mask = 0xFF,
                        dynamicGeometry = residency == PhotonMeshInfo.MeshResidency.Dynamic,
                        enableTriangleCulling = true,
                        frontTriangleCounterClockwise = false,
                        subMeshIndex = (uint)subMeshIndex
                    };

                    Matrix4x4 matrix = renderer.localToWorldMatrix;
                    Matrix4x4? prevMatrix = null;
                    int handle = rtas.AddInstance(config, matrix, prevMatrix, instanceId);

                    if (handle == 0)
                    {
#if UNITY_EDITOR
                        Debug.LogWarning($"[RayTraceManager] AddInstance failed for renderer {renderer.name} subMesh {subMeshIndex}");
#endif
                        continue;
                    }

                    InstanceRecord record = new InstanceRecord
                    {
                        InstanceId = instanceId,
                        SlotToken = EncodeSlotToken(slot, residency),
                        LocalToWorld = matrix,
                        Residency = residency,
                        SubMeshIndex = subMeshIndex,
                        SubMeshTriangleOffset = subMeshTriangleOffset
                    };
                    _instanceRecords.Add(record);
                    instanceId++;
                }
            }

            FinalizeMeshUsage();
            _staticPool?.PostFrameMaintenance(fragmentationThreshold, autoShrink, shrinkThreshold);
            _dynamicPool?.PostFrameMaintenance(fragmentationThreshold, autoShrink, shrinkThreshold);
        }

        private MeshSlot EnsureMeshSlot(Mesh mesh, PhotonMeshInfo.MeshResidency residency, Renderer owner)
        {
            if (mesh == null)
                return null;

            int meshId = mesh.GetInstanceID();
            _meshRecords.TryGetValue(meshId, out MeshRecord record);

            MeshBufferPool pool = residency == PhotonMeshInfo.MeshResidency.Static ? _staticPool : _dynamicPool;

            bool forceUpload = residency == PhotonMeshInfo.MeshResidency.Dynamic;
            if (record == null || record.Residency != residency)
            {
                MeshSlot newSlot = AllocateSlotForMesh(mesh, residency, pool);
                if (newSlot == null)
                    return null;

                _meshRecords[meshId] = new MeshRecord
                {
                    Mesh = mesh,
                    Residency = residency,
                    Slot = newSlot,
                    SlotToken = EncodeSlotToken(newSlot, residency),
                    LastFrameUsed = _frameIndex
                };
                return newSlot;
            }

            record.LastFrameUsed = _frameIndex;

            if (forceUpload)
            {
                UploadMeshData(mesh, record.Slot, pool);
            }

            return record.Slot;
        }

        private MeshSlot AllocateSlotForMesh(Mesh mesh, PhotonMeshInfo.MeshResidency residency, MeshBufferPool pool)
        {
            MeshGeometryData data = MeshGeometryData.Create(mesh);
            if (!data.IsValid)
                return null;

            MeshSlot slot = pool.TryAllocate(mesh, data.VertexCount, data.TriangleCount);
            if (slot == null)
            {
                Debug.LogWarning($"[RayTraceManager] {residency} buffer has reached its capacity. Mesh {mesh.name} will be skipped.");
                return null;
            }

            UploadMeshData(data, slot, pool);
            return slot;
        }

        private void UploadMeshData(Mesh mesh, MeshSlot slot, MeshBufferPool pool)
        {
            MeshGeometryData data = MeshGeometryData.Create(mesh);
            if (!data.IsValid)
                return;
            UploadMeshData(data, slot, pool);
        }

        private void UploadMeshData(MeshGeometryData data, MeshSlot slot, MeshBufferPool pool)
        {
            if (!data.IsValid || slot == null || pool == null)
                return;

            EnsureStagingBuffer(ref _stagingFloat3, data.VertexCount, sizeof(float) * 3);
            EnsureStagingBuffer(ref _stagingFloat2, data.VertexCount, sizeof(float) * 2);
            EnsureStagingBuffer(ref _stagingUInt3, data.TriangleCount, sizeof(uint) * 3);

            _stagingFloat3.SetData(data.Normals, 0, 0, data.VertexCount);
            _stagingFloat2.SetData(data.UVs, 0, 0, data.VertexCount);
            _stagingUInt3.SetData(data.Indices, 0, 0, data.TriangleCount);

            DispatchCopyFloat3(_stagingFloat3, pool.NormalBuffer, data.VertexCount, 0, slot.VertexOffset);
            DispatchCopyFloat2(_stagingFloat2, pool.UvBuffer, data.VertexCount, 0, slot.VertexOffset);
            DispatchCopyUInt3(_stagingUInt3, pool.IndexBuffer, data.TriangleCount, 0, slot.TriangleOffset);

            pool.MarkSlotUploaded(slot, data.VertexCount, data.TriangleCount);
        }

        private void FinalizeMeshUsage()
        {
            List<int> toRemove = null;
            foreach (var pair in _meshRecords)
            {
                MeshRecord record = pair.Value;
                if (record.LastFrameUsed == _frameIndex)
                    continue;

                toRemove ??= new List<int>();
                toRemove.Add(pair.Key);
                MeshBufferPool pool = record.Residency == PhotonMeshInfo.MeshResidency.Dynamic ? _dynamicPool : _staticPool;
                pool?.Release(record.Slot);
            }

            if (toRemove != null)
            {
                foreach (int key in toRemove)
                {
                    _meshRecords.Remove(key);
                }
            }
        }

        private static int EncodeSlotToken(MeshSlot slot, PhotonMeshInfo.MeshResidency residency)
        {
            if (slot == null)
                return 0;

            int baseIndex = slot.SlotIndex + 1;
            return residency == PhotonMeshInfo.MeshResidency.Static ? baseIndex : -baseIndex;
        }

        private static bool ApplyMeshPoolSettings(MeshBufferSettings target, MeshPoolQualitySettings source)
        {
            int clampedVertices = Mathf.Max(128, source.initialVertexCapacity);
            int clampedTriangles = Mathf.Max(128, source.initialTriangleCapacity);
            float clampedMemory = Mathf.Max(8f, source.maxMemoryMB);

            bool changed = target.initialVertexCapacity != clampedVertices
                || target.initialTriangleCapacity != clampedTriangles
                || !Mathf.Approximately(target.maxMemoryMB, clampedMemory);

            target.initialVertexCapacity = clampedVertices;
            target.initialTriangleCapacity = clampedTriangles;
            target.maxMemoryMB = clampedMemory;

            return changed;
        }

        #endregion

        #region Private Methods - Compute Upload Helpers

        private void EnsureStagingBuffer(ref ComputeBuffer buffer, int requiredCount, int stride)
        {
            if (requiredCount <= 0)
                requiredCount = 1;

            if (buffer != null && buffer.count >= requiredCount && buffer.stride == stride)
                return;

            buffer?.Release();
            buffer = new ComputeBuffer(Mathf.NextPowerOfTwo(requiredCount), stride, ComputeBufferType.Structured);
        }

        private ComputeShader MeshBufferCompute
        {
            get
            {
                if (ResourceManager.Instance == null)
                    return null;
                return ResourceManager.Instance.MeshBufferCompute;
            }
        }

        private void InitializeKernels()
        {
            if (_kernelCopyFloat3 >= 0)
                return;

            ComputeShader cs = MeshBufferCompute;
            if (cs == null)
                return;

            _kernelCopyFloat3 = cs.FindKernel("CopyFloat3");
            _kernelCopyFloat2 = cs.FindKernel("CopyFloat2");
            _kernelCopyUInt3 = cs.FindKernel("CopyUInt3");
        }

        private void DispatchCopyFloat3(ComputeBuffer source, ComputeBuffer target, int count, int sourceOffset, int targetOffset)
        {
            if (count <= 0 || source == null || target == null)
                return;

            InitializeKernels();
            if (_kernelCopyFloat3 < 0)
                return;

            ComputeShader cs = MeshBufferCompute;
            int groups = Mathf.Max(1, Mathf.CeilToInt(count / 64f));
            using var cmd = CommandBufferPool.Get("MeshPool_CopyFloat3");
            cmd.SetComputeIntParam(cs, "g_CopyCount", count);
            cmd.SetComputeIntParam(cs, "g_TargetOffset", targetOffset);
            cmd.SetComputeIntParam(cs, "g_SourceOffset", sourceOffset);
            cmd.SetComputeBufferParam(cs, _kernelCopyFloat3, "g_TargetFloat3", target);
            cmd.SetComputeBufferParam(cs, _kernelCopyFloat3, "g_SourceFloat3", source);
            cmd.DispatchCompute(cs, _kernelCopyFloat3, groups, 1, 1);
            Graphics.ExecuteCommandBuffer(cmd);
        }

        private void DispatchCopyFloat2(ComputeBuffer source, ComputeBuffer target, int count, int sourceOffset, int targetOffset)
        {
            if (count <= 0 || source == null || target == null)
                return;

            InitializeKernels();
            if (_kernelCopyFloat2 < 0)
                return;

            ComputeShader cs = MeshBufferCompute;
            int groups = Mathf.Max(1, Mathf.CeilToInt(count / 64f));
            using var cmd = CommandBufferPool.Get("MeshPool_CopyFloat2");
            cmd.SetComputeIntParam(cs, "g_CopyCount", count);
            cmd.SetComputeIntParam(cs, "g_TargetOffset", targetOffset);
            cmd.SetComputeIntParam(cs, "g_SourceOffset", sourceOffset);
            cmd.SetComputeBufferParam(cs, _kernelCopyFloat2, "g_TargetFloat2", target);
            cmd.SetComputeBufferParam(cs, _kernelCopyFloat2, "g_SourceFloat2", source);
            cmd.DispatchCompute(cs, _kernelCopyFloat2, groups, 1, 1);
            Graphics.ExecuteCommandBuffer(cmd);
        }

        private void DispatchCopyUInt3(ComputeBuffer source, ComputeBuffer target, int count, int sourceOffset, int targetOffset)
        {
            if (count <= 0 || source == null || target == null)
                return;

            InitializeKernels();
            if (_kernelCopyUInt3 < 0)
                return;

            ComputeShader cs = MeshBufferCompute;
            int groups = Mathf.Max(1, Mathf.CeilToInt(count / 64f));
            using var cmd = CommandBufferPool.Get("MeshPool_CopyUInt3");
            cmd.SetComputeIntParam(cs, "g_CopyCount", count);
            cmd.SetComputeIntParam(cs, "g_TargetOffset", targetOffset);
            cmd.SetComputeIntParam(cs, "g_SourceOffset", sourceOffset);
            cmd.SetComputeBufferParam(cs, _kernelCopyUInt3, "g_TargetUInt3", target);
            cmd.SetComputeBufferParam(cs, _kernelCopyUInt3, "g_SourceUInt3", source);
            cmd.DispatchCompute(cs, _kernelCopyUInt3, groups, 1, 1);
            Graphics.ExecuteCommandBuffer(cmd);
        }

        private void ReleaseStagingBuffers()
        {
            _stagingFloat3?.Release();
            _stagingFloat2?.Release();
            _stagingUInt3?.Release();
            _stagingFloat3 = null;
            _stagingFloat2 = null;
            _stagingUInt3 = null;
        }

        private static void ReleaseFallbackBuffers()
        {
            _fallbackInstanceDataBuffer?.Release();
            _fallbackInstanceLookupBuffer?.Release();
            _fallbackInstanceDataBuffer = null;
            _fallbackInstanceLookupBuffer = null;
        }

        private static ComputeBuffer GetFallbackInstanceDataBuffer()
        {
            if (_fallbackInstanceDataBuffer == null)
            {
                _fallbackInstanceDataBuffer = new ComputeBuffer(1, PhotonInstanceData.Stride, ComputeBufferType.Structured);
                var defaults = new PhotonInstanceData[1];
                _fallbackInstanceDataBuffer.SetData(defaults);
            }

            return _fallbackInstanceDataBuffer;
        }

        private static ComputeBuffer GetFallbackInstanceLookupBuffer()
        {
            if (_fallbackInstanceLookupBuffer == null)
            {
                _fallbackInstanceLookupBuffer = new ComputeBuffer(1, PhotonInstanceLookup.Stride, ComputeBufferType.Structured);
                var defaults = new PhotonInstanceLookup[1];
                _fallbackInstanceLookupBuffer.SetData(defaults);
            }

            return _fallbackInstanceLookupBuffer;
        }

        #endregion

        #region Private Methods - Instance Buffers

        private void UploadInstanceBuffers()
        {
            if (_instanceRecords.Count == 0)
            {
                ReleaseInstanceBuffers();
                return;
            }

            _instanceDataCpu.Clear();
            _instanceLookupCpu.Clear();

            foreach (InstanceRecord record in _instanceRecords)
            {
                _instanceDataCpu.Add(PhotonInstanceData.FromMatrix(record.LocalToWorld));
                _instanceLookupCpu.Add(new PhotonInstanceLookup
                {
                    SlotToken = record.SlotToken,
                    SubMeshTriangleOffset = (uint)Mathf.Max(0, record.SubMeshTriangleOffset),
                    Padding0 = 0u,
                    Padding1 = 0u
                });
            }

            EnsureBuffer(ref _instanceDataBuffer, _instanceDataCpu.Count, PhotonInstanceData.Stride);
            EnsureBuffer(ref _instanceLookupBuffer, _instanceLookupCpu.Count, PhotonInstanceLookup.Stride);

            _instanceDataBuffer.SetData(_instanceDataCpu);
            _instanceLookupBuffer.SetData(_instanceLookupCpu);
        }

        private void ReleaseInstanceBuffers()
        {
            _instanceDataBuffer?.Release();
            _instanceLookupBuffer?.Release();
            _instanceDataBuffer = null;
            _instanceLookupBuffer = null;
        }

        private static void EnsureBuffer(ref ComputeBuffer buffer, int count, int stride)
        {
            if (count <= 0)
                count = 1;

            if (buffer != null && buffer.count == count && buffer.stride == stride)
                return;

            buffer?.Release();
            buffer = new ComputeBuffer(count, stride, ComputeBufferType.Structured);
        }

        private void BindGeometryBuffers(CommandBuffer cmd)
        {
            _staticPool?.Bind(cmd, "_PhotonStaticNormals", "_PhotonStaticUVs", "_PhotonStaticIndices", "_PhotonStaticSlots");
            _dynamicPool?.Bind(cmd, "_PhotonDynamicNormals", "_PhotonDynamicUVs", "_PhotonDynamicIndices", "_PhotonDynamicSlots");

            cmd.SetGlobalBuffer("_PhotonInstanceData", _instanceDataBuffer ?? GetFallbackInstanceDataBuffer());
            cmd.SetGlobalBuffer("_PhotonInstanceLookup", _instanceLookupBuffer ?? GetFallbackInstanceLookupBuffer());
        }

        #endregion

        #region Private Methods - Environment Capture

        /// <summary>
        /// Ensures the hidden environment capture camera and cubemap exist.
        /// </summary>
        private void EnsureEnvironmentCamera()
        {
            if (_environmentCaptureGO == null)
            {
                _environmentCaptureGO = new GameObject(PhotonRendererFeature.EnvironmentCameraName);
                _environmentCaptureGO.hideFlags = HideFlags.HideAndDontSave;
            }

            if (_environmentCamera == null)
            {
                _environmentCamera = _environmentCaptureGO.AddComponent<Camera>();
                _environmentCamera.enabled = false;
                _environmentCamera.clearFlags = CameraClearFlags.Skybox;
                _environmentCamera.cullingMask = 0;
                _environmentCamera.fieldOfView = 90f;
                _environmentCamera.aspect = 1f;
            }

            if (_environmentCubemap == null)
            {
                _environmentCubemap = new RenderTexture(environmentCubemapSize, environmentCubemapSize, 16)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    dimension = TextureDimension.Cube,
                    enableRandomWrite = false,
                    autoGenerateMips = false
                };
                _environmentCubemap.Create();
            }
        }

        /// <summary>
        /// Destroys the hidden environment capture camera and cubemap.
        /// </summary>
        private void DestroyEnvironmentCamera()
        {
            if (_environmentCubemap != null)
            {
                _environmentCubemap.Release();
                DestroyImmediate(_environmentCubemap);
                _environmentCubemap = null;
            }

            if (_environmentCamera != null)
            {
                DestroyImmediate(_environmentCamera);
                _environmentCamera = null;
            }

            if (_environmentCaptureGO != null)
            {
                DestroyImmediate(_environmentCaptureGO);
                _environmentCaptureGO = null;
            }
        }

        /// <summary>
        /// Updates the environment cubemap using the hidden capture camera.
        /// </summary>
        private void RenderSkyboxToCubemap()
        {
            EnsureEnvironmentCamera();
            if (_environmentCamera == null || _environmentCubemap == null)
                return;

            bool success = _environmentCamera.RenderToCubemap(_environmentCubemap);
            if (!success)
            {
                Debug.LogError("[RayTraceManager] Failed to render skybox cubemap.");
            }
        }

        /// <summary>
        /// Provides a fallback cubemap when no environment capture is available.
        /// </summary>
        /// <returns>A black cubemap as fallback.</returns>
        private static Cubemap GetFallbackEnvironmentCubemap()
        {
            if (_fallbackEnvironmentCubemap != null)
                return _fallbackEnvironmentCubemap;

            const int size = 16;
            _fallbackEnvironmentCubemap = new Cubemap(size, TextureFormat.RGBAHalf, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; ++i)
            {
                pixels[i] = Color.black;
            }

            for (int face = 0; face < 6; ++face)
            {
                _fallbackEnvironmentCubemap.SetPixels(pixels, (CubemapFace)face);
            }
            _fallbackEnvironmentCubemap.Apply();
            return _fallbackEnvironmentCubemap;
        }

        #endregion

        #region Private Methods - Geometry Helpers

        private static Mesh ResolveMesh(Renderer renderer)
        {
            var smr = renderer as SkinnedMeshRenderer;
            if (smr != null)
                return smr.sharedMesh;

            var mf = renderer.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private static Material ResolveSubMeshMaterial(Renderer renderer, Material[] sharedMaterials, int subMeshIndex)
        {
            if (renderer == null)
                return null;

            if (sharedMaterials != null)
            {
                if (subMeshIndex >= 0 && subMeshIndex < sharedMaterials.Length)
                {
                    var material = sharedMaterials[subMeshIndex];
                    if (material != null)
                        return material;
                }

                for (int i = sharedMaterials.Length - 1; i >= 0; --i)
                {
                    var fallback = sharedMaterials[i];
                    if (fallback != null)
                        return fallback;
                }
            }

            return renderer.sharedMaterial;
        }

        private static int CountMeshTriangles(Mesh mesh)
        {
            if (mesh == null)
                return 0;

            int total = 0;
            int subMeshCount = mesh.subMeshCount;
            for (int sub = 0; sub < subMeshCount; ++sub)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                    continue;

                total += (int)(mesh.GetIndexCount(sub) / 3);
            }

            return total;
        }

        private static int ComputeSubMeshTriangleOffset(Mesh mesh, int subMeshIndex)
        {
            if (mesh == null || subMeshIndex <= 0)
                return 0;

            int offset = 0;
            int maxIndex = Mathf.Min(subMeshIndex, mesh.subMeshCount);
            for (int i = 0; i < maxIndex; ++i)
            {
                if (mesh.GetTopology(i) != MeshTopology.Triangles)
                    continue;

                offset += (int)(mesh.GetIndexCount(i) / 3);
            }

            return Mathf.Max(0, offset);
        }

        private static Vector3[] EnsureNormals(Mesh mesh)
        {
            var normals = mesh.normals;
            if (normals != null && normals.Length == mesh.vertexCount)
                return normals;

            return GenerateNormals(mesh);
        }

        private static Vector3[] GenerateNormals(Mesh mesh)
        {
            var normals = new Vector3[mesh.vertexCount];
            var vertices = mesh.vertices;
            if (vertices == null || vertices.Length == 0)
                return normals;

            for (int sub = 0; sub < mesh.subMeshCount; ++sub)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                    continue;

                var indices = mesh.GetIndices(sub);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int i0 = indices[i];
                    int i1 = indices[i + 1];
                    int i2 = indices[i + 2];

                    Vector3 v0 = vertices[i0];
                    Vector3 v1 = vertices[i1];
                    Vector3 v2 = vertices[i2];
                    Vector3 faceNormal = Vector3.Cross(v1 - v0, v2 - v0);

                    normals[i0] += faceNormal;
                    normals[i1] += faceNormal;
                    normals[i2] += faceNormal;
                }
            }

            for (int i = 0; i < normals.Length; ++i)
            {
                Vector3 n = normals[i];
                normals[i] = n.sqrMagnitude > 0.0f ? n.normalized : Vector3.up;
            }

            return normals;
        }

        private static Vector2[] EnsureUvs(Mesh mesh)
        {
            var uvs = mesh.uv;
            if (uvs != null && uvs.Length == mesh.vertexCount)
                return uvs;

            return new Vector2[mesh.vertexCount];
        }

        #endregion

        #region Nested Types

        [Serializable]
        public sealed class MeshBufferSettings
        {
            public int initialVertexCapacity = 8192;
            public int initialTriangleCapacity = 16384;
            public float maxMemoryMB = 2048f;

            public static MeshBufferSettings CreateDefaultStatic()
            {
                return new MeshBufferSettings
                {
                    initialVertexCapacity = 32768,
                    initialTriangleCapacity = 65536,
                    maxMemoryMB = 2048f
                };
            }

            public static MeshBufferSettings CreateDefaultDynamic()
            {
                return new MeshBufferSettings
                {
                    initialVertexCapacity = 8192,
                    initialTriangleCapacity = 16384,
                    maxMemoryMB = 2048f
                };
            }
        }

        public readonly struct MeshMemoryStats
        {
            public readonly int VertexCapacity;
            public readonly int VertexUsage;
            public readonly int TriangleCapacity;
            public readonly int TriangleUsage;
            public readonly int ActiveSlots;
            public readonly int TotalSlots;

            public static MeshMemoryStats Empty => new MeshMemoryStats(0, 0, 0, 0, 0, 0);

            public MeshMemoryStats(int vertexCapacity, int vertexUsage, int triangleCapacity, int triangleUsage, int activeSlots, int totalSlots)
            {
                VertexCapacity = vertexCapacity;
                VertexUsage = vertexUsage;
                TriangleCapacity = triangleCapacity;
                TriangleUsage = triangleUsage;
                ActiveSlots = activeSlots;
                TotalSlots = totalSlots;
            }

            public float UsedVertexMB => VertexCapacity == 0 ? 0f : VertexUsage * 12f / (1024f * 1024f);
            public float CapacityVertexMB => VertexCapacity * 12f / (1024f * 1024f);
        }

        private sealed class MeshBufferPool : IDisposable
        {
            private readonly RayTraceManager _owner;
            private readonly PhotonMeshInfo.MeshResidency _residency;
            private readonly List<MeshSlot> _slots = new();
            private readonly Queue<int> _freeSlots = new();
            private readonly List<PhotonMeshSlot> _slotCpu = new();

            private ComputeBuffer _normals;
            private ComputeBuffer _uvs;
            private ComputeBuffer _indices;
            private ComputeBuffer _slotBuffer;

            private int _vertexCapacity;
            private int _triangleCapacity;
            private int _vertexUsage;
            private int _triangleUsage;
            private int _invalidVertexCount;
            private int _invalidTriangleCount;
            private float _maxMemoryBytes;

            private readonly MeshBufferSettings _settings;

            public MeshBufferPool(RayTraceManager owner, PhotonMeshInfo.MeshResidency residency, MeshBufferSettings settings)
            {
                _owner = owner;
                _residency = residency;
                _settings = settings;
                _vertexCapacity = Mathf.Max(1024, settings.initialVertexCapacity);
                _triangleCapacity = Mathf.Max(1024, settings.initialTriangleCapacity);
                _maxMemoryBytes = settings.maxMemoryMB * 1024f * 1024f;
                EnsureBuffers();
            }

            public MeshMemoryStats Stats => new MeshMemoryStats(
                _vertexCapacity,
                _vertexUsage,
                _triangleCapacity,
                _triangleUsage,
                _slots.Count - _freeSlots.Count,
                _slots.Count);

            public ComputeBuffer NormalBuffer => _normals;
            public ComputeBuffer UvBuffer => _uvs;
            public ComputeBuffer IndexBuffer => _indices;

            public void SetMaxMemory(float megabytes)
            {
                _maxMemoryBytes = Mathf.Max(8f, megabytes) * 1024f * 1024f;
            }

            public MeshSlot TryAllocate(Mesh mesh, int vertexCount, int triangleCount)
            {
                if (!EnsureCapacity(vertexCount, triangleCount))
                    return null;

                int slotIndex = _freeSlots.Count > 0 ? _freeSlots.Dequeue() : _slots.Count;
                MeshSlot slot;
                if (slotIndex < _slots.Count)
                {
                    slot = _slots[slotIndex];
                }
                else
                {
                    slot = new MeshSlot { SlotIndex = slotIndex };
                    _slots.Add(slot);
                    _slotCpu.Add(new PhotonMeshSlot());
                }

            slot.SlotIndex = slotIndex;
                slot.Mesh = mesh;
                slot.VertexOffset = _vertexUsage;
                slot.VertexCount = vertexCount;
                slot.TriangleOffset = _triangleUsage;
                slot.TriangleCount = triangleCount;
                slot.Valid = true;

                _vertexUsage += vertexCount;
                _triangleUsage += triangleCount;
                _slots[slotIndex] = slot;

                UpdateSlotCpu(slotIndex, slot);
                UploadSlotBuffer();
                return slot;
            }

            public void MarkSlotUploaded(MeshSlot slot, int vertexCount, int triangleCount)
            {
                slot.VertexCount = vertexCount;
                slot.TriangleCount = triangleCount;
                _slots[slot.SlotIndex] = slot;
                UpdateSlotCpu(slot.SlotIndex, slot);
                UploadSlotBuffer();
            }

            public void Release(MeshSlot slot)
            {
                if (slot == null || !slot.Valid)
                    return;

                _invalidVertexCount += slot.VertexCount;
                _invalidTriangleCount += slot.TriangleCount;
                slot.Valid = false;
                _slots[slot.SlotIndex] = slot;
                UpdateSlotCpu(slot.SlotIndex, slot);
                UploadSlotBuffer();
                _freeSlots.Enqueue(slot.SlotIndex);
            }

            public void PostFrameMaintenance(float fragmentationThreshold, bool autoShrink, float shrinkThreshold)
            {
                float fragVertices = _vertexCapacity > 0 ? (float)_invalidVertexCount / _vertexCapacity : 0f;
                float fragTriangles = _triangleCapacity > 0 ? (float)_invalidTriangleCount / _triangleCapacity : 0f;
                bool needsDefrag = fragVertices >= fragmentationThreshold || fragTriangles >= fragmentationThreshold;

                if (needsDefrag)
                {
                    Defragment();
                }
                else if (autoShrink)
                {
                    float usageRatio = _vertexCapacity > 0 ? (float)_vertexUsage / _vertexCapacity : 0f;
                    if (usageRatio <= shrinkThreshold)
                    {
                        Shrink();
                    }
                }
            }

            public void Bind(CommandBuffer cmd, string normalsId, string uvsId, string indicesId, string slotBufferId)
            {
                if (_normals != null)
                    cmd.SetGlobalBuffer(normalsId, _normals);
                if (_uvs != null)
                    cmd.SetGlobalBuffer(uvsId, _uvs);
                if (_indices != null)
                    cmd.SetGlobalBuffer(indicesId, _indices);
                if (_slotBuffer != null)
                    cmd.SetGlobalBuffer(slotBufferId, _slotBuffer);
            }

            public void Dispose()
            {
                _normals?.Release();
                _uvs?.Release();
                _indices?.Release();
                _slotBuffer?.Release();

                _normals = null;
                _uvs = null;
                _indices = null;
                _slotBuffer = null;
                _slots.Clear();
                _freeSlots.Clear();
                _slotCpu.Clear();
            }

            private bool EnsureCapacity(int addVertices, int addTriangles)
            {
                bool resized = false;
                while (_vertexUsage + addVertices > _vertexCapacity)
                {
                    int newCapacity = Mathf.Min(_vertexCapacity * 2, CapacityFromMemoryLimit(_normals, addVertices, 12));
                    if (newCapacity <= _vertexCapacity)
                        return false;
                    _vertexCapacity = newCapacity;
                    resized = true;
                }

                while (_triangleUsage + addTriangles > _triangleCapacity)
                {
                    int newCapacity = Mathf.Min(_triangleCapacity * 2, CapacityFromMemoryLimit(_indices, addTriangles, 12));
                    if (newCapacity <= _triangleCapacity)
                        return false;
                    _triangleCapacity = newCapacity;
                    resized = true;
                }

                if (resized)
                {
                    ResizeBuffersPreservingData();
                }

                return true;
            }

            private int CapacityFromMemoryLimit(ComputeBuffer buffer, int required, int stride)
            {
                if (_maxMemoryBytes <= 0f)
                    return int.MaxValue;

                float otherBuffers =
                    (_normals == buffer ? 0f : BufferBytes(_normals)) +
                    (_uvs == buffer ? 0f : BufferBytes(_uvs)) +
                    (_indices == buffer ? 0f : BufferBytes(_indices));
                float available = Mathf.Max(0f, _maxMemoryBytes - otherBuffers);

                int maxByMemory = (int)(available / stride);
                return Math.Max(required, maxByMemory);
            }

            private static float BufferBytes(ComputeBuffer buffer)
            {
                return buffer == null ? 0f : buffer.count * buffer.stride;
            }

            private void EnsureBuffers()
            {
                EnsureBuffer(ref _normals, _vertexCapacity, sizeof(float) * 3);
                EnsureBuffer(ref _uvs, _vertexCapacity, sizeof(float) * 2);
                EnsureBuffer(ref _indices, _triangleCapacity, sizeof(uint) * 3);
                EnsureBuffer(ref _slotBuffer, Math.Max(1, _slotCpu.Count), PhotonMeshSlot.Stride);
            }

            private void ResizeBuffersPreservingData()
            {
                var oldNormals = _normals;
                var oldUvs = _uvs;
                var oldIndices = _indices;

                var newNormals = new ComputeBuffer(_vertexCapacity, sizeof(float) * 3);
                var newUvs = new ComputeBuffer(_vertexCapacity, sizeof(float) * 2);
                var newIndices = new ComputeBuffer(_triangleCapacity, sizeof(uint) * 3);

                int copyVertexCount = Mathf.Min(_vertexUsage, oldNormals != null ? oldNormals.count : 0);
                int copyTriangleCount = Mathf.Min(_triangleUsage, oldIndices != null ? oldIndices.count : 0);

                if (copyVertexCount > 0)
                {
                    if (oldNormals != null)
                        _owner.DispatchCopyFloat3(oldNormals, newNormals, copyVertexCount, 0, 0);
                    if (oldUvs != null)
                        _owner.DispatchCopyFloat2(oldUvs, newUvs, copyVertexCount, 0, 0);
                }

                if (copyTriangleCount > 0 && oldIndices != null)
                {
                    _owner.DispatchCopyUInt3(oldIndices, newIndices, copyTriangleCount, 0, 0);
                }

                _normals = newNormals;
                _uvs = newUvs;
                _indices = newIndices;

                oldNormals?.Release();
                oldUvs?.Release();
                oldIndices?.Release();

                EnsureBuffer(ref _slotBuffer, Math.Max(1, _slotCpu.Count), PhotonMeshSlot.Stride);
                UploadSlotBuffer();
            }

            private void EnsureBuffer(ref ComputeBuffer buffer, int count, int stride)
            {
                if (count <= 0)
                    count = 1;

                if (buffer != null && buffer.count == count && buffer.stride == stride)
                    return;

                buffer?.Release();
                buffer = new ComputeBuffer(count, stride, ComputeBufferType.Structured);
            }

            private void Defragment()
            {
                if (_slots.Count == 0)
                    return;

                var newNormals = new ComputeBuffer(_vertexCapacity, sizeof(float) * 3);
                var newUvs = new ComputeBuffer(_vertexCapacity, sizeof(float) * 2);
                var newIndices = new ComputeBuffer(_triangleCapacity, sizeof(uint) * 3);

                int vertexCursor = 0;
                int triangleCursor = 0;
                for (int i = 0; i < _slots.Count; i++)
                {
                    MeshSlot slot = _slots[i];
                    if (!slot.Valid)
                        continue;

                    DispatchDefragCopy(slot.VertexOffset, vertexCursor, slot.VertexCount, _normals, newNormals);
                    DispatchDefragCopy(slot.VertexOffset, vertexCursor, slot.VertexCount, _uvs, newUvs);
                    DispatchDefragCopy(slot.TriangleOffset, triangleCursor, slot.TriangleCount, _indices, newIndices);

                    slot.VertexOffset = vertexCursor;
                    slot.TriangleOffset = triangleCursor;

                    vertexCursor += slot.VertexCount;
                    triangleCursor += slot.TriangleCount;
                    _slots[i] = slot;
                    UpdateSlotCpu(i, slot);
                }

                _normals.Release();
                _uvs.Release();
                _indices.Release();

                _normals = newNormals;
                _uvs = newUvs;
                _indices = newIndices;

                _vertexUsage = vertexCursor;
                _triangleUsage = triangleCursor;
                _invalidVertexCount = 0;
                _invalidTriangleCount = 0;
                UploadSlotBuffer();
            }

            private void Shrink()
            {
                _vertexCapacity = Mathf.Max(_settings.initialVertexCapacity, Mathf.NextPowerOfTwo(Mathf.Max(_vertexUsage, 1)));
                _triangleCapacity = Mathf.Max(_settings.initialTriangleCapacity, Mathf.NextPowerOfTwo(Mathf.Max(_triangleUsage, 1)));
                Defragment();
            }

            private void DispatchDefragCopy(int srcOffset, int dstOffset, int count, ComputeBuffer source, ComputeBuffer target)
            {
                if (count <= 0)
                    return;

                if (source == _normals)
                    _owner.DispatchCopyFloat3(source, target, count, srcOffset, dstOffset);
                else if (source == _uvs)
                    _owner.DispatchCopyFloat2(source, target, count, srcOffset, dstOffset);
                else
                    _owner.DispatchCopyUInt3(source, target, count, srcOffset, dstOffset);
            }

            private void UpdateSlotCpu(int slotIndex, MeshSlot slot)
            {
                while (_slotCpu.Count <= slotIndex)
                {
                    _slotCpu.Add(new PhotonMeshSlot());
                }

                _slotCpu[slotIndex] = new PhotonMeshSlot
                {
                    vertexBase = (uint)Mathf.Max(0, slot.VertexOffset),
                    vertexCount = (uint)Mathf.Max(0, slot.VertexCount),
                    triangleBase = (uint)Mathf.Max(0, slot.TriangleOffset),
                    triangleCount = (uint)Mathf.Max(0, slot.TriangleCount)
                };
            }

            private void UploadSlotBuffer()
            {
                EnsureBuffer(ref _slotBuffer, Math.Max(1, _slotCpu.Count), PhotonMeshSlot.Stride);
                if (_slotCpu.Count > 0)
                    _slotBuffer.SetData(_slotCpu);
            }
        }

        private sealed class MeshSlot
        {
            public int SlotIndex;
            public Mesh Mesh;
            public int VertexOffset;
            public int VertexCount;
            public int TriangleOffset;
            public int TriangleCount;
            public bool Valid;
        }

        private sealed class MeshRecord
        {
            public Mesh Mesh;
            public PhotonMeshInfo.MeshResidency Residency;
            public MeshSlot Slot;
            public int SlotToken;
            public uint LastFrameUsed;
        }

        private struct InstanceRecord
        {
            public uint InstanceId;
            public int SlotToken;
            public Matrix4x4 LocalToWorld;
            public PhotonMeshInfo.MeshResidency Residency;
            public int SubMeshIndex;
            public int SubMeshTriangleOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct PhotonInstanceData
        {
            public readonly Vector4 Row0;
            public readonly Vector4 Row1;
            public readonly Vector4 Row2;
            public readonly Vector4 Row3;
            public readonly Vector4 NormalRow0;
            public readonly Vector4 NormalRow1;
            public readonly Vector4 NormalRow2;

            public const int Stride = sizeof(float) * 4 * 7;

            private PhotonInstanceData(Matrix4x4 matrix)
            {
                Row0 = matrix.GetRow(0);
                Row1 = matrix.GetRow(1);
                Row2 = matrix.GetRow(2);
                Row3 = matrix.GetRow(3);

                Matrix4x4 normalMatrix = matrix.inverse.transpose;
                NormalRow0 = normalMatrix.GetRow(0);
                NormalRow1 = normalMatrix.GetRow(1);
                NormalRow2 = normalMatrix.GetRow(2);
            }

            public static PhotonInstanceData FromMatrix(Matrix4x4 matrix)
            {
                return new PhotonInstanceData(matrix);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PhotonInstanceLookup
        {
            public int SlotToken;
            public uint SubMeshTriangleOffset;
            public uint Padding0;
            public uint Padding1;

            public static int Stride => sizeof(int) * 4;
        }

        private struct PhotonMeshSlot
        {
            public uint vertexBase;
            public uint vertexCount;
            public uint triangleBase;
            public uint triangleCount;

            public static int Stride => sizeof(uint) * 4;
        }

        private readonly struct MeshGeometryData
        {
            public readonly Vector3[] Normals;
            public readonly Vector2[] UVs;
            public readonly Vector3Int[] Indices;

            public int VertexCount => Normals?.Length ?? 0;
            public int TriangleCount => Indices?.Length ?? 0;
            public bool IsValid => VertexCount > 0 && TriangleCount > 0;

            public MeshGeometryData(Vector3[] normals, Vector2[] uvs, Vector3Int[] indices)
            {
                Normals = normals;
                UVs = uvs;
                Indices = indices;
            }

            public static MeshGeometryData Create(Mesh mesh)
            {
                if (mesh == null)
                    return default;

                Vector3[] normals = EnsureNormals(mesh);
                Vector2[] uvs = EnsureUvs(mesh);
                int[] rawIndices = mesh.triangles;
                if (rawIndices == null || rawIndices.Length < 3)
                    return default;

                int triCount = rawIndices.Length / 3;
                Vector3Int[] packed = new Vector3Int[triCount];
                for (int i = 0; i < triCount; i++)
                {
                    int baseIndex = i * 3;
                    packed[i] = new Vector3Int(rawIndices[baseIndex], rawIndices[baseIndex + 1], rawIndices[baseIndex + 2]);
                }

                return new MeshGeometryData(normals, uvs, packed);
            }
        }

        #endregion
    }
}
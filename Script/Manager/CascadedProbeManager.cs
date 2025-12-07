using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PhotonGISystem2;
using PhotonSystem;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace PhotonGISystem2
{
    /// <summary>
    /// Manages cascaded probe grids for global illumination.
    /// Generates probe positions in cascaded layers and bakes SH coefficients using ray tracing.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class CascadedProbeManager : PGSingleton<CascadedProbeManager>
    {
        #region Serialized Fields

        [Header("Cascade Settings")]
        [SerializeField] private int probesPerAxis = 16;
        [SerializeField] private int cascadeCount = 9;
        [SerializeField] private float smallestCellSize = 20f;

        [Header("Gizmo Settings")]
        [SerializeField] private bool drawGizmos = true;
        [SerializeField] private float gizmoSize = 0.5f;
        [SerializeField] private Color gizmoColor = Color.cyan;

        [Header("Probe Bake Settings")]
        [SerializeField, Min(1)] private uint bakeSamplesPerProbe = 64;
        [SerializeField, Min(1)] private uint bakeMaxBounces = 3;
        [SerializeField] private float bakeRayBias = 0.02f;
        [SerializeField, Min(0f)] private float bakeMinRadianceWeight = 0.01f;

        #endregion

        #region Private Fields

        private readonly Dictionary<Camera, ProbeStorage> _cameraProbeCache = new();

        #endregion

        #region Properties

        /// <summary>
        /// Gets the number of probes per axis in each cascade.
        /// </summary>
        public int ProbesPerAxis => probesPerAxis;

        /// <summary>
        /// Gets the number of cascades.
        /// </summary>
        public int CascadeCount => cascadeCount;
        /// <summary>
        /// Gets the smallest cell size of the first cascade.
        /// </summary>
        public float SmallestCellSize => smallestCellSize;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// Validates and clamps serialized values on awake.
        /// </summary>
        protected override void OnAwake()
        {
            base.OnAwake();
            probesPerAxis = Mathf.Max(1, probesPerAxis);
            cascadeCount = Mathf.Max(1, cascadeCount);
            smallestCellSize = Mathf.Max(0.01f, smallestCellSize);
            bakeMinRadianceWeight = Mathf.Max(0f, bakeMinRadianceWeight);
        }

        /// <summary>
        /// Cleans up all probe data on system destruction.
        /// </summary>
        public override void DestroySystem()
        {
            base.DestroySystem();
            ClearAllProbes();
        }

        /// <summary>
        /// Draws gizmos for all cached probes if enabled.
        /// </summary>
        private void OnDrawGizmos()
        {
            if (!drawGizmos)
                return;

            Gizmos.color = gizmoColor;
            foreach (var kvp in _cameraProbeCache)
            {
                if (!kvp.Value.Positions.IsCreated)
                    continue;

                NativeArray<ProbePosition> probes = kvp.Value.Positions;
                for (int i = 0; i < probes.Length; i++)
                {
                    Gizmos.DrawSphere(probes[i].position, gizmoSize);
                }
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets or generates probe buffers for the given camera.
        /// Releases old probes for the camera and generates new ones if needed.
        /// </summary>
        /// <param name="renderingData">The rendering context containing camera and command buffer.</param>
        /// <returns>Probe buffers containing positions and SH coefficients, or empty if invalid.</returns>
        public ProbeBuffers GetProbes(PhotonRenderingData renderingData)
        {
            if (renderingData == null || renderingData.camera == null || renderingData.cmd == null)
                return ProbeBuffers.Empty;

            Camera camera = renderingData.camera;

            ProbeGenerationResult generation = GenerateProbes(camera);
            int requiredCount = generation.Positions.Length;
            string cameraKey = camera.GetInstanceID().ToString();

            if (!_cameraProbeCache.TryGetValue(camera, out var storage) || storage == null || !storage.CanReuse(requiredCount))
            {
                storage?.Dispose();
                ComputeBuffer newPositionBuffer = RTManager.Instance.GetAdjustableCB(
                    $"{cameraKey}_ProbePositions",
                    requiredCount,
                    ProbePosition.Stride,
                    ComputeBufferType.Structured);
                ComputeBuffer newShBuffer = RTManager.Instance.GetAdjustableCB(
                    $"{cameraKey}_ProbeSH",
                    requiredCount,
                    ProbeSHL2.Stride,
                    ComputeBufferType.Structured);

                storage = new ProbeStorage(generation.Positions, generation.SHCoefficients, newPositionBuffer, newShBuffer);
                newPositionBuffer.SetData(generation.Positions);
                newShBuffer.SetData(generation.SHCoefficients);
                _cameraProbeCache[camera] = storage;
            }
            else
            {
                storage.PositionBuffer.SetData(generation.Positions);
                storage.SHBuffer.SetData(generation.SHCoefficients);
                storage.UpdateNativeData(generation.Positions, generation.SHCoefficients);
            }

            BakeProbeSHCoefficients(renderingData, storage.PositionBuffer, storage.SHBuffer, storage);

            return new ProbeBuffers(storage.PositionBuffer, storage.SHBuffer);
        }

        /// <summary>
        /// Releases probe data for a camera when it is removed.
        /// </summary>
        /// <param name="camera">The camera whose probes should be released.</param>
        public void OnCameraRemoved(Camera camera)
        {
            ReleaseCameraProbes(camera);
        }

        /// <summary>
        /// Attempts to retrieve probe data for a specific probe in the grid.
        /// </summary>
        /// <param name="camera">The camera associated with the probe grid.</param>
        /// <param name="cascadeIndex">The cascade index (0-based).</param>
        /// <param name="x">The X coordinate in the probe grid (0-based).</param>
        /// <param name="y">The Y coordinate in the probe grid (0-based).</param>
        /// <param name="z">The Z coordinate in the probe grid (0-based).</param>
        /// <param name="position">Output parameter for the probe position.</param>
        /// <param name="shData">Output parameter for the probe SH coefficients.</param>
        /// <returns>True if the probe data was successfully retrieved, false otherwise.</returns>
        public bool TryGetProbeData(Camera camera, int cascadeIndex, int x, int y, int z, out ProbePosition position, out ProbeSHL2 shData)
        {
            position = default;
            shData = default;

            if (camera == null)
                return false;

            if (!_cameraProbeCache.TryGetValue(camera, out var storage))
                return false;

            if (storage.IsDisposed)
                return false;

            if (cascadeIndex < 0 || cascadeIndex >= cascadeCount)
                return false;

            if (x < 0 || x >= probesPerAxis || y < 0 || y >= probesPerAxis || z < 0 || z >= probesPerAxis)
                return false;

            int axis = probesPerAxis;
            int axisSq = axis * axis;
            int probesPerCascade = axis * axisSq;
            int index = cascadeIndex * probesPerCascade + x * axisSq + y * axis + z;

            if (storage.CpuPositions == null || storage.CpuSHCoefficients == null)
                return false;

            if (index < 0 || index >= storage.CpuPositions.Length || index >= storage.CpuSHCoefficients.Length)
                return false;

            position = storage.CpuPositions[index];
            shData = storage.CpuSHCoefficients[index];
            return true;
        }

        /// <summary>
        /// Attempts to find the cascade and grid indices of the probe closest to the specified world position.
        /// </summary>
        public bool TryFindProbeIndices(
            Camera camera,
            Vector3 worldPosition,
            out int cascadeIndex,
            out Vector3 cellCoordinates)
        {
            cascadeIndex = 0;
            cellCoordinates = Vector3.zero;

            if (camera == null)
                return false;

            int axisCount = Mathf.Max(1, probesPerAxis);
            float baseCell = Mathf.Max(0.0001f, smallestCellSize);
            Vector3 offset = worldPosition - camera.transform.position;

            int selectedCascade = cascadeCount - 1;
            for (int c = 0; c < cascadeCount; c++)
            {
                float cellSizeForCascade = baseCell * Mathf.Pow(2f, c);
                float halfExtent = (axisCount - 1) * 0.5f * cellSizeForCascade;

                if (Mathf.Abs(offset.x) <= halfExtent &&
                    Mathf.Abs(offset.y) <= halfExtent &&
                    Mathf.Abs(offset.z) <= halfExtent)
                {
                    selectedCascade = c;
                    break;
                }
            }

            float selectedCellSize = baseCell * Mathf.Pow(2f, selectedCascade);
            float selectedHalfExtent = (axisCount - 1) * 0.5f * selectedCellSize;

            float normalizedX = Mathf.Clamp((offset.x + selectedHalfExtent) / selectedCellSize, 0f, axisCount - 1);
            float normalizedY = Mathf.Clamp((offset.y + selectedHalfExtent) / selectedCellSize, 0f, axisCount - 1);
            float normalizedZ = Mathf.Clamp((offset.z + selectedHalfExtent) / selectedCellSize, 0f, axisCount - 1);

            cascadeIndex = Mathf.Clamp(selectedCascade, 0, cascadeCount - 1);
            cellCoordinates = new Vector3(normalizedX, normalizedY, normalizedZ);
            return true;
        }

        /// <summary>
        /// Gets statistics about the current probe state.
        /// </summary>
        /// <returns>A structure containing probe count and configuration information.</returns>
        public ProbeStatistics GetProbeStatistics()
        {
            int totalProbes = 0;
            int cameraCount = _cameraProbeCache.Count;
            
            foreach (var kvp in _cameraProbeCache)
            {
                if (kvp.Value.Positions.IsCreated)
                {
                    totalProbes += kvp.Value.Positions.Length;
                }
            }

            int probesPerCascade = probesPerAxis * probesPerAxis * probesPerAxis;
            int expectedProbesPerCamera = probesPerCascade * cascadeCount;

            return new ProbeStatistics
            {
                TotalProbes = totalProbes,
                CameraCount = cameraCount,
                ProbesPerCascade = probesPerCascade,
                CascadeCount = cascadeCount,
                ProbesPerAxis = probesPerAxis,
                ExpectedProbesPerCamera = expectedProbesPerCamera
            };
        }

        /// <summary>
        /// Binds probe data buffers and metadata for the provided shader.
        /// </summary>
        public bool SetProbeShaderData(PhotonRenderingData renderingData, RayTracingShader shader, ProbeBuffers probeBuffers)
        {
            if (renderingData == null || renderingData.cmd == null || renderingData.camera == null)
                return false;

            if (shader == null || !probeBuffers.IsValid)
                return false;

            ComputeBuffer positions = probeBuffers.Positions;
            ComputeBuffer shCoefficients = probeBuffers.SHCoefficients;
            if (positions == null || shCoefficients == null)
                return false;

            int probeCount = Mathf.Min(positions.count, shCoefficients.count);
            if (probeCount <= 0)
                return false;

            CommandBuffer cmd = renderingData.cmd;
            cmd.SetRayTracingBufferParam(shader, "g_ProbePositions", positions);
            cmd.SetRayTracingBufferParam(shader, "g_ProbeSHCoefficients", shCoefficients);
            cmd.SetRayTracingIntParam(shader, "g_ProbeCount", probeCount);
            cmd.SetRayTracingIntParam(shader, "g_ProbesPerAxis", Mathf.Max(1, probesPerAxis));
            cmd.SetRayTracingIntParam(shader, "g_CascadeCount", Mathf.Max(1, cascadeCount));
            cmd.SetRayTracingFloatParam(shader, "g_SmallestCellSize", Mathf.Max(0.0001f, smallestCellSize));

            Vector3 cameraPosition = renderingData.camera.transform.position;
            cmd.SetRayTracingVectorParam(shader, "g_ProbeCameraPosition", new Vector4(cameraPosition.x, cameraPosition.y, cameraPosition.z, 1f));

            return true;
        }

        #endregion

        #region Private Methods - Probe Generation

        /// <summary>
        /// Generates probe positions and initializes SH coefficients for a camera.
        /// </summary>
        /// <param name="camera">The camera to generate probes for.</param>
        /// <returns>A result containing probe positions and SH coefficient arrays.</returns>
        private ProbeGenerationResult GenerateProbes(Camera camera)
        {
            int probesPerCascade = probesPerAxis * probesPerAxis * probesPerAxis;
            int totalProbes = probesPerCascade * cascadeCount;
            NativeArray<ProbePosition> positions = new NativeArray<ProbePosition>(totalProbes, Allocator.Persistent);
            NativeArray<ProbeSHL2> shCoefficients = new NativeArray<ProbeSHL2>(totalProbes, Allocator.Persistent);

            Vector3 origin = camera.transform.position;
            float baseCell = smallestCellSize;
            int index = 0;

            for (int cascade = 0; cascade < cascadeCount; cascade++)
            {
                float cellSize = baseCell * Mathf.Pow(2f, cascade);
                float halfExtent = (probesPerAxis - 1) * 0.5f * cellSize;

                for (int x = 0; x < probesPerAxis; x++)
                for (int y = 0; y < probesPerAxis; y++)
                for (int z = 0; z < probesPerAxis; z++)
                {
                    Vector3 offset = new Vector3(
                        (x * cellSize) - halfExtent,
                        (y * cellSize) - halfExtent,
                        (z * cellSize) - halfExtent);

                    ProbePosition position = new ProbePosition
                    {
                        position = origin + offset,
                        padding = 0f
                    };

                    positions[index] = position;
                    shCoefficients[index] = ProbeSHL2.Zero;
                    index++;
                }
            }

            return new ProbeGenerationResult(positions, shCoefficients);
        }

        /// <summary>
        /// Releases all probe data for a specific camera.
        /// </summary>
        /// <param name="camera">The camera whose probes should be released.</param>
        private void ReleaseCameraProbes(Camera camera)
        {
            if (camera == null)
                return;

            if (_cameraProbeCache.TryGetValue(camera, out var storage))
            {
                storage.Dispose();
                _cameraProbeCache.Remove(camera);
            }
        }

        /// <summary>
        /// Clears all cached probe data for all cameras.
        /// </summary>
        private void ClearAllProbes()
        {
            foreach (var kvp in _cameraProbeCache)
            {
                kvp.Value.Dispose();
            }
            _cameraProbeCache.Clear();
        }

        #endregion

        #region Private Methods - Probe Baking

        /// <summary>
        /// Bakes SH coefficients for all probes using ray tracing.
        /// </summary>
        /// <param name="renderingData">The rendering context.</param>
        /// <param name="positionBuffer">Buffer containing probe positions.</param>
        /// <param name="shBuffer">Buffer to write SH coefficients to.</param>
        /// <param name="storage">Storage that will receive CPU-side copies.</param>
        private void BakeProbeSHCoefficients(PhotonRenderingData renderingData, ComputeBuffer positionBuffer, ComputeBuffer shBuffer, ProbeStorage storage)
        {
            if (renderingData == null || positionBuffer == null || shBuffer == null || storage == null)
                return;

            int probeCount = positionBuffer.count;
            if (probeCount <= 0 || probeCount != shBuffer.count)
                return;

            RayTracingShader bakeShader = ResourceManager.Instance?.BakeProbeSHRayTrace;
            if (bakeShader == null)
                return;

            RayTraceManager rayTraceManager = RayTraceManager.Instance;
            if (rayTraceManager == null)
                return;
            rayTraceManager.ExecuteRayTracingJob(
                renderingData,
                bakeShader,
                "MainRayGenShader",
                (uint)probeCount,
                1u,
                1u,
                (cmd, camera) => ConfigureProbeBakeShader(cmd, bakeShader, positionBuffer, shBuffer, probeCount, rayTraceManager));
            
            ScheduleProbeReadback(renderingData, storage);
        }

        /// <summary>
        /// Configures shader parameters for probe baking.
        /// </summary>
        /// <param name="cmd">The command buffer to set parameters on.</param>
        /// <param name="bakeShader">The ray tracing shader being used.</param>
        /// <param name="positionBuffer">Buffer containing probe positions.</param>
        /// <param name="shBuffer">Buffer to write SH coefficients to.</param>
        /// <param name="probeCount">Total number of probes.</param>
        /// <param name="rayTraceManager">The ray trace manager for accessing settings.</param>
        private void ConfigureProbeBakeShader(
            CommandBuffer cmd,
            RayTracingShader bakeShader,
            ComputeBuffer positionBuffer,
            ComputeBuffer shBuffer,
            int probeCount,
            RayTraceManager rayTraceManager)
        {
            int safeSamples = Mathf.Max(1, (int)bakeSamplesPerProbe);
            int safeBounces = Mathf.Max(1, (int)bakeMaxBounces);
            float safeBias = Mathf.Max(0.0001f, bakeRayBias);
            float maxDistance = rayTraceManager != null ? rayTraceManager.MaxRayDistance : 1000f;
            float minRadianceWeight = Mathf.Max(0f, bakeMinRadianceWeight);

            cmd.SetRayTracingBufferParam(bakeShader, "g_ProbePositions", positionBuffer);
            cmd.SetRayTracingBufferParam(bakeShader, "g_ProbeSHCoefficients", shBuffer);
            cmd.SetRayTracingIntParam(bakeShader, "g_ProbeCount", probeCount);
            cmd.SetRayTracingIntParam(bakeShader, "g_SamplesPerProbe", safeSamples);
            cmd.SetRayTracingIntParam(bakeShader, "g_MaxBounces", safeBounces);
            cmd.SetRayTracingIntParam(bakeShader, "g_BaseSeed", Time.frameCount);//Time.frameCount
            cmd.SetRayTracingFloatParam(bakeShader, "g_RayBias", safeBias);
            cmd.SetRayTracingFloatParam(bakeShader, "g_MaxRayDistance", maxDistance);
            cmd.SetRayTracingFloatParam(bakeShader, "g_MinRadianceWeight", minRadianceWeight);
        }

        /// <summary>
        /// Schedules asynchronous readbacks to populate CPU arrays with GPU buffer content.
        /// </summary>
        private void ScheduleProbeReadback(PhotonRenderingData renderingData, ProbeStorage storage)
        {
            if (renderingData?.cmd == null || storage == null || storage.IsDisposed)
                return;

            storage.EnsureCpuCacheSize();

            if (storage.PositionBuffer != null)
            {
                renderingData.cmd.RequestAsyncReadback(storage.PositionBuffer, request =>
                    OnPositionReadbackCompleted(request, storage));
            }

            if (storage.SHBuffer != null)
            {
                renderingData.cmd.RequestAsyncReadback(storage.SHBuffer, request =>
                    OnShReadbackCompleted(request, storage));
            }
        }

        private static void OnPositionReadbackCompleted(AsyncGPUReadbackRequest request, ProbeStorage storage)
        {
            if (storage == null || storage.IsDisposed || storage.CpuPositions == null || request.hasError)
                return;
            var data = request.GetData<ProbePosition>();
            if (!data.IsCreated || data.Length != storage.CpuPositions.Length)
                return;

            data.CopyTo(storage.CpuPositions);
        }

        private static void OnShReadbackCompleted(AsyncGPUReadbackRequest request, ProbeStorage storage)
        {
            if (storage == null || storage.IsDisposed || storage.CpuSHCoefficients == null || request.hasError)
                return;
            var data = request.GetData<ProbeSHL2>();
            if (!data.IsCreated || data.Length != storage.CpuSHCoefficients.Length)
                return;

            data.CopyTo(storage.CpuSHCoefficients);
        }

        #endregion

        #region Nested Types

        /// <summary>
        /// Statistics about the current probe state.
        /// </summary>
        public struct ProbeStatistics
        {
            /// <summary>Total number of probes across all cameras.</summary>
            public int TotalProbes;
            /// <summary>Number of cameras with probe data.</summary>
            public int CameraCount;
            /// <summary>Number of probes per cascade.</summary>
            public int ProbesPerCascade;
            /// <summary>Number of cascades.</summary>
            public int CascadeCount;
            /// <summary>Number of probes per axis in each cascade.</summary>
            public int ProbesPerAxis;
            /// <summary>Expected number of probes per camera.</summary>
            public int ExpectedProbesPerCamera;
        }

        /// <summary>
        /// Represents a probe position in world space.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ProbePosition
        {
            /// <summary>World space position of the probe.</summary>
            public Vector3 position;
            /// <summary>Padding to align to 16 bytes.</summary>
            public float padding;

            /// <summary>Gets the stride in bytes for this structure.</summary>
            public static int Stride => sizeof(float) * 4;
        }

        /// <summary>
        /// Represents L2 spherical harmonics coefficients (9 floats).
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ProbeSHL2
        {
            /// <summary>L0 coefficient.</summary>
            public float shL00;
            /// <summary>L1, m=-1 coefficient.</summary>
            public float shL1_1;
            /// <summary>L1, m=0 coefficient.</summary>
            public float shL10;
            /// <summary>L1, m=1 coefficient.</summary>
            public float shL11;
            /// <summary>L2, m=-2 coefficient.</summary>
            public float shL2_2;
            /// <summary>L2, m=-1 coefficient.</summary>
            public float shL2_1;
            /// <summary>L2, m=0 coefficient.</summary>
            public float shL20;
            /// <summary>L2, m=1 coefficient.</summary>
            public float shL21;
            /// <summary>L2, m=2 coefficient.</summary>
            public float shL22;

            /// <summary>Gets the stride in bytes for this structure.</summary>
            public static int Stride => sizeof(float) * 9;

            /// <summary>Gets a zero-initialized SH coefficient structure.</summary>
            public static ProbeSHL2 Zero => new ProbeSHL2();
        }

        /// <summary>
        /// Contains compute buffers for probe positions and SH coefficients.
        /// </summary>
        public readonly struct ProbeBuffers
        {
            /// <summary>Empty probe buffers instance.</summary>
            public static readonly ProbeBuffers Empty = new ProbeBuffers(null, null);

            /// <summary>Buffer containing probe positions.</summary>
            public readonly ComputeBuffer Positions;
            /// <summary>Buffer containing SH coefficients.</summary>
            public readonly ComputeBuffer SHCoefficients;

            /// <summary>
            /// Initializes a new instance of ProbeBuffers.
            /// </summary>
            /// <param name="positions">Buffer containing probe positions.</param>
            /// <param name="shCoefficients">Buffer containing SH coefficients.</param>
            public ProbeBuffers(ComputeBuffer positions, ComputeBuffer shCoefficients)
            {
                Positions = positions;
                SHCoefficients = shCoefficients;
            }

            /// <summary>Gets whether both buffers are valid (non-null).</summary>
            public bool IsValid => Positions != null && SHCoefficients != null;
        }

        /// <summary>
        /// Result of probe generation containing native arrays.
        /// </summary>
        private readonly struct ProbeGenerationResult
        {
            /// <summary>Native array of probe positions.</summary>
            public readonly NativeArray<ProbePosition> Positions;
            /// <summary>Native array of SH coefficients.</summary>
            public readonly NativeArray<ProbeSHL2> SHCoefficients;

            /// <summary>
            /// Initializes a new instance of ProbeGenerationResult.
            /// </summary>
            /// <param name="positions">Native array of probe positions.</param>
            /// <param name="shCoefficients">Native array of SH coefficients.</param>
            public ProbeGenerationResult(NativeArray<ProbePosition> positions, NativeArray<ProbeSHL2> shCoefficients)
            {
                Positions = positions;
                SHCoefficients = shCoefficients;
            }
        }

        /// <summary>
        /// Storage for probe data including native arrays and compute buffers.
        /// </summary>
        private sealed class ProbeStorage : IDisposable
        {
            /// <summary>Native array of probe positions.</summary>
            public NativeArray<ProbePosition> Positions;
            /// <summary>Native array of SH coefficients.</summary>
            public NativeArray<ProbeSHL2> SHCoefficients;
            /// <summary>Compute buffer for probe positions.</summary>
            public ComputeBuffer PositionBuffer;
            /// <summary>Compute buffer for SH coefficients.</summary>
            public ComputeBuffer SHBuffer;
            /// <summary>CPU-side cached probe positions.</summary>
            public ProbePosition[] CpuPositions;
            /// <summary>CPU-side cached SH coefficients.</summary>
            public ProbeSHL2[] CpuSHCoefficients;
            /// <summary>Indicates whether the storage has been disposed.</summary>
            public bool IsDisposed { get; private set; }

            /// <summary>
            /// Initializes a new instance of ProbeStorage.
            /// </summary>
            /// <param name="positions">Native array of probe positions.</param>
            /// <param name="shCoefficients">Native array of SH coefficients.</param>
            /// <param name="positionBuffer">Compute buffer for probe positions.</param>
            /// <param name="shBuffer">Compute buffer for SH coefficients.</param>
            public ProbeStorage(NativeArray<ProbePosition> positions, NativeArray<ProbeSHL2> shCoefficients, ComputeBuffer positionBuffer, ComputeBuffer shBuffer)
            {
                Positions = positions;
                SHCoefficients = shCoefficients;
                PositionBuffer = positionBuffer;
                SHBuffer = shBuffer;
                CpuPositions = positions.IsCreated ? positions.ToArray() : null;
                CpuSHCoefficients = shCoefficients.IsCreated ? shCoefficients.ToArray() : null;
            }

            /// <summary>
            /// Checks whether this storage can be reused for the given probe count.
            /// </summary>
            public bool CanReuse(int requiredCount)
            {
                return !IsDisposed &&
                       PositionBuffer != null && PositionBuffer.count == requiredCount &&
                       SHBuffer != null && SHBuffer.count == requiredCount;
            }

            /// <summary>
            /// Replaces the native and CPU cached data with new values.
            /// </summary>
            public void UpdateNativeData(NativeArray<ProbePosition> positions, NativeArray<ProbeSHL2> shCoefficients)
            {
                if (Positions.IsCreated)
                    Positions.Dispose();
                if (SHCoefficients.IsCreated)
                    SHCoefficients.Dispose();

                Positions = positions;
                SHCoefficients = shCoefficients;
            }

            /// <summary>
            /// Ensures the CPU cache arrays match the buffer sizes.
            /// </summary>
            public void EnsureCpuCacheSize()
            {
                int probeCount = PositionBuffer != null ? PositionBuffer.count : 0;
                if (probeCount > 0 && (CpuPositions == null || CpuPositions.Length != probeCount))
                {
                    CpuPositions = new ProbePosition[probeCount];
                }

                int shCount = SHBuffer != null ? SHBuffer.count : 0;
                if (shCount > 0 && (CpuSHCoefficients == null || CpuSHCoefficients.Length != shCount))
                {
                    CpuSHCoefficients = new ProbeSHL2[shCount];
                }
            }

            /// <summary>
            /// Releases all resources held by this storage.
            /// </summary>
            public void Dispose()
            {
                if (IsDisposed)
                    return;

                if (PositionBuffer != null)
                {
                    RTManager.Instance.ReleaseCB(PositionBuffer);
                    PositionBuffer = null;
                }

                if (SHBuffer != null)
                {
                    RTManager.Instance.ReleaseCB(SHBuffer);
                    SHBuffer = null;
                }

                if (Positions.IsCreated)
                {
                    Positions.Dispose();
                    Positions = default;
                }
                if (SHCoefficients.IsCreated)
                {
                    SHCoefficients.Dispose();
                    SHCoefficients = default;
                }

                CpuPositions = null;
                CpuSHCoefficients = null;
                IsDisposed = true;
            }
        }

        #endregion
    }
}

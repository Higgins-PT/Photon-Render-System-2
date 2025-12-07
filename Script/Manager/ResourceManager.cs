using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace PhotonGISystem2
{
    /// <summary>
    /// Manages shared ray tracing shader resources used across the rendering pipeline.
    /// </summary>
    public class ResourceManager : PGSingleton<ResourceManager>
    {
        #region Serialized Fields

        [SerializeField]
        private RayTracingShader _bakeProbeSHRayTrace;
        [SerializeField, FormerlySerializedAs("_diffuseTracingShader")]
        private RayTracingShader _brdfTracingShader;
        [SerializeField]
        private ComputeShader _restirCompute;
        [SerializeField]
        private ComputeShader _svgfDenoiseCompute;
        [SerializeField, FormerlySerializedAs("_diffuseCompositeCompute")]
        private ComputeShader _brdfCompositeCompute;
        [SerializeField]
        private ComputeShader _meshBufferCompute;
        [SerializeField]
        private RayTracingShader _primaryRayGBufferShader;
        [SerializeField]
        private Shader _motionVectorShader;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the ray tracing shader used for baking probe SH coefficients.
        /// </summary>
        public RayTracingShader BakeProbeSHRayTrace => _bakeProbeSHRayTrace;
        /// <summary>
        /// Gets the ray tracing shader used for BRDF rendering.
        /// </summary>
        public RayTracingShader BrdfTracingShader => _brdfTracingShader;
        /// <summary>
        /// Gets the compute shader used for ReSTIR temporal and spatial passes.
        /// </summary>
        public ComputeShader ReSTIRCompute => _restirCompute;
        /// <summary>
        /// Gets the compute shader used for SVGF denoising.
        /// </summary>
        public ComputeShader SvgfDenoiseCompute => _svgfDenoiseCompute;
        /// <summary>
        /// Gets the compute shader used for mesh buffer operations.
        /// </summary>
        public ComputeShader MeshBufferCompute => _meshBufferCompute;
        /// <summary>
        /// Gets the compute shader used for final BRDF compositing.
        /// </summary>
        public ComputeShader BrdfCompositeCompute => _brdfCompositeCompute;
        /// <summary>
        /// Gets the ray tracing shader that builds the primary ray g-buffer.
        /// </summary>
        public RayTracingShader PrimaryRayGBufferShader => _primaryRayGBufferShader;
        /// <summary>
        /// Gets the shader used for motion vector rendering.
        /// </summary>
        public Shader MotionVectorShader => _motionVectorShader;

        #endregion
    }
}

using UnityEngine;

namespace PhotonGISystem2
{
    /// <summary>
    /// Tag component that describes whether a renderer should use the static or dynamic ray tracing mesh buffer.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Renderer))]
    public sealed class PhotonMeshInfo : MonoBehaviour
    {
        public enum MeshResidency
        {
            Static,
            Dynamic
        }

        [SerializeField]
        private MeshResidency residency = MeshResidency.Static;

        /// <summary>
        /// Gets or sets the residency type for this renderer.
        /// </summary>
        public MeshResidency Residency
        {
            get => residency;
            set => residency = value;
        }
    }
}


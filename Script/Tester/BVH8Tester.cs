using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhotonGISystem2
{
    public class BVH8Tester : MonoBehaviour
    {
        private BVH8 bvh;
        private List<BVH8.Node> allNodes;
        private Dictionary<BVH8.Node, Color> nodeColors;
        private Dictionary<GameObject, TransformData> trackedTransforms;
        private HashSet<GameObject> meshObjects;

        private class TransformData
        {
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 scale;
            public bool enabled;

            public bool Equals(TransformData other)
            {
                return position == other.position && 
                       rotation == other.rotation && 
                       scale == other.scale &&
                       enabled == other.enabled;
            }
        }

        void Start()
        {
            bvh = new BVH8();
            trackedTransforms = new Dictionary<GameObject, TransformData>();
            meshObjects = new HashSet<GameObject>();
            
#if UNITY_2020_1_OR_NEWER
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>(true))
#else
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
#endif
            {
                if (r != null && r.gameObject != null)
                {
                    var meshFilter = r.GetComponent<MeshFilter>();
                    var meshRenderer = r.GetComponent<MeshRenderer>();
                    var skinnedMeshRenderer = r.GetComponent<SkinnedMeshRenderer>();
                    
                    if ((meshFilter != null && meshFilter.sharedMesh != null) ||
                        (skinnedMeshRenderer != null && skinnedMeshRenderer.sharedMesh != null))
                    {
                        meshObjects.Add(r.gameObject);
                    }
                }
            }
            
            bvh.BuildFromGameObjects(meshObjects);
            
            foreach (var go in meshObjects)
            {
                if (go != null)
                {
                    var t = go.transform;
                    trackedTransforms[go] = new TransformData
                    {
                        position = t.position,
                        rotation = t.rotation,
                        scale = t.lossyScale,
                        enabled = go.activeSelf
                    };
                }
            }
            
            UpdateNodeColors();
            
            Debug.Log($"BVH8Tester: Built BVH with {allNodes.Count} nodes from {meshObjects.Count} mesh objects.");
        }

        void Update()
        {
            if (bvh == null || trackedTransforms == null) return;
            
            //DebugTimer.Start("BVH8Tester: Update");
            bool needsUpdate = false;

            foreach (var kvp in trackedTransforms)
            {
                var go = kvp.Key;
                if (go == null) continue;

                var currentTransform = go.transform;
                bool currentEnabled = go.activeSelf;
                var storedData = kvp.Value;

                bool enabledChanged = storedData.enabled != currentEnabled;

                if (enabledChanged)
                {
                    if (currentEnabled)
                    {
                        if (!bvh.Contains(go))
                        {
                            bvh.Add(go);
                            needsUpdate = true;
                        }
                        storedData.position = currentTransform.position;
                        storedData.rotation = currentTransform.rotation;
                        storedData.scale = currentTransform.lossyScale;
                    }
                    else
                    {
                        if (bvh.Contains(go))
                        {
                            bvh.Remove(go);
                            needsUpdate = true;
                        }
                    }
                    storedData.enabled = currentEnabled;
                }
                else if (currentEnabled)
                {
                    Vector3 currentPosition = currentTransform.position;
                    Quaternion currentRotation = currentTransform.rotation;
                    Vector3 currentScale = currentTransform.lossyScale;

                    bool transformChanged = 
                        storedData.position != currentPosition ||
                        storedData.rotation != currentRotation ||
                        storedData.scale != currentScale;

                    if (transformChanged)
                    {
                        if (bvh.Contains(go))
                        {
                            bvh.Refresh(go);
                            needsUpdate = true;
                        }
                        storedData.position = currentPosition;
                        storedData.rotation = currentRotation;
                        storedData.scale = currentScale;
                    }
                }
            }

            if (needsUpdate)
            {
                UpdateNodeColors();
            }
            //DebugTimer.End("BVH8Tester: Update");
        }

        private void UpdateNodeColors()
        {
            allNodes = bvh.GetAllNodes();
            nodeColors = new Dictionary<BVH8.Node, Color>();
            
            for (int i = 0; i < allNodes.Count; i++)
            {
                var node = allNodes[i];
                var random = new System.Random(i);
                nodeColors[node] = new Color(
                    (float)random.NextDouble(),
                    (float)random.NextDouble(),
                    (float)random.NextDouble(),
                    1f
                );
            }
        }

        void OnDrawGizmos()
        {
            if (allNodes == null || allNodes.Count == 0) return;
            
            foreach (var node in allNodes)
            {
                if (!nodeColors.ContainsKey(node)) continue;
                
                Color color = nodeColors[node];
                Gizmos.color = color;
                
                Bounds bounds = node.Bounds;
                Vector3 center = bounds.center;
                Vector3 size = bounds.size;
                
                Gizmos.DrawWireCube(center, size);
                
                if (node.IsLeaf)
                {
                    Color fillColor = color;
                    fillColor.a = 0.2f;
                    Gizmos.color = fillColor;
                    Gizmos.DrawCube(center, size);
                }
            }
        }
    }
}

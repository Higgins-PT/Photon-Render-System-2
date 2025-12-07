using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhotonGISystem2
{
    public class BVH8
    {
        public int MaxDepth { get; private set; }
        public int MaxLeafSize { get; private set; }
        public int MaxChildren { get; private set; } = 8;
        public int BinCount { get; private set; } = 32;
        public float SmallBoxSize { get; private set; } = 0.01f;

        public float TraversalCost { get; set; } = 1.0f;
        public float IntersectionCost { get; set; } = 1.0f;

        public class Node
        {
            public Bounds Bounds;
            public bool IsLeaf;
            public GameObject Item;
            public Node[] Children;
            public int Depth;
        }

        public Node Root { get; private set; }

        private struct Primitive
        {
            public GameObject go;
            public Bounds bounds;
            public Vector3 centroid;
        }

        private List<Primitive> _prims;
        private Dictionary<GameObject, Node> _gameObjectToNode;

        /// <summary>
        /// Initializes a new instance of the BVH8 class.
        /// </summary>
        /// <param name="maxDepth">Maximum tree depth. Default is 32.</param>
        /// <param name="maxLeafSize">Maximum number of items in a leaf node. Default is 1.</param>
        /// <param name="maxChildren">Maximum number of children per node. Default is 8.</param>
        /// <param name="binCount">Number of bins for SAH optimization. Default is 32.</param>
        public BVH8(int maxDepth = 32, int maxLeafSize = 1, int maxChildren = 8, int binCount = 32)
        {
            MaxDepth = Mathf.Max(1, maxDepth);
            MaxLeafSize = Mathf.Max(1, maxLeafSize);
            MaxChildren = Mathf.Clamp(maxChildren, 2, 8);
            BinCount = Mathf.Clamp(binCount, 8, 64);
            _gameObjectToNode = new Dictionary<GameObject, Node>();
        }

        /// <summary>
        /// Builds the BVH tree from a collection of GameObjects.
        /// </summary>
        /// <param name="objects">Collection of GameObjects to build the tree from.</param>
        /// <exception cref="ArgumentNullException">Thrown when objects is null.</exception>
        public void BuildFromGameObjects(IEnumerable<GameObject> objects)
        {
            if (objects == null) throw new ArgumentNullException(nameof(objects));

            _prims = new List<Primitive>();
            foreach (var go in objects)
            {
                if (go == null) continue;

                if (TryGetWorldBounds(go, out var b))
                {
                    _prims.Add(new Primitive { go = go, bounds = b, centroid = b.center });
                }
                else
                {
                    var c = go.transform.position;
                    var small = new Bounds(c, Vector3.one * SmallBoxSize);
                    _prims.Add(new Primitive { go = go, bounds = small, centroid = c });
                }
            }

            var indices = new List<int>(_prims.Count);
            for (int i = 0; i < _prims.Count; i++) indices.Add(i);

            Root = BuildRecursive(indices, 0);
            BuildGameObjectToNodeMap();
        }

        /// <summary>
        /// Builds the BVH tree from all objects in the scene.
        /// </summary>
        /// <param name="includeInactive">Whether to include inactive GameObjects. Default is false.</param>
        /// <param name="includeNoMesh">Whether to include GameObjects without mesh components. Default is false.</param>
        public void BuildFromScene(bool includeInactive = false, bool includeNoMesh = false)
        {
            var set = new HashSet<GameObject>();

#if UNITY_2020_1_OR_NEWER
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>(includeInactive))
#else
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
#endif
            {
                if (r && r.gameObject) set.Add(r.gameObject);
            }

#if UNITY_2020_1_OR_NEWER
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>(includeInactive))
#else
            foreach (var mf in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
#endif
            {
                if (mf && mf.gameObject) set.Add(mf.gameObject);
            }

            if (includeNoMesh)
            {
#if UNITY_2020_1_OR_NEWER
                foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(includeInactive))
#else
                foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
#endif
                {
                    if (t && t.gameObject) set.Add(t.gameObject);
                }
            }

            BuildFromGameObjects(set);
        }

        /// <summary>
        /// Finds all GameObjects whose bounds overlap with the query bounds.
        /// </summary>
        /// <param name="query">The query bounds to test against.</param>
        /// <param name="results">List to store the overlapping GameObjects.</param>
        /// <exception cref="ArgumentNullException">Thrown when results is null.</exception>
        public void Overlap(Bounds query, List<GameObject> results)
        {
            if (Root == null) return;
            if (results == null) throw new ArgumentNullException(nameof(results));
            OverlapNode(Root, query, results);
        }

        /// <summary>
        /// Recursively traverses the BVH tree to find overlapping GameObjects.
        /// </summary>
        /// <param name="node">Current node to test.</param>
        /// <param name="query">The query bounds to test against.</param>
        /// <param name="results">List to store the overlapping GameObjects.</param>
        private void OverlapNode(Node node, Bounds query, List<GameObject> results)
        {
            if (!node.Bounds.Intersects(query)) return;

            if (node.IsLeaf)
            {
                var go = node.Item;
                if (go)
                {
                    if (TryGetWorldBounds(go, out var b))
                    {
                        if (b.Intersects(query)) results.Add(go);
                    }
                    else
                    {
                        var small = new Bounds(go.transform.position, Vector3.one * SmallBoxSize);
                        if (small.Intersects(query)) results.Add(go);
                    }
                }
                return;
            }

            var children = node.Children;
            if (children == null) return;
            for (int i = 0; i < children.Length; i++)
            {
                var ch = children[i];
                if (ch != null) OverlapNode(ch, query, results);
            }
        }

        /// <summary>
        /// Checks if the BVH tree contains the specified GameObject.
        /// </summary>
        /// <param name="gameObject">The GameObject to check.</param>
        /// <returns>True if the GameObject is in the tree, false otherwise.</returns>
        public bool Contains(GameObject gameObject)
        {
            if (gameObject == null) return false;
            return _gameObjectToNode != null && _gameObjectToNode.ContainsKey(gameObject);
        }

        /// <summary>
        /// Adds a GameObject to the BVH tree.
        /// </summary>
        /// <param name="gameObject">The GameObject to add.</param>
        /// <returns>True if the GameObject was successfully added, false otherwise.</returns>
        public bool Add(GameObject gameObject)
        {
            if (gameObject == null) return false;
            if (_gameObjectToNode != null && _gameObjectToNode.ContainsKey(gameObject)) return false;

            Bounds bounds;
            if (TryGetWorldBounds(gameObject, out bounds))
            {
                // bounds already set
            }
            else
            {
                var c = gameObject.transform.position;
                bounds = new Bounds(c, Vector3.one * SmallBoxSize);
            }

            if (Root == null)
            {
                var node = new Node
                {
                    Bounds = bounds,
                    IsLeaf = true,
                    Item = gameObject,
                    Depth = 0
                };
                Root = node;
                if (_gameObjectToNode == null) _gameObjectToNode = new Dictionary<GameObject, Node>();
                _gameObjectToNode[gameObject] = node;
                return true;
            }

            Node targetNode = FindBestNodeForInsert(Root, bounds);
            var newNode = new Node
            {
                Bounds = bounds,
                IsLeaf = true,
                Item = gameObject,
                Depth = targetNode != null ? targetNode.Depth + 1 : 0
            };

            if (targetNode == null)
            {
                var parentNode = new Node
                {
                    IsLeaf = false,
                    Depth = 0,
                    Children = new Node[] { Root, newNode }
                };
                Encapsulate(ref parentNode.Bounds, Root.Bounds);
                Encapsulate(ref parentNode.Bounds, newNode.Bounds);
                Root = parentNode;
            }
            else if (targetNode == Root)
            {
                var parentNode = new Node
                {
                    IsLeaf = false,
                    Depth = 0,
                    Children = new Node[] { Root, newNode }
                };
                Encapsulate(ref parentNode.Bounds, Root.Bounds);
                Encapsulate(ref parentNode.Bounds, newNode.Bounds);
                Root = parentNode;
            }
            else
            {
                Node parent = FindParentNode(Root, targetNode);
                if (parent != null && parent.Children != null && parent.Children.Length < MaxChildren && targetNode.Depth < MaxDepth)
                {
                    var newChildren = new List<Node>(parent.Children);
                    newChildren.Add(newNode);
                    parent.Children = newChildren.ToArray();
                    Encapsulate(ref parent.Bounds, newNode.Bounds);
                    UpdateBoundsUpward(parent);
                }
                else
                {
                    var parentNode = new Node
                    {
                        IsLeaf = false,
                        Depth = targetNode.Depth,
                        Children = new Node[] { targetNode, newNode }
                    };
                    Encapsulate(ref parentNode.Bounds, targetNode.Bounds);
                    Encapsulate(ref parentNode.Bounds, newNode.Bounds);

                    Node grandParent = FindParentNode(Root, targetNode);
                    if (grandParent != null)
                    {
                        ReplaceChildInParent(grandParent, targetNode, parentNode);
                    }
                    else
                    {
                        Root = parentNode;
                    }
                    UpdateBoundsUpward(parentNode);
                }
            }

            if (_gameObjectToNode == null) _gameObjectToNode = new Dictionary<GameObject, Node>();
            _gameObjectToNode[gameObject] = newNode;
            return true;
        }

        /// <summary>
        /// Removes a GameObject from the BVH tree.
        /// </summary>
        /// <param name="gameObject">The GameObject to remove.</param>
        /// <returns>True if the GameObject was successfully removed, false otherwise.</returns>
        public bool Remove(GameObject gameObject)
        {
            if (gameObject == null) return false;
            if (_gameObjectToNode == null || !_gameObjectToNode.ContainsKey(gameObject)) return false;

            Node nodeToRemove = _gameObjectToNode[gameObject];
            _gameObjectToNode.Remove(gameObject);

            if (nodeToRemove == Root)
            {
                Root = null;
                return true;
            }

            Node parent = FindParentNode(Root, nodeToRemove);
            if (parent != null)
            {
                RemoveChildFromParent(parent, nodeToRemove);
                UpdateBoundsUpward(parent);
            }

            return true;
        }

        /// <summary>
        /// Refreshes a GameObject in the BVH tree by removing and re-adding it.
        /// </summary>
        /// <param name="gameObject">The GameObject to refresh.</param>
        /// <returns>True if the GameObject was successfully refreshed, false otherwise.</returns>
        public bool Refresh(GameObject gameObject)
        {
            if (gameObject == null) return false;
            bool removed = Remove(gameObject);
            if (removed || !Contains(gameObject))
            {
                return Add(gameObject);
            }
            return false;
        }

        /// <summary>
        /// Flattens the BVH tree and returns all nodes in a list.
        /// </summary>
        /// <returns>A list containing all nodes in the BVH tree, or an empty list if the tree is not built.</returns>
        public List<Node> GetAllNodes()
        {
            var nodes = new List<Node>();
            if (Root == null) return nodes;
            CollectNodesRecursive(Root, nodes);
            return nodes;
        }

        /// <summary>
        /// Recursively collects all nodes from the tree.
        /// </summary>
        /// <param name="node">Current node to process.</param>
        /// <param name="nodes">List to store collected nodes.</param>
        private void CollectNodesRecursive(Node node, List<Node> nodes)
        {
            if (node == null) return;
            nodes.Add(node);

            if (!node.IsLeaf && node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    if (child != null)
                    {
                        CollectNodesRecursive(child, nodes);
                    }
                }
            }
        }

        /// <summary>
        /// Recursively builds the BVH tree using SAH optimization.
        /// </summary>
        /// <param name="primIndices">List of primitive indices to build the node from.</param>
        /// <param name="depth">Current depth in the tree.</param>
        /// <returns>The constructed node.</returns>
        private Node BuildRecursive(List<int> primIndices, int depth)
        {
            var node = new Node { Depth = depth };
            var nodeBounds = ComputeUnionBounds(primIndices);
            node.Bounds = nodeBounds;
            int N = primIndices.Count;

            if (depth >= MaxDepth || N <= MaxLeafSize)
            {
                node.IsLeaf = true;
                node.Item = IndicesToGo(primIndices);
                return node;
            }

            float parentArea = SurfaceArea(nodeBounds);
            if (parentArea <= 1e-12f)
            {
                node.IsLeaf = true;
                node.Item = IndicesToGo(primIndices);
                return node;
            }

            Vector3 cmin, cmax;
            ComputeCentroidBounds(primIndices, out cmin, out cmax);
            Vector3 cExt = cmax - cmin;

            int axis = 0;
            if (cExt.y > cExt.x && cExt.y >= cExt.z) axis = 1;
            else if (cExt.z > cExt.x && cExt.z > cExt.y) axis = 2;

            if (cExt[axis] <= 1e-9f)
            {
                var groups = EqualCountSplit(primIndices, Mathf.Min(MaxChildren, Math.Max(2, Math.Min(N, MaxChildren))));
                return MakeInternalOrLeafFromGroups(node, groups, depth, leafFallbackOnBadSplit: true);
            }

            int B = BinCount;
            int[] binCounts = new int[B];
            Bounds[] binBounds = new Bounds[B];
            bool[] binInit = new bool[B];

            float invLen = 1.0f / cExt[axis];
            int nLocal = primIndices.Count;
            int[] primLocal = primIndices.ToArray();
            int[] primBin = new int[nLocal];

            for (int i = 0; i < nLocal; i++)
            {
                int primIdx = primLocal[i];
                var p = _prims[primIdx];
                float t = Mathf.Clamp01((p.centroid[axis] - cmin[axis]) * invLen);
                int b = Mathf.Clamp((int)(t * (B - 1)), 0, B - 1);
                primBin[i] = b;

                binCounts[b]++;
                if (!binInit[b])
                {
                    binBounds[b] = p.bounds;
                    binInit[b] = true;
                }
                else
                {
                    Encapsulate(ref binBounds[b], p.bounds);
                }
            }

            int Kmax = Mathf.Min(MaxChildren, NonEmptyBins(binCounts));
            if (Kmax < 2)
            {
                node.IsLeaf = true;
                node.Item = IndicesToGo(primIndices);
                return node;
            }

            float INF = 1e30f;
            float[,] dp = new float[Kmax + 1, B + 1];
            int[,] cut = new int[Kmax + 1, B + 1];
            for (int k = 0; k <= Kmax; k++)
            {
                for (int i = 0; i <= B; i++)
                {
                    dp[k, i] = INF;
                    cut[k, i] = -1;
                }
            }
            dp[0, 0] = 0f;

            int[] prefixCount = new int[B + 1];
            for (int i = 0; i < B; i++) prefixCount[i + 1] = prefixCount[i] + binCounts[i];

            for (int k = 1; k <= Kmax; k++)
            {
                for (int i = 1; i <= B; i++)
                {
                    float best = INF;
                    int bestJ = -1;

                    int jMin = k - 1;
                    for (int j = jMin; j < i; j++)
                    {
                        int segCount = prefixCount[i] - prefixCount[j];
                        if (segCount == 0) continue;

                        Bounds segB = default;
                        bool segInit = false;
                        for (int t = j; t < i; t++)
                        {
                            if (binCounts[t] == 0) continue;
                            if (!segInit)
                            {
                                segB = binBounds[t];
                                segInit = true;
                            }
                            else
                            {
                                Encapsulate(ref segB, binBounds[t]);
                            }
                        }
                        if (!segInit) continue;

                        float segArea = SurfaceArea(segB);
                        float segCost = (segArea / parentArea) * IntersectionCost * segCount;

                        float cand = dp[k - 1, j] + segCost;
                        if (cand < best)
                        {
                            best = cand;
                            bestJ = j;
                        }
                    }

                    dp[k, i] = best;
                    cut[k, i] = bestJ;
                }
            }

            float leafCost = IntersectionCost * N;
            float bestCost = INF;
            int bestK = -1;
            for (int k = 2; k <= Kmax; k++)
            {
                float cost = TraversalCost + dp[k, B];
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestK = k;
                }
            }

            if (bestK < 2 || leafCost <= bestCost || depth >= MaxDepth)
            {
                node.IsLeaf = true;
                node.Item = IndicesToGo(primIndices);
                return node;
            }

            var segments = new List<(int j, int i)>(bestK);
            {
                int i = B;
                for (int k = bestK; k >= 1; k--)
                {
                    int j = cut[k, i];
                    if (j < 0)
                    {
                        segments.Clear();
                        break;
                    }
                    segments.Add((j, i));
                    i = j;
                }
                segments.Reverse();
            }

            if (segments.Count == 0)
            {
                var groups = EqualCountSplit(primIndices, bestK);
                return MakeInternalOrLeafFromGroups(node, groups, depth, leafFallbackOnBadSplit: true);
            }

            var childGroups = new List<List<int>>(segments.Count);
            for (int s = 0; s < segments.Count; s++) childGroups.Add(new List<int>());

            for (int k = 0; k < nLocal; k++)
            {
                int b = primBin[k];
                for (int s = 0; s < segments.Count; s++)
                {
                    var seg = segments[s];
                    if (b >= seg.j && b < seg.i)
                    {
                        childGroups[s].Add(primLocal[k]);
                        break;
                    }
                }
            }

            for (int s = childGroups.Count - 1; s >= 0; s--)
            {
                if (childGroups[s].Count == 0) childGroups.RemoveAt(s);
            }
            if (childGroups.Count == 0)
            {
                node.IsLeaf = true;
                node.Item = IndicesToGo(primIndices);
                return node;
            }

            return MakeInternalOrLeafFromGroups(node, childGroups, depth, leafFallbackOnBadSplit: true);
        }

        /// <summary>
        /// Creates internal nodes from groups of primitives, or converts to leaf if needed.
        /// </summary>
        /// <param name="node">The node to configure.</param>
        /// <param name="groups">List of groups, each containing primitive indices.</param>
        /// <param name="depth">Current depth in the tree.</param>
        /// <param name="leafFallbackOnBadSplit">Whether to fallback to leaf node if split fails.</param>
        /// <returns>The configured node (internal or leaf).</returns>
        private Node MakeInternalOrLeafFromGroups(Node node, List<List<int>> groups, int depth, bool leafFallbackOnBadSplit)
        {
            if (groups.Count <= 1 && leafFallbackOnBadSplit)
            {
                node.IsLeaf = true;
                var merged = new List<int>();
                foreach (var g in groups) merged.AddRange(g);
                node.Item = IndicesToGo(merged);
                return node;
            }

            var kids = new List<Node>(Mathf.Min(groups.Count, MaxChildren));
            foreach (var g in groups)
            {
                if (g == null || g.Count == 0) continue;
                kids.Add(BuildRecursive(g, depth + 1));
            }

            if (kids.Count == 0 && leafFallbackOnBadSplit)
            {
                node.IsLeaf = true;
                var merged = new List<int>();
                foreach (var g in groups) merged.AddRange(g);
                node.Item = IndicesToGo(merged);
                return node;
            }

            node.IsLeaf = false;
            node.Children = kids.ToArray();

            if (node.Children.Length > 0)
            {
                var b = node.Children[0].Bounds;
                for (int i = 1; i < node.Children.Length; i++) Encapsulate(ref b, node.Children[i].Bounds);
                node.Bounds = b;
            }
            return node;
        }

        /// <summary>
        /// Converts a list of primitive indices to a single GameObject (takes the first one).
        /// </summary>
        /// <param name="indices">List of primitive indices.</param>
        /// <returns>The first GameObject corresponding to the indices, or null if empty.</returns>
        private GameObject IndicesToGo(List<int> indices)
        {
            if (indices == null || indices.Count == 0) return null;
            return _prims[indices[0]].go;
        }


        /// <summary>
        /// Checks if a bounding box is completely zero.
        /// </summary>
        /// <param name="b">The bounding box to check.</param>
        /// <returns>True if the bounding box is completely zero, false otherwise.</returns>
        private static bool IsCompletelyZeroBounds(Bounds b)
        {
            return b.size.sqrMagnitude < 1e-12f ||
                   b.center.sqrMagnitude < 1e-12f;
        }

        /// <summary>
        /// Checks if a bounding box has any zero values.
        /// </summary>
        /// <param name="b">The bounding box to check.</param>
        /// <returns>True if the bounding box has any zero values, false otherwise.</returns>
        private static bool AnyZero(Bounds b)
        {
            if(Mathf.Abs(b.size.x) < 1e-12f || Mathf.Abs(b.size.y) < 1e-12f || Mathf.Abs(b.size.z) < 1e-12f)
            {
                Debug.Log("AnyZero: size is zero");
                Debug.Log($"size: {b.size.x}, {b.size.y}, {b.size.z}, center: {b.center.x}, {b.center.y}, {b.center.z}");
                return true;
            }
            if(Mathf.Abs(b.center.x) < 1e-12f || Mathf.Abs(b.center.y) < 1e-12f || Mathf.Abs(b.center.z) < 1e-12f)
            {
                Debug.Log("AnyZero: center is zero");
                Debug.Log($"size: {b.size.x}, {b.size.y}, {b.size.z}, center: {b.center.x}, {b.center.y}, {b.center.z}");
                return true;
            }
            if(Mathf.Abs(b.min.x) < 1e-12f || Mathf.Abs(b.min.y) < 1e-12f || Mathf.Abs(b.min.z) < 1e-12f)
            {
                Debug.Log("AnyZero: min is zero");
                Debug.Log($"size: {b.size.x}, {b.size.y}, {b.size.z}, center: {b.center.x}, {b.center.y}, {b.center.z}");
                return true;
            }
            if(Mathf.Abs(b.max.x) < 1e-12f || Mathf.Abs(b.max.y) < 1e-12f || Mathf.Abs(b.max.z) < 1e-12f)
            {
                Debug.Log("AnyZero: max is zero");
                Debug.Log($"size: {b.size.x}, {b.size.y}, {b.size.z}, center: {b.center.x}, {b.center.y}, {b.center.z}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Expands bounds A to include bounds B.
        /// </summary>
        /// <param name="a">The bounds to expand (modified by reference).</param>
        /// <param name="b">The bounds to include.</param>
        private static void Encapsulate(ref Bounds a, Bounds b)
        {
            if (IsCompletelyZeroBounds(a))
            {
                a = b;
            }
            else
            {
                a.Encapsulate(b.min);
                a.Encapsulate(b.max);
            }
        }

        /// <summary>
        /// Computes the union bounds of all primitives specified by the indices.
        /// </summary>
        /// <param name="indices">List of primitive indices.</param>
        /// <returns>The union bounds of all specified primitives.</returns>
        private Bounds ComputeUnionBounds(List<int> indices)
        {
            if (indices.Count == 0) return new Bounds(Vector3.zero, Vector3.zero);
            var b = _prims[indices[0]].bounds;
            for (int i = 1; i < indices.Count; i++) Encapsulate(ref b, _prims[indices[i]].bounds);
            return b;
        }

        /// <summary>
        /// Computes the bounding box of all primitive centroids.
        /// </summary>
        /// <param name="indices">List of primitive indices.</param>
        /// <param name="cmin">Output parameter for the minimum corner of the centroid bounds.</param>
        /// <param name="cmax">Output parameter for the maximum corner of the centroid bounds.</param>
        private void ComputeCentroidBounds(List<int> indices, out Vector3 cmin, out Vector3 cmax)
        {
            if (indices.Count == 0)
            {
                cmin = cmax = Vector3.zero;
                return;
            }
            var c = _prims[indices[0]].centroid;
            cmin = cmax = c;
            for (int i = 1; i < indices.Count; i++)
            {
                var cc = _prims[indices[i]].centroid;
                cmin = Vector3.Min(cmin, cc);
                cmax = Vector3.Max(cmax, cc);
            }
        }

        /// <summary>
        /// Counts the number of non-empty bins.
        /// </summary>
        /// <param name="binCounts">Array of bin counts.</param>
        /// <returns>The number of bins with count greater than zero.</returns>
        private static int NonEmptyBins(int[] binCounts)
        {
            int n = 0;
            for (int i = 0; i < binCounts.Length; i++) if (binCounts[i] > 0) n++;
            return n;
        }

        /// <summary>
        /// Splits a list of indices into k groups with approximately equal counts.
        /// </summary>
        /// <param name="indices">The list of indices to split.</param>
        /// <param name="k">Number of groups to create.</param>
        /// <returns>List of groups, each containing a subset of indices.</returns>
        private static List<List<int>> EqualCountSplit(List<int> indices, int k)
        {
            k = Mathf.Clamp(k, 1, indices.Count);
            var groups = new List<List<int>>(k);
            int per = Mathf.Max(1, indices.Count / k);
            int ptr = 0;
            for (int g = 0; g < k; g++)
            {
                var list = new List<int>(per);
                int remain = indices.Count - ptr;
                int take = (g == k - 1) ? remain : Mathf.Min(per, remain);
                for (int t = 0; t < take; t++) list.Add(indices[ptr++]);
                if (list.Count > 0) groups.Add(list);
            }
            return groups;
        }

        /// <summary>
        /// Calculates the surface area of a bounding box.
        /// </summary>
        /// <param name="b">The bounding box.</param>
        /// <returns>The surface area of the bounding box.</returns>
        private static float SurfaceArea(Bounds b)
        {
            var s = b.size;
            return 2f * (s.x * s.y + s.y * s.z + s.z * s.x);
        }

        /// <summary>
        /// Attempts to get the world-space bounds of a GameObject.
        /// </summary>
        /// <param name="go">The GameObject to get bounds from.</param>
        /// <param name="bounds">Output parameter for the world-space bounds.</param>
        /// <returns>True if bounds were successfully retrieved, false otherwise.</returns>
        private bool TryGetWorldBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;

            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                bounds = renderer.bounds;
                if (IsFinite(bounds)) return true;
            }

            var skinned = go.GetComponent<SkinnedMeshRenderer>();
            if (skinned != null)
            {
                bounds = skinned.bounds;
                if (IsFinite(bounds)) return true;
            }

            var mf = go.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
            {
                var local = mf.sharedMesh.bounds;
                bounds = TransformBounds(local, go.transform.localToWorldMatrix);
                if (IsFinite(bounds)) return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if all components of a bounding box are finite.
        /// </summary>
        /// <param name="b">The bounding box to check.</param>
        /// <returns>True if all components are finite, false otherwise.</returns>
        private static bool IsFinite(Bounds b)
        {
            var c = b.center;
            var e = b.extents;
            return IsFinite(c.x) && IsFinite(c.y) && IsFinite(c.z)
                && IsFinite(e.x) && IsFinite(e.y) && IsFinite(e.z);
        }

        /// <summary>
        /// Checks if a float value is finite (not NaN and not infinity).
        /// </summary>
        /// <param name="x">The float value to check.</param>
        /// <returns>True if the value is finite, false otherwise.</returns>
        private static bool IsFinite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);

        /// <summary>
        /// Transforms a bounding box from local space to world space using a transformation matrix.
        /// </summary>
        /// <param name="local">The bounding box in local space.</param>
        /// <param name="m">The transformation matrix.</param>
        /// <returns>The bounding box in world space.</returns>
        private static Bounds TransformBounds(Bounds local, Matrix4x4 m)
        {
            Vector3 worldCenter = m.MultiplyPoint3x4(local.center);
            Vector3 lx = m.MultiplyVector(new Vector3(local.extents.x, 0, 0));
            Vector3 ly = m.MultiplyVector(new Vector3(0, local.extents.y, 0));
            Vector3 lz = m.MultiplyVector(new Vector3(0, 0, local.extents.z));
            Vector3 worldExtents = new Vector3(
                Mathf.Abs(lx.x) + Mathf.Abs(ly.x) + Mathf.Abs(lz.x),
                Mathf.Abs(lx.y) + Mathf.Abs(ly.y) + Mathf.Abs(lz.y),
                Mathf.Abs(lx.z) + Mathf.Abs(ly.z) + Mathf.Abs(lz.z)
            );
            return new Bounds(worldCenter, worldExtents * 2f);
        }

        /// <summary>
        /// Builds the dictionary mapping GameObjects to their nodes.
        /// </summary>
        private void BuildGameObjectToNodeMap()
        {
            if (_gameObjectToNode == null) _gameObjectToNode = new Dictionary<GameObject, Node>();
            _gameObjectToNode.Clear();
            if (Root == null) return;
            BuildGameObjectToNodeMapRecursive(Root);
        }

        /// <summary>
        /// Recursively builds the dictionary mapping GameObjects to their nodes.
        /// </summary>
        /// <param name="node">Current node to process.</param>
        private void BuildGameObjectToNodeMapRecursive(Node node)
        {
            if (node == null) return;

            if (node.IsLeaf && node.Item != null)
            {
                _gameObjectToNode[node.Item] = node;
            }

            if (!node.IsLeaf && node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    if (child != null)
                    {
                        BuildGameObjectToNodeMapRecursive(child);
                    }
                }
            }
        }

        /// <summary>
        /// Finds the best node to insert a new GameObject with the given bounds near.
        /// </summary>
        /// <param name="node">Current node to search from.</param>
        /// <param name="bounds">Bounds of the GameObject to insert.</param>
        /// <returns>The best node to insert near, or null if root should be used.</returns>
        private Node FindBestNodeForInsert(Node node, Bounds bounds)
        {
            if (node == null) return null;
            if (node.IsLeaf) return node;

            if (node.Children == null || node.Children.Length == 0) return node;

            Node bestNode = null;
            float bestCost = float.MaxValue;

            foreach (var child in node.Children)
            {
                if (child == null) continue;

                Bounds expandedBounds = child.Bounds;
                Encapsulate(ref expandedBounds, bounds);
                float cost = SurfaceArea(expandedBounds) - SurfaceArea(child.Bounds);

                if (child.IsLeaf)
                {
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestNode = child;
                    }
                }
                else
                {
                    Node candidate = FindBestNodeForInsert(child, bounds);
                    if (candidate != null && cost < bestCost)
                    {
                        bestCost = cost;
                        bestNode = candidate;
                    }
                }
            }

            return bestNode ?? (node.Children.Length > 0 ? node.Children[0] : null);
        }

        /// <summary>
        /// Updates the bounds of nodes upward from the given node to the root.
        /// </summary>
        /// <param name="node">The node to start updating from.</param>
        private void UpdateBoundsUpward(Node node)
        {
            if (node == null || node == Root) return;

            Node parent = FindParentNode(Root, node);
            if (parent != null)
            {
                parent.Bounds = node.Bounds;
                if (parent.Children != null)
                {
                    foreach (var child in parent.Children)
                    {
                        if (child != null)
                        {
                            Encapsulate(ref parent.Bounds, child.Bounds);
                        }
                    }
                }
                UpdateBoundsUpward(parent);
            }
        }

        /// <summary>
        /// Finds the parent node of a given child node.
        /// </summary>
        /// <param name="root">Root node to search from.</param>
        /// <param name="target">Target child node to find parent for.</param>
        /// <returns>The parent node, or null if not found or if target is root.</returns>
        private Node FindParentNode(Node root, Node target)
        {
            if (root == null || target == null || root == target) return null;

            if (root.Children == null) return null;

            foreach (var child in root.Children)
            {
                if (child == target) return root;

                Node parent = FindParentNode(child, target);
                if (parent != null) return parent;
            }

            return null;
        }

        /// <summary>
        /// Replaces a child node in a parent node with a new node.
        /// </summary>
        /// <param name="parent">The parent node.</param>
        /// <param name="oldChild">The old child node to replace.</param>
        /// <param name="newChild">The new child node.</param>
        private void ReplaceChildInParent(Node parent, Node oldChild, Node newChild)
        {
            if (parent == null || parent.Children == null) return;

            for (int i = 0; i < parent.Children.Length; i++)
            {
                if (parent.Children[i] == oldChild)
                {
                    parent.Children[i] = newChild;
                    break;
                }
            }
        }

        /// <summary>
        /// Removes a child node from a parent node.
        /// </summary>
        /// <param name="parent">The parent node.</param>
        /// <param name="childToRemove">The child node to remove.</param>
        private void RemoveChildFromParent(Node parent, Node childToRemove)
        {
            if (parent == null || parent.Children == null) return;

            var newChildren = new List<Node>();
            for (int i = 0; i < parent.Children.Length; i++)
            {
                if (parent.Children[i] != childToRemove && parent.Children[i] != null)
                {
                    newChildren.Add(parent.Children[i]);
                }
            }

            if (newChildren.Count == 0)
            {
                parent.IsLeaf = true;
                parent.Item = null;
                parent.Children = null;
            }
            else if (newChildren.Count == 1)
            {
                Node onlyChild = newChildren[0];
                Node grandParent = FindParentNode(Root, parent);
                if (grandParent != null)
                {
                    ReplaceChildInParent(grandParent, parent, onlyChild);
                    onlyChild.Depth = parent.Depth;
                }
                else
                {
                    Root = onlyChild;
                    onlyChild.Depth = 0;
                }
            }
            else
            {
                parent.Children = newChildren.ToArray();
                parent.Bounds = newChildren[0].Bounds;
                for (int i = 1; i < newChildren.Count; i++)
                {
                    Encapsulate(ref parent.Bounds, newChildren[i].Bounds);
                }
            }
        }
    }
}


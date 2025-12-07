using UnityEditor;
using UnityEngine;

namespace PhotonGISystem2.Editor
{
    [CustomEditor(typeof(RayTraceManager))]
    public sealed class RayTraceManagerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var manager = (RayTraceManager)target;
            if (manager == null || !manager.ShowMemoryStats)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Mesh Buffer Stats", EditorStyles.boldLabel);

            DrawStats("Static Buffer", manager.StaticBufferStats);
            DrawStats("Dynamic Buffer", manager.DynamicBufferStats);

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Shrink Buffers"))
            {
                manager.AutoShrink = true;
                manager.AutoShrinkThreshold = 0.5f;
            }
            if (GUILayout.Button("Increase Static Memory"))
            {
                manager.SetMaxMemoryMB(false, manager.StaticBufferStats.CapacityVertexMB * 2f);
            }
            if (GUILayout.Button("Increase Dynamic Memory"))
            {
                manager.SetMaxMemoryMB(true, manager.DynamicBufferStats.CapacityVertexMB * 2f);
            }
            if (GUILayout.Button("Reset All Buffers"))
            {
                if (EditorUtility.DisplayDialog("Reset Buffers",
                        "This will clear all cached geometry data and rebuild the ray tracing buffers from scratch. Continue?",
                        "Reset", "Cancel"))
                {
                    manager.ResetAllBuffers();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private static void DrawStats(string label, RayTraceManager.MeshMemoryStats stats)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Vertex Capacity", $"{stats.VertexCapacity:N0} ({stats.CapacityVertexMB:F2} MB)");
            EditorGUILayout.LabelField("Vertex Usage", $"{stats.VertexUsage:N0} ({stats.UsedVertexMB:F2} MB)");
            EditorGUILayout.LabelField("Triangle Capacity", $"{stats.TriangleCapacity:N0}");
            EditorGUILayout.LabelField("Triangle Usage", $"{stats.TriangleUsage:N0}");
            EditorGUILayout.LabelField("Active Slots", $"{stats.ActiveSlots} / {stats.TotalSlots}");
            EditorGUILayout.EndVertical();
        }
    }
}


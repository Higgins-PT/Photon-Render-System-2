using UnityEditor;
using UnityEngine;
using PhotonGISystem2;

namespace PhotonGISystem2
{
    [CustomEditor(typeof(CascadedProbeManager))]
    [CanEditMultipleObjects]
    public class CascadedProbeManagerEditor : UnityEditor.Editor
    {
        private bool _showStatistics = true;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            CascadedProbeManager manager = (CascadedProbeManager)target;
            if (manager == null)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Probe Statistics", EditorStyles.boldLabel);
            
            _showStatistics = EditorGUILayout.Foldout(_showStatistics, "Show Statistics", true);
            
            if (_showStatistics)
            {
                EditorGUI.indentLevel++;
                
                var stats = manager.GetProbeStatistics();
                
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                
                EditorGUILayout.LabelField("Configuration", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Probes Per Axis: {stats.ProbesPerAxis}");
                EditorGUILayout.LabelField($"Cascade Count: {stats.CascadeCount}");
                EditorGUILayout.LabelField($"Probes Per Cascade: {stats.ProbesPerCascade:N0}");
                EditorGUILayout.LabelField($"Expected Probes Per Camera: {stats.ExpectedProbesPerCamera:N0}");
                
                EditorGUILayout.Space();
                
                EditorGUILayout.LabelField("Runtime Status", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Active Cameras: {stats.CameraCount}");
                EditorGUILayout.LabelField($"Total Probes: {stats.TotalProbes:N0}");
                
                if (stats.CameraCount > 0)
                {
                    int avgProbesPerCamera = stats.TotalProbes / stats.CameraCount;
                    EditorGUILayout.LabelField($"Average Probes Per Camera: {avgProbesPerCamera:N0}");
                    
                    if (avgProbesPerCamera != stats.ExpectedProbesPerCamera)
                    {
                        EditorGUILayout.HelpBox(
                            $"Warning: Average probes per camera ({avgProbesPerCamera}) doesn't match expected ({stats.ExpectedProbesPerCamera}). " +
                            "This may indicate incomplete probe generation.",
                            MessageType.Warning);
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox("No probes generated yet. Probes will be created when rendering starts.", MessageType.Info);
                }
                
                EditorGUILayout.EndVertical();
                
                EditorGUI.indentLevel--;
            }
        }
    }
}


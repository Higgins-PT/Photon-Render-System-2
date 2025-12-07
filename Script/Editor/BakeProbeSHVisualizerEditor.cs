using PhotonGISystem2;
using UnityEditor;
using UnityEngine;

namespace PhotonGISystem2.Editor
{
    [CustomEditor(typeof(BakeProbeSHVisualizer))]
    public class BakeProbeSHVisualizerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            var visualizer = (BakeProbeSHVisualizer)target;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("PDF Sampling Test", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Accepted Samples", visualizer.LastPdfSampleCount.ToString());
            EditorGUILayout.LabelField("Total Attempts", visualizer.LastPdfSampleAttemptCount.ToString());

            if (GUILayout.Button("Run PDF Sampling Test"))
            {
                visualizer.RunPdfSamplingTest();
                EditorUtility.SetDirty(visualizer);
                SceneView.RepaintAll();
            }
        }
    }
}


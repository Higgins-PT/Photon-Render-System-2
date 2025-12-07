using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PhotonGISystem2.Editor
{
    [CustomEditor(typeof(PhotonRenderSystemManager))]
    public class PhotonRenderSystemManagerEditor : UnityEditor.Editor
    {
        private SerializedProperty _currentConfigProp;
        private SerializedProperty _presetsProp;
        private SerializedProperty _selectedIndexProp;
        private SerializedProperty _pendingNameProp;

        private SerializedProperty _photonRenderProp;
        private SerializedProperty _rayTraceProp;
        private SerializedProperty _svgfProp;
        private SerializedProperty _dlssProp;

        private bool _photonFoldout = true;
        private bool _rayTraceFoldout = true;
        private bool _svgfFoldout = true;
        private bool _dlssFoldout = true;

        private void OnEnable()
        {
            _currentConfigProp = serializedObject.FindProperty("currentConfiguration");
            _presetsProp = serializedObject.FindProperty("configurationPresets");
            _selectedIndexProp = serializedObject.FindProperty("selectedPresetIndex");
            _pendingNameProp = serializedObject.FindProperty("pendingSaveName");

            _photonRenderProp = _currentConfigProp.FindPropertyRelative("photonRender");
            _rayTraceProp = _currentConfigProp.FindPropertyRelative("rayTrace");
            _svgfProp = _currentConfigProp.FindPropertyRelative("svgf");
            _dlssProp = _currentConfigProp.FindPropertyRelative("dlss");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            DrawSystemCheckAndFix();
            EditorGUILayout.Space();

            DrawPresetControls();
            EditorGUILayout.Space();
            DrawConfigurationSection();
            EditorGUILayout.Space();
            DrawUtilityButtons();

            serializedObject.ApplyModifiedProperties();
            ((PhotonRenderSystemManager)target).NotifyConfigurationEditedFromInspector();
        }

        private void DrawSystemCheckAndFix()
        {
            bool isD3D12 = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;
            bool hasPhotonFeature = CheckPhotonRendererFeature();

            if (isD3D12 && !hasPhotonFeature)
            {
                EditorGUILayout.HelpBox(
                    "Current graphics API is D3D12, but PhotonRendererFeature is not found in URP RendererData. " +
                    "Please add PhotonRendererFeature to ensure proper rendering.",
                    MessageType.Warning);

                if (GUILayout.Button("Fix: Add PhotonRendererFeature", GUILayout.Height(30f)))
                {
                    FixPhotonRendererFeature();
                }
            }
        }

        private bool CheckPhotonRendererFeature()
        {
            var urpAsset = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset == null)
                return false;

            var rendererDataList = GetRendererDataList(urpAsset);
            if (rendererDataList == null || rendererDataList.Length == 0)
                return false;

            foreach (var rendererData in rendererDataList)
            {
                if (rendererData == null)
                    continue;

                var rendererFeatures = rendererData.rendererFeatures;
                if (rendererFeatures != null)
                {
                    foreach (var feature in rendererFeatures)
                    {
                        if (feature is PhotonRendererFeature)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private ScriptableRendererData[] GetRendererDataList(UniversalRenderPipelineAsset urpAsset)
        {
            var fieldInfo = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (fieldInfo != null)
            {
                return fieldInfo.GetValue(urpAsset) as ScriptableRendererData[];
            }
            return null;
        }

        private void FixPhotonRendererFeature()
        {
            var urpAsset = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (urpAsset == null)
            {
                EditorUtility.DisplayDialog("Error", "No URP Asset found in Graphics Settings.", "OK");
                return;
            }

            var rendererDataList = GetRendererDataList(urpAsset);
            if (rendererDataList == null || rendererDataList.Length == 0)
            {
                EditorUtility.DisplayDialog("Error", "No RendererData found in URP Asset.", "OK");
                return;
            }

            bool anyFixed = false;
            foreach (var rendererData in rendererDataList)
            {
                if (rendererData == null)
                    continue;

                bool hasPhotonFeature = false;
                var rendererFeatures = rendererData.rendererFeatures;
                if (rendererFeatures != null)
                {
                    foreach (var feature in rendererFeatures)
                    {
                        if (feature is PhotonRendererFeature)
                        {
                            hasPhotonFeature = true;
                            break;
                        }
                    }
                }

                if (!hasPhotonFeature)
                {
                    Undo.RecordObject(rendererData, "Add PhotonRendererFeature");
                    
                    var photonFeature = ScriptableObject.CreateInstance<PhotonRendererFeature>();
                    photonFeature.name = "PhotonRendererFeature";
                    
                    string rendererDataPath = AssetDatabase.GetAssetPath(rendererData);
                    if (!string.IsNullOrEmpty(rendererDataPath))
                    {
                        AssetDatabase.AddObjectToAsset(photonFeature, rendererData);
                    }
                    
                    using (var serializedObject = new SerializedObject(rendererData))
                    {
                        var rendererFeaturesProp = serializedObject.FindProperty("m_RendererFeatures");
                        if (rendererFeaturesProp != null)
                        {
                            rendererFeaturesProp.arraySize++;
                            var newFeatureProp = rendererFeaturesProp.GetArrayElementAtIndex(rendererFeaturesProp.arraySize - 1);
                            newFeatureProp.objectReferenceValue = photonFeature;
                            serializedObject.ApplyModifiedProperties();
                        }
                    }

                    EditorUtility.SetDirty(rendererData);
                    EditorUtility.SetDirty(photonFeature);
                    anyFixed = true;
                }
            }

            if (anyFixed)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("Success", "PhotonRendererFeature has been added to all RendererData.", "OK");
            }
            else
            {
                EditorUtility.DisplayDialog("Info", "PhotonRendererFeature already exists in all RendererData.", "OK");
            }
        }

        private void DrawPresetControls()
        {
            EditorGUILayout.PropertyField(_presetsProp, new GUIContent("Preset Assets"), true);
            EditorGUILayout.Space(4f);

            var manager = (PhotonRenderSystemManager)target;
            int presetCount = manager.ConfigurationPresets?.Count ?? 0;
            if (presetCount == 0)
            {
                _selectedIndexProp.intValue = -1;
                EditorGUILayout.HelpBox("Assign preset assets to enable loading.", MessageType.Info);
            }
            else
            {
                _selectedIndexProp.intValue = Mathf.Clamp(_selectedIndexProp.intValue, -1, presetCount - 1);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.PrefixLabel("Preset");
                    string[] names = BuildPresetNames(manager);
                    int newIndex = EditorGUILayout.Popup(Mathf.Max(_selectedIndexProp.intValue, 0), names);
                    if (newIndex != _selectedIndexProp.intValue)
                    {
                        _selectedIndexProp.intValue = newIndex;
                    }

                    GUI.enabled = _selectedIndexProp.intValue >= 0 && _selectedIndexProp.intValue < presetCount;
                    if (GUILayout.Button("Load", GUILayout.Width(70f)))
                    {
                        ApplyPresetFromPopup();
                    }
                    GUI.enabled = true;
                }
            }

            _pendingNameProp.stringValue = EditorGUILayout.TextField("Config Name", _pendingNameProp.stringValue);
        }

        private void DrawConfigurationSection()
        {
            _photonFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_photonFoldout, "Photon Render Manager");
            if (_photonFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_photonRenderProp.FindPropertyRelative("enableRayTracing"));
                EditorGUILayout.PropertyField(_photonRenderProp.FindPropertyRelative("brdfSettings"), true);
                EditorGUILayout.PropertyField(_photonRenderProp.FindPropertyRelative("maxIterations"));
                EditorGUILayout.PropertyField(_photonRenderProp.FindPropertyRelative("skyboxExposure"));
                EditorGUILayout.PropertyField(_photonRenderProp.FindPropertyRelative("mainLight"), true);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            EditorGUILayout.Space(4f);

            _rayTraceFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_rayTraceFoldout, "Ray Trace Manager");
            if (_rayTraceFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_rayTraceProp.FindPropertyRelative("environmentCubemapSize"));
                EditorGUILayout.PropertyField(_rayTraceProp.FindPropertyRelative("maxRayDistance"));
                EditorGUILayout.PropertyField(_rayTraceProp.FindPropertyRelative("staticPool"), true);
                EditorGUILayout.PropertyField(_rayTraceProp.FindPropertyRelative("dynamicPool"), true);
                EditorGUILayout.PropertyField(_rayTraceProp.FindPropertyRelative("fragmentationThreshold"));
                EditorGUILayout.PropertyField(_rayTraceProp.FindPropertyRelative("autoShrink"));
                EditorGUILayout.PropertyField(_rayTraceProp.FindPropertyRelative("shrinkThreshold"));
                EditorGUILayout.PropertyField(_rayTraceProp.FindPropertyRelative("showMemoryStats"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            EditorGUILayout.Space(4f);

            _svgfFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_svgfFoldout, "SVGF Denoiser Manager");
            if (_svgfFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_svgfProp.FindPropertyRelative("temporalBlend"));
                EditorGUILayout.PropertyField(_svgfProp.FindPropertyRelative("depthSigma"));
                EditorGUILayout.PropertyField(_svgfProp.FindPropertyRelative("normalSigma"));
                EditorGUILayout.PropertyField(_svgfProp.FindPropertyRelative("atrousIterations"));
                EditorGUILayout.PropertyField(_svgfProp.FindPropertyRelative("enableTemporalAccumulation"));
                EditorGUILayout.PropertyField(_svgfProp.FindPropertyRelative("enableSpatialFiltering"));
                EditorGUILayout.PropertyField(_svgfProp.FindPropertyRelative("enableHistoryClamping"));
                EditorGUILayout.PropertyField(_svgfProp.FindPropertyRelative("normalHistoryThreshold"));
                EditorGUILayout.PropertyField(_svgfProp.FindPropertyRelative("worldPositionHistoryThreshold"));
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            EditorGUILayout.Space(4f);

            _dlssFoldout = EditorGUILayout.BeginFoldoutHeaderGroup(_dlssFoldout, "DLSS Denoiser Manager");
            if (_dlssFoldout)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_dlssProp.FindPropertyRelative("enableDlss"));
                EditorGUILayout.PropertyField(_dlssProp.FindPropertyRelative("enableDlssDenoise"));
                EditorGUILayout.PropertyField(_dlssProp.FindPropertyRelative("useHalfResolutionInput"));
                EditorGUILayout.PropertyField(_dlssProp.FindPropertyRelative("quality"));
                EditorGUILayout.PropertyField(_dlssProp.FindPropertyRelative("renderSettings"), true);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawUtilityButtons()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Save", GUILayout.Width(80f)))
                {
                    SaveCurrentConfiguration();
                }

                if (GUILayout.Button("Reload From Scene"))
                {
                    serializedObject.ApplyModifiedProperties();
                    ((PhotonRenderSystemManager)target).SyncFromManagers();
                    serializedObject.Update();
                }
            }
        }

        private string[] BuildPresetNames(PhotonRenderSystemManager manager)
        {
            var presets = manager.ConfigurationPresets;
            string[] names = new string[presets.Count];
            for (int i = 0; i < presets.Count; i++)
            {
                names[i] = presets[i] != null ? presets[i].name : "<Missing>";
            }
            return names;
        }

        private void ApplyPresetFromPopup()
        {
            serializedObject.ApplyModifiedProperties();
            var manager = (PhotonRenderSystemManager)target;
            manager.LoadPresetByIndex(_selectedIndexProp.intValue);
            _pendingNameProp.stringValue = manager.PendingSaveName;
            serializedObject.Update();
        }

        private void SaveCurrentConfiguration()
        {
            var manager = (PhotonRenderSystemManager)target;
            string desiredName = _pendingNameProp.stringValue?.Trim();
            if (string.IsNullOrEmpty(desiredName))
            {
                EditorUtility.DisplayDialog("Save Configuration", "Please enter a name for the configuration.", "OK");
                return;
            }

            serializedObject.ApplyModifiedProperties();

            PhotonRenderSystemConfig existing = null;
            int existingIndex = -1;
            var presets = manager.ConfigurationPresets;
            if (presets != null)
            {
                for (int i = 0; i < presets.Count; i++)
                {
                    if (presets[i] != null && string.Equals(presets[i].name, desiredName, StringComparison.OrdinalIgnoreCase))
                    {
                        existing = presets[i];
                        existingIndex = i;
                        break;
                    }
                }
            }

            if (existing != null)
            {
                bool overwrite = EditorUtility.DisplayDialog(
                    "Overwrite Configuration?",
                    $"Preset '{existing.name}' already exists. Overwrite it?",
                    "Overwrite",
                    "Cancel");

                if (!overwrite)
                {
                    serializedObject.Update();
                    return;
                }

                Undo.RecordObject(existing, "Overwrite Photon Render Config");
                existing.Data = manager.CurrentConfiguration;
                EditorUtility.SetDirty(existing);
                AssetDatabase.SaveAssets();
                _selectedIndexProp.intValue = existingIndex;
                manager.SetPendingSaveName(existing.name);
            }
            else
            {
                const string rootFolder = "Assets/PhotonGISystem2";
                if (!AssetDatabase.IsValidFolder(rootFolder))
                {
                    AssetDatabase.CreateFolder("Assets", "PhotonGISystem2");
                }

                string folder = $"{rootFolder}/PhotonRenderSystemConfigs";
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    AssetDatabase.CreateFolder(rootFolder, "PhotonRenderSystemConfigs");
                }

                string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{desiredName}.asset");
                var newConfig = ScriptableObject.CreateInstance<PhotonRenderSystemConfig>();
                newConfig.Data = manager.CurrentConfiguration;
                AssetDatabase.CreateAsset(newConfig, path);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                int newIndex = _presetsProp.arraySize;
                _presetsProp.InsertArrayElementAtIndex(newIndex);
                _presetsProp.GetArrayElementAtIndex(newIndex).objectReferenceValue = newConfig;
                _selectedIndexProp.intValue = newIndex;
                manager.SetPendingSaveName(newConfig.name);
            }

            _pendingNameProp.stringValue = manager.PendingSaveName;
            serializedObject.Update();
        }
    }
}


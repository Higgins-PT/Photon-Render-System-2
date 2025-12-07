using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PhotonGISystem2.Editor
{
    /// <summary>
    /// Utility that converts selected objects (and their children) from URP Lit materials to PhotonLit materials.
    /// </summary>
    public static class PhotonMaterialConverter
    {
        private const string TargetShaderName = "PhotonSystem/PhotonLitShader";
        private const string SourceShaderName = "Universal Render Pipeline/Lit";
        private const string DefaultOutputFolder = "Assets/PhotonConvertedMaterials";

        [MenuItem("Tools/Photon/Materials/Convert Selected (Duplicate)", priority = 200)]
        public static void ConvertSelectedDuplicate()
        {
            ConvertSelected(MaterialConversionMode.Duplicate);
        }

        [MenuItem("Tools/Photon/Materials/Convert Selected (Replace)", priority = 201)]
        public static void ConvertSelectedReplace()
        {
            ConvertSelected(MaterialConversionMode.Replace);
        }

        private enum MaterialConversionMode
        {
            Duplicate,
            Replace
        }

        private readonly struct RendererMaterialSlot
        {
            public Renderer Renderer { get; }
            public int MaterialIndex { get; }

            public RendererMaterialSlot(Renderer renderer, int materialIndex)
            {
                Renderer = renderer;
                MaterialIndex = materialIndex;
            }
        }

        private static void ConvertSelected(MaterialConversionMode mode)
        {
            if (Selection.transforms == null || Selection.transforms.Length == 0)
            {
                EditorUtility.DisplayDialog("Photon Material Converter", "Please select at least one GameObject in the hierarchy.", "OK");
                return;
            }

            Shader targetShader = Shader.Find(TargetShaderName);
            if (targetShader == null)
            {
                EditorUtility.DisplayDialog("Photon Material Converter", $"Could not find shader '{TargetShaderName}'.", "OK");
                return;
            }

            if (mode == MaterialConversionMode.Replace)
            {
                bool confirmed = EditorUtility.DisplayDialog(
                    "Photon Material Converter",
                    "This operation permanently replaces the original URP Lit materials (assets will be deleted). Continue?",
                    "Replace", "Cancel");
                if (!confirmed)
                    return;
            }

            var renderers = CollectRenderersFromSelection();
            if (renderers.Count == 0)
            {
                EditorUtility.DisplayDialog("Photon Material Converter", "No MeshRenderer or SkinnedMeshRenderer components were found on the selected objects.", "OK");
                return;
            }

            int convertedCount = 0;
            var conversionMap = new Dictionary<Material, Material>();
            var materialUsage = new Dictionary<Material, List<RendererMaterialSlot>>();

            foreach (var renderer in renderers)
            {
                var sharedMats = renderer.sharedMaterials;
                for (int i = 0; i < sharedMats.Length; i++)
                {
                    var original = sharedMats[i];
                    if (!ShouldConvert(original))
                        continue;

                    if (!materialUsage.TryGetValue(original, out var usageList))
                    {
                        usageList = new List<RendererMaterialSlot>();
                        materialUsage.Add(original, usageList);
                    }

                    usageList.Add(new RendererMaterialSlot(renderer, i));
                }
            }

            var rendererMaterialCache = new Dictionary<Renderer, Material[]>();
            var modifiedRenderers = new HashSet<Renderer>();

            foreach (var entry in materialUsage)
            {
                var original = entry.Key;
                var slots = entry.Value;

                if (!conversionMap.TryGetValue(original, out var converted))
                {
                    converted = mode == MaterialConversionMode.Duplicate
                        ? CreateConvertedMaterial(original, targetShader)
                        : ReplaceOriginalMaterial(original, targetShader);

                    if (converted == null)
                        continue;

                    conversionMap.Add(original, converted);
                    convertedCount++;
                }

                foreach (var slot in slots)
                {
                    if (slot.Renderer == null)
                        continue;

                    if (!rendererMaterialCache.TryGetValue(slot.Renderer, out var sharedMats))
                    {
                        sharedMats = slot.Renderer.sharedMaterials;
                        rendererMaterialCache.Add(slot.Renderer, sharedMats);
                    }

                    if (slot.MaterialIndex < 0 || slot.MaterialIndex >= sharedMats.Length)
                        continue;

                    sharedMats[slot.MaterialIndex] = converted;
                    modifiedRenderers.Add(slot.Renderer);
                }
            }

            foreach (var renderer in modifiedRenderers)
            {
                renderer.sharedMaterials = rendererMaterialCache[renderer];
                EditorUtility.SetDirty(renderer);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "Photon Material Converter",
                convertedCount > 0
                    ? $"Converted {convertedCount} material(s) to '{TargetShaderName}'."
                    : "No materials using the URP Lit shader were found on the selected objects.",
                "OK");
        }

        private static bool ShouldConvert(Material material)
        {
            if (material == null)
                return false;

            var shader = material.shader;
            if (shader == null)
                return false;

            if (shader.name != SourceShaderName)
                return false;

            return true;
        }

        private static HashSet<Renderer> CollectRenderersFromSelection()
        {
            var result = new HashSet<Renderer>();
            foreach (var transform in Selection.transforms)
            {
                if (transform == null)
                    continue;

                foreach (var renderer in transform.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer != null)
                        result.Add(renderer);
                }
            }

            return result;
        }

        private static Material CreateConvertedMaterial(Material original, Shader targetShader)
        {
            if (original == null || targetShader == null)
                return null;

            var copy = new Material(original)
            {
                name = $"{original.name}_Photon",
                shader = targetShader
            };

            string sourcePath = AssetDatabase.GetAssetPath(original);
            string newPath;

            if (string.IsNullOrEmpty(sourcePath))
            {
                EnsureDefaultFolder();
                newPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(DefaultOutputFolder, $"{copy.name}.mat"));
            }
            else
            {
                string directory = Path.GetDirectoryName(sourcePath);
                string fileName = Path.GetFileNameWithoutExtension(sourcePath);
                string targetPath = Path.Combine(directory ?? "Assets", $"{fileName}_Photon.mat");
                newPath = AssetDatabase.GenerateUniqueAssetPath(targetPath);
            }

            AssetDatabase.CreateAsset(copy, newPath);
            return copy;
        }

        private static Material ReplaceOriginalMaterial(Material original, Shader targetShader)
        {
            if (original == null || targetShader == null)
                return null;

            string assetPath = AssetDatabase.GetAssetPath(original);
            bool hasAsset = !string.IsNullOrEmpty(assetPath);

            if (!hasAsset)
            {
                return CreateConvertedMaterial(original, targetShader);
            }

            var photonMaterial = new Material(targetShader)
            {
                name = Path.GetFileNameWithoutExtension(assetPath)
            };
            photonMaterial.CopyPropertiesFromMaterial(original);

            string folder = Path.GetDirectoryName(assetPath);
            string photonPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(folder ?? "Assets", $"{photonMaterial.name}_Photon.mat"));
            AssetDatabase.CreateAsset(photonMaterial, photonPath);

            AssetDatabase.DeleteAsset(assetPath);

            return photonMaterial;
        }

        private static void EnsureDefaultFolder()
        {
            if (AssetDatabase.IsValidFolder(DefaultOutputFolder))
                return;

            var parts = DefaultOutputFolder.Split('/');
            if (parts.Length == 0)
                return;

            string currentPath = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string folderName = parts[i];
                string targetPath = $"{currentPath}/{folderName}";
                if (!AssetDatabase.IsValidFolder(targetPath))
                {
                    AssetDatabase.CreateFolder(currentPath, folderName);
                }
                currentPath = targetPath;
            }
        }
    }
}


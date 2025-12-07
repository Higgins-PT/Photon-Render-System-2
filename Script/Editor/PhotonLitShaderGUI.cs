using System;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace PhotonGISystem2.Editor
{
    internal class PhotonLitShaderGUI : BaseShaderGUI
    {
        private LitGUI.LitProperties litProperties;
        private LitDetailGUI.LitProperties litDetailProperties;
        private MaterialProperty anisotropyProp;
        private MaterialProperty iorProp;

        private static readonly GUIContent AnisotropyLabel = EditorGUIUtility.TrTextContent(
            "Anisotropy",
            "Controls the anisotropic stretch applied to ray-traced specular responses.");
        private static readonly GUIContent IorLabel = EditorGUIUtility.TrTextContent(
            "Index of Refraction",
            "Sets the per-material IOR used by the ray-traced transmission path.");

        public override void FillAdditionalFoldouts(MaterialHeaderScopeList materialScopesList)
        {
            materialScopesList.RegisterHeaderScope(
                LitDetailGUI.Styles.detailInputs,
                Expandable.Details,
                _ => LitDetailGUI.DoDetailArea(litDetailProperties, materialEditor));
        }

        public override void FindProperties(MaterialProperty[] properties)
        {
            base.FindProperties(properties);
            litProperties = new LitGUI.LitProperties(properties);
            litDetailProperties = new LitDetailGUI.LitProperties(properties);
            anisotropyProp = BaseShaderGUI.FindProperty("_Anisotropy", properties, false);
            iorProp = BaseShaderGUI.FindProperty("_Ior", properties, false);
        }

        public override void ValidateMaterial(Material material)
        {
            SetMaterialKeywords(material, SetPhotonMaterialKeywords, LitDetailGUI.SetMaterialKeywords);
        }

        public override void DrawSurfaceOptions(Material material)
        {
            EditorGUIUtility.labelWidth = 0f;

            base.DrawSurfaceOptions(material);
        }

        public override void DrawSurfaceInputs(Material material)
        {
            base.DrawSurfaceInputs(material);
            DrawPhotonSurfaceInputs(material);
            DrawEmissionProperties(material, true);
            DrawTileOffset(materialEditor, baseMapProp);
        }

        public override void DrawAdvancedOptions(Material material)
        {
            if (litProperties.reflections != null && litProperties.highlights != null)
            {
                materialEditor.ShaderProperty(litProperties.highlights, LitGUI.Styles.highlightsText);
                materialEditor.ShaderProperty(litProperties.reflections, LitGUI.Styles.reflectionsText);
            }

            base.DrawAdvancedOptions(material);
        }

        public override void AssignNewShaderToMaterial(Material material, Shader oldShader, Shader newShader)
        {
            if (material == null)
                throw new ArgumentNullException(nameof(material));

            if (material.HasProperty("_Emission"))
            {
                material.SetColor("_EmissionColor", material.GetColor("_Emission"));
            }

            base.AssignNewShaderToMaterial(material, oldShader, newShader);

            if (oldShader == null || !oldShader.name.Contains("Legacy Shaders/"))
            {
                SetupMaterialBlendMode(material);
                return;
            }

            var surfaceType = SurfaceType.Opaque;
            var blendMode = BlendMode.Alpha;

            if (oldShader.name.Contains("/Transparent/Cutout/"))
            {
                surfaceType = SurfaceType.Opaque;
                material.SetFloat("_AlphaClip", 1);
            }
            else if (oldShader.name.Contains("/Transparent/"))
            {
                surfaceType = SurfaceType.Transparent;
                blendMode = BlendMode.Alpha;
            }

            material.SetFloat("_Blend", (float)blendMode);
            material.SetFloat("_Surface", (float)surfaceType);

            if (surfaceType == SurfaceType.Opaque)
            {
                material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            else
            {
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }

            if (oldShader.name.Equals("Standard (Specular setup)"))
            {
                var specGloss = material.GetTexture("_SpecGlossMap");
                if (specGloss != null)
                    material.SetTexture("_MetallicSpecGlossMap", specGloss);
            }
            else
            {
                var metallic = material.GetTexture("_MetallicGlossMap");
                if (metallic != null)
                    material.SetTexture("_MetallicSpecGlossMap", metallic);
            }

            material.SetFloat("_Anisotropy", 0.0f);
            material.SetFloat("_Ior", 1.5f);
        }

        private void DrawPhotonSurfaceInputs(Material material)
        {
            DrawPhotonMetallicSpecularArea(material);
            BaseShaderGUI.DrawNormalArea(materialEditor, litProperties.bumpMapProp, litProperties.bumpScaleProp);

            bool hasExtraControls = anisotropyProp != null || iorProp != null;
            if (hasExtraControls)
            {
                EditorGUILayout.Space();
            }

            if (anisotropyProp != null)
            {
                materialEditor.ShaderProperty(anisotropyProp, AnisotropyLabel);
            }

            if (iorProp != null)
            {
                materialEditor.ShaderProperty(iorProp, IorLabel);
            }

            if (HasHeightmap(material))
            {
                materialEditor.TexturePropertySingleLine(
                    LitGUI.Styles.heightMapText,
                    litProperties.parallaxMapProp,
                    litProperties.parallaxMapProp.textureValue != null ? litProperties.parallaxScaleProp : null);
            }

            if (litProperties.occlusionMap != null)
            {
                materialEditor.TexturePropertySingleLine(
                    LitGUI.Styles.occlusionText,
                    litProperties.occlusionMap,
                    litProperties.occlusionMap.textureValue != null ? litProperties.occlusionStrength : null);
            }

            if (HasClearCoat(material))
            {
                LitGUI.DoClearCoat(litProperties, materialEditor, material);
            }
        }

        private void DrawPhotonMetallicSpecularArea(Material material)
        {
            if (litProperties.specGlossMap != null || litProperties.specColor != null)
            {
                BaseShaderGUI.TextureColorProps(
                    materialEditor,
                    LitGUI.Styles.specularMapText,
                    litProperties.specGlossMap,
                    litProperties.specColor);
            }

            if (litProperties.metallicGlossMap != null || litProperties.metallic != null)
            {
                materialEditor.TexturePropertySingleLine(
                    LitGUI.Styles.metallicMapText,
                    litProperties.metallicGlossMap,
                    litProperties.metallic);
            }

            var smoothnessChannelNames = ShouldUseSpecularWorkflow(material)
                ? LitGUI.Styles.specularSmoothnessChannelNames
                : LitGUI.Styles.metallicSmoothnessChannelNames;

            LitGUI.DoSmoothness(
                materialEditor,
                material,
                litProperties.smoothness,
                litProperties.smoothnessMapChannel,
                smoothnessChannelNames);
        }

        private static bool HasHeightmap(Material material)
        {
            return material != null &&
                   material.HasProperty("_Parallax") &&
                   material.HasProperty("_ParallaxMap");
        }

        private static bool HasClearCoat(Material material)
        {
            return material != null &&
                   material.HasProperty("_ClearCoat") &&
                   material.HasProperty("_ClearCoatMap") &&
                   material.HasProperty("_ClearCoatMask") &&
                   material.HasProperty("_ClearCoatSmoothness");
        }

        private static void SetPhotonMaterialKeywords(Material material)
        {
            if (material == null)
                return;

            bool useSpecularWorkflow = ShouldUseSpecularWorkflow(material);
            CoreUtils.SetKeyword(material, "_SPECULAR_SETUP", useSpecularWorkflow);

            string glossMapProperty = useSpecularWorkflow ? "_SpecGlossMap" : "_MetallicGlossMap";
            bool hasGlossMap = material.HasProperty(glossMapProperty) && material.GetTexture(glossMapProperty) != null;
            CoreUtils.SetKeyword(material, "_METALLICSPECGLOSSMAP", hasGlossMap);

            if (material.HasProperty("_SpecularHighlights"))
            {
                CoreUtils.SetKeyword(
                    material,
                    "_SPECULARHIGHLIGHTS_OFF",
                    material.GetFloat("_SpecularHighlights") == 0.0f);
            }

            if (material.HasProperty("_EnvironmentReflections"))
            {
                CoreUtils.SetKeyword(
                    material,
                    "_ENVIRONMENTREFLECTIONS_OFF",
                    material.GetFloat("_EnvironmentReflections") == 0.0f);
            }

            if (material.HasProperty("_OcclusionMap"))
            {
                CoreUtils.SetKeyword(material, "_OCCLUSIONMAP", material.GetTexture("_OcclusionMap"));
            }

            if (material.HasProperty("_ParallaxMap"))
            {
                CoreUtils.SetKeyword(material, "_PARALLAXMAP", material.GetTexture("_ParallaxMap"));
            }

            if (material.HasProperty("_SmoothnessTextureChannel"))
            {
                bool useAlbedoAlpha = Mathf.Approximately(material.GetFloat("_SmoothnessTextureChannel"), 1f);
                CoreUtils.SetKeyword(material, "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A", useAlbedoAlpha && IsOpaqueMaterial(material));
            }

            UpdateClearCoatKeywords(material);
        }

        private static void UpdateClearCoatKeywords(Material material)
        {
            if (!HasClearCoat(material))
            {
                CoreUtils.SetKeyword(material, "_CLEARCOAT", false);
                CoreUtils.SetKeyword(material, "_CLEARCOATMAP", false);
                return;
            }

            bool clearCoatEnabled = material.GetFloat("_ClearCoat") > 0.0f;
            if (!clearCoatEnabled)
            {
                CoreUtils.SetKeyword(material, "_CLEARCOAT", false);
                CoreUtils.SetKeyword(material, "_CLEARCOATMAP", false);
                return;
            }

            bool hasClearCoatMap = material.GetTexture("_ClearCoatMap") != null;
            CoreUtils.SetKeyword(material, "_CLEARCOAT", !hasClearCoatMap);
            CoreUtils.SetKeyword(material, "_CLEARCOATMAP", hasClearCoatMap);
        }

        private static bool ShouldUseSpecularWorkflow(Material material)
        {
            if (material == null)
                return false;

            if (material.GetTexture("_SpecGlossMap") != null)
                return true;

            if (!material.HasProperty("_SpecColor"))
                return false;

            return !ColorsApproximatelyEqual(material.GetColor("_SpecColor"), Color.white);
        }

        private static bool IsOpaqueMaterial(Material material)
        {
            if (material == null || !material.HasProperty("_Surface"))
                return true;

            return Mathf.Approximately(material.GetFloat("_Surface"), (float)SurfaceType.Opaque);
        }

        private static bool ColorsApproximatelyEqual(Color a, Color b)
        {
            const float threshold = 0.0001f;
            return Mathf.Abs(a.r - b.r) < threshold &&
                   Mathf.Abs(a.g - b.g) < threshold &&
                   Mathf.Abs(a.b - b.b) < threshold &&
                   Mathf.Abs(a.a - b.a) < threshold;
        }
    }
    internal class LitDetailGUI
    {
        internal static class Styles
        {
            public static readonly GUIContent detailInputs = EditorGUIUtility.TrTextContent("Detail Inputs",
                "These settings define the surface details by tiling and overlaying additional maps on the surface.");

            public static readonly GUIContent detailMaskText = EditorGUIUtility.TrTextContent("Mask",
                "Select a mask for the Detail map. The mask uses the alpha channel of the selected texture. The Tiling and Offset settings have no effect on the mask.");

            public static readonly GUIContent detailAlbedoMapText = EditorGUIUtility.TrTextContent("Base Map",
                "Select the surface detail texture.The alpha of your texture determines surface hue and intensity.");

            public static readonly GUIContent detailNormalMapText = EditorGUIUtility.TrTextContent("Normal Map",
                "Designates a Normal Map to create the illusion of bumps and dents in the details of this Material's surface.");

            public static readonly GUIContent detailAlbedoMapScaleInfo = EditorGUIUtility.TrTextContent("Setting the scaling factor to a value other than 1 results in a less performant shader variant.");
            public static readonly GUIContent detailAlbedoMapFormatError = EditorGUIUtility.TrTextContent("This texture is not in linear space.");
        }

        public struct LitProperties
        {
            public MaterialProperty detailMask;
            public MaterialProperty detailAlbedoMapScale;
            public MaterialProperty detailAlbedoMap;
            public MaterialProperty detailNormalMapScale;
            public MaterialProperty detailNormalMap;

            public LitProperties(MaterialProperty[] properties)
            {
                detailMask = BaseShaderGUI.FindProperty("_DetailMask", properties, false);
                detailAlbedoMapScale = BaseShaderGUI.FindProperty("_DetailAlbedoMapScale", properties, false);
                detailAlbedoMap = BaseShaderGUI.FindProperty("_DetailAlbedoMap", properties, false);
                detailNormalMapScale = BaseShaderGUI.FindProperty("_DetailNormalMapScale", properties, false);
                detailNormalMap = BaseShaderGUI.FindProperty("_DetailNormalMap", properties, false);
            }
        }

        public static void DoDetailArea(LitProperties properties, MaterialEditor materialEditor)
        {
            materialEditor.TexturePropertySingleLine(Styles.detailMaskText, properties.detailMask);
            materialEditor.TexturePropertySingleLine(Styles.detailAlbedoMapText, properties.detailAlbedoMap,
                properties.detailAlbedoMap.textureValue != null ? properties.detailAlbedoMapScale : null);
            if (properties.detailAlbedoMapScale.floatValue != 1.0f)
            {
                EditorGUILayout.HelpBox(Styles.detailAlbedoMapScaleInfo.text, MessageType.Info, true);
            }
            var detailAlbedoTexture = properties.detailAlbedoMap.textureValue as Texture2D;
            if (detailAlbedoTexture != null && GraphicsFormatUtility.IsSRGBFormat(detailAlbedoTexture.graphicsFormat))
            {
                EditorGUILayout.HelpBox(Styles.detailAlbedoMapFormatError.text, MessageType.Warning, true);
            }
            materialEditor.TexturePropertySingleLine(Styles.detailNormalMapText, properties.detailNormalMap,
                properties.detailNormalMap.textureValue != null ? properties.detailNormalMapScale : null);
            materialEditor.TextureScaleOffsetProperty(properties.detailAlbedoMap);
        }

        public static void SetMaterialKeywords(Material material)
        {
            if (material.HasProperty("_DetailAlbedoMap") && material.HasProperty("_DetailNormalMap") && material.HasProperty("_DetailAlbedoMapScale"))
            {
                bool isScaled = material.GetFloat("_DetailAlbedoMapScale") != 1.0f;
                bool hasDetailMap = material.GetTexture("_DetailAlbedoMap") || material.GetTexture("_DetailNormalMap");
                CoreUtils.SetKeyword(material, "_DETAIL_MULX2", !isScaled && hasDetailMap);
                CoreUtils.SetKeyword(material, "_DETAIL_SCALED", isScaled && hasDetailMap);
            }
        }
    }
}


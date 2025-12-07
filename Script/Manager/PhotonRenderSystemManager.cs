using System.Collections.Generic;
using UnityEngine;

namespace PhotonGISystem2
{
    /// <summary>
    /// Central manager that coordinates render-quality presets across all Photon managers.
    /// </summary>
    public class PhotonRenderSystemManager : PGSingleton<PhotonRenderSystemManager>
    {
        [SerializeField] private PhotonRenderSystemConfigData currentConfiguration = PhotonRenderSystemConfigData.CreateDefault();
        [SerializeField] private bool capturedInitialValues;
        [SerializeField] private List<PhotonRenderSystemConfig> configurationPresets = new();
        [SerializeField] private int selectedPresetIndex = -1;
        [SerializeField] private string pendingSaveName = string.Empty;

        private bool _suppressApply;

        public IReadOnlyList<PhotonRenderSystemConfig> ConfigurationPresets => configurationPresets;
        public PhotonRenderSystemConfigData CurrentConfiguration => currentConfiguration;
        public int SelectedPresetIndex => selectedPresetIndex;
        public string PendingSaveName => pendingSaveName;

        protected override void OnAwake()
        {
            base.OnAwake();
            InitializeFromManagersIfNeeded();
            ApplyCurrentConfiguration(false);
        }

        private void OnValidate()
        {
            if (!enabled || _suppressApply)
                return;
            ApplyCurrentConfiguration(false);
        }

        private void InitializeFromManagersIfNeeded()
        {
            if (capturedInitialValues)
                return;

            SyncFromManagers(applyAfterSync: false);
            capturedInitialValues = true;
        }

        public void SyncFromManagers(bool applyAfterSync = true)
        {
            _suppressApply = true;

            PhotonRenderManager renderManager = PhotonRenderManager.Instance;
            if (renderManager != null)
                currentConfiguration.photonRender = renderManager.CaptureQualitySettings();

            RayTraceManager rayTraceManager = RayTraceManager.Instance;
            if (rayTraceManager != null)
                currentConfiguration.rayTrace = rayTraceManager.CaptureQualitySettings();

            SvgfDenoiserManager svgfManager = SvgfDenoiserManager.Instance;
            if (svgfManager != null)
                currentConfiguration.svgf = svgfManager.CaptureQualitySettings();

            DlssDenoiserManager dlssManager = DlssDenoiserManager.Instance;
            if (dlssManager != null)
                currentConfiguration.dlss = dlssManager.CaptureQualitySettings();

            _suppressApply = false;

            if (applyAfterSync)
                ApplyCurrentConfiguration(false);
        }

        public void ApplyConfigurationAsset(PhotonRenderSystemConfig asset, bool rebuild = true)
        {
            if (asset == null)
                return;

            ApplyConfigurationData(asset.Data, rebuild);
            pendingSaveName = asset.name;
        }

        public void ApplyConfigurationData(PhotonRenderSystemConfigData data, bool rebuildCriticalResources = true)
        {
            currentConfiguration = data;
            ApplyCurrentConfiguration(rebuildCriticalResources);
        }

        public void LoadPresetByIndex(int index, bool rebuild = true)
        {
            if (index < 0 || index >= configurationPresets.Count)
                return;

            PhotonRenderSystemConfig preset = configurationPresets[index];
            if (preset == null)
                return;

            selectedPresetIndex = index;
            pendingSaveName = preset.name;
            ApplyConfigurationAsset(preset, rebuild);
        }

        public void SetPendingSaveName(string name)
        {
            pendingSaveName = name;
        }

        public void NotifyConfigurationEditedFromInspector()
        {
            ApplyCurrentConfiguration(false);
        }

        private void ApplyCurrentConfiguration(bool rebuildCriticalResources)
        {
            if (_suppressApply)
                return;

            PhotonRenderManager renderManager = PhotonRenderManager.Instance;
            renderManager?.ApplyQualitySettings(currentConfiguration.photonRender);

            RayTraceManager rayTraceManager = RayTraceManager.Instance;
            rayTraceManager?.ApplyQualitySettings(currentConfiguration.rayTrace, rebuildCriticalResources);

            SvgfDenoiserManager svgfManager = SvgfDenoiserManager.Instance;
            svgfManager?.ApplyQualitySettings(currentConfiguration.svgf);

            DlssDenoiserManager dlssManager = DlssDenoiserManager.Instance;
            dlssManager?.ApplyQualitySettings(currentConfiguration.dlss);
        }
    }
}


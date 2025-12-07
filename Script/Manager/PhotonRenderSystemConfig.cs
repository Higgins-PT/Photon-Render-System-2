using System;
using UnityEngine;

namespace PhotonGISystem2
{
    [CreateAssetMenu(menuName = "PhotonGI/Render System Config", fileName = "PhotonRenderSystemConfig")]
    public sealed class PhotonRenderSystemConfig : ScriptableObject
    {
        [SerializeField]
        private PhotonRenderSystemConfigData data = PhotonRenderSystemConfigData.CreateDefault();

        public PhotonRenderSystemConfigData Data
        {
            get => data;
            set => data = value;
        }
    }

    [Serializable]
    public struct PhotonRenderSystemConfigData
    {
        public PhotonRenderManager.QualitySettings photonRender;
        public RayTraceManager.QualitySettings rayTrace;
        public SvgfDenoiserManager.QualitySettings svgf;
        public DlssDenoiserManager.DlssDenoiseParameters dlss;

        public static PhotonRenderSystemConfigData CreateDefault()
        {
            return new PhotonRenderSystemConfigData
            {
                photonRender = PhotonRenderManager.QualitySettings.Default,
                rayTrace = RayTraceManager.QualitySettings.Default,
                svgf = SvgfDenoiserManager.QualitySettings.Default,
                dlss = DlssDenoiserManager.DlssDenoiseParameters.Default
            };
        }
    }
}


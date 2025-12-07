#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
namespace PhotonGISystem2
{
    /// <summary>
    /// 单例模式
    /// 使用此单例模式时，请使用OnAwake方法而不是Awake方法
    /// </summary>
    /// <typeparam name="T">单例类型</typeparam>
    [ExecuteInEditMode]
    public abstract class PGSingleton<T> : MonoBehaviour where T : MonoBehaviour
    {   
        #region 示例说明
        /*
        public class SingletonTest : Singleton<SingletonTest>
        {
            protected override void OnAwake()
            {
                base.OnAwake();
            }
        }
        */
        #endregion
        #region 私有成员
        private static T _instance;
        private readonly static object _lock = new();
        private static bool _applicationQuitting = false;
        private static bool _destroyed = false;

        #endregion
        #region 公共成员
        [SerializeField]
        private bool _persistent = true;
        [SerializeField]
        private bool _executeInEditMode = true;
        #endregion

        /// <summary>
        /// 单例实例
        /// </summary>
        public static T Instance
        {
            get
            {


                lock (_lock)
                {
                    if (_instance != null)
                        return _instance;

                    var found = GameObject.FindFirstObjectByType<T>();
                    if (found != null)
                    {
                        _instance = found;
                        var all = GameObject.FindObjectsByType<T>(FindObjectsSortMode.None);
                        if (all.Length > 1)
                        {
                            Debug.LogWarning($"[Singleton<{typeof(T)}>] 多于一个实例，已清除额外实例。");
                            for (int i = 0; i < all.Length; i++)
                            {
                                if (all[i] != _instance)
                                    Destroy(all[i].gameObject);
                            }
                        }
                    }
                    else
                    {
                        GameObject singletonGO = new($"[Singleton] {typeof(T).Name}");
                        _instance = singletonGO.AddComponent<T>();
                    }

                    if (_instance != null && _instance is PGSingleton<T> single)
                    {
                        if (single._persistent)
                            SafeDontDestroyOnLoad(single.gameObject);
                    }
                    _instance.enabled = true;
                    return _instance;
                }
            }
        }

        protected virtual void Awake()
        {
            _applicationQuitting = false;
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this as T;

#if UNITY_EDITOR
        if (_executeInEditMode && !Application.isPlaying)
        {
            UnityEditor.EditorApplication.update -= EditModeUpdate;
            UnityEditor.EditorApplication.update += EditModeUpdate;
        }
#endif
            _applicationQuitting = false;
            _destroyed = false;
            SafeDontDestroyOnLoad(gameObject);
            OnAwake();
            
        }

        /// <summary>
        /// 安全Awake
        /// </summary>
        protected virtual void OnAwake()
        {

        }

        protected virtual void OnApplicationQuit()
        {
            DestroySystem();
            _applicationQuitting = true;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update -= EditModeUpdate;
#endif
        }

        protected virtual void OnDestroy()
        {
            
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditModeUpdate;
#endif
            _destroyed = true;
            if (_instance == this)
            {
                DestroySystem();
                _instance = null;
            }

        }

        /// <summary>
        /// Resets the system (override from PhotonSingleton).
        /// </summary>
        public virtual void ResetSystem()
        {

        }

        /// <summary>
        /// Destroys the system (override from PhotonSingleton).
        /// </summary>
        public virtual void DestroySystem()
        {

        }

        /// <summary>
        /// Safely calls DontDestroyOnLoad only when in play mode.
        /// In edit mode, this method does nothing to avoid issues.
        /// </summary>
        /// <param name="target">The GameObject to mark as DontDestroyOnLoad.</param>
        public static void SafeDontDestroyOnLoad(GameObject target)
        {
            if (target == null)
                return;

#if UNITY_EDITOR
            if (!Application.isPlaying)
                return;
#endif
            DontDestroyOnLoad(target);
        }

#if UNITY_EDITOR
    private void EditModeUpdate()
    {
        if (!Application.isPlaying && this != null)
        {
            OnEditModeUpdate();
        }
    }

    protected virtual void OnEditModeUpdate()
    {
    }
#endif
    }
}
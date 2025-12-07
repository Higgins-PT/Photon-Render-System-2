using PhotonGISystem2;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PhotonGISystem2
{
    /// <summary>
    /// RTManager is responsible for managing RenderTextures, Cubemaps, GraphicsBuffers, and ComputeBuffers.
    /// It caches created resources to avoid duplicate allocations and provides adjustable-size variants.
    /// </summary>
    [System.Serializable]
    public class RTManager : PGSingleton<RTManager>
    {
        #region Private Fields

        // Cache for RenderTextures
        private Dictionary<string, RenderTexture> _rtCache = new Dictionary<string, RenderTexture>();
        // Cache for Cubemaps
        private Dictionary<string, Cubemap> _cbCache = new Dictionary<string, Cubemap>();
        // Cache for GraphicsBuffers
        private Dictionary<string, GraphicsBuffer> _gbCache = new Dictionary<string, GraphicsBuffer>();
        // Cache for ComputeBuffers
        private readonly Dictionary<string, ComputeBuffer> _computeBufferCache =
            new Dictionary<string, ComputeBuffer>();

        #endregion

        #region RenderTexture Methods

        /// <summary>
        /// Gets or creates a RenderTexture that can be adjusted by width and height.
        /// The key does NOT include width/height. If the cached RT has different size,
        /// it will be released and replaced by a new one.
        /// </summary>
        /// <param name="name">The unique name (not including width/height) for the key.</param>
        /// <param name="width">Requested width.</param>
        /// <param name="height">Requested height.</param>
        /// <param name="format">RenderTexture format.</param>
        /// <param name="wrapMode">Texture wrap mode.</param>
        /// <param name="filterMode">Texture filter mode.</param>
        /// <param name="useMipMap">Whether to use mip maps.</param>
        /// <param name="autoGenerateMips">Whether to auto-generate mip maps (only if useMipMap is true).</param>
        /// <param name="enableRandomWrite">Whether random write is enabled.</param>
        /// <param name="mipCount">Mip map count (if > 0, use the appropriate constructor).</param>
        /// <param name="readWrite">Read/write mode for the RT.</param>
        /// <param name="depth">Depth buffer bits.</param>
        /// <param name="dimension">Texture dimension.</param>
        /// <returns>A RenderTexture that matches the latest requested size and other parameters.</returns>
        public RenderTexture GetAdjustableRT(
            string name,
            int width,
            int height,
            RenderTextureFormat format = RenderTextureFormat.Default,
            TextureWrapMode wrapMode = TextureWrapMode.Repeat,
            FilterMode filterMode = FilterMode.Trilinear,
            bool useMipMap = false,
            bool autoGenerateMips = false,
            bool enableRandomWrite = true,
            int mipCount = 0,
            RenderTextureReadWrite readWrite = RenderTextureReadWrite.Linear,
            int depth = 0,
            TextureDimension dimension = TextureDimension.Tex2D
        )
        {
            // Key does NOT include width/height
            string key = $"{name}_{format}_{wrapMode}_{filterMode}_{useMipMap}_{autoGenerateMips}_{enableRandomWrite}_{mipCount}_{readWrite}_{dimension}";

            RenderTexture rt;
            if (_rtCache.TryGetValue(key, out rt))
            {
                // If the cached RT exists but has different size, release it and create a new one
                if (rt.width != width || rt.height != height)
                {
                    rt.DiscardContents();
                    rt.Release();
                    DestroyImmediate(rt);
                    _rtCache.Remove(key); // remove old RT from cache
                    rt = CreateRenderTexture(key, width, height, format, wrapMode, filterMode,
                        useMipMap, autoGenerateMips, enableRandomWrite, mipCount, readWrite, depth, dimension);
                    _rtCache[key] = rt;
                }
            }
            else
            {
                // If no cached RT for this key, create and cache a new one
                rt = CreateRenderTexture(key, width, height, format, wrapMode, filterMode,
                    useMipMap, autoGenerateMips, enableRandomWrite, mipCount, readWrite, depth, dimension);
                _rtCache[key] = rt;
            }
            return rt;
        }

        /// <summary>
        /// Gets or creates a RenderTexture based on a RenderTextureDescriptor. Width/height changes will resize the cached RT.
        /// </summary>
        /// <param name="name">Cache key base.</param>
        /// <param name="descriptor">Descriptor describing the texture.</param>
        /// <returns>A RenderTexture matching the descriptor.</returns>
        public RenderTexture GetAdjustableRT(string name, RenderTextureDescriptor descriptor)
        {
            descriptor.msaaSamples = Mathf.Max(1, descriptor.msaaSamples);
            descriptor.depthBufferBits = Mathf.Clamp(descriptor.depthBufferBits, 0, 32);

            string key = $"{name}_{descriptor.graphicsFormat}_{descriptor.colorFormat}_{descriptor.depthBufferBits}_{descriptor.msaaSamples}_{descriptor.volumeDepth}_{descriptor.enableRandomWrite}_{descriptor.useMipMap}_{descriptor.dimension}_{descriptor.shadowSamplingMode}_{descriptor.vrUsage}";

            if (_rtCache.TryGetValue(key, out var rt))
            {
                if (!MatchesDescriptor(rt, descriptor))
                {
                    rt.DiscardContents();
                    rt.Release();
                    DestroyImmediate(rt);
                    rt = CreateRenderTexture(descriptor, key);
                    _rtCache[key] = rt;
                }
                else if (rt.width != descriptor.width || rt.height != descriptor.height)
                {
                    rt.DiscardContents();
                    rt.Release();
                    DestroyImmediate(rt);
                    rt = CreateRenderTexture(descriptor, key);
                    _rtCache[key] = rt;
                }
            }
            else
            {
                rt = CreateRenderTexture(descriptor, key);
                _rtCache[key] = rt;
            }

            return rt;
        }

        /// <summary>
        /// Helper function to create a RenderTexture based on the provided parameters.
        /// </summary>
        /// <param name="rtName">Name for the RenderTexture.</param>
        /// <param name="width">Width of the texture.</param>
        /// <param name="height">Height of the texture.</param>
        /// <param name="format">RenderTexture format.</param>
        /// <param name="wrapMode">Texture wrap mode.</param>
        /// <param name="filterMode">Texture filter mode.</param>
        /// <param name="useMipMap">Whether to use mip maps.</param>
        /// <param name="autoGenerateMips">Whether to auto-generate mip maps.</param>
        /// <param name="enableRandomWrite">Whether random write is enabled.</param>
        /// <param name="mipCount">Mip map count.</param>
        /// <param name="readWrite">Read/write mode.</param>
        /// <param name="depth">Depth buffer bits.</param>
        /// <param name="dimension">Texture dimension.</param>
        /// <returns>A newly created RenderTexture.</returns>
        private RenderTexture CreateRenderTexture(
            string rtName,
            int width,
            int height,
            RenderTextureFormat format,
            TextureWrapMode wrapMode,
            FilterMode filterMode,
            bool useMipMap,
            bool autoGenerateMips,
            bool enableRandomWrite,
            int mipCount,
            RenderTextureReadWrite readWrite,
            int depth = 0,
            TextureDimension dimension = TextureDimension.Tex2D
        )
        {
            RenderTexture newRT;
            if (mipCount > 0)
            {
                // Use the constructor that takes mipCount
                newRT = new RenderTexture(width, height, depth, format, mipCount)
                {
                    name = rtName,
                    wrapMode = wrapMode,
                    filterMode = filterMode,
                    useMipMap = useMipMap,
                    autoGenerateMips = autoGenerateMips,
                    enableRandomWrite = enableRandomWrite,
                    dimension = dimension
                };
            }
            else
            {
                // Use the constructor that takes readWrite
                newRT = new RenderTexture(width, height, depth, format, readWrite)
                {
                    name = rtName,
                    wrapMode = wrapMode,
                    filterMode = filterMode,
                    useMipMap = useMipMap,
                    autoGenerateMips = autoGenerateMips,
                    enableRandomWrite = enableRandomWrite,
                    dimension = dimension
                };
            }

            newRT.Create();
            return newRT;
        }

        private RenderTexture CreateRenderTexture(RenderTextureDescriptor descriptor, string rtName)
        {
            var desc = descriptor;
            desc.msaaSamples = Mathf.Max(1, desc.msaaSamples);
            var newRT = new RenderTexture(desc)
            {
                name = rtName
            };
            newRT.Create();
            return newRT;
        }

        private bool MatchesDescriptor(RenderTexture rt, RenderTextureDescriptor descriptor)
        {
            if (rt == null)
                return false;

            return rt.graphicsFormat == descriptor.graphicsFormat &&
                   rt.depth == descriptor.depthBufferBits &&
                   rt.dimension == descriptor.dimension &&
                   rt.enableRandomWrite == descriptor.enableRandomWrite &&
                   rt.useMipMap == descriptor.useMipMap &&
                   rt.volumeDepth == descriptor.volumeDepth &&
                   rt.antiAliasing == descriptor.msaaSamples;
        }

        /// <summary>
        /// Release the adjustable RT for a given key (since key doesn't contain width/height).
        /// </summary>
        /// <param name="name">The unique name for the key.</param>
        /// <param name="format">RenderTexture format.</param>
        /// <param name="wrapMode">Texture wrap mode.</param>
        /// <param name="filterMode">Texture filter mode.</param>
        /// <param name="useMipMap">Whether mip maps were used.</param>
        /// <param name="autoGenerateMips">Whether mip maps were auto-generated.</param>
        /// <param name="enableRandomWrite">Whether random write was enabled.</param>
        /// <param name="mipCount">Mip map count.</param>
        /// <param name="readWrite">Read/write mode.</param>
        public void ReleaseAdjustableRT(
            string name,
            RenderTextureFormat format = RenderTextureFormat.Default,
            TextureWrapMode wrapMode = TextureWrapMode.Repeat,
            FilterMode filterMode = FilterMode.Trilinear,
            bool useMipMap = false,
            bool autoGenerateMips = false,
            bool enableRandomWrite = true,
            int mipCount = 0,
            RenderTextureReadWrite readWrite = RenderTextureReadWrite.Linear
        )
        {
            // Key does NOT include width/height
            string key = $"{name}_{format}_{wrapMode}_{filterMode}_{useMipMap}_{autoGenerateMips}_{enableRandomWrite}_{mipCount}_{readWrite}";

            if (_rtCache.TryGetValue(key, out RenderTexture rt))
            {
                rt.DiscardContents();
                rt.Release();
                DestroyImmediate(rt);
                _rtCache.Remove(key);
            }
        }

        /// <summary>
        /// Retrieves or creates a RenderTexture with the specified parameters.
        /// <para>
        /// If <paramref name="height"/> is less than 1, the texture will be square,
        /// using <paramref name="widthHeight"/> for both width and height.
        /// </para>
        /// </summary>
        /// <param name="name">Name or base identifier for the RenderTexture.</param>
        /// <param name="widthHeight">Width of the texture; if height < 1, this value is used for both width and height.</param>
        /// <param name="height">Height of the texture; optional, defaults to -1 (meaning it uses <paramref name="widthHeight"/> for both dimensions).</param>
        /// <param name="format">RenderTexture format. Defaults to <see cref="RenderTextureFormat.Default"/>.</param>
        /// <param name="wrapMode">Texture wrap mode. Defaults to <see cref="TextureWrapMode.Repeat"/>.</param>
        /// <param name="filterMode">Texture filter mode. Defaults to <see cref="FilterMode.Trilinear"/>.</param>
        /// <param name="useMipMap">Whether MipMap is used. Defaults to false.</param>
        /// <param name="autoGenerateMips">Whether MipMaps are automatically generated (only valid if <paramref name="useMipMap"/> is true). Defaults to false.</param>
        /// <param name="enableRandomWrite">Whether random write is enabled. Defaults to true.</param>
        /// <param name="mipCount">MipMap count. If greater than 0, it uses the constructor that includes mipCount. Defaults to 0.</param>
        /// <param name="readWrite">Read/write mode. Defaults to <see cref="RenderTextureReadWrite.Linear"/>.</param>
        /// <returns>A RenderTexture that matches the specified parameters.</returns>
        /// <example>
        /// <code>
        /// // Example usage:
        /// var rt1 = GetRT("MyRT", 1024); 
        /// // Creates a 1024x1024 texture with default settings (Trilinear, Repeat, etc.)
        ///
        /// var rt2 = GetRT("MyRT", 512, 256, RenderTextureFormat.ARGBHalf); 
        /// // Creates a 512x256 texture with ARGBHalf format
        /// </code>
        /// </example>
        public RenderTexture GetRT(
            string name,
            int widthHeight,
            int height = -1,
            RenderTextureFormat format = RenderTextureFormat.Default,
            TextureWrapMode wrapMode = TextureWrapMode.Repeat,
            FilterMode filterMode = FilterMode.Trilinear,
            bool useMipMap = false,
            bool autoGenerateMips = false,
            bool enableRandomWrite = true,
            int mipCount = 0,
            RenderTextureReadWrite readWrite = RenderTextureReadWrite.Linear
        )
        {
            // If height is not specified or is less than 1, treat it as a square texture
            if (height < 1)
            {
                height = widthHeight;
            }

            // Generate a unique key to distinguish different RenderTextures
            string realName = $"{name}_{widthHeight}x{height}_{format}_{wrapMode}_{filterMode}_{useMipMap}_{autoGenerateMips}_{enableRandomWrite}_{mipCount}_{readWrite}";

            // Return the texture from cache if it already exists
            if (_rtCache.ContainsKey(realName))
            {
                return _rtCache[realName];
            }

            RenderTexture newRT;
            // Choose different constructors based on whether mipCount > 0
            if (mipCount > 0)
            {
                newRT = new RenderTexture(widthHeight, height, 0, format, mipCount)
                {
                    wrapMode = wrapMode,
                    filterMode = filterMode,
                    useMipMap = useMipMap,
                    autoGenerateMips = autoGenerateMips,
                    enableRandomWrite = enableRandomWrite,
                    name = realName
                };
            }
            else
            {
                newRT = new RenderTexture(widthHeight, height, 0, format, readWrite)
                {
                    wrapMode = wrapMode,
                    filterMode = filterMode,
                    useMipMap = useMipMap,
                    autoGenerateMips = autoGenerateMips,
                    enableRandomWrite = enableRandomWrite,
                    name = realName
                };
            }

            newRT.Create();
            _rtCache[realName] = newRT;

            return newRT;
        }

        /// <summary>
        /// Releases a RenderTexture based on the same parameters used to create it.
        /// </summary>
        /// <param name="name">Name or base identifier used when the RenderTexture was created.</param>
        /// <param name="widthHeight">Width used when the RenderTexture was created.</param>
        /// <param name="height">Height used when the RenderTexture was created. If less than 1, it implies a square texture.</param>
        /// <param name="format">RenderTexture format.</param>
        /// <param name="wrapMode">Texture wrap mode.</param>
        /// <param name="filterMode">Texture filter mode.</param>
        /// <param name="useMipMap">Indicates if MipMap was used.</param>
        /// <param name="autoGenerateMips">Indicates if MipMaps were automatically generated.</param>
        /// <param name="enableRandomWrite">Indicates if random write was enabled.</param>
        /// <param name="mipCount">MipMap count used during creation.</param>
        /// <param name="readWrite">Read/write mode used during creation.</param>
        public void ReleaseRT(
            string name,
            int widthHeight,
            int height,
            RenderTextureFormat format = RenderTextureFormat.Default,
            TextureWrapMode wrapMode = TextureWrapMode.Repeat,
            FilterMode filterMode = FilterMode.Trilinear,
            bool useMipMap = false,
            bool autoGenerateMips = false,
            bool enableRandomWrite = true,
            int mipCount = 0,
            RenderTextureReadWrite readWrite = RenderTextureReadWrite.Linear
        )
        {
            // If height is not specified or is less than 1, treat it as a square texture
            if (height < 1)
            {
                height = widthHeight;
            }

            string realName = $"{name}_{widthHeight}x{height}_{format}_{wrapMode}_{filterMode}_{useMipMap}_{autoGenerateMips}_{enableRandomWrite}_{mipCount}_{readWrite}";

            if (_rtCache.ContainsKey(realName))
            { 
                _rtCache[realName].DiscardContents();
                _rtCache[realName].Release();
                DestroyImmediate(_rtCache[realName]);
                _rtCache.Remove(realName);
            }
        }

        /// <summary>
        /// Releases a specific RenderTexture instance directly.
        /// </summary>
        /// <param name="renderTexture">RenderTexture instance to be released.</param>
        public void ReleaseRT(RenderTexture renderTexture)
        {
            string keyToRemove = null;
            foreach (var kv in _rtCache)
            {
                if (kv.Value == renderTexture)
                {
                    kv.Value.DiscardContents();
                    kv.Value.Release();
                    DestroyImmediate(kv.Value);
                    keyToRemove = kv.Key;
                    break;
                }
            }

            if (!string.IsNullOrEmpty(keyToRemove))
            {
                _rtCache.Remove(keyToRemove);
            }
        }

        #endregion

        #region Cubemap Methods

        /// <summary>
        /// Retrieves or creates a Cubemap with the specified parameters.
        /// </summary>
        /// <param name="name">Name or base identifier for the Cubemap.</param>
        /// <param name="size">Size of the Cubemap (width/height of each face).</param>
        /// <param name="format">TextureFormat for the Cubemap. Defaults to <see cref="TextureFormat.RGBA32"/>.</param>
        /// <param name="wrapMode">Texture wrap mode. Defaults to <see cref="TextureWrapMode.Repeat"/>.</param>
        /// <param name="filterMode">Texture filter mode. Defaults to <see cref="FilterMode.Trilinear"/>.</param>
        /// <returns>A Cubemap that matches the specified parameters.</returns>
        public Cubemap GetCubemap(
            string name,
            int size,
            TextureFormat format = TextureFormat.RGBA32,
            TextureWrapMode wrapMode = TextureWrapMode.Repeat,
            FilterMode filterMode = FilterMode.Trilinear
        )
        {
            string realName = $"{name}_{size}_{format}_{wrapMode}_{filterMode}";
            if (_cbCache.ContainsKey(realName))
            {
                return _cbCache[realName];
            }

            Cubemap newCB = new Cubemap(size, format, false)
            {
                wrapMode = wrapMode,
                filterMode = filterMode,
                name = realName
            };

            _cbCache[realName] = newCB;
            return newCB;
        }

        /// <summary>
        /// Releases a Cubemap identified by the same parameters used when it was created.
        /// </summary>
        /// <param name="name">Name or base identifier for the Cubemap.</param>
        /// <param name="size">Size of the Cubemap.</param>
        /// <param name="format">TextureFormat used during creation.</param>
        /// <param name="wrapMode">Texture wrap mode used during creation.</param>
        /// <param name="filterMode">Texture filter mode used during creation.</param>
        public void ReleaseCubemap(
            string name,
            int size,
            TextureFormat format,
            TextureWrapMode wrapMode = TextureWrapMode.Repeat,
            FilterMode filterMode = FilterMode.Trilinear
        )
        {
            string realName = $"{name}_{size}_{format}_{wrapMode}_{filterMode}";
            if (_cbCache.ContainsKey(realName))
            {
                var cubemap = _cbCache[realName];
                _cbCache.Remove(realName);
                if (cubemap != null)
                {
                    Object.DestroyImmediate(cubemap);
                }
            }
        }

        /// <summary>
        /// Releases a specific Cubemap instance directly.
        /// </summary>
        /// <param name="cubemap">Cubemap instance to be released.</param>
        public void ReleaseCubemap(Cubemap cubemap)
        {
            string keyToRemove = null;
            foreach (var kv in _cbCache)
            {
                if (kv.Value == cubemap)
                {
                    keyToRemove = kv.Key;
                    break;
                }
            }

            if (!string.IsNullOrEmpty(keyToRemove))
            {
                _cbCache.Remove(keyToRemove);
                Object.DestroyImmediate(cubemap);
            }
        }

        #endregion

        #region GraphicsBuffer Methods

        /// <summary>
        /// Gets or creates a GraphicsBuffer with adjustable size.
        /// The key does NOT include count. If the cached buffer has different count,
        /// it will be released and replaced by a new one.
        /// </summary>
        /// <param name="name">The unique name (not including count) for the key.</param>
        /// <param name="count">Requested element count.</param>
        /// <param name="stride">Stride in bytes per element.</param>
        /// <param name="target">GraphicsBuffer target flags.</param>
        /// <returns>A GraphicsBuffer that matches the latest requested count and other parameters.</returns>
        public GraphicsBuffer GetAdjustableGB(
            string name,
            int count,
            int stride,
            GraphicsBuffer.Target target =
                GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.Raw |
                GraphicsBuffer.Target.Append)
        {
            string key = $"{name}_{target}_{stride}"; 

            if (_gbCache.TryGetValue(key, out var gb))
            {
                if (gb.count != count)
                {
                    gb.Release();
                    gb.Dispose();
                    _gbCache.Remove(key);
                    gb = CreateGraphicsBuffer(key, count, stride, target);
                    _gbCache[key] = gb;
                }
            }
            else
            {
                gb = CreateGraphicsBuffer(key, count, stride, target);
                _gbCache[key] = gb;
            }

            return gb;
        }

        /// <summary>
        /// Creates a new GraphicsBuffer with the specified parameters.
        /// </summary>
        /// <param name="gbName">Name for the GraphicsBuffer.</param>
        /// <param name="count">Element count.</param>
        /// <param name="stride">Stride in bytes per element.</param>
        /// <param name="target">GraphicsBuffer target flags.</param>
        /// <returns>A newly created GraphicsBuffer.</returns>
        private static GraphicsBuffer CreateGraphicsBuffer(
            string gbName,
            int count,
            int stride,
            GraphicsBuffer.Target target)
        {
            var gb = new GraphicsBuffer(target, count, stride)
            {
#if UNITY_EDITOR
                name = gbName
#endif
            };
            return gb;
        }

        /// <summary>
        /// Gets or creates a GraphicsBuffer with fixed size.
        /// The key includes count, so different sizes are cached separately.
        /// </summary>
        /// <param name="name">Name or base identifier for the GraphicsBuffer.</param>
        /// <param name="count">Element count.</param>
        /// <param name="stride">Stride in bytes per element.</param>
        /// <param name="target">GraphicsBuffer target flags.</param>
        /// <returns>A GraphicsBuffer that matches the specified parameters.</returns>
        public GraphicsBuffer GetGB(
            string name,
            int count,
            int stride,
            GraphicsBuffer.Target target =
                GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.Raw)
        {
            string realName = $"{name}_{count}_{stride}_{target}";

            if (_gbCache.TryGetValue(realName, out var gb))
                return gb;

            gb = CreateGraphicsBuffer(realName, count, stride, target);
            _gbCache[realName] = gb;
            return gb;
        }

        /// <summary>
        /// Releases a GraphicsBuffer identified by the same parameters used when it was created.
        /// </summary>
        /// <param name="name">Name or base identifier for the GraphicsBuffer.</param>
        /// <param name="count">Element count used during creation.</param>
        /// <param name="stride">Stride in bytes used during creation.</param>
        /// <param name="target">GraphicsBuffer target flags used during creation.</param>
        public void ReleaseGB(
            string name,
            int count,
            int stride,
            GraphicsBuffer.Target target =
                GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.Raw)
        {
            string realName = $"{name}_{count}_{stride}_{target}";

            if (_gbCache.TryGetValue(realName, out var gb))
            {
                gb.Dispose();
                _gbCache.Remove(realName);
            }
        }

        /// <summary>
        /// Releases a specific GraphicsBuffer instance directly.
        /// </summary>
        /// <param name="buffer">GraphicsBuffer instance to be released.</param>
        public void ReleaseGB(GraphicsBuffer buffer)
        {
            string keyToRemove = null;
            foreach (var kv in _gbCache)
            {
                if (kv.Value == buffer)
                {
                    keyToRemove = kv.Key;
                    break;
                }
            }

            if (keyToRemove != null)
            {
                _gbCache[keyToRemove].Release();
                _gbCache[keyToRemove].Dispose();
                _gbCache.Remove(keyToRemove);
            }
        }

        #endregion

        #region ComputeBuffer Methods

        /// <summary>
        /// Gets or creates a ComputeBuffer with adjustable size.
        /// Uses <paramref name="name"/>+<paramref name="type"/>+<paramref name="stride"/> as key.
        /// If the cached buffer has different count, it will be released and replaced by a new one.
        /// </summary>
        /// <param name="name">The unique name for the key.</param>
        /// <param name="count">Requested element count.</param>
        /// <param name="stride">Stride in bytes per element.</param>
        /// <param name="type">ComputeBuffer type.</param>
        /// <returns>A ComputeBuffer that matches the latest requested count and other parameters.</returns>
        public ComputeBuffer GetAdjustableCB(
            string name,
            int count,
            int stride,
            ComputeBufferType type = ComputeBufferType.Structured)
        {
            // key does not include count, so it can be replaced when count changes
            string key = $"{name}_{type}_{stride}";

            if (_computeBufferCache.TryGetValue(key, out var cb))
            {
                if (cb.count != count)
                {
                    cb.Release();
                    _computeBufferCache.Remove(key);

                    cb = CreateComputeBuffer(count, stride, type);
                    _computeBufferCache[key] = cb;
                }
            }
            else
            {
                cb = CreateComputeBuffer(count, stride, type);
                _computeBufferCache[key] = cb;
            }

            return cb;
        }

        /// <summary>
        /// Gets or creates a ComputeBuffer with fixed size.
        /// Uses a fully qualified key including count, so different sizes are cached separately.
        /// Use this when you are certain the size will not change, such as for static LUTs.
        /// </summary>
        /// <param name="name">Name or base identifier for the ComputeBuffer.</param>
        /// <param name="count">Element count.</param>
        /// <param name="stride">Stride in bytes per element.</param>
        /// <param name="type">ComputeBuffer type.</param>
        /// <returns>A ComputeBuffer that matches the specified parameters.</returns>
        public ComputeBuffer GetCB(
            string name,
            int count,
            int stride,
            ComputeBufferType type = ComputeBufferType.Structured)
        {
            string realKey = $"{name}_{count}_{stride}_{type}";

            if (_computeBufferCache.TryGetValue(realKey, out var cb))
                return cb;

            cb = CreateComputeBuffer(count, stride, type);
            _computeBufferCache[realKey] = cb;
            return cb;
        }

        /// <summary>
        /// Releases an adjustable-size ComputeBuffer identified by name, type, and stride.
        /// </summary>
        /// <param name="name">The unique name for the key.</param>
        /// <param name="type">ComputeBuffer type.</param>
        /// <param name="stride">Stride in bytes (needed to distinguish buffers with same name but different stride).</param>
        public void ReleaseAdjustableCB(
            string name,
            ComputeBufferType type = ComputeBufferType.Structured,
            int stride = 0)
        {
            string key = $"{name}_{type}_{stride}";
            if (_computeBufferCache.TryGetValue(key, out var cb))
            {
                cb.Release();
                _computeBufferCache.Remove(key);
            }
        }

        /// <summary>
        /// Releases a fixed-size ComputeBuffer identified by the same parameters used when it was created.
        /// </summary>
        /// <param name="name">Name or base identifier for the ComputeBuffer.</param>
        /// <param name="count">Element count used during creation.</param>
        /// <param name="stride">Stride in bytes used during creation.</param>
        /// <param name="type">ComputeBuffer type used during creation.</param>
        public void ReleaseCB(
            string name,
            int count,
            int stride,
            ComputeBufferType type = ComputeBufferType.Structured)
        {
            string realKey = $"{name}_{count}_{stride}_{type}";
            if (_computeBufferCache.TryGetValue(realKey, out var cb))
            {
                cb.Release();
                _computeBufferCache.Remove(realKey);
            }
        }

        /// <summary>
        /// Releases a specific ComputeBuffer instance directly.
        /// Automatically finds and removes it from the cache.
        /// </summary>
        /// <param name="buffer">ComputeBuffer instance to be released.</param>
        public void ReleaseCB(ComputeBuffer buffer)
        {
            string keyToRemove = null;
            foreach (var kv in _computeBufferCache)
            {
                if (kv.Value == buffer)
                {
                    keyToRemove = kv.Key;
                    break;
                }
            }

            if (keyToRemove != null)
            {
                _computeBufferCache[keyToRemove].Release();
                _computeBufferCache.Remove(keyToRemove);
            }
        }

        /// <summary>
        /// Internal helper to create a ComputeBuffer.
        /// </summary>
        /// <param name="count">Element count.</param>
        /// <param name="stride">Stride in bytes per element.</param>
        /// <param name="type">ComputeBuffer type.</param>
        /// <returns>A newly created ComputeBuffer.</returns>
        private static ComputeBuffer CreateComputeBuffer(
            int count,
            int stride,
            ComputeBufferType type)
        {
            var cb = new ComputeBuffer(count, stride, type);
            return cb;
        }

        #endregion

        #region System Lifecycle

        /// <summary>
        /// Resets the manager by releasing all cached resources.
        /// </summary>
        public void ResetDic()
        {
            // Release RenderTextures
            foreach (var kv in _rtCache)
            {
                kv.Value.DiscardContents();
                kv.Value.Release();
                DestroyImmediate(kv.Value);
            }
            _rtCache.Clear();

            // Release Cubemaps
            foreach (var kv in _cbCache)
                DestroyImmediate(kv.Value);
            _cbCache.Clear();

            // Release GraphicsBuffers
            foreach (var kv in _gbCache)
            {
                kv.Value.Release();
                kv.Value.Dispose();
            }

            _gbCache.Clear();

            // Release ComputeBuffers
            foreach (var kv in _computeBufferCache)
            {
                kv.Value.Release();
            }
            _computeBufferCache.Clear();
        }

        /// <summary>
        /// Resets the system (override from PGSingleton).
        /// </summary>
        public override void ResetSystem()
        {
            ResetDic();
        }

        /// <summary>
        /// Destroys the system (override from PGSingleton).
        /// </summary>
        public override void DestroySystem()
        {
            ResetDic();
        }

        #endregion
    }
}

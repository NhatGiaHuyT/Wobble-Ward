// Assets/Scripts/Systems/Pooling/PoolingSystem.cs
using System;
using System.Collections.Generic;
using UnityEngine;
using Core;

namespace Systems.Pooling
{
    /// <summary>
    /// IPoolingSystem - runtime pool manager contract.
    /// Provides convenience methods to get/return pooled instances without needing to hold Pool<T> references.
    /// </summary>
    public interface IPoolingSystem
    {
        /// <summary>
        /// Get a pooled instance of type T for the provided prefab. If a pool does not exist, one will be created.
        /// </summary>
        T Get<T>(GameObject prefab, int initialSize = 0, bool autoExpand = true, int maxSize = 0) where T : Component;

        /// <summary>
        /// Return an instance previously obtained via Get(...) back to its pool.
        /// </summary>
        void Return<T>(T instance) where T : Component;

        /// <summary>
        /// Ensure a pool exists for the given prefab and prewarm it with 'count' inactive instances.
        /// </summary>
        void Prewarm<T>(GameObject prefab, int count, int maxSize = 0) where T : Component;

        /// <summary>
        /// Get the underlying IPool<T> if one exists (or create it with provided parameters).
        /// </summary>
        IPool<T> GetPool<T>(GameObject prefab, int initialSize = 0, bool autoExpand = true, int maxSize = 0) where T : Component;

        /// <summary>
        /// Clears and destroys all pools managed by this system.
        /// </summary>
        void ClearAll();
    }

    /// <summary>
    /// PoolingSystem - MonoBehaviour that manages per-prefab pools (Pool<T>).
    /// - Register this component in the scene (Bootstrapper will create/Register it if present).
    /// - Services.Register<IPoolingSystem>(this) is called on Enable to make it discoverable.
    ///
    /// Implementation notes:
    /// - Internally keeps a dictionary from prefab GameObject -> object (boxed IPool<T>).
    /// - Also tracks a mapping from active instance -> the pool object so Return(instance) works conveniently.
    /// - All operations are executed on the main thread in typical Unity usage; a simple lock is used for defensive safety.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    public class PoolingSystem : MonoBehaviour, IPoolingSystem
    {
        // map prefab -> boxed pool (actual type is Pool<T>)
        private readonly Dictionary<GameObject, object> _pools = new Dictionary<GameObject, object>(new GameObjectReferenceEqualityComparer());

        // map active instance -> prefab key so we can find its pool on return
        private readonly Dictionary<Component, GameObject> _instanceToPrefab = new Dictionary<Component, GameObject>();

        private readonly object _lock = new object();

        private void OnEnable()
        {
            // Register ourselves in the Services locator for other systems to find.
            try
            {
                Services.Register<IPoolingSystem>(this, replaceExisting: true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PoolingSystem] Services registration failed: {e.Message}");
            }
        }

        private void OnDisable()
        {
            // Optional: unregister to allow editor hot-reload
            try
            {
                Services.Unregister<IPoolingSystem>();
            }
            catch { /* ignore */ }
        }

        private void OnDestroy()
        {
            ClearAll();
        }

        /// <summary>
        /// Get or create pool and return an instance of type T.
        /// This will also register the instance->prefab mapping so Return(instance) works.
        /// </summary>
        public T Get<T>(GameObject prefab, int initialSize = 0, bool autoExpand = true, int maxSize = 0) where T : Component
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));

            var pool = GetPool<T>(prefab, initialSize, autoExpand, maxSize);
            T inst = pool.Get();

            lock (_lock)
            {
                // Track the mapping for convenient returns.
                if (!_instanceToPrefab.ContainsKey(inst))
                    _instanceToPrefab[inst] = prefab;
                else
                    _instanceToPrefab[inst] = prefab; // overwrite just in case
            }

            return inst;
        }

        /// <summary>
        /// Return an instance previously obtained via this PoolingSystem.Get call.
        /// If this instance was not tracked, attempt best-effort to find its pool by matching prefab type.
        /// </summary>
        public void Return<T>(T instance) where T : Component
        {
            if (instance == null) return;

            GameObject prefabKey = null;
            lock (_lock)
            {
                if (_instanceToPrefab.TryGetValue(instance, out var p))
                {
                    prefabKey = p;
                    _instanceToPrefab.Remove(instance);
                }
            }

            if (prefabKey != null)
            {
                // try to find pool and return
                if (TryGetPoolForPrefab(prefabKey, out var boxedPool))
                {
                    // boxedPool is Pool<T> of matching generic type. Cast via IPool<T>.
                    var pool = boxedPool as IPool<T>;
                    if (pool != null)
                    {
                        pool.Return(instance);
                        return;
                    }
                    else
                    {
                        // If generic mismatch (shouldn't happen), fall through to destroy fallback.
                        Debug.LogWarning($"[PoolingSystem] Pool type mismatch when returning instance of {typeof(T).FullName}.");
                    }
                }
            }

            // Fallback: If no mapping found, try to find a pool by searching pools that contain the same component type.
            lock (_lock)
            {
                foreach (var kv in _pools)
                {
                    var boxed = kv.Value;
                    if (boxed is IPool<T> typedPool)
                    {
                        // Heuristic: return to first pool that accepts this component type.
                        try
                        {
                            typedPool.Return(instance);
                            return;
                        }
                        catch
                        {
                            // ignore and continue
                        }
                    }
                }
            }

            // Last resort: destroy instance to avoid leaking orphan instances.
            try
            {
                Debug.LogWarning($"[PoolingSystem] No pool found for instance {instance.name}; destroying it.");
                UnityEngine.Object.Destroy(instance.gameObject);
            }
            catch { /* swallow errors */ }
        }

        /// <summary>
        /// Ensure a pool exists for prefab (and return the IPool<T>).
        /// </summary>
        public IPool<T> GetPool<T>(GameObject prefab, int initialSize = 0, bool autoExpand = true, int maxSize = 0) where T : Component
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));

            lock (_lock)
            {
                if (_pools.TryGetValue(prefab, out var boxed))
                {
                    if (boxed is IPool<T> existingPool)
                    {
                        // Optionally prewarm if requested
                        if (initialSize > existingPool.InactiveCount)
                            existingPool.Prewarm(initialSize);

                        return existingPool;
                    }
                    else
                    {
                        // There's already a pool for this prefab but for a different component type.
                        // This typically indicates a misuse (same prefab used for different component types).
                        throw new InvalidOperationException($"A pool for prefab '{prefab.name}' exists but with a different component generic type.");
                    }
                }

                // Create a new Pool<T> instance
                var newPool = new Pool<T>(prefab, parent: null, initialSize: initialSize, autoExpand: autoExpand, maxSize: maxSize);
                _pools[prefab] = newPool;
                return newPool;
            }
        }

        /// <summary>
        /// Prewarm pool for a prefab: create at least 'count' inactive instances.
        /// </summary>
        public void Prewarm<T>(GameObject prefab, int count, int maxSize = 0) where T : Component
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            var pool = GetPool<T>(prefab, initialSize: count, autoExpand: true, maxSize: maxSize);
            pool.Prewarm(count);
        }

        /// <summary>
        /// Clear and destroy all pools and tracked mappings.
        /// </summary>
        public void ClearAll()
        {
            lock (_lock)
            {
                foreach (var kv in _pools)
                {
                    if (kv.Value == null) continue;
                    try
                    {
                        // Call Clear via dynamic dispatch on IPool<T>
                        var boxed = kv.Value;
                        var clearMethod = boxed.GetType().GetMethod("Clear");
                        clearMethod?.Invoke(boxed, null);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[PoolingSystem] Failed to clear pool for prefab {kv.Key?.name}: {e.Message}");
                    }
                }
                _pools.Clear();
                _instanceToPrefab.Clear();

                // Optionally destroy parent "Pools" GameObject (clean scene)
                var root = GameObject.Find("Pools");
                if (root != null)
                {
#if UNITY_EDITOR
                    UnityEngine.Object.DestroyImmediate(root);
#else
                    UnityEngine.Object.Destroy(root);
#endif
                }
            }
        }

        /// <summary>
        /// Helper: attempt to find boxed pool for a prefab.
        /// </summary>
        private bool TryGetPoolForPrefab(GameObject prefab, out object boxedPool)
        {
            lock (_lock)
            {
                return _pools.TryGetValue(prefab, out boxedPool);
            }
        }

        /// <summary>
        /// Custom equality comparer that compares GameObject instances by reference/instance id.
        /// This prevents accidental mismatches due to overwritten GetHashCode implementations.
        /// </summary>
        private class GameObjectReferenceEqualityComparer : IEqualityComparer<GameObject>
        {
            public bool Equals(GameObject x, GameObject y)
            {
                return x == y;
            }

            public int GetHashCode(GameObject obj)
            {
                return obj == null ? 0 : obj.GetInstanceID();
            }
        }
    }
}
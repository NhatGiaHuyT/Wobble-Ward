// Assets/Scripts/Systems/Pooling/Pool.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Systems.Pooling
{
    /// <summary>
    /// Generic pool implementation for Unity Components.
    /// - T must be a Component present on the prefab GameObject.
    /// - Avoids allocations in Get/Return hot paths (uses Stack).
    /// - Supports prewarm, optional auto expansion and an optional maximum capacity.
    ///
    /// Usage:
    ///   var pool = new Pool<MyEnemy>(enemyPrefab, parentTransform, initialSize: 20, autoExpand: true);
    ///   var e = pool.Get();
    ///   pool.Return(e);
    ///
    /// Notes:
    /// - If the pooled component implements IPoolable (defined below), ResetForReuse and OnReturnedToPool
    ///   will be called during lifecycle transitions.
    /// - The pool creates a named GameObject under root "Pools" to parent inactive instances for clarity.
    /// - All Create/Destroy calls use UnityEngine.Object methods.
    /// </summary>
    /// <typeparam name="T">Component type attached to the pooled prefab.</typeparam>
    public class Pool<T> : IPool<T> where T : Component
    {
        public GameObject Prefab { get; private set; }

        private readonly Stack<T> _inactive;
        private readonly HashSet<T> _active;
        private readonly Transform _poolParent;
        private readonly bool _autoExpand;
        private readonly int _maxSize; // 0 = unlimited
        private int _totalCreated; // active + inactive

        /// <summary>
        /// Optional hook to call when creating a new instance (for extra initialization).
        /// </summary>
        public Action<T> OnCreateInstance;

        /// <summary>
        /// Optional hook to call when an instance is fetched from the pool (before returned to caller).
        /// </summary>
        public Action<T> OnGetInstance;

        /// <summary>
        /// Optional hook to call when an instance is returned (after reset).
        /// </summary>
        public Action<T> OnReturnInstance;

        /// <summary>
        /// Create a pool for given prefab.
        /// </summary>
        /// <param name="prefab">Prefab GameObject that must contain component T.</param>
        /// <param name="parent">Optional parent transform for pooled inactive instances. If null, a dedicated pool root is created.</param>
        /// <param name="initialSize">Prewarm count (number of inactive instances to create immediately).</param>
        /// <param name="autoExpand">If true, pool will instantiate more when empty. Otherwise Get() will throw if empty.</param>
        /// <param name="maxSize">Maximum total instances allowed (0 = unlimited).</param>
        public Pool(GameObject prefab, Transform parent = null, int initialSize = 0, bool autoExpand = true, int maxSize = 0)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            Prefab = prefab;
            _inactive = new Stack<T>(Mathf.Max(16, initialSize));
            _active = new HashSet<T>();
            _autoExpand = autoExpand;
            _maxSize = Mathf.Max(0, maxSize);

            // ensure prefab contains T
            var comp = prefab.GetComponent<T>();
            if (comp == null)
                throw new ArgumentException($"Prefab '{prefab.name}' does not contain component of type {typeof(T).FullName}");

            // find/create parent for this pool
            _poolParent = parent ?? CreatePoolParent(prefab.name);

            // prewarm
            if (initialSize > 0)
                Prewarm(initialSize);
        }

        /// <summary>
        /// Create/Get the pool root in scene and a child to hold instances for this prefab.
        /// </summary>
        private Transform CreatePoolParent(string prefabName)
        {
            GameObject root = GameObject.Find("Pools");
            if (root == null)
            {
                root = new GameObject("Pools");
#if UNITY_EDITOR
                // In editor, avoid marking DontDestroyOnLoad to make cleanup easier during iteration.
#else
                UnityEngine.Object.DontDestroyOnLoad(root);
#endif
            }

            var poolGO = new GameObject($"Pool_{prefabName}");
            poolGO.transform.SetParent(root.transform, worldPositionStays: false);
            return poolGO.transform;
        }

        /// <summary>
        /// Get an instance from the pool. If none available and expansion allowed, a new instance is created.
        /// </summary>
        public T Get()
        {
            T instance;
            if (_inactive.Count > 0)
            {
                instance = _inactive.Pop();
                _active.Add(instance);
                // Reactivate & prepare
                PrepareInstanceForGet(instance);
                OnGetInstance?.Invoke(instance);
                return instance;
            }

            // No inactive instance available
            if (_maxSize > 0 && _totalCreated >= _maxSize)
            {
                throw new InvalidOperationException($"Pool for '{Prefab.name}' exhausted (maxSize={_maxSize}).");
            }

            if (!_autoExpand && _totalCreated > 0)
            {
                // If not allowed to auto expand and we already created some, we prefer to throw than to allocate.
                throw new InvalidOperationException($"Pool for '{Prefab.name}' is empty and autoExpand is false.");
            }

            // Create new
            instance = CreateNewInstance();
            _active.Add(instance);
            OnGetInstance?.Invoke(instance);
            return instance;
        }

        /// <summary>
        /// Return an instance back to the pool. If the instance wasn't created by this pool, it will be destroyed.
        /// </summary>
        public void Return(T instance)
        {
            if (instance == null) return;

            // If instance isn't part of our active set, it might be from a different pool or not managed.
            if (!_active.Remove(instance))
            {
                // For safety: destroy instance if it isn't ours to manage.
                try
                {
                    UnityEngine.Object.Destroy(instance.gameObject);
                }
                catch { /* swallow */ }
                return;
            }

            // Reset instance state
            ResetInstanceForReturn(instance);

            // Deactivate & parent to pool
            instance.gameObject.SetActive(false);
            try
            {
                instance.transform.SetParent(_poolParent, worldPositionStays: false);
            }
            catch { /* ignore transform errors in domain reloads */ }

            _inactive.Push(instance);
            OnReturnInstance?.Invoke(instance);
        }

        /// <summary>
        /// Number of instances currently checked out.
        /// </summary>
        public int ActiveCount => _active.Count;

        /// <summary>
        /// Number of instances available in the pool.
        /// </summary>
        public int InactiveCount => _inactive.Count;

        /// <summary>
        /// Ensure at least `count` inactive instances exist. Will create up to (count - InactiveCount) new instances.
        /// </summary>
        public void Prewarm(int count)
        {
            if (count <= 0) return;

            int toCreate = count - _inactive.Count;
            for (int i = 0; i < toCreate; i++)
            {
                if (_maxSize > 0 && _totalCreated >= _maxSize) break;
                var inst = CreateNewInstance();
                // Immediately return to inactive list
                inst.gameObject.SetActive(false);
                inst.transform.SetParent(_poolParent, worldPositionStays: false);
                _inactive.Push(inst);
            }
        }

        /// <summary>
        /// Destroy all pooled instances and reset the pool.
        /// </summary>
        public void Clear()
        {
            // Destroy inactive
            while (_inactive.Count > 0)
            {
                var inst = _inactive.Pop();
                if (inst != null)
                {
#if UNITY_EDITOR
                    UnityEngine.Object.DestroyImmediate(inst.gameObject);
#else
                    UnityEngine.Object.Destroy(inst.gameObject);
#endif
                }
            }

            // Destroy active (shouldn't typically happen but handle defensively)
            foreach (var inst in _active)
            {
                if (inst != null)
                {
#if UNITY_EDITOR
                    UnityEngine.Object.DestroyImmediate(inst.gameObject);
#else
                    UnityEngine.Object.Destroy(inst.gameObject);
#endif
                }
            }
            _active.Clear();

            // Optionally destroy parent container
            if (_poolParent != null)
            {
#if UNITY_EDITOR
                UnityEngine.Object.DestroyImmediate(_poolParent.gameObject);
#else
                UnityEngine.Object.Destroy(_poolParent.gameObject);
#endif
            }

            _totalCreated = 0;
        }

        /// <summary>
        /// Create a new instance from the prefab and initialize bookkeeping.
        /// </summary>
        private T CreateNewInstance()
        {
            var go = UnityEngine.Object.Instantiate(Prefab, _poolParent, worldPositionStays: false);
            go.name = Prefab.name; // strip "(Clone)" for clarity
            var comp = go.GetComponent<T>();
            if (comp == null)
            {
                // If the prefab unexpectedly lacks the component (e.g., designer changed prefab), destroy and throw.
                UnityEngine.Object.Destroy(go);
                throw new InvalidOperationException($"Created instance from prefab '{Prefab.name}' but it does not contain component {typeof(T).FullName}.");
            }

            comp.gameObject.SetActive(false);
            _totalCreated++;

            // If component supports IPoolable, call initialization hook
            if (comp is IPoolable poolable)
            {
                poolable.ResetForPoolCreation();
            }

            return comp;
        }

        /// <summary>
        /// Prepare an instance when it is checked out (activate + call IPoolable.PrepareForGet).
        /// </summary>
        private void PrepareInstanceForGet(T instance)
        {
            // Reset transform to default local state (optional - caller may override)
            instance.transform.SetParent(null, worldPositionStays: false);

            // Activate
            instance.gameObject.SetActive(true);

            // Call IPoolable hook if present
            if (instance is IPoolable poolable)
            {
                poolable.PrepareForGet();
            }
        }

        /// <summary>
        /// Reset instance state before returning to pool; call IPoolable.OnReturnedToPool if implemented.
        /// </summary>
        private void ResetInstanceForReturn(T instance)
        {
            // Reset transform local values to defaults to avoid stray transforms.
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            // Call optional IPoolable hook to clear component-specific state
            if (instance is IPoolable poolable)
            {
                poolable.OnReturnedToPool();
            }
        }
    }

    /// <summary>
    /// Optional interface that pooled components can implement to receive lifecycle callbacks.
    /// Implementing this is not required but recommended for reusable pooled components to
    /// reset their runtime state without the pool needing domain-specific knowledge.
    /// </summary>
    public interface IPoolable
    {
        /// <summary>
        /// Called by the pool once when the instance is first created.
        /// Use this to cache components, allocate internal buffers, etc.
        /// </summary>
        void ResetForPoolCreation();

        /// <summary>
        /// Called by the pool when the instance is handed out via Get().
        /// Use to initialize runtime state, enable particle emitters, etc.
        /// </summary>
        void PrepareForGet();

        /// <summary>
        /// Called by the pool when the instance is returned. Use this to stop coroutines,
        /// disable particle systems, clear state, etc.
        /// </summary>
        void OnReturnedToPool();
    }
}
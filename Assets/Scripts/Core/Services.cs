// Assets/Scripts/Core/Services.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Core
{
    /// <summary>
    /// Simple Service Locator / Registry for runtime singletons and test doubles.
    /// - Register instances with Register{T}(instance).
    /// - Resolve with Get{T}() or TryGet{T}(out T).
    /// - Use Clear() to remove everything (useful for tests / scene teardown).
    ///
    /// Notes:
    /// - Keeps a Dictionary<Type, object> internally.
    /// - Thread-safe for Register/Get operations via a private lock.
    /// - Designed to be minimal and explicit. Prefer constructor-injection in complex systems;
    ///   use this when convenient for Unity-style global services or for bootstrapping.
    /// </summary>
    public static class Services
    {
        // Internal storage of services by their concrete type key
        private static readonly Dictionary<Type, object> _registry = new Dictionary<Type, object>();

        // Lock used to ensure thread-safety if services are registered from background threads (rare).
        private static readonly object _lock = new object();

        /// <summary>
        /// Registers a service instance for type T. If a service for T already exists, this will throw
        /// unless replaceExisting==true.
        /// </summary>
        /// <typeparam name="T">Service interface/type.</typeparam>
        /// <param name="instance">Instance to register (must be non-null).</param>
        /// <param name="replaceExisting">If true, replace any existing registration for T.</param>
        public static void Register<T>(T instance, bool replaceExisting = false) where T : class
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance), "Cannot register null service instance.");

            var type = typeof(T);
            lock (_lock)
            {
                if (_registry.TryGetValue(type, out var existing))
                {
                    if (!replaceExisting)
                    {
                        throw new InvalidOperationException($"Service of type {type.FullName} is already registered. Use replaceExisting=true to overwrite.");
                    }

                    _registry[type] = instance;
                }
                else
                {
                    _registry.Add(type, instance);
                }
            }
        }

        /// <summary>
        /// Registers a service instance for a given runtime type key. This is useful when you want to
        /// register with a base type or non-generic code path.
        /// </summary>
        /// <param name="serviceType">The type key to register under (must not be null).</param>
        /// <param name="instance">Instance to register (must not be null).</param>
        /// <param name="replaceExisting">If true, replace existing entry.</param>
        public static void Register(Type serviceType, object instance, bool replaceExisting = false)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (!serviceType.IsInstanceOfType(instance))
                throw new ArgumentException($"Instance is not assignable to {serviceType.FullName}", nameof(instance));

            lock (_lock)
            {
                if (_registry.TryGetValue(serviceType, out var existing))
                {
                    if (!replaceExisting)
                    {
                        throw new InvalidOperationException($"Service of type {serviceType.FullName} is already registered. Use replaceExisting=true to overwrite.");
                    }

                    _registry[serviceType] = instance;
                }
                else
                {
                    _registry.Add(serviceType, instance);
                }
            }
        }

        /// <summary>
        /// Resolve a service of type T. Throws an exception if not registered.
        /// </summary>
        /// <typeparam name="T">Requested service type</typeparam>
        /// <returns>Registered instance cast to T</returns>
        public static T Get<T>() where T : class
        {
            var type = typeof(T);
            lock (_lock)
            {
                if (_registry.TryGetValue(type, out var instance))
                {
                    return instance as T;
                }
            }

            throw new KeyNotFoundException($"Service of type {type.FullName} is not registered.");
        }

        /// <summary>
        /// Try to resolve a service of type T. Returns true if present.
        /// </summary>
        public static bool TryGet<T>(out T instance) where T : class
        {
            var type = typeof(T);
            lock (_lock)
            {
                if (_registry.TryGetValue(type, out var obj))
                {
                    instance = obj as T;
                    return true;
                }
            }

            instance = null;
            return false;
        }

        /// <summary>
        /// Unregister a service of type T if it exists. Returns true if an entry was removed.
        /// </summary>
        public static bool Unregister<T>() where T : class
        {
            var type = typeof(T);
            lock (_lock)
            {
                return _registry.Remove(type);
            }
        }

        /// <summary>
        /// Clear all registered services. Intended for test teardown or a full reset.
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _registry.Clear();
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only helper: list service keys for debugging.
        /// </summary>
        [UnityEditor.MenuItem("Services/Print Registered Services")]
        private static void PrintRegisteredServices()
        {
            lock (_lock)
            {
                Debug.Log($"Services: {_registry.Count} registered.");
                foreach (var kv in _registry)
                {
                    Debug.Log($" - {kv.Key.FullName} => {kv.Value?.GetType().FullName}");
                }
            }
        }
#endif
    }
}
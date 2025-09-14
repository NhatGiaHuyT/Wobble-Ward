// Assets/Scripts/Systems/Pooling/IPool.cs
using UnityEngine;

namespace Systems.Pooling
{
    /// <summary>
    /// Generic pool contract for pooled Unity components.
    /// - T is expected to be a Component attached to a pooled prefab instance.
    /// - Implementations should avoid allocations in Get/Return hot paths and should
    ///   support prewarming to avoid runtime spikes.
    /// </summary>
    /// <typeparam name="T">Component type pooled (e.g., Enemy, Spell, Particle)</typeparam>
    public interface IPool<T> where T : Component
    {
        /// <summary>
        /// The prefab used as a template for instances in this pool.
        /// </summary>
        GameObject Prefab { get; }

        /// <summary>
        /// Get an instance from the pool. If the pool is empty it may instantiate a new one
        /// depending on its expansion policy.
        /// Returned instance must be considered "owned" by the caller until Return() is called.
        /// </summary>
        /// <returns>Pooled instance of T (active and ready-to-use).</returns>
        T Get();

        /// <summary>
        /// Return an instance back to the pool. Implementations should reset instance state,
        /// disable it, and make it available for reuse.
        /// </summary>
        /// <param name="instance">Instance previously obtained from Get()</param>
        void Return(T instance);

        /// <summary>
        /// Current count of active (checked-out) instances.
        /// </summary>
        int ActiveCount { get; }

        /// <summary>
        /// Current count of inactive (available) instances in the pool.
        /// </summary>
        int InactiveCount { get; }

        /// <summary>
        /// Pre-warm the pool to ensure at least <paramref name="count"/> instances are available.
        /// Implementations should create instances now to avoid runtime allocations later.
        /// </summary>
        /// <param name="count">Number of inactive instances to ensure</param>
        void Prewarm(int count);

        /// <summary>
        /// Clear the pool: destroy all pooled instances and reset internal state.
        /// Useful for scene teardown or switching contexts.
        /// </summary>
        void Clear();
    }
}
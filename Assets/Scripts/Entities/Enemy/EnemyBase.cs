// Assets/Scripts/Entities/Enemy/EnemyBase.cs
using System;
using Core;
using UnityEngine;
using Systems.Pooling;
using Entities.Diamond;

namespace Entities.Enemy
{
    /// <summary>
    /// EnemyBase - generic enemy behaviour used by enemy prefabs.
    /// Responsibilities:
    /// - Move toward the diamond core each frame (kinematic MoveTowards).
    /// - Expose public Initialize(EnemyData) to set stats when spawned.
    /// - Expose TakeDamage(int) for spells to call. On death, fire events and return to pool.
    /// - When close enough to the diamond (collisionRadius + diamond radius), fire OnEnemyReachedDiamond and return to pool.
    ///
    /// Notes:
    /// - This class deliberately does NOT depend on spell implementations; spells should call TakeDamage()
    ///   on the EnemyBase they collide with.
    /// - On death or reach-diamond this enemy will attempt to return to the PoolingSystem. If no pooling system
    ///   is registered, the instance will be destroyed.
    /// - Other systems can subscribe to the static events OnAnyEnemyDied and OnAnyEnemyReachedDiamond to react
    ///   (award XP, push diamond, etc).
    /// </summary>
    [DisallowMultipleComponent]
    public class EnemyBase : MonoBehaviour, IPoolable
    {
        // Public data assigned at spawn time (via EnemySpawner/PoolingSystem)
        [Tooltip("Optional fallback EnemyData; spawner should call Initialize with the proper data.")]
        public EnemyData defaultEnemyData;

        // Runtime state
        private EnemyData _data;
        private int _currentHealth;
        private Transform _diamondTransform;
        private IDiamondSystem _diamondSystem;
        private ITimeService _timeService;
        private IPoolingSystem _poolingSystem;

        // Movement helper
        private Vector3 _cachedTargetPos;

        // Events for other systems to subscribe to
        public static event Action<EnemyBase, EnemyData> OnAnyEnemyDied;
        public static event Action<EnemyBase> OnAnyEnemyReachedDiamond;

        #region Public API

        /// <summary>
        /// Initialize enemy with EnemyData. Called by spawner when checking out from the pool.
        /// </summary>
        public void Initialize(EnemyData data)
        {
            _data = data ?? defaultEnemyData;
            if (_data == null)
            {
                Debug.LogError("[EnemyBase] Initialize called with null EnemyData and no default set.");
                _data = ScriptableObject.CreateInstance<EnemyData>(); // minimal fallback to avoid nulls
                _data.maxHealth = 1;
                _data.moveSpeed = 1f;
                _data.collisionRadius = 0.25f;
                _data.xpValue = 0;
                _data.scoreValue = 0;
            }

            _currentHealth = Mathf.Max(1, _data.maxHealth);

            // Apply visual scale if provided
            try
            {
                transform.localScale = _data.spawnScale;
            }
            catch { /* ignore editor-time issues */ }
        }

        /// <summary>
        /// Apply damage to this enemy. Spells should call this; collisions are handled in Spell scripts.
        /// </summary>
        /// <param name="damage">Damage amount (positive).</param>
        public void TakeDamage(int damage)
        {
            if (damage <= 0) return;
            _currentHealth -= damage;

            // Optional: small hit feedback (particles / sound) can be triggered here.

            if (_currentHealth <= 0)
            {
                Die();
            }
        }

        /// <summary>
        /// Get current health for UI/debug purposes.
        /// </summary>
        public int GetCurrentHealth() => _currentHealth;

        /// <summary>
        /// Get reference to underlying EnemyData for other systems.
        /// </summary>
        public EnemyData GetData() => _data;

        #endregion

        #region Unity lifecycle

        private void Awake()
        {
            // Cache services if available
            Services.TryGet<ITimeService>(out _timeService);
            Services.TryGet<IDiamondSystem>(out _diamondSystem);
            Services.TryGet<IPoolingSystem>(out _poolingSystem);

            if (_diamondSystem != null)
                _diamondTransform = _diamondSystem.DiamondTransform;
        }

        private void OnEnable()
        {
            // If Initialize wasn't called by spawner, use default data
            if (_data == null && defaultEnemyData != null)
            {
                Initialize(defaultEnemyData);
            }

            // Refresh cached services in case bootstrapper ran later
            Services.TryGet<IDiamondSystem>(out _diamondSystem);
            Services.TryGet<IPoolingSystem>(out _poolingSystem);
            Services.TryGet<ITimeService>(out _timeService);

            if (_diamondSystem != null)
                _diamondTransform = _diamondSystem.DiamondTransform;
            else
                _diamondTransform = null;
        }

        private void Update()
        {
            // Movement: kinematic movement toward diamond; skip if no diamond present
            if (_diamondTransform == null)
            {
                // Try to re-acquire diamond service if it becomes available
                Services.TryGet<IDiamondSystem>(out _diamondSystem);
                if (_diamondSystem != null)
                {
                    _diamondTransform = _diamondSystem.DiamondTransform;
                }
                else
                {
                    return;
                }
            }

            float dt = (_timeService != null) ? _timeService.DeltaTime : Time.deltaTime;
            if (dt <= 0f) return;

            _cachedTargetPos = _diamondTransform.position;

            // Move towards target using MoveTowards for stable kinematic movement
            float speed = (_data != null) ? _data.moveSpeed : 1f;
            transform.position = Vector3.MoveTowards(transform.position, _cachedTargetPos, speed * dt);

            // If close enough to the diamond, trigger reach event and return to pool
            float dist = Vector3.Distance(transform.position, _cachedTargetPos);
            float threshold = (_data != null ? _data.collisionRadius : 0.25f) + (_diamondSystem != null ? _diamondSystem.Radius : 0.5f);
            if (dist <= threshold)
            {
                HandleReachedDiamond();
            }
        }

        private void OnDisable()
        {
            // Nothing special here; pool return handles cleanup
        }

        private void OnDestroy()
        {
            // Clean up static event subscriptions only (none here).
        }

        #endregion

        #region Death / pooling

        /// <summary>
        /// Handle death logic: fire events, award score (via GameManager), and return to pool.
        /// </summary>
        private void Die()
        {
            // Fire global died event for XP/analytics listeners
            try
            {
                OnAnyEnemyDied?.Invoke(this, _data);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[EnemyBase] Exception in OnAnyEnemyDied subscribers: {e.Message}");
            }

            // Award score via GameManager if available
            if (Services.TryGet<GameManager>(out var gm))
            {
                try
                {
                    gm.AddScore(_data != null ? _data.scoreValue : 0);
                }
                catch { /* ignore */ }
            }

            // Attempt to return to pool
            ReturnToPool();
        }

        /// <summary>
        /// Handle behavior when enemy reaches the diamond (touch).
        /// Fire event and return to pool. Other systems (e.g., game manager or diamond system)
        /// can subscribe to react (push diamond, reduce health, etc).
        /// </summary>
        private void HandleReachedDiamond()
        {
            try
            {
                OnAnyEnemyReachedDiamond?.Invoke(this);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[EnemyBase] Exception in OnAnyEnemyReachedDiamond subscribers: {e.Message}");
            }

            // Return to pool (enemy consumed on contact). Some games might instead attach or persist; change as needed.
            ReturnToPool();
        }

        /// <summary>
        /// Return this instance to the pooling system if present; otherwise destroy GameObject.
        /// </summary>
        private void ReturnToPool()
        {
            if (_poolingSystem == null)
            {
                Services.TryGet<IPoolingSystem>(out _poolingSystem);
            }

            if (_poolingSystem != null)
            {
                try
                {
                    _poolingSystem.Return<EnemyBase>(this);
                    return;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[EnemyBase] PoolingSystem.Return failed: {e.Message}");
                    // fallback to destroy
                }
            }

            // If no pooling, destroy the GameObject to avoid leaks
            try
            {
#if UNITY_EDITOR
                DestroyImmediate(this.gameObject);
#else
                Destroy(this.gameObject);
#endif
            }
            catch { /* ignore */ }
        }

        #endregion

        #region IPoolable implementation

        public void ResetForPoolCreation()
        {
            // Called once when the pool creates the instance. Cache components here if needed.
            // Example: cache references to common components to avoid GetComponent in hot paths.
        }

        public void PrepareForGet()
        {
            // Called when the instance is checked out from the pool.
            // Reset health and enable object for use.
            if (_data != null)
            {
                _currentHealth = Mathf.Max(1, _data.maxHealth);
            }
            else if (defaultEnemyData != null)
            {
                _data = defaultEnemyData;
                _currentHealth = Mathf.Max(1, _data.maxHealth);
            }
            gameObject.SetActive(true);
        }

        public void OnReturnedToPool()
        {
            // Called when the instance is returned to the pool.
            // Stop any running coroutines, reset state and disable the GameObject.
            try
            {
                StopAllCoroutines();
            }
            catch { /* ignore */ }

            // Defensive: reset transform and disable
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = _data != null ? _data.spawnScale : Vector3.one;

            // Let Pool implementation handle setting active = false; we ensure here that object is ready.
        }

        #endregion
    }
}
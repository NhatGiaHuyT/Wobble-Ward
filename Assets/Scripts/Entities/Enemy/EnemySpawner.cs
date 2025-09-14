// Assets/Scripts/Entities/Enemy/EnemySpawner.cs
using System;
using System.Collections.Generic;
using Core;
using UnityEngine;
using Systems.Pooling;

namespace Entities.Enemy
{
    /// <summary>
    /// EnemySpawner
    /// - Responsible for spawning enemy prefabs (using PoolingSystem) at screen edges and optionally in waves.
    /// - Supports deterministic seeding via ResetAndStart(seed).
    /// - Uses ITimeService when available for deterministic timing; falls back to MonoBehaviour.Update otherwise.
    ///
    /// Public usage:
    ///  - ResetAndStart(seed) to (re)initialize and begin spawning.
    ///  - Stop() to stop spawning.
    ///  - SpawnNow(enemyData, optionalWorldPos) to immediately spawn one.
    ///
    /// Events:
    ///  - OnEnemySpawned(EnemyBase, EnemyData) fired after the spawned enemy has been initialized and activated.
    /// </summary>
    [DefaultExecutionOrder(-150)]
    public class EnemySpawner : MonoBehaviour
    {
        [Header("Spawner Settings")]
        [Tooltip("Base time between spawns (s).")]
        [SerializeField] private float spawnInterval = 1.5f;

        [Tooltip("Minimum allowed spawn interval (s) when ramping difficulty).")]
        [SerializeField] private float minSpawnInterval = 0.35f;

        [Tooltip("Multiplier applied to spawnInterval over time to increase difficulty (e.g., 0.99 -> slightly faster).")]
        [SerializeField] private float spawnIntervalDecay = 0.9995f;

        [Tooltip("Initial pool prewarm per enemy type when ResetAndStart is called (if pooling system available).")]
        [SerializeField] private int prewarmPerType = 6;

        [Tooltip("Distance outside viewport (world units) to place spawned enemies.")]
        [SerializeField] private float spawnOutsidePadding = 0.5f;

        [Tooltip("Maximum attempts to find a valid spawn position on edge.")]
        [SerializeField] private int maxSpawnPositionAttempts = 8;

        [Header("Debug")]
        [SerializeField] private bool drawDebugGizmos = false;

        // Public events
        public event Action<EnemyBase, EnemyData> OnEnemySpawned;
        public event Action OnSpawnerStarted;
        public event Action OnSpawnerStopped;

        // Internal
        private ITimeService _timeService;
        private IPoolingSystem _poolingSystem;
        private Camera _cam;
        private System.Random _rnd;
        private float _spawnTimer = 0f;
        private bool _running = false;
        private float _currentSpawnInterval;
        private readonly List<EnemyData> _registeredEnemyTypes = new List<EnemyData>();

        // Track active spawn instances (optional diagnostic)
        private readonly HashSet<EnemyBase> _activeEnemies = new HashSet<EnemyBase>();

        private void Awake()
        {
            Services.TryGet<ITimeService>(out _timeService);
            Services.TryGet<IPoolingSystem>(out _poolingSystem);

            _cam = Camera.main ?? Camera.current ?? FindObjectOfType<Camera>();
            _currentSpawnInterval = spawnInterval;
            Services.Register<EnemySpawner>(this, replaceExisting: true);
        }

        private void OnEnable()
        {
            // subscribe to TimeService ticks if available
            if (_timeService != null)
            {
                _timeService.OnTick += HandleTick;
            }
        }

        private void OnDisable()
        {
            if (_timeService != null)
            {
                _timeService.OnTick -= HandleTick;
            }
        }

        private void Update()
        {
            // Only apply the non-TimeService update path if no time service is present.
            if (_timeService == null && _running)
            {
                HandleTick(Time.deltaTime);
            }
        }

        /// <summary>
        /// Reset the spawner and start spawning. If seed is non-zero, a deterministic RNG will be used.
        /// </summary>
        /// <param name="seed">Optional seed for deterministic spawn sequence</param>
        public void ResetAndStart(int seed = 0)
        {
            Stop(); // ensure stopped first
            _rnd = (seed == 0) ? new System.Random() : new System.Random(seed);
            _spawnTimer = 0f;
            _running = true;
            _currentSpawnInterval = spawnInterval;
            _activeEnemies.Clear();

            // Refresh services
            Services.TryGet<IPoolingSystem>(out _poolingSystem);
            Services.TryGet<ITimeService>(out _timeService);
            _cam = Camera.main ?? Camera.current ?? FindObjectOfType<Camera>();

            // Prewarm pooling for registered types (best effort)
            if (_poolingSystem != null && prewarmPerType > 0)
            {
                foreach (var et in _registeredEnemyTypes)
                {
                    if (et != null && et.prefab != null)
                    {
                        // We don't know the exact component generic type here; leave prewarm to callers or specific code.
                        // But we can attempt to create a pool assuming EnemyBase exists on prefab.
                        try
                        {
                            _poolingSystem.Prewarm<EnemyBase>(et.prefab, prewarmPerType);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"[EnemySpawner] Prewarm failed for {et.name}: {e.Message}");
                        }
                    }
                }
            }

            OnSpawnerStarted?.Invoke();
        }

        /// <summary>
        /// Stop spawning; running enemies continue their lifecycle.
        /// </summary>
        public void Stop()
        {
            _running = false;
            _spawnTimer = 0f;
            OnSpawnerStopped?.Invoke();
        }

        /// <summary>
        /// Register an enemy type so prewarm and designer flows can use it.
        /// </summary>
        public void RegisterEnemyType(EnemyData data)
        {
            if (data == null) return;
            if (!_registeredEnemyTypes.Contains(data))
                _registeredEnemyTypes.Add(data);
        }

        /// <summary>
        /// Immediately spawn one enemy of given type. If worldPos is null, a random edge position will be chosen.
        /// </summary>
        public EnemyBase SpawnNow(EnemyData data, Vector3? worldPos = null)
        {
            if (data == null)
            {
                Debug.LogWarning("[EnemySpawner] SpawnNow called with null EnemyData.");
                return null;
            }

            if (data.prefab == null)
            {
                Debug.LogWarning($"[EnemySpawner] EnemyData '{data.name}' has no prefab assigned.");
                return null;
            }

            // Determine spawn position
            Vector3 pos;
            if (worldPos.HasValue)
            {
                pos = worldPos.Value;
            }
            else
            {
                pos = GetRandomEdgeWorldPosition();
            }

            // Acquire pooling instance
            try
            {
                // Request an EnemyBase from pooling system for this prefab
            var enemyInstance = (_poolingSystem != null)
                ? _poolingSystem.Get<EnemyBase>(data.prefab)
                : InstantiateFallbackEnemy(data.prefab);

                if (enemyInstance == null)
                {
                    Debug.LogWarning($"[EnemySpawner] Failed to obtain enemy instance for {data.name}");
                    return null;
                }

                // Position and initialize
                enemyInstance.transform.position = pos;
                enemyInstance.transform.SetParent(null, worldPositionStays: true);

                // Ensure the component's Initialize is called with the data
                enemyInstance.Initialize(data);

                // Track active for diagnostics
                _activeEnemies.Add(enemyInstance);

                // Optionally subscribe to the enemy's death/reach events to remove from active set
                // We'll use the static events on EnemyBase for simplicity
                EnemyBase.OnAnyEnemyDied += OnEnemyDiedInternal;
                EnemyBase.OnAnyEnemyReachedDiamond += OnEnemyReachedInternal;

                // Fire spawned event
                try
                {
                    OnEnemySpawned?.Invoke(enemyInstance, data);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[EnemySpawner] Exception in OnEnemySpawned listeners: {e.Message}");
                }

                return enemyInstance;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[EnemySpawner] Exception while spawning enemy '{data.name}': {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Internal hook invoked when an enemy dies. Used to maintain active set.
        /// </summary>
        private void OnEnemyDiedInternal(EnemyBase enemy, EnemyData data)
        {
            if (enemy == null) return;
            _activeEnemies.Remove(enemy);
            // No unsubscribe from static event here to avoid losing other listeners. Static events should be managed elsewhere.
        }

        /// <summary>
        /// Internal hook invoked when enemy reaches diamond.
        /// </summary>
        private void OnEnemyReachedInternal(EnemyBase enemy)
        {
            if (enemy == null) return;
            _activeEnemies.Remove(enemy);
        }

        /// <summary>
        /// Handle ticking from either TimeService.OnTick or Update() fallback.
        /// Accumulates spawn timer and spawns when interval reached.
        /// </summary>
        private void HandleTick(float delta)
        {
            if (!_running) return;
            if (delta <= 0f) return;

            _spawnTimer += delta;
            if (_spawnTimer >= _currentSpawnInterval)
            {
                _spawnTimer = 0f;

                // Choose an enemy type to spawn randomly among registered types (fallback to default types if empty)
                var data = ChooseEnemyTypeToSpawn();
                if (data != null)
                {
                    SpawnNow(data, null);
                }

                // Decay spawn interval toward minimum to ramp difficulty over time
                _currentSpawnInterval = Mathf.Max(minSpawnInterval, _currentSpawnInterval * spawnIntervalDecay);
            }
        }

        /// <summary>
        /// Choose an enemy type to spawn. Basic strategy: choose uniformly from registered types.
        /// If none registered, return null.
        /// </summary>
        private EnemyData ChooseEnemyTypeToSpawn()
        {
            if (_registeredEnemyTypes.Count == 0) return null;
            if (_registeredEnemyTypes.Count == 1) return _registeredEnemyTypes[0];

            int idx;
            if (_rnd != null)
                idx = _rnd.Next(0, _registeredEnemyTypes.Count);
            else
                idx = UnityEngine.Random.Range(0, _registeredEnemyTypes.Count);

            return _registeredEnemyTypes[idx];
        }

        /// <summary>
        /// Compute a random world position just outside the camera viewport, along one of the four edges.
        /// </summary>
        private Vector3 GetRandomEdgeWorldPosition()
        {
            if (_cam == null)
            {
                _cam = Camera.main ?? Camera.current ?? FindObjectOfType<Camera>();
                if (_cam == null)
                {
                    // fallback to (0,0,0) to avoid crash
                    return Vector3.zero;
                }
            }

            // Decide edge: 0=left,1=right,2=bottom,3=top
            int edge;
            if (_rnd != null)
                edge = _rnd.Next(0, 4);
            else
                edge = UnityEngine.Random.Range(0, 4);

            // Choose a random position along that edge in viewport coordinates, then convert to world point
            float v;
            if (_rnd != null)
                v = (float)_rnd.NextDouble();
            else
                v = UnityEngine.Random.value;

            Vector3 viewportPos = Vector3.zero;
            switch (edge)
            {
                case 0: // left
                    viewportPos = new Vector3(0f, v, _cam.nearClipPlane + 1f);
                    break;
                case 1: // right
                    viewportPos = new Vector3(1f, v, _cam.nearClipPlane + 1f);
                    break;
                case 2: // bottom
                    viewportPos = new Vector3(v, 0f, _cam.nearClipPlane + 1f);
                    break;
                case 3: // top
                default:
                    viewportPos = new Vector3(v, 1f, _cam.nearClipPlane + 1f);
                    break;
            }

            // Convert viewport to world point and push outward by spawnOutsidePadding along screen-space normal
            Vector3 world = _cam.ViewportToWorldPoint(viewportPos);
            // Nudging outward a bit along the direction from camera to world point
            Vector3 dirFromCam = (world - _cam.transform.position).normalized;
            world += dirFromCam * spawnOutsidePadding;

            // A quick validity loop to try slightly different positions if something odd occurs (e.g., behind camera)
            int attempts = 0;
            while (attempts < maxSpawnPositionAttempts && float.IsNaN(world.x))
            {
                attempts++;
                if (_rnd != null)
                    viewportPos.x = (float)_rnd.NextDouble();
                else
                    viewportPos.x = UnityEngine.Random.value;
                world = _cam.ViewportToWorldPoint(viewportPos) + (world - _cam.transform.position).normalized * spawnOutsidePadding;
            }

            return world;
        }

        /// <summary>
        /// Fallback instantiation if pooling system isn't available. Returns an active EnemyBase instance.
        /// </summary>
        private EnemyBase InstantiateFallbackEnemy(GameObject prefab)
        {
            var go = Instantiate(prefab);
            var eb = go.GetComponent<EnemyBase>();
            if (eb == null)
            {
                Debug.LogWarning($"[EnemySpawner] Instantiated prefab '{prefab.name}' but no EnemyBase component found.");
            }
            return eb;
        }

        private void OnDrawGizmosSelected()
        {
            if (!drawDebugGizmos || _cam == null) return;
            Gizmos.color = Color.magenta;
            // Draw a simple rectangle showing viewport in world space at near clip plane
            var bl = _cam.ViewportToWorldPoint(new Vector3(0, 0, _cam.nearClipPlane + 1f));
            var br = _cam.ViewportToWorldPoint(new Vector3(1, 0, _cam.nearClipPlane + 1f));
            var tl = _cam.ViewportToWorldPoint(new Vector3(0, 1, _cam.nearClipPlane + 1f));
            var tr = _cam.ViewportToWorldPoint(new Vector3(1, 1, _cam.nearClipPlane + 1f));
            Gizmos.DrawLine(bl, br);
            Gizmos.DrawLine(br, tr);
            Gizmos.DrawLine(tr, tl);
            Gizmos.DrawLine(tl, bl);
        }
    }
}
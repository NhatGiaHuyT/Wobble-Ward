// Assets/Scripts/Core/GameManager.cs
using System;
using UnityEngine;
using Core;
using Entities.Diamond;
using Entities.Hero;
using Entities.Enemy;
using Systems.Pooling;

namespace Core
{
    /// <summary>
    /// IGameManager - simple runtime contract for the global game lifecycle.
    /// Implemented by GameManager MonoBehaviour below.
    /// </summary>
    public interface IGameManager
    {
        GameState CurrentState { get; }
        bool IsRunning { get; }
        int CurrentScore { get; }

        /// <summary>
        /// Start a new run. Optional deterministic seed can be provided for systems that support seeded initialization.
        /// </summary>
        void StartRun(int seed = 0);

        /// <summary>
        /// End the current run (GameOver flow).
        /// </summary>
        void EndRun();

        /// <summary>
        /// Pause the game (time / gameplay updates).
        /// </summary>
        void Pause();

        /// <summary>
        /// Resume the game.
        /// </summary>
        void Resume();

        event Action<GameState> OnStateChanged;
        event Action OnRunStarted;
        event Action<int /*finalScore*/> OnRunEnded;
        event Action<int /*newScore*/> OnScoreChanged;
    }

    /// <summary>
    /// GameState enum - describes top-level game lifecycle.
    /// </summary>
    public enum GameState
    {
        Boot,
        Menu,
        Running,
        Paused,
        GameOver
    }

    /// <summary>
    /// GameManager - central coordinator of game lifecycle.
    /// Responsibilities:
    ///  - Manage run lifecycle (start / end / pause / resume)
    ///  - Track runtime score and run time
    ///  - Wire diamond edge -> EndRun
    ///  - Reset basic systems when starting a new run (diamond reset, optional prewarm hooks)
    ///  - Provide deterministic StartRun(seed) where possible
    ///
    /// Design notes:
    ///  - Lightweight, uses Services locator to find other systems (TimeService, PoolingSystem, IDiamondSystem, IHeroSystem).
    ///  - Safe when systems are not present yet (defensive TryGet).
    ///  - Uses ITimeService.OnTick to update run timer for deterministic stepping in tests/manual mode.
    /// </summary>
    [DefaultExecutionOrder(-250)]
    public class GameManager : MonoBehaviour, IGameManager
    {
        [Header("Game Manager Settings")]
        [Tooltip("If true, the GameManager will auto-start a run on Awake (useful for quick play/test).")]
        [SerializeField] private bool autoStartRun = false;

        [Tooltip("Optional prewarm count applied to pools on StartRun (if pooling system is available).")]
        [SerializeField] private int poolPrewarmCount = 8;

        // State
        public GameState CurrentState { get; private set; } = GameState.Boot;
        public bool IsRunning => CurrentState == GameState.Running;
        public int CurrentScore { get; private set; } = 0;

        // Run timing
        private float _runElapsed = 0f;
        private int _runSeed = 0;
        private ITimeService _timeService;
        private IDiamondSystem _diamondSystem;
        private IHeroSystem _heroSystem;
        private IPoolingSystem _poolingSystem;

        // Events
        public event Action<GameState> OnStateChanged;
        public event Action OnRunStarted;
        public event Action<int> OnRunEnded;
        public event Action<int> OnScoreChanged;

        private void Awake()
        {
            // Register this GameManager in Services so other systems can find it.
            try
            {
                Services.Register<IGameManager>(this, replaceExisting: true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GameManager] Services registration failed: {e.Message}");
            }
        }

        private void OnEnable()
        {
            // Try to fetch known services (may be registered by Bootstrapper)
            Services.TryGet<ITimeService>(out _timeService);
            Services.TryGet<IDiamondSystem>(out _diamondSystem);
            Services.TryGet<IHeroSystem>(out _heroSystem);
            Services.TryGet<IPoolingSystem>(out _poolingSystem);

            // Subscribe to diamond edge event if diamond present
            if (_diamondSystem != null)
            {
                _diamondSystem.OnDiamondEdge += HandleDiamondEdge;
            }

            // Use TimeService.OnTick if available for deterministic update; otherwise fallback to Update loop.
            if (_timeService != null)
            {
                _timeService.OnTick += OnTick;
            }

            // Auto start if requested
            if (autoStartRun && CurrentState != GameState.Running)
            {
                StartRun(seed: Environment.TickCount & 0x7FFFFFFF);
            }
            else
            {
                SetState(GameState.Menu);
            }
        }

        private void OnDisable()
        {
            // Unsubscribe
            if (_diamondSystem != null)
                _diamondSystem.OnDiamondEdge -= HandleDiamondEdge;

            if (_timeService != null)
                _timeService.OnTick -= OnTick;

            try
            {
                Services.Unregister<IGameManager>();
            }
            catch { /* ignore */ }
        }

        private void Update()
        {
            // If no TimeService available, update run time here for normal runtime.
            if (_timeService == null && IsRunning)
            {
                _runElapsed += Time.deltaTime;
            }
        }

        /// <summary>
        /// Called by TimeService every frame with the scaled delta applied (or by Advance during tests).
        /// </summary>
        private void OnTick(float delta)
        {
            if (IsRunning)
            {
                _runElapsed += delta;
            }
        }

        /// <summary>
        /// Start a new run with optional deterministic seed. Resets score, timer, and basic systems.
        /// </summary>
        public void StartRun(int seed = 0)
        {
            // Reset runtime state
            _runSeed = seed;
            CurrentScore = 0;
            _runElapsed = 0f;

            // Defensive: refresh service references (in case bootstrap order changed)
            Services.TryGet<ITimeService>(out _timeService);
            Services.TryGet<IDiamondSystem>(out _diamondSystem);
            Services.TryGet<IHeroSystem>(out _heroSystem);
            Services.TryGet<IPoolingSystem>(out _poolingSystem);
            Services.TryGet<EnemySpawner>(out var spawner);

            // Reset or clear systems to a known-good state
            ResetForNewRun();

            // If diamond exists, ensure it is inside screen and clear edge flag
            _diamondSystem?.ResetState();

            // If pooling system present, optionally prewarm common pools (designer decision)
            if (_poolingSystem != null && poolPrewarmCount > 0)
            {
                // Prewarm is best-effort: caller must supply prefab types later.
                // As a generic safety step, we do nothing here else risk referencing unknown prefabs.
                // Implementers should call IPoolingSystem.Prewarm() for specific prefabs when ready.
            }

            if (Services.TryGet<EnemySpawner>(out var enemySpawner))
            {
                enemySpawner.ResetAndStart(seed); // now spawner has enemy types
            }

            SetState(GameState.Running);
            OnRunStarted?.Invoke();

            Debug.Log($"[GameManager] Run started (seed={seed}).");
        }

        /// <summary>
        /// End the current run and fire GameOver flow.
        /// </summary>
        public void EndRun()
        {
            if (!IsRunning)
            {
                // Already ended or not started; still transition to GameOver to be safe.
                SetState(GameState.GameOver);
                OnRunEnded?.Invoke(CurrentScore);
                return;
            }

            SetState(GameState.GameOver);

            // Optionally do run-end bookkeeping (persistence, analytics)
            try
            {
                // Example: log elapsed and score (Analytics system may not be present yet).
                Services.TryGet<ITimeService>(out var ts);
                float runtime = _runElapsed;
                Debug.Log($"[GameManager] Run ended. Score={CurrentScore} Time={runtime:F2}s");
            }
            catch { /* ignore analytics absence */ }

            if (Services.TryGet<EnemySpawner>(out var spawner))
            {
                spawner.Stop();
            }


            OnRunEnded?.Invoke(CurrentScore);
        }

        /// <summary>
        /// Pause game (delegates to TimeService if available).
        /// </summary>
        public void Pause()
        {
            if (CurrentState != GameState.Running) return;
            _timeService?.Pause();
            SetState(GameState.Paused);
        }

        /// <summary>
        /// Resume from pause.
        /// </summary>
        public void Resume()
        {
            if (CurrentState != GameState.Paused) return;
            _timeService?.Resume();
            SetState(GameState.Running);
        }

        /// <summary>
        /// Add to current score and fire event.
        /// </summary>
        public void AddScore(int amount)
        {
            if (amount == 0) return;
            CurrentScore += amount;
            OnScoreChanged?.Invoke(CurrentScore);
        }

        /// <summary>
        /// Reset various systems to a clean state at the start of a run.
        /// This method is defensive and will only act on services that exist.
        /// </summary>
        private void ResetForNewRun()
        {
            // Reset pools (clear & let systems prewarm explicitly later). Clearing avoids stale active objects.
            if (_poolingSystem != null)
            {
                try
                {
                    _poolingSystem.ClearAll();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[GameManager] PoolingSystem.ClearAll threw: {e.Message}");
                }
            }

            // Reset diamond
            try
            {
                _diamondSystem?.ResetState();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GameManager] Diamond.ResetState threw: {e.Message}");
            }

            // Hero reset: place near diamond if hero system is present
            if (_heroSystem != null && _diamondSystem != null)
            {
                try
                {
                    var heroT = _heroSystem.HeroTransform;
                    if (heroT != null && _diamondSystem.DiamondTransform != null)
                    {
                        heroT.position = _diamondSystem.DiamondTransform.position;
                    }
                }
                catch { /* ignore */ }
            }

            // Reset run timer
            _runElapsed = 0f;
        }

        /// <summary>
        /// Changes CurrentState and fires OnStateChanged.
        /// </summary>
        private void SetState(GameState newState)
        {
            if (CurrentState == newState) return;
            CurrentState = newState;
            OnStateChanged?.Invoke(newState);
        }

        /// <summary>
        /// Called when the diamond reports it intersected the screen edge.
        /// We end the run immediately.
        /// </summary>
        private void HandleDiamondEdge()
        {
            Debug.Log("[GameManager] Diamond reached edge -> ending run.");
            EndRun();
        }

        /// <summary>
        /// Expose run elapsed time for HUDs and tests.
        /// </summary>
        public float GetRunElapsed() => _runElapsed;

        #region Editor helpers
#if UNITY_EDITOR
        [ContextMenu("Force End Run")]
        private void DebugForceEndRun()
        {
            EndRun();
        }
#endif
        #endregion
    }
}
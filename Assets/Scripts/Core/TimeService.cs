// Assets/Scripts/Core/TimeService.cs
using System;
using UnityEngine;

namespace Core
{
    /// <summary>
    /// ITimeService - abstracts time access so gameplay code can be deterministic and testable.
    /// Implementation (TimeService) is a MonoBehaviour that registers itself with Services on Enable.
    ///
    /// Features:
    /// - TimeScale (global speed multiplier)
    /// - Pause/Resume API (IsPaused)
    /// - DeltaTime / UnscaledDeltaTime properties (respecting pause and timescale)
    /// - Manual mode for tests: when ManualMode==true, Update() does not advance internal time;
    ///   callers must call Advance(seconds) to step time.
    /// - Events: OnTick (per-frame with computed delta), OnPauseChanged, OnTimeScaleChanged
    /// - Advance(seconds) lets tests / deterministic simulations progress time manually.
    /// </summary>
    public interface ITimeService
    {
        /// <summary>Global game timescale multiplier. 1 = real-time; 0.5 = half speed.</summary>
        float TimeScale { get; }

        /// <summary>True if the service is currently paused (DeltaTime reports 0).</summary>
        bool IsPaused { get; }

        /// <summary>Returns the delta time usable for most gameplay updates (already scaled & zero when paused).</summary>
        float DeltaTime { get; }

        /// <summary>Returns the unscaled delta time (ignores TimeScale but returns 0 when paused).</summary>
        float UnscaledDeltaTime { get; }

        /// <summary>Elapsed simulated game time (accumulated DeltaTime).</summary>
        float ElapsedTime { get; }

        /// <summary>When true, the TimeService will not auto-advance from Unity's Update(); Advance() must be used.</summary>
        bool ManualMode { get; }

        /// <summary>Switch to manual mode (tests) or normal mode (runtime).</summary>
        void SetManualMode(bool manual);

        /// <summary>Advance simulated time by seconds (works in manual mode or runtime; respects pause/scale).</summary>
        void Advance(float seconds);

        /// <summary>Pause the game time (DeltaTime becomes 0).</summary>
        void Pause();

        /// <summary>Resume the game time (DeltaTime returns to scaled updates).</summary>
        void Resume();

        /// <summary>Toggle pause state.</summary>
        void TogglePause();

        /// <summary>Set timescale (non-negative). Fires OnTimeScaleChanged.</summary>
        void SetTimeScale(float scale);

        /// <summary>Event fired every tick/frame with the effective delta applied this frame (scaled, zero if paused).</summary>
        event Action<float> OnTick;

        /// <summary>Event fired when pause/resume changes. Passes new paused state.</summary>
        event Action<bool> OnPauseChanged;

        /// <summary>Event fired when TimeScale changes. Passes new timescale.</summary>
        event Action<float> OnTimeScaleChanged;
    }

    /// <summary>
    /// TimeService - MonoBehaviour implementation of ITimeService.
    /// Recommended usage:
    /// - Place a single GameObject with this component in Bootstrap.unity or created by Bootstrapper.
    /// - It auto-registers with Services as ITimeService (replaceExisting = true).
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public class TimeService : MonoBehaviour, ITimeService
    {
        [Header("Time Service Settings")]
        [Tooltip("Initial timescale. 1 = normal speed.")]
        [SerializeField] private float _initialTimeScale = 1f;

        [Tooltip("If true, TimeService will not auto-advance on Update; call Advance() manually.")]
        [SerializeField] private bool _manualMode = false;

        // Internal state
        private float _timeScale = 1f;
        private bool _isPaused = false;
        private float _elapsedTime = 0f;
        private float _lastTickDelta = 0f;

        /// <summary>Event fired every tick with effective delta applied.</summary>
        public event Action<float> OnTick;

        /// <summary>Event fired when pause/resume changes.</summary>
        public event Action<bool> OnPauseChanged;

        /// <summary>Event fired when TimeScale changes.</summary>
        public event Action<float> OnTimeScaleChanged;

        /// <summary>Expose TimeScale (read-only); use SetTimeScale to change.</summary>
        public float TimeScale => _timeScale;

        /// <summary>True if paused.</summary>
        public bool IsPaused => _isPaused;

        /// <summary>Delta time to use for gameplay updates (scaled & zero when paused).</summary>
        public float DeltaTime => _isPaused ? 0f : _lastTickDelta;

        /// <summary>Unscaled delta time (ignores TimeScale, zero when paused).</summary>
        public float UnscaledDeltaTime
        {
            get
            {
                if (_isPaused) return 0f;
                return Time.unscaledDeltaTime;
            }
        }

        /// <summary>Elapsed simulated time tracked by this service (sums DeltaTime).</summary>
        public float ElapsedTime => _elapsedTime;

        /// <summary>Manual mode flag (if true, Update() doesn't advance time).</summary>
        public bool ManualMode => _manualMode;

        #region Unity lifecycle
        private void Awake()
        {
            _timeScale = Mathf.Max(0f, _initialTimeScale);
            // If somebody forgot to add a Bootstrapper, allow the TimeService to self-register.
            try
            {
                Services.Register<ITimeService>(this, replaceExisting: true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"TimeService registration failed: {e.Message}");
            }
        }

        private void OnEnable()
        {
            // ensure registration on enable
            Services.Register<ITimeService>(this, replaceExisting: true);
        }

        private void OnDisable()
        {
            // unregister when disabled to allow hot-reload/teardown in editor/tests
            Services.Unregister<ITimeService>();
        }

        private void Start()
        {
            // initialize elapsed to Unity time, but we keep our own elapsed since Start
            _elapsedTime = 0f;
            _lastTickDelta = 0f;
        }

        private void Update()
        {
            if (_manualMode)
            {
                // Do not auto-advance in manual mode.
                _lastTickDelta = 0f;
                OnTick?.Invoke(0f);
                return;
            }

            // Compute scaled delta for this frame. We deliberately use Unity's deltaTime for smoothness,
            // then multiply by TimeScale. Pause forces zero delta.
            float rawDelta = Time.deltaTime;
            float scaledDelta = _isPaused ? 0f : rawDelta * _timeScale;

            // Update internal state
            _lastTickDelta = scaledDelta;
            _elapsedTime += scaledDelta;

            // Fire event
            OnTick?.Invoke(scaledDelta);
        }
        #endregion

        #region Public API
        /// <summary>Advance the simulated time by the given seconds. Useful for tests or manual stepping.</summary>
        public void Advance(float seconds)
        {
            if (seconds <= 0f)
            {
                return;
            }

            if (_isPaused)
            {
                // If paused, don't advance (consistent with DeltaTime behavior).
                _lastTickDelta = 0f;
                OnTick?.Invoke(0f);
                return;
            }

            // Apply TimeScale when advancing; this mirrors Update() semantics.
            float applied = seconds * _timeScale;
            _lastTickDelta = applied;
            _elapsedTime += applied;
            OnTick?.Invoke(applied);
        }

        /// <summary>Enter manual mode (tests) or exit manual mode.</summary>
        public void SetManualMode(bool manual)
        {
            if (_manualMode == manual) return;
            _manualMode = manual;

            // when entering manual mode, zero out last tick so gameplay stops until Advance() is called.
            if (_manualMode)
            {
                _lastTickDelta = 0f;
                OnTick?.Invoke(0f);
            }
        }

        /// <summary>Pause time (DeltaTime becomes 0). Does not change TimeScale.</summary>
        public void Pause()
        {
            if (_isPaused) return;
            _isPaused = true;
            _lastTickDelta = 0f;
            OnPauseChanged?.Invoke(true);
        }

        /// <summary>Resume time.</summary>
        public void Resume()
        {
            if (!_isPaused) return;
            _isPaused = false;
            OnPauseChanged?.Invoke(false);
        }

        /// <summary>Toggle paused state.</summary>
        public void TogglePause()
        {
            if (_isPaused) Resume(); else Pause();
        }

        /// <summary>Set global timescale (non-negative). Will immediately affect subsequent DeltaTime calculations.</summary>
        public void SetTimeScale(float scale)
        {
            float clamped = Mathf.Max(0f, scale);
            if (Mathf.Approximately(clamped, _timeScale)) return;
            _timeScale = clamped;
            OnTimeScaleChanged?.Invoke(_timeScale);
        }
        #endregion

        #region Editor helpers and debug
#if UNITY_EDITOR
        private void OnValidate()
        {
            _initialTimeScale = Mathf.Max(0f, _initialTimeScale);
        }
#endif
        #endregion
    }
}
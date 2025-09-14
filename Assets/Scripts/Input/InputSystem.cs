// Assets/Scripts/Input/InputSystem.cs
using System;
using System.Collections.Generic;
using Core;
using UnityEngine;

namespace Systems.Input
{
    /// <summary>
    /// InputSystem - captures mouse/touch input and emits Stroke lifecycle events.
    /// - Emits OnStrokeBegin, OnStrokeUpdate, OnStrokeEnd with a Stroke instance (screen-space points).
    /// - Supports SimulateStroke for tests.
    /// - Simple sampling logic: samples on pointer move when distance > minSampleDistance OR when time since last sample > maxSampleInterval.
    /// - Lightweight and defensive: can be enabled/disabled, and reports an input bounds rect.
    ///
    /// Notes about allocations:
    /// - This implementation creates a new Stroke object per user stroke (Begin->End). For short arcade runs this is acceptable.
    ///   If you need maximum allocation avoidance, later replace with a small Stroke pool or provide an option to clone emitted strokes.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class InputSystem : MonoBehaviour, IInputSystem
    {
        [Header("Sampling")]
        [Tooltip("Minimum screen-space distance (pixels) between successive sampled points.")]
        [SerializeField] private float minSampleDistance = 6f;

        [Tooltip("Maximum time (s) between successive samples, even if pointer hasn't moved.")]
        [SerializeField] private float maxSampleInterval = 0.05f;

        [Header("Input Area (optional)")]
        [Tooltip("If assigned, input is clamped/ignored outside this RectTransform's screen rect. If null, the entire screen is used.")]
        [SerializeField] private RectTransform inputArea;

        [Header("Debug")]
        [SerializeField] private bool debugLogStrokes = false;

        // Events
        public event Action<Stroke> OnStrokeBegin;
        public event Action<Stroke> OnStrokeUpdate;
        public event Action<Stroke> OnStrokeEnd;

        // Public state
        public bool IsEnabled { get; private set; } = true;

        // Internal tracking per pointer id (touch fingerId or mouse=0)
        private class ActiveStrokeState
        {
            public Stroke stroke;
            public Vector2 lastSamplePoint;
            public float lastSampleTime;
        }

        private readonly Dictionary<int, ActiveStrokeState> _activeStrokes = new Dictionary<int, ActiveStrokeState>();

        // Input bounds cached
        private Rect _inputBoundsCache = new Rect(0, 0, Screen.width, Screen.height);
        private bool _boundsDirty = true;

        private void OnEnable()
        {
            Services.Register<IInputSystem>(this, replaceExisting: true);
            _boundsDirty = true;
        }

        private void OnDisable()
        {
            try { Services.Unregister<IInputSystem>(); } catch { }
            // Clear active strokes defensively
            _activeStrokes.Clear();
        }

        private void Update()
        {
            if (!IsEnabled) return;

            // Update cached bounds if needed
            if (_boundsDirty)
            {
                RecomputeInputBounds();
            }

            // Touch input (multi-touch)
            if (UnityEngine.Input.touchSupported && UnityEngine.Input.touchCount > 0)
            {
                ProcessTouches();
            }
            else
            {
                // Mouse fallback (single pointer)
                ProcessMouse();
            }

            // Sampling: for each active stroke we may sample time-based point even if pointer hasn't moved.
            // We'll iterate active strokes and, if sufficient time has passed since last sample, sample current pointer pos.
            var now = Time.time;
            var states = new List<KeyValuePair<int, ActiveStrokeState>>(_activeStrokes);
            foreach (var kv in states)
            {
                var id = kv.Key;
                var state = kv.Value;
                if (state == null || state.stroke == null) continue;

                if ((now - state.lastSampleTime) >= maxSampleInterval)
                {
                    // get current position for this pointer; if pointer no longer exists skip
                    Vector2 pos;
                    if (!TryGetPointerPosition(id, out pos)) continue;

                    // Add sample if moved a tiny bit (or even if not moved - time-based sampling)
                    state.stroke.AddPoint(pos);
                    state.lastSamplePoint = pos;
                    state.lastSampleTime = now;
                    OnStrokeUpdate?.Invoke(state.stroke);
                }
            }
        }

        #region Public API

        public void Enable(bool on)
        {
            IsEnabled = on;
            if (!IsEnabled)
            {
                // End any active strokes immediately to ensure downstream systems get end events.
                var active = new List<int>(_activeStrokes.Keys);
                foreach (var id in active)
                {
                    EndStroke(id);
                }
            }
        }

        public Rect GetInputBounds()
        {
            if (_boundsDirty) RecomputeInputBounds();
            return _inputBoundsCache;
        }

        /// <summary>
        /// Simulate a full stroke programmatically. Emits Begin->Update*->End synchronously.
        /// Useful for tests.
        /// </summary>
        public void SimulateStroke(Stroke stroke)
        {
            if (stroke == null) throw new ArgumentNullException(nameof(stroke));
            if (!IsEnabled)
            {
                if (debugLogStrokes) Debug.Log("[InputSystem] SimulateStroke called while disabled - ignoring.");
                return;
            }

            // Synchronous emission: create a new stroke clone to avoid caller-mutating passed instance.
            var s = stroke.Clone();

            // If no points nothing to do
            if (s.Count == 0)
            {
                s.End(Time.time);
                OnStrokeBegin?.Invoke(s);
                OnStrokeEnd?.Invoke(s);
                return;
            }

            // Emit Begin with first point
            if (s.StartTime <= 0f) s.SetStartTime(Time.time);
            OnStrokeBegin?.Invoke(s);

            // Emit updates for intermediate points (excluding first and last)
            for (int i = 1; i < s.Count - 1; i++)
            {
                OnStrokeUpdate?.Invoke(s);
            }

            // End stroke
            s.End(Time.time);
            OnStrokeEnd?.Invoke(s);

            if (debugLogStrokes) Debug.Log($"[InputSystem] Simulated stroke (points={s.Count}, len={s.Length:F1})");
        }

        #endregion

        #region Touch / Mouse processing helpers

        private void ProcessTouches()
        {
            for (int i = 0; i < UnityEngine.Input.touchCount; i++)
            {
                var t = UnityEngine.Input.GetTouch(i);
                var id = t.fingerId;
                var pos = t.position;

                if (!IsWithinInputBounds(pos)) continue;

                switch (t.phase)
                {
                    case TouchPhase.Began:
                        BeginStroke(id, pos);
                        break;
                    case TouchPhase.Moved:
                    case TouchPhase.Stationary:
                        UpdateStroke(id, pos);
                        break;
                    case TouchPhase.Ended:
                    case TouchPhase.Canceled:
                        EndStroke(id, pos);
                        break;
                }
            }
        }

        private void ProcessMouse()
        {
            const int MOUSE_ID = 0;
            var pos = (Vector2)UnityEngine.Input.mousePosition;

            if (UnityEngine.Input.GetMouseButtonDown(0))
            {
                if (IsWithinInputBounds(pos))
                    BeginStroke(MOUSE_ID, pos);
            }
            else if (UnityEngine.Input.GetMouseButton(0))
            {
                if (IsWithinInputBounds(pos))
                    UpdateStroke(MOUSE_ID, pos);
                else
                {
                    // If pointer moved outside bounds while drawing, still allow update so user can drag out
                    UpdateStroke(MOUSE_ID, pos);
                }
            }
            else if (UnityEngine.Input.GetMouseButtonUp(0))
            {
                EndStroke(MOUSE_ID, pos);
            }
        }

        private void BeginStroke(int pointerId, Vector2 screenPos)
        {
            if (!IsEnabled) return;
            // If a stroke for this pointer already exists, end it first
            if (_activeStrokes.ContainsKey(pointerId))
            {
                EndStroke(pointerId);
            }

            var s = new Stroke();
            s.Begin(pointerId, Time.time);
            s.AddPoint(screenPos);

            var state = new ActiveStrokeState()
            {
                stroke = s,
                lastSamplePoint = screenPos,
                lastSampleTime = Time.time
            };
            _activeStrokes[pointerId] = state;

            // Fire event
            try
            {
                OnStrokeBegin?.Invoke(s);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[InputSystem] Exception in OnStrokeBegin listeners: {e.Message}");
            }

            if (debugLogStrokes) Debug.Log($"[InputSystem] BeginStroke id={pointerId} pts={s.Count}");
        }

        private void UpdateStroke(int pointerId, Vector2 screenPos)
        {
            if (!IsEnabled) return;
            if (!_activeStrokes.TryGetValue(pointerId, out var state))
            {
                // If no active but pointer moved (e.g., began out of bounds), start a new stroke implicitly
                BeginStroke(pointerId, screenPos);
                return;
            }

            // Add sample only if distance threshold exceeded OR maxSampleInterval elapsed (handled in Update loop)
            float dist = Vector2.Distance(screenPos, state.lastSamplePoint);
            float now = Time.time;
            if (dist >= minSampleDistance || (now - state.lastSampleTime) >= maxSampleInterval)
            {
                state.stroke.AddPoint(screenPos);
                state.lastSamplePoint = screenPos;
                state.lastSampleTime = now;

                try
                {
                    OnStrokeUpdate?.Invoke(state.stroke);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[InputSystem] Exception in OnStrokeUpdate listeners: {e.Message}");
                }

                if (debugLogStrokes) Debug.Log($"[InputSystem] UpdateStroke id={pointerId} pts={state.stroke.Count}");
            }
        }

        private void EndStroke(int pointerId)
        {
            // End without a provided final position
            if (!_activeStrokes.TryGetValue(pointerId, out var state))
                return;

            EndStroke(pointerId, state.lastSamplePoint);
        }

        private void EndStroke(int pointerId, Vector2 finalPos)
        {
            if (!_activeStrokes.TryGetValue(pointerId, out var state))
                return;

            var s = state.stroke;
            // Add final sample if it's sufficiently different from last
            if (s.Count == 0 || Vector2.Distance(finalPos, s[s.Count - 1]) > 0.01f)
            {
                s.AddPoint(finalPos);
            }

            s.End(Time.time);
            _activeStrokes.Remove(pointerId);

            try
            {
                OnStrokeEnd?.Invoke(s);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[InputSystem] Exception in OnStrokeEnd listeners: {e.Message}");
            }

            if (debugLogStrokes) Debug.Log($"[InputSystem] EndStroke id={pointerId} pts={s.Count} len={s.Length:F1}");

            // Note: not pooling strokes here. If pooling desired, add a StrokePool and ensure consumers do not hold references.
        }

        /// <summary>
        /// Try to get current pointer position for the provided pointer id (mouse=0, touches use fingerId).
        /// Returns false if pointer not active.
        /// </summary>
        private bool TryGetPointerPosition(int pointerId, out Vector2 pos)
        {
            pos = Vector2.zero;
            if (UnityEngine.Input.touchSupported && UnityEngine.Input.touchCount > 0)
            {
                for (int i = 0; i < UnityEngine.Input.touchCount; i++)
                {
                    var t = UnityEngine.Input.GetTouch(i);
                    if (t.fingerId == pointerId)
                    {
                        pos = t.position;
                        return true;
                    }
                }
                return false;
            }
            else
            {
                // mouse fallback - id 0 maps to mouse
                if (pointerId == 0)
                {
                    pos = (Vector2)UnityEngine.Input.mousePosition;
                    return true;
                }
                return false;
            }
        }

        #endregion

        #region Input bounds helpers

        private void RecomputeInputBounds()
        {
            if (inputArea != null)
            {
                // Convert RectTransform to screen-space rect
                Vector3[] corners = new Vector3[4];
                inputArea.GetWorldCorners(corners);
                // World corners -> screen points:
                Vector2 min = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
                Vector2 max = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
                _inputBoundsCache = new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
            }
            else
            {
                _inputBoundsCache = new Rect(0, 0, Screen.width, Screen.height);
            }

            _boundsDirty = false;
        }

        private bool IsWithinInputBounds(Vector2 screenPos)
        {
            if (_boundsDirty) RecomputeInputBounds();
            return _inputBoundsCache.Contains(screenPos);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            minSampleDistance = Mathf.Max(1f, minSampleDistance);
            maxSampleInterval = Mathf.Max(0.01f, maxSampleInterval);
        }
#endif
    }
    #endregion
}
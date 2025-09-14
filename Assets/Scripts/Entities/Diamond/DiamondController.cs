// Assets/Scripts/Entities/Diamond/DiamondController.cs
using System;
using Core;
using UnityEngine;

namespace Entities.Diamond
{
    /// <summary>
    /// IDiamondSystem - service contract for the diamond core object.
    /// Registered automatically with Services in Awake/OnEnable so other systems can find it.
    /// </summary>
    public interface IDiamondSystem
    {
        Transform DiamondTransform { get; }
        /// <summary>
        /// Event fired once when the diamond touches or intersects the screen edge (game over condition).
        /// </summary>
        event Action OnDiamondEdge;

        /// <summary>
        /// Reset internal state (clear edge triggered flag, reposition optionally).
        /// </summary>
        void ResetState();

        /// <summary>
        /// Force set diamond world position.
        /// </summary>
        void SetPosition(Vector3 worldPos);

        /// <summary>
        /// Returns world-space radius used for edge detection.
        /// </summary>
        float Radius { get; }
    }

    /// <summary>
    /// DiamondController
    /// - Central "life" object. When the diamond's visual bounds intersect the screen edge, it triggers OnDiamondEdge.
    /// - Designed to be robust across orthographic and perspective cameras by measuring screen-space radius dynamically.
    /// - Registers itself as IDiamondSystem on enable so other systems can look it up via Services.Get<IDiamondSystem>().
    /// </summary>
    [DisallowMultipleComponent]
    public class DiamondController : MonoBehaviour, IDiamondSystem
    {
        [Header("Diamond Settings")]
        [Tooltip("Logical radius (world units) used for edge intersection checks.")]
        [SerializeField] private float _radius = 0.5f;

        [Tooltip("If true, the diamond will be clamped to camera view when ResetState is called.")]
        [SerializeField] private bool _clampOnReset = true;

        [Tooltip("Small delay (s) after edge triggered before allowing ResetState to clear it. Prevents immediate re-trigger.")]
        [SerializeField] private float _edgeCooldown = 0.25f;

        // Exposed via interface
        public Transform DiamondTransform => this.transform;
        public float Radius => _radius;

        public event Action OnDiamondEdge;

        // Internal
        private Camera _cam;
        private bool _edgeTriggered = false;
        private float _edgeTriggeredTime = -Mathf.Infinity;

        private void Awake()
        {
            // Try to register with Services so other systems can find the diamond:
            try
            {
                Services.Register<IDiamondSystem>(this, replaceExisting: true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DiamondController] Failed to register IDiamondSystem: {e.Message}");
            }
        }

        private void OnEnable()
        {
            _cam = Camera.main ?? Camera.current;
            if (_cam == null)
            {
                // Fallback: try to find any camera
                _cam = FindObjectOfType<Camera>();
            }

            // Defensive: if no camera found, log error; edge detection will be skipped until camera available.
            if (_cam == null)
            {
                Debug.LogError("[DiamondController] No Camera found in scene. Edge detection will be disabled until a Camera is present.");
            }
        }

        private void OnDisable()
        {
            // Unregister from Services to avoid stale references on domain reload.
            try
            {
                Services.Unregister<IDiamondSystem>();
            }
            catch { /* ignore */ }
        }

        private void Update()
        {
            if (_cam == null)
            {
                // Try to re-acquire camera (in case camera was created after)
                _cam = Camera.main ?? Camera.current ?? FindObjectOfType<Camera>();
                if (_cam == null) return;
            }

            // Only perform edge detection if not already triggered (or if cooldown passed)
            if (!_edgeTriggered || (Time.time - _edgeTriggeredTime) >= _edgeCooldown)
            {
                if (CheckIntersectScreenEdge())
                {
                    // Mark triggered and fire event once
                    _edgeTriggered = true;
                    _edgeTriggeredTime = Time.time;
                    try
                    {
                        OnDiamondEdge?.Invoke();
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[DiamondController] Exception in OnDiamondEdge subscribers: {e.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Checks whether the diamond's screen-space circle intersects or goes beyond the screen rectangle.
        /// Works for both orthographic and perspective cameras by computing screen-space radius via WorldToScreenPoint.
        /// </summary>
        /// <returns>true if diamond intersects the screen edge, false otherwise.</returns>
        private bool CheckIntersectScreenEdge()
        {
            if (_cam == null) return false;

            // Get screen position for the diamond center
            Vector3 screenPos = _cam.WorldToScreenPoint(transform.position);

            // If behind the camera (z < 0) treat as off-screen but not edge-hit
            if (screenPos.z < 0f)
            {
                return false;
            }

            // Compute screen-space radius by projecting a world-space offset point (position + right * radius)
            Vector3 screenOffset = _cam.WorldToScreenPoint(transform.position + transform.right * _radius);
            float screenRadius = Mathf.Abs(screenOffset.x - screenPos.x);

            // If for some reason computed radius is zero or NaN, fallback to small epsilon
            if (screenRadius <= 0f || float.IsNaN(screenRadius) || float.IsInfinity(screenRadius))
                screenRadius = Mathf.Max(1f, 0.5f * Mathf.Min(Screen.width, Screen.height) * 0.01f);

            // Edge check: if any part of the circle is outside (<=0 or >=width/height)
            if (screenPos.x - screenRadius <= 0f) return true;
            if (screenPos.x + screenRadius >= Screen.width) return true;
            if (screenPos.y - screenRadius <= 0f) return true;
            if (screenPos.y + screenRadius >= Screen.height) return true;

            return false;
        }

        /// <summary>
        /// Reset internal state (clear edge triggered flag, reposition optionally).
        /// Call this when starting a new run.
        /// </summary>
        public void ResetState()
        {
            _edgeTriggered = false;
            _edgeTriggeredTime = -Mathf.Infinity;

            if (_clampOnReset && _cam != null)
            {
                // Ensure diamond is inside screen bounds after reset: place at screen center or clamp current pos.
                Vector3 screenPos = _cam.WorldToScreenPoint(transform.position);
                float screenRadius = Mathf.Abs(_cam.WorldToScreenPoint(transform.position + transform.right * _radius).x - screenPos.x);

                // Clamp screen pos within [screenRadius, width-screenRadius], [screenRadius, height-screenRadius]
                screenPos.x = Mathf.Clamp(screenPos.x, screenRadius + 1f, Screen.width - screenRadius - 1f);
                screenPos.y = Mathf.Clamp(screenPos.y, screenRadius + 1f, Screen.height - screenRadius - 1f);

                Vector3 world = _cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, screenPos.z));
                transform.position = world;
            }
        }

        /// <summary>
        /// Force set diamond world position. Useful for tests or game start placement.
        /// </summary>
        /// <param name="worldPos"></param>
        public void SetPosition(Vector3 worldPos)
        {
            transform.position = worldPos;
            // clear edge flag if we've been moved back inside; defensive
            _edgeTriggered = false;
            _edgeTriggeredTime = -Mathf.Infinity;
        }

        #region Gizmos & Editor
#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireSphere(transform.position, _radius);

            if (Camera.main != null)
            {
                try
                {
                    var cachedCam = Camera.main;
                    Vector3 screenPos = cachedCam.WorldToScreenPoint(transform.position);
                    Vector3 screenOffset = cachedCam.WorldToScreenPoint(transform.position + transform.right * _radius);
                    float screenRadius = Mathf.Abs(screenOffset.x - screenPos.x);
                    // approximate viewport radius for visualization
                    Vector3 v0 = cachedCam.ScreenToWorldPoint(new Vector3(screenPos.x - screenRadius, screenPos.y, screenPos.z));
                    Vector3 v1 = cachedCam.ScreenToWorldPoint(new Vector3(screenPos.x + screenRadius, screenPos.y, screenPos.z));
                    Gizmos.color = Color.yellow;
                    Gizmos.DrawLine(v0, v1);
                }
                catch { /* ignore editor issues */ }
            }
        }
#endif
        #endregion
    }
}
// Assets/Scripts/Input/StrokeVisualizer.cs
using System;
using System.Collections;
using System.Collections.Generic;
using Core;
using Systems.Pooling;
using UnityEngine;

namespace Systems.Input
{
    /// <summary>
    /// StrokeVisualizer
    /// - Listens to IInputSystem stroke events and renders strokes with LineRenderer instances.
    /// - Uses PoolingSystem (if available) with a configurable LineRenderer prefab. Falls back to runtime-created LineRenderers.
    /// - Keeps a mapping from Stroke -> active LineRenderer and updates positions on stroke updates.
    /// - When stroke ends, the visual is released back to pool (or destroyed) after a configurable lifetime.
    ///
    /// Notes:
    /// - Provide a prefab with a LineRenderer component (recommended) in the inspector.
    /// - If no prefab is provided the component will create simple LineRenderers at runtime.
    /// - Keeps allocations minimal by reusing LineRenderer components either via PoolingSystem or internal pool.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class StrokeVisualizer : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Optional prefab that contains a LineRenderer component. If set, PoolingSystem will be used to obtain line instances.")]
        [SerializeField] private GameObject linePrefab;

        [Header("Line settings")]
        [Tooltip("Max points to render per stroke (pre-alloc).")]
        [SerializeField] private int maxPoints = 128;

        [Tooltip("Line width (world units).")]
        [SerializeField] private float lineWidth = 0.06f;

        [Tooltip("How long (seconds) to keep a finished stroke visible before returning to pool/destroy.")]
        [SerializeField] private float finishedLifetime = 0.5f;

        [Tooltip("If true, uses Camera.main to convert screen->world using Z plane of 0. If false, uses near clip plane + 1.")]
        [SerializeField] private bool projectToWorldZZero = true;

        // Services
        private IPoolingSystem _poolingSystem;
        private IInputSystem _inputSystem;

        // Active visuals mapping: stroke instance -> active LineRenderer wrapper
        private readonly Dictionary<Stroke, LineRenderer> _activeVisuals = new Dictionary<Stroke, LineRenderer>();

        // Fallback tracking for runtime-created line renderers (if pooling absent)
        private readonly List<GameObject> _runtimeCreatedLines = new List<GameObject>();

        // Camera cache
        private Camera _cam;

        private void OnEnable()
        {
            Services.TryGet<IPoolingSystem>(out _poolingSystem);
            Services.TryGet<IInputSystem>(out _inputSystem);

            // Hook input events
            if (_inputSystem != null)
            {
                _inputSystem.OnStrokeBegin += HandleStrokeBegin;
                _inputSystem.OnStrokeUpdate += HandleStrokeUpdate;
                _inputSystem.OnStrokeEnd += HandleStrokeEnd;
            }

            _cam = Camera.main ?? Camera.current ?? FindObjectOfType<Camera>();
        }

        private void OnDisable()
        {
            if (_inputSystem != null)
            {
                _inputSystem.OnStrokeBegin -= HandleStrokeBegin;
                _inputSystem.OnStrokeUpdate -= HandleStrokeUpdate;
                _inputSystem.OnStrokeEnd -= HandleStrokeEnd;
            }

            // Return/destroy any active visuals
            foreach (var kv in _activeVisuals)
            {
                TryReleaseVisual(kv.Value);
            }
            _activeVisuals.Clear();

            // Cleanup runtime-created line GameObjects
            foreach (var go in _runtimeCreatedLines)
            {
                if (go == null) continue;
#if UNITY_EDITOR
                DestroyImmediate(go);
#else
                Destroy(go);
#endif
            }
            _runtimeCreatedLines.Clear();
        }

        #region Event handlers

        private void HandleStrokeBegin(Stroke s)
        {
            if (s == null) return;
            var lr = AcquireLineRenderer();
            if (lr == null) return;

            ConfigureLineRenderer(lr);

            // Set initial point(s)
            var worldPos = ScreenToWorldPoint(GetPointSafe(s, 0));
            lr.positionCount = 1;
            lr.SetPosition(0, worldPos);

            // Store mapping
            _activeVisuals[s] = lr;
        }

        private void HandleStrokeUpdate(Stroke s)
        {
            if (s == null) return;
            if (!_activeVisuals.TryGetValue(s, out var lr)) return;

            int points = Mathf.Min(s.Count, maxPoints);
            if (points <= 0) return;

            // Ensure positionCount matches
            if (lr.positionCount != points)
                lr.positionCount = points;

            // Fill positions (convert screen->world)
            // We iterate and set positions; this avoids temporary arrays.
            for (int i = 0; i < points; i++)
            {
                var screen = GetPointSafe(s, i);
                Vector3 world = ScreenToWorldPoint(screen);
                lr.SetPosition(i, world);
            }
        }

        private void HandleStrokeEnd(Stroke s)
        {
            if (s == null) return;
            if (!_activeVisuals.TryGetValue(s, out var lr)) return;

            // Final update to ensure last point is present
            HandleStrokeUpdate(s);

            // Schedule release after finishedLifetime
            StartCoroutine(ReleaseAfterDelay(lr, finishedLifetime));

            // Remove mapping immediately so new strokes can reuse same Stroke object/instance
            _activeVisuals.Remove(s);
        }

        #endregion

        #region LineRenderer lifecycle / pooling

        private LineRenderer AcquireLineRenderer()
        {
            // Try pooling system with provided prefab
            if (_poolingSystem != null && linePrefab != null)
            {
                try
                {
                    var lr = _poolingSystem.Get<LineRenderer>(linePrefab);
                    return lr;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[StrokeVisualizer] PoolingSystem.Get failed: {e.Message}");
                    // fallback to runtime creation below
                }
            }

            // Fallback: create a runtime LineRenderer GameObject
            var go = new GameObject("StrokeLine");
            var lrComp = go.AddComponent<LineRenderer>();
            // Basic configuration
            lrComp.useWorldSpace = true;
            lrComp.loop = false;
            lrComp.numCapVertices = 4;
            lrComp.numCornerVertices = 2;
            lrComp.positionCount = 0;
            lrComp.startWidth = lineWidth;
            lrComp.endWidth = lineWidth;
            lrComp.material = new Material(Shader.Find("Sprites/Default"));
            lrComp.receiveShadows = false;
            lrComp.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            // parent under this GameObject for tidiness
            go.transform.SetParent(this.transform, worldPositionStays: false);

            _runtimeCreatedLines.Add(go);
            return lrComp;
        }

        private void ConfigureLineRenderer(LineRenderer lr)
        {
            if (lr == null) return;
            lr.startWidth = lineWidth;
            lr.endWidth = lineWidth;
            lr.loop = false;
            lr.useWorldSpace = true;
            // set a default material if none
            if (lr.material == null)
            {
                lr.material = new Material(Shader.Find("Sprites/Default"));
            }
        }

        private IEnumerator ReleaseAfterDelay(LineRenderer lr, float delay)
        {
            if (lr == null)
                yield break;

            if (delay > 0f)
                yield return new WaitForSeconds(delay);

            TryReleaseVisual(lr);
        }

        private void TryReleaseVisual(LineRenderer lr)
        {
            if (lr == null) return;

            // Prefer returning to pooling system if possible (and this instance came from a pool).
            if (_poolingSystem != null)
            {
                try
                {
                    // Note: PoolingSystem.Return will attempt to match instance to its pool.
                    _poolingSystem.Return<LineRenderer>(lr);
                    return;
                }
                catch
                {
                    // swallow and fallback to immediate destroy
                }
            }

            // If not pooled, destroy the GameObject
            if (lr != null && lr.gameObject != null)
            {
                if (_runtimeCreatedLines.Contains(lr.gameObject))
                {
                    _runtimeCreatedLines.Remove(lr.gameObject);
#if UNITY_EDITOR
                    DestroyImmediate(lr.gameObject);
#else
                    Destroy(lr.gameObject);
#endif
                }
                else
                {
                    // If it's not tracked as runtime-created and we couldn't return to pool, safely destroy
#if UNITY_EDITOR
                    DestroyImmediate(lr.gameObject);
#else
                    Destroy(lr.gameObject);
#endif
                }
            }
        }

        #endregion

        #region Utilities

        private Vector2 GetPointSafe(Stroke s, int index)
        {
            if (s == null || s.Count == 0) return Vector2.zero;
            int idx = Mathf.Clamp(index, 0, s.Count - 1);
            return s[idx];
        }

        private Vector3 ScreenToWorldPoint(Vector2 screen)
        {
            if (_cam == null)
            {
                _cam = Camera.main ?? Camera.current ?? FindObjectOfType<Camera>();
                if (_cam == null)
                {
                    // fallback to identity mapping (rare)
                    return new Vector3(screen.x, screen.y, 0f);
                }
            }

            if (projectToWorldZZero)
            {
                // For orthographic cameras this returns correct world XY with z=0 plane.
                // For perspective we compute a z that maps to world z = 0 by using -cam.position.z
                float zWorldPlane = 0f;
                float z = Mathf.Abs(_cam.transform.position.z - zWorldPlane);
                var sw = new Vector3(screen.x, screen.y, z);
                return _cam.ScreenToWorldPoint(sw);
            }
            else
            {
                // Use near clip plane + 1 as depth (safe generic fallback)
                float z = _cam.nearClipPlane + 1f;
                var sw = new Vector3(screen.x, screen.y, z);
                return _cam.ScreenToWorldPoint(sw);
            }
        }

        #endregion

#if UNITY_EDITOR
        private void OnValidate()
        {
            maxPoints = Mathf.Clamp(maxPoints, 8, 4096);
            lineWidth = Mathf.Max(0.001f, lineWidth);
            finishedLifetime = Mathf.Max(0f, finishedLifetime);
        }
#endif
    }
}
// Assets/Scripts/Entities/Hero/HeroController.cs
using System;
using Core;
using Entities.Diamond;
using UnityEngine;

namespace Entities.Hero
{
    /// <summary>
    /// IHeroSystem - small service contract for the hero.
    /// Other systems (SpellSystem, VFX) can request the spell origin/transform via this interface.
    /// </summary>
    public interface IHeroSystem
    {
        Transform HeroTransform { get; }
        /// <summary>
        /// World position to spawn spells from (typically wand tip).
        /// </summary>
        Vector3 GetSpellOrigin();
        /// <summary>
        /// Optional: set explicit follow target (overrides diamond).
        /// </summary>
        void SetFollowTarget(Transform target);
    }

    /// <summary>
    /// HeroController
    /// - Simple follower that smoothly follows the diamond (or an explicitly set target).
    /// - Exposes a wand tip transform used as the spell spawn origin.
    /// - Registers as IHeroSystem in Services for other systems to locate.
    /// - Uses ITimeService when available for deterministic updates (tests / time scaling).
    /// </summary>
    [DisallowMultipleComponent]
    public class HeroController : MonoBehaviour, IHeroSystem
    {
        [Header("Follow Settings")]
        [Tooltip("Follow speed (world units per second). Higher is snappier.")]
        [SerializeField] private float followSpeed = 8f;

        [Tooltip("Smoothing time used by SmoothDamp; lower = snappier.")]
        [SerializeField] private float smoothTime = 0.08f;

        [Tooltip("Maximum allowed distance from target before teleporting (prevents long lerp after huge teleport).")]
        [SerializeField] private float maxTeleportDistance = 5f;

        [Header("Wand / Spell Origin")]
        [Tooltip("Optional child transform to use as the wand tip / spell origin. If not assigned, a default offset is used.")]
        [SerializeField] private Transform wandTip;

        [Tooltip("Default local offset used when wandTip is not assigned.")]
        [SerializeField] private Vector3 defaultWandLocalOffset = new Vector3(0f, 0.4f, 0f);

        [Header("Debug")]
        [SerializeField] private bool showGizmos = true;

        // Cached services
        private ITimeService _timeService;
        private IDiamondSystem _diamondSystem;

        // Follow state
        private Transform _followTarget; // primary target (diamond) or overridden target
        private Vector3 _velocity = Vector3.zero;

        // Expose hero transform via interface
        public Transform HeroTransform => this.transform;

        private void Awake()
        {
            // Register IHeroSystem
            try
            {
                Services.Register<IHeroSystem>(this, replaceExisting: true);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HeroController] Failed to register IHeroSystem: {e.Message}");
            }
        }

        private void OnEnable()
        {
            // Try obtain services (may be registered by Bootstrapper)
            Services.TryGet<ITimeService>(out _timeService);
            Services.TryGet<IDiamondSystem>(out _diamondSystem);

            if (_diamondSystem != null)
            {
                _followTarget = _diamondSystem.DiamondTransform;
            }

            // Defensive: if wandTip is not assigned, create a hidden child to act as wand tip
            if (wandTip == null)
            {
                var go = new GameObject("WandTip");
                go.transform.SetParent(transform, worldPositionStays: false);
                go.transform.localPosition = defaultWandLocalOffset;
                go.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
                wandTip = go.transform;
            }
        }

        private void OnDisable()
        {
            // Unregister IHeroSystem to avoid stale references on domain reload in editor/tests
            try
            {
                Services.Unregister<IHeroSystem>();
            }
            catch { /* ignore */ }
        }

        private void Update()
        {
            // Compute deltaTime (prefer TimeService if present)
            float dt = (_timeService != null) ? _timeService.DeltaTime : Time.deltaTime;

            if (dt <= 0f) return;

            if (_followTarget == null)
            {
                // Try to re-acquire diamond if available
                if (_diamondSystem != null)
                {
                    _followTarget = _diamondSystem.DiamondTransform;
                }
            }

            if (_followTarget == null)
            {
                // Nothing to follow
                return;
            }

            Vector3 targetPos = _followTarget.position;

            // Teleport if too far away (prevents long lerp after large repositioning)
            if (Vector3.Distance(transform.position, targetPos) > maxTeleportDistance)
            {
                transform.position = targetPos;
                _velocity = Vector3.zero;
                return;
            }

            // Smooth follow using SmoothDamp for smooth, frame-rate independent motion
            // SmoothDamp expects smoothTime in seconds; we clamp it to avoid degenerate values
            float clampedSmooth = Mathf.Max(0.0001f, smoothTime);
            float maxSpeedSafe   = Mathf.Max(0f, followSpeed);
            transform.position = Vector3.SmoothDamp(transform.position, targetPos, ref _velocity, clampedSmooth, maxSpeedSafe, dt);
        }

        /// <summary>
        /// Get the world position used for spell origin. Prefer wandTip if present.
        /// </summary>
        public Vector3 GetSpellOrigin()
        {
            if (wandTip != null) return wandTip.position;
            return transform.position + defaultWandLocalOffset;
        }

        /// <summary>
        /// Allow code to explicitly set the follow target (e.g., for tutorials or special behaviors).
        /// </summary>
        public void SetFollowTarget(Transform target)
        {
            _followTarget = target;
        }

        #region Gizmos
        private void OnDrawGizmos()
        {
            if (!showGizmos) return;

            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(transform.position, 0.12f);

            if (wandTip != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawSphere(wandTip.position, 0.06f);
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(transform.position, wandTip.position);
            }
            else
            {
                Gizmos.color = Color.yellow;
                Vector3 origin = transform.position + defaultWandLocalOffset;
                Gizmos.DrawSphere(origin, 0.06f);
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(transform.position, origin);
            }
        }
        #endregion
    }
}
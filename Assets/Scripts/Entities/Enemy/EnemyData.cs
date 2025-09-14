// Assets/Scripts/Entities/Enemy/EnemyData.cs
using UnityEngine;

namespace Entities.Enemy
{
    /// <summary>
    /// Enemy behavior types used by spawn/wave systems and simple AI branching.
    /// </summary>
    public enum EnemyBehaviorType
    {
        Basic,      // slow, direct path
        Fast,       // quick, low HP
        Shielded,   // high HP, slower
        Elite       // optional: custom movement/AI
    }

    /// <summary>
    /// ScriptableObject that defines enemy stats & metadata.
    /// Create instances via: Assets -> Create -> Game -> Enemy Data
    /// This SO is intended to be data-driven so designers can tune enemies without code changes.
    /// </summary>
    [CreateAssetMenu(fileName = "EnemyData_", menuName = "Game/Enemy Data", order = 100)]
    public class EnemyData : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Unique id for this enemy type (e.g., 'enemy_basic'). Used for wave data and analytics.")]
        public string enemyId = "enemy_basic";

        [Tooltip("Friendly display name for editor use.")]
        public string displayName = "Basic Enemy";

        [Header("Stats")]
        [Tooltip("Hit points of the enemy.")]
        public int maxHealth = 1;

        [Tooltip("Movement speed in world units per second.")]
        public float moveSpeed = 1.0f;

        [Tooltip("Amount of XP awarded to the player when this enemy dies.")]
        public int xpValue = 5;

        [Tooltip("Points/score awarded (optional).")]
        public int scoreValue = 1;

        [Header("Visual / Prefab")]
        [Tooltip("Sprite used for previewing this enemy in editors or small UIs.")]
        public Sprite previewSprite;

        [Tooltip("Prefab to spawn for this enemy type. Must contain an EnemyBase (or derived) component.")]
        public GameObject prefab;

        [Header("Collision / Size")]
        [Tooltip("Approximate collision radius (world units) used for simple checks (not a replacement for actual collider).")]
        public float collisionRadius = 0.25f;

        [Tooltip("Scale applied to the instantiated prefab (useful to vary visual size without new prefab).")]
        public Vector3 spawnScale = Vector3.one;

        [Header("Behavior")]
        [Tooltip("Select the high-level behavior type for this enemy.")]
        public EnemyBehaviorType behavior = EnemyBehaviorType.Basic;

        [Tooltip("Optional chance (0..1) for special spawn behavior for this enemy type (used by spawner/designer).")]
        [Range(0f, 1f)]
        public float specialSpawnChance = 0f;

        [Header("Editor / Debug")]
        [Tooltip("Developer notes (editor-only).")]
        [TextArea(2, 6)]
        public string notes = "";

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Basic sanitization to prevent invalid runtime values.
            if (string.IsNullOrEmpty(enemyId))
            {
                enemyId = name.Replace(" ", "_").ToLower();
            }

            maxHealth = Mathf.Max(1, maxHealth);
            moveSpeed = Mathf.Max(0f, moveSpeed);
            xpValue = Mathf.Max(0, xpValue);
            scoreValue = Mathf.Max(0, scoreValue);
            collisionRadius = Mathf.Max(0.01f, collisionRadius);
            if (spawnScale == Vector3.zero) spawnScale = Vector3.one;
        }
#endif
    }
}
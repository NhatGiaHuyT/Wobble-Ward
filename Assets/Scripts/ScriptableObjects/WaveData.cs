// Assets/Scripts/ScriptableObjects/WaveData.cs
using System;
using System.Collections.Generic;
using Entities.Enemy;
using UnityEngine;

namespace ScriptableObjects
{
    /// <summary>
    /// WaveData - data-driven definition of a spawn wave.
    /// A wave consists of a list of spawn instructions. Each instruction describes:
    ///  - which EnemyData to spawn
    ///  - when (timeOffset from wave start)
    ///  - how many to spawn
    ///  - optional spacing between spawns for that instruction
    ///
    /// Designers can create multiple WaveData assets and the WaveManager / EnemySpawner will read them.
    /// </summary>
    [CreateAssetMenu(fileName = "WaveData_", menuName = "Game/Wave Data", order = 110)]
    public class WaveData : ScriptableObject
    {
        [Serializable]
        public class SpawnInstruction
        {
            [Tooltip("Enemy data asset to spawn.")]
            public EnemyData enemy;

            [Tooltip("Time offset (seconds) from the start of the wave when this instruction begins.")]
            public float timeOffset = 0f;

            [Tooltip("How many instances to spawn for this instruction.")]
            public int count = 1;

            [Tooltip("Delay (seconds) between spawns for this instruction. If 0, spawns are instanced at timeOffset.")]
            public float spacing = 0f;

            [Tooltip("Optional additional random spread (seconds) to add/subtract to each spawn time.")]
            public float jitter = 0f;

            [Tooltip("Optional weight used by randomized pickers. Higher weight -> more likely to be chosen.")]
            public float weight = 1f;

            /// <summary>
            /// Helper to compute the scheduled times for this instruction (relative to wave start).
            /// </summary>
            public IEnumerable<float> GetSpawnTimes()
            {
                if (count <= 0)
                    yield break;

                for (int i = 0; i < count; i++)
                {
                    float t = timeOffset + i * spacing;
                    if (jitter != 0f)
                    {
                        float rnd = UnityEngine.Random.Range(-jitter, jitter);
                        t += rnd;
                    }
                    yield return Mathf.Max(0f, t);
                }
            }
        }

        [Header("Wave Metadata")]
        [Tooltip("Friendly name for the wave (for editor/runtime debugging).")]
        public string waveName = "Wave";

        [Tooltip("List of spawn instructions for this wave.")]
        public List<SpawnInstruction> instructions = new List<SpawnInstruction>();

        [Tooltip("If true, the wave will loop (useful for endless modes). Otherwise it runs once.")]
        public bool loop = false;

        [Tooltip("Optional difficulty multiplier applied to enemy stats for this wave (applies at spawn-time).")]
        [Range(0.1f, 5f)]
        public float difficultyMultiplier = 1f;

        /// <summary>
        /// Compute a conservative duration for the wave: max(timeOffset + (count-1)*spacing) across instructions.
        /// Returns 0 if no instructions.
        /// </summary>
        public float GetEstimatedDuration()
        {
            float max = 0f;
            foreach (var inst in instructions)
            {
                if (inst == null) continue;
                if (inst.count <= 0)
                {
                    max = Mathf.Max(max, inst.timeOffset);
                }
                else
                {
                    float lastTime = inst.timeOffset + (inst.count - 1) * inst.spacing;
                    // account for jitter conservatively by adding absolute jitter
                    lastTime += Mathf.Abs(inst.jitter);
                    max = Mathf.Max(max, lastTime);
                }
            }
            return max;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // sanitize and clamp some fields to prevent invalid runtimes
            if (string.IsNullOrEmpty(waveName))
                waveName = name;

            difficultyMultiplier = Mathf.Clamp(difficultyMultiplier, 0.1f, 5f);
            if (instructions == null)
                instructions = new List<SpawnInstruction>();

            for (int i = 0; i < instructions.Count; i++)
            {
                var inst = instructions[i];
                if (inst == null) continue;
                inst.timeOffset = Mathf.Max(0f, inst.timeOffset);
                inst.count = Mathf.Max(0, inst.count);
                inst.spacing = Mathf.Max(0f, inst.spacing);
                inst.jitter = Mathf.Max(0f, inst.jitter);
                inst.weight = Mathf.Max(0f, inst.weight);
            }
        }
#endif
    }
}
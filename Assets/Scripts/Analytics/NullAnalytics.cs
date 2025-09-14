// Assets/Scripts/Analytics/NullAnalytics.cs
using System.Collections.Generic;
using UnityEngine;

namespace Systems.Analytics
{
    /// <summary>
    /// Minimal no-op analytics provider used as a fallback when no real analytics SDK is present.
    /// - Public, non-Mono class so Bootstrapper can instantiate it via reflection.
    /// - Logs events to Console only in the Editor for debugging; otherwise it's a no-op.
    /// </summary>
    public class NullAnalytics : IAnalyticsProvider
    {
        private string _userId;
        private readonly Dictionary<string, string> _userProperties = new Dictionary<string, string>();

        public void LogEvent(string name, IDictionary<string, object> meta = null)
        {
#if UNITY_EDITOR
            string metaStr = "{}";
            if (meta != null)
            {
                var parts = new List<string>();
                foreach (var kv in meta)
                {
                    parts.Add($"{kv.Key}={kv.Value}");
                }
                metaStr = "{" + string.Join(", ", parts) + "}";
            }
            Debug.Log($"[NullAnalytics] Event: {name} Meta: {metaStr}");
#endif
            // No-op in runtime builds (keeps behavior silent and cheap).
        }

        public void LogEvent(string name, string key, object value)
        {
#if UNITY_EDITOR
            Debug.Log($"[NullAnalytics] Event: {name} {key}={value}");
#endif
            // No-op in runtime builds.
        }

        public void SetUserId(string userId)
        {
            _userId = userId;
#if UNITY_EDITOR
            Debug.Log($"[NullAnalytics] SetUserId: {userId}");
#endif
        }

        public void SetUserProperty(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            _userProperties[key] = value;
#if UNITY_EDITOR
            Debug.Log($"[NullAnalytics] SetUserProperty: {key}={value}");
#endif
        }

        public void Flush()
        {
            // Nothing to flush for the null provider.
#if UNITY_EDITOR
            Debug.Log("[NullAnalytics] Flush called.");
#endif
        }
    }
}
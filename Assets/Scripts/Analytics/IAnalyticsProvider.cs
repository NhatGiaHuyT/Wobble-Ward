// Assets/Scripts/Analytics/IAnalyticsProvider.cs
using System.Collections.Generic;

namespace Systems.Analytics
{
    /// <summary>
    /// Contract for analytics/telemetry providers.
    /// Implementations may forward events to Unity Analytics, Firebase, or a custom backend.
    /// Bootstrapper will register a fallback NullAnalytics if no concrete implementation is present.
    /// </summary>
    public interface IAnalyticsProvider
    {
        /// <summary>
        /// Log a named event with optional metadata. Implementations should handle null meta.
        /// </summary>
        /// <param name="name">Event name (e.g., "run_start", "level_up").</param>
        /// <param name="meta">Optional key/value metadata (small payloads only).</param>
        void LogEvent(string name, IDictionary<string, object> meta = null);

        /// <summary>
        /// Convenience overload to log a single key/value pair.
        /// </summary>
        void LogEvent(string name, string key, object value);

        /// <summary>
        /// Set a persistent user identifier (optional).
        /// </summary>
        void SetUserId(string userId);

        /// <summary>
        /// Set a user property / attribute (optional).
        /// </summary>
        void SetUserProperty(string key, string value);

        /// <summary>
        /// Flush any queued telemetry to the provider (may be a no-op for some implementations).
        /// </summary>
        void Flush();
    }
}
// Assets/Scripts/Input/Stroke.cs
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Systems.Input
{
    /// <summary>
    /// Stroke - represents a single pointer/touch stroke.
    /// - Efficient for runtime use: keeps an internal List<Vector2> and exposes read-only accessors (Count + indexer).
    /// - Tracks pointer id, start/end times, duration and cached path length.
    /// - Designed to be reused by calling Reset().
    /// </summary>
    [Serializable]
    public class Stroke
    {
        // Internal backing list of points (in screen-space coordinates by convention).
        // Initial capacity tuned for typical gestures; will grow if needed.
        private List<Vector2> _points = new List<Vector2>(64);

        // Cached total length (world/screen units) of the polyline. Updated incrementally when points are added.
        private float _cachedLength = 0f;

        /// <summary>
        /// Pointer/finger id for this stroke (mouse can use 0).
        /// </summary>
        public int PointerId { get; private set; } = 0;

        /// <summary>
        /// Time.time when stroke began (seconds). May be 0 if not set.
        /// </summary>
        public float StartTime { get; private set; } = 0f;

        public void SetStartTime(float time)
        {
            StartTime = time;
        }

        /// <summary>
        /// Time.time when stroke ended (seconds). May be 0 if not yet ended.
        /// </summary>
        public float EndTime { get; private set; } = 0f;

        /// <summary>
        /// Duration in seconds (EndTime - StartTime). Returns 0 if not ended yet.
        /// </summary>
        public float Duration => Mathf.Max(0f, EndTime - StartTime);

        /// <summary>
        /// Number of points in the stroke.
        /// </summary>
        public int Count => _points.Count;

        /// <summary>
        /// Cached polyline length. Updated on AddPoint.
        /// </summary>
        public float Length => _cachedLength;

        /// <summary>
        /// Quick indexer to get point at index.
        /// </summary>
        public Vector2 this[int index] => _points[index];

        /// <summary>
        /// Provides a copy of the internal points as an array.
        /// Use sparingly to avoid allocations in hot paths.
        /// </summary>
        public Vector2[] ToArray() => _points.ToArray();

        /// <summary>
        /// Begin a stroke: sets pointer id and start time, clears any existing points.
        /// </summary>
        /// <param name="pointerId">Pointer id (mouse=0).</param>
        /// <param name="startTime">Start time (Time.time or test-provided time).</param>
        public void Begin(int pointerId, float startTime)
        {
            PointerId = pointerId;
            StartTime = startTime;
            EndTime = 0f;
            _cachedLength = 0f;
            _points.Clear();
        }

        /// <summary>
        /// Add a sampled point to the stroke. Updates cached length incrementally.
        /// </summary>
        /// <param name="pt">Point in screen coordinates (Vector2).</param>
        public void AddPoint(Vector2 pt)
        {
            if (_points.Count > 0)
            {
                // Incrementally accumulate distance to avoid recomputing entire path.
                _cachedLength += Vector2.Distance(_points[_points.Count - 1], pt);
            }
            _points.Add(pt);
        }

        /// <summary>
        /// End the stroke. Sets EndTime (used to compute Duration).
        /// </summary>
        /// <param name="endTime">Time.time when the stroke ended (or simulated time).</param>
        public void End(float endTime)
        {
            EndTime = endTime;
        }

        /// <summary>
        /// Reset the stroke so it can be reused by input system (clears points and metadata).
        /// </summary>
        public void Reset()
        {
            PointerId = 0;
            StartTime = 0f;
            EndTime = 0f;
            _cachedLength = 0f;
            _points.Clear();
        }

        /// <summary>
        /// Create a new Stroke instance populated from a provided list of points.
        /// Useful for tests to simulate a stroke without using the live InputSystem.
        /// </summary>
        /// <param name="pts">Points (screen-space)</param>
        /// <param name="pointerId">Pointer id</param>
        /// <param name="startTime">Start time for the stroke</param>
        /// <param name="endTime">End time for the stroke</param>
        /// <returns>New Stroke instance</returns>
        public static Stroke FromPoints(IList<Vector2> pts, int pointerId = 0, float startTime = 0f, float endTime = 0.05f)
        {
            var s = new Stroke();
            s.PointerId = pointerId;
            s.StartTime = startTime;
            s.EndTime = endTime;
            s._cachedLength = 0f;
            s._points.Clear();

            if (pts != null)
            {
                for (int i = 0; i < pts.Count; i++)
                {
                    var p = pts[i];
                    if (i > 0)
                        s._cachedLength += Vector2.Distance(pts[i - 1], p);
                    s._points.Add(p);
                }
            }

            return s;
        }

        /// <summary>
        /// Create a deep copy of this Stroke. Useful if you need to hold onto stroke data beyond lifecycle.
        /// </summary>
        public Stroke Clone()
        {
            var s = new Stroke();
            s.PointerId = PointerId;
            s.StartTime = StartTime;
            s.EndTime = EndTime;
            s._cachedLength = _cachedLength;
            s._points = new List<Vector2>(_points);
            return s;
        }

        /// <summary>
        /// Convenience: get the centroid (average) of stroke points. Returns Vector2.zero if empty.
        /// </summary>
        public Vector2 GetCentroid()
        {
            if (_points.Count == 0) return Vector2.zero;
            Vector2 sum = Vector2.zero;
            for (int i = 0; i < _points.Count; i++)
                sum += _points[i];
            return sum / _points.Count;
        }
    }
}
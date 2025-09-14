// Assets/Scripts/Input/IInputSystem.cs
using System;
using UnityEngine;

namespace Systems.Input
{
    /// <summary>
    /// Contract for the game's input/stroke capture system.
    /// - Produces Stroke lifecycle events (Begin, Update, End).
    /// - Provides ability to enable/disable input (useful for UI modal blocking).
    /// - Exposes an input bounds rect (screen-space) so other systems (UI, recognizer) can adapt.
    /// - Includes a test-friendly SimulateStroke method so automated tests can feed strokes without real touches.
    /// 
    /// Note: the concrete Stroke type is declared in Stroke.cs and is expected to live in the same namespace.
    /// </summary>
    public interface IInputSystem
    {
        /// <summary>
        /// Fired when a new stroke begins (first sampled point).
        /// </summary>
        event Action<Stroke> OnStrokeBegin;

        /// <summary>
        /// Fired while a stroke is being drawn; frequent updates during pointer movement.
        /// </summary>
        event Action<Stroke> OnStrokeUpdate;

        /// <summary>
        /// Fired once when the stroke ends (pointer up / touch end). Consumers should treat the stroke as immutable after this event.
        /// </summary>
        event Action<Stroke> OnStrokeEnd;

        /// <summary>
        /// Returns the current input bounds in screen coordinates (x,y,width,height).
        /// Useful for clamping or ignoring strokes that begin outside of gameplay area.
        /// </summary>
        Rect GetInputBounds();

        /// <summary>
        /// Enable or disable input processing. When disabled, no events should be fired.
        /// </summary>
        /// <param name="on">true to enable, false to disable</param>
        void Enable(bool on);

        /// <summary>
        /// Returns whether the input system is currently enabled.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Test helper: simulate a complete stroke (Begin->Update*->End) programmatically.
        /// Useful for unit / integration tests. Implementations should treat this stroke as if the user drew it.
        /// </summary>
        /// <param name="stroke">Stroke data to simulate (points, duration, pointer id).</param>
        void SimulateStroke(Stroke stroke);
    }
}
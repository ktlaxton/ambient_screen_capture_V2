using System;
using System.Collections.Generic;
using AmbientEffectsEngine.Models;

namespace AmbientEffectsEngine.Services.Rendering.Effects
{
    /// <summary>
    /// Interface for visual effects that can be rendered on secondary monitors.
    /// This interface implements the Strategy pattern to allow different effect implementations.
    /// </summary>
    public interface IEffect : IDisposable
    {
        /// <summary>
        /// Gets the unique identifier for this effect type.
        /// </summary>
        string EffectId { get; }

        /// <summary>
        /// Gets the display name for this effect type.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the description for this effect type.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Initializes the effect with the target monitors where it should be rendered.
        /// This method should create and configure all necessary UI elements.
        /// </summary>
        /// <param name="targetMonitors">The monitors where the effect should be displayed (excluding primary)</param>
        void Initialize(IEnumerable<DisplayMonitor> targetMonitors);

        /// <summary>
        /// Updates the effect with new processed data from the data processing service.
        /// This method is called in real-time as new audio and color data becomes available.
        /// </summary>
        /// <param name="data">The processed data containing dominant color and audio intensity</param>
        void UpdateEffect(ProcessedData data);

        /// <summary>
        /// Shows the effect on all target monitors.
        /// </summary>
        void Show();

        /// <summary>
        /// Hides the effect on all target monitors without disposing resources.
        /// </summary>
        void Hide();
    }
}
using System;
using System.Collections.Generic;
using AmbientEffectsEngine.Services.Rendering.Effects;

namespace AmbientEffectsEngine.Services.Rendering
{
    /// <summary>
    /// Interface for creating and managing effect instances.
    /// </summary>
    public interface IEffectFactory
    {
        /// <summary>
        /// Registers a new effect type with the factory.
        /// </summary>
        /// <param name="effectId">Unique identifier for the effect type</param>
        /// <param name="creator">Factory function to create instances of the effect</param>
        void RegisterEffect(string effectId, Func<IEffect> creator);

        /// <summary>
        /// Creates a new instance of the specified effect type.
        /// </summary>
        /// <param name="effectId">The effect type identifier</param>
        /// <returns>A new instance of the requested effect</returns>
        IEffect CreateEffect(string effectId);

        /// <summary>
        /// Gets all available effect types that can be created by this factory.
        /// </summary>
        /// <returns>Collection of effect identifiers</returns>
        IEnumerable<string> GetAvailableEffectIds();

        /// <summary>
        /// Checks if an effect type is registered with the factory.
        /// </summary>
        /// <param name="effectId">The effect type identifier to check</param>
        /// <returns>True if the effect type is registered, false otherwise</returns>
        bool IsEffectRegistered(string effectId);

        /// <summary>
        /// Unregisters an effect type from the factory.
        /// </summary>
        /// <param name="effectId">The effect type identifier to unregister</param>
        /// <returns>True if the effect was unregistered, false if it wasn't registered</returns>
        bool UnregisterEffect(string effectId);
    }
}
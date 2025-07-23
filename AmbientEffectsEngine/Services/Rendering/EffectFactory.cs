using System;
using System.Collections.Generic;
using System.Linq;
using AmbientEffectsEngine.Services.Rendering.Effects;

namespace AmbientEffectsEngine.Services.Rendering
{
    /// <summary>
    /// Factory for creating and managing effect instances.
    /// Implements the Factory pattern to provide centralized effect creation and registration.
    /// </summary>
    public class EffectFactory : IEffectFactory
    {
        private readonly Dictionary<string, Func<IEffect>> _effectCreators = new();

        public EffectFactory()
        {
            // Register built-in effects
            RegisterEffect("softglow", () => new SoftGlowEffect());
            RegisterEffect("generativevisualizer", () => new GenerativeVisualizerEffect());
        }

        /// <summary>
        /// Registers a new effect type with the factory.
        /// </summary>
        /// <param name="effectId">Unique identifier for the effect type</param>
        /// <param name="creator">Factory function to create instances of the effect</param>
        public void RegisterEffect(string effectId, Func<IEffect> creator)
        {
            if (string.IsNullOrWhiteSpace(effectId))
                throw new ArgumentException("Effect ID cannot be null or empty", nameof(effectId));
            
            if (creator == null)
                throw new ArgumentNullException(nameof(creator));

            _effectCreators[effectId.ToLowerInvariant()] = creator;
        }

        /// <summary>
        /// Creates a new instance of the specified effect type.
        /// </summary>
        /// <param name="effectId">The effect type identifier</param>
        /// <returns>A new instance of the requested effect</returns>
        /// <exception cref="ArgumentException">Thrown when the effect type is not registered</exception>
        public IEffect CreateEffect(string effectId)
        {
            if (string.IsNullOrWhiteSpace(effectId))
                throw new ArgumentException("Effect ID cannot be null or empty", nameof(effectId));

            var normalizedId = effectId.ToLowerInvariant();
            if (!_effectCreators.TryGetValue(normalizedId, out var creator))
            {
                throw new ArgumentException($"Effect type '{effectId}' is not registered", nameof(effectId));
            }

            return creator();
        }

        /// <summary>
        /// Gets all available effect types that can be created by this factory.
        /// </summary>
        /// <returns>Collection of effect identifiers</returns>
        public IEnumerable<string> GetAvailableEffectIds()
        {
            return _effectCreators.Keys.ToList();
        }

        /// <summary>
        /// Checks if an effect type is registered with the factory.
        /// </summary>
        /// <param name="effectId">The effect type identifier to check</param>
        /// <returns>True if the effect type is registered, false otherwise</returns>
        public bool IsEffectRegistered(string effectId)
        {
            if (string.IsNullOrWhiteSpace(effectId))
                return false;

            return _effectCreators.ContainsKey(effectId.ToLowerInvariant());
        }

        /// <summary>
        /// Unregisters an effect type from the factory.
        /// </summary>
        /// <param name="effectId">The effect type identifier to unregister</param>
        /// <returns>True if the effect was unregistered, false if it wasn't registered</returns>
        public bool UnregisterEffect(string effectId)
        {
            if (string.IsNullOrWhiteSpace(effectId))
                return false;

            return _effectCreators.Remove(effectId.ToLowerInvariant());
        }
    }
}
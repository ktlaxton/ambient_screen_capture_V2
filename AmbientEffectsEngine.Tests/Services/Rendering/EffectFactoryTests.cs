using System;
using System.Linq;
using AmbientEffectsEngine.Services.Rendering;
using AmbientEffectsEngine.Services.Rendering.Effects;
using Xunit;

namespace AmbientEffectsEngine.Tests.Services.Rendering
{
    public class EffectFactoryTests
    {
        private readonly EffectFactory _factory;

        public EffectFactoryTests()
        {
            _factory = new EffectFactory();
        }

        [Fact]
        public void Constructor_ShouldRegisterBuiltInEffects()
        {
            // Assert
            var availableEffects = _factory.GetAvailableEffectIds().ToList();
            Assert.Contains("softglow", availableEffects);
            Assert.Contains("generativevisualizer", availableEffects);
        }

        [Fact]
        public void GetAvailableEffectIds_ShouldReturnRegisteredEffects()
        {
            // Act
            var effectIds = _factory.GetAvailableEffectIds().ToList();

            // Assert
            Assert.NotEmpty(effectIds);
            Assert.Contains("softglow", effectIds);
            Assert.Contains("generativevisualizer", effectIds);
        }

        [Fact]
        public void IsEffectRegistered_WithRegisteredEffect_ShouldReturnTrue()
        {
            // Act & Assert
            Assert.True(_factory.IsEffectRegistered("softglow"));
            Assert.True(_factory.IsEffectRegistered("generativevisualizer"));
        }

        [Fact]
        public void IsEffectRegistered_WithUnregisteredEffect_ShouldReturnFalse()
        {
            // Act & Assert
            Assert.False(_factory.IsEffectRegistered("nonexistent"));
            Assert.False(_factory.IsEffectRegistered("unknown"));
        }

        [Fact]
        public void IsEffectRegistered_WithNullOrEmpty_ShouldReturnFalse()
        {
            // Act & Assert
            Assert.False(_factory.IsEffectRegistered(null!));
            Assert.False(_factory.IsEffectRegistered(""));
            Assert.False(_factory.IsEffectRegistered("   "));
        }

        [Fact]
        public void IsEffectRegistered_IsCaseInsensitive()
        {
            // Act & Assert
            Assert.True(_factory.IsEffectRegistered("SoftGlow"));
            Assert.True(_factory.IsEffectRegistered("SOFTGLOW"));
            Assert.True(_factory.IsEffectRegistered("GenerativeVisualizer"));
            Assert.True(_factory.IsEffectRegistered("GENERATIVEVISUALIZER"));
        }

        [Fact]
        public void CreateEffect_WithRegisteredEffect_ShouldReturnCorrectInstance()
        {
            // Act
            var softGlowEffect = _factory.CreateEffect("softglow");
            var generativeVisualizerEffect = _factory.CreateEffect("generativevisualizer");

            // Assert
            Assert.NotNull(softGlowEffect);
            Assert.IsType<SoftGlowEffect>(softGlowEffect);
            Assert.Equal("softglow", softGlowEffect.EffectId);

            Assert.NotNull(generativeVisualizerEffect);
            Assert.IsType<GenerativeVisualizerEffect>(generativeVisualizerEffect);
            Assert.Equal("generativevisualizer", generativeVisualizerEffect.EffectId);

            // Clean up
            softGlowEffect.Dispose();
            generativeVisualizerEffect.Dispose();
        }

        [Fact]
        public void CreateEffect_WithUnregisteredEffect_ShouldThrowArgumentException()
        {
            // Act & Assert
            var exception = Assert.Throws<ArgumentException>(() => _factory.CreateEffect("nonexistent"));
            Assert.Contains("nonexistent", exception.Message);
            Assert.Contains("not registered", exception.Message);
        }

        [Fact]
        public void CreateEffect_WithNullOrEmpty_ShouldThrowArgumentException()
        {
            // Act & Assert
            Assert.Throws<ArgumentException>(() => _factory.CreateEffect(null!));
            Assert.Throws<ArgumentException>(() => _factory.CreateEffect(""));
            Assert.Throws<ArgumentException>(() => _factory.CreateEffect("   "));
        }

        [Fact]
        public void CreateEffect_IsCaseInsensitive()
        {
            // Act
            var effect1 = _factory.CreateEffect("SoftGlow");
            var effect2 = _factory.CreateEffect("SOFTGLOW");

            // Assert
            Assert.NotNull(effect1);
            Assert.NotNull(effect2);
            Assert.Equal("softglow", effect1.EffectId);
            Assert.Equal("softglow", effect2.EffectId);

            // Clean up
            effect1.Dispose();
            effect2.Dispose();
        }

        [Fact]
        public void CreateEffect_MultipleInstances_ShouldCreateSeparateInstances()
        {
            // Act
            var effect1 = _factory.CreateEffect("softglow");
            var effect2 = _factory.CreateEffect("softglow");

            // Assert
            Assert.NotNull(effect1);
            Assert.NotNull(effect2);
            Assert.NotSame(effect1, effect2); // Should be different instances

            // Clean up
            effect1.Dispose();
            effect2.Dispose();
        }

        [Fact]
        public void RegisterEffect_WithValidParameters_ShouldAddEffect()
        {
            // Arrange
            var testEffectId = "testeffect";
            Func<IEffect> creator = () => new SoftGlowEffect(); // Using SoftGlowEffect as a mock

            // Act
            _factory.RegisterEffect(testEffectId, creator);

            // Assert
            Assert.True(_factory.IsEffectRegistered(testEffectId));
            var availableEffects = _factory.GetAvailableEffectIds();
            Assert.Contains(testEffectId, availableEffects);

            var createdEffect = _factory.CreateEffect(testEffectId);
            Assert.NotNull(createdEffect);
            createdEffect.Dispose();
        }

        [Fact]
        public void RegisterEffect_WithNullEffectId_ShouldThrowArgumentException()
        {
            // Arrange
            Func<IEffect> creator = () => new SoftGlowEffect();

            // Act & Assert
            Assert.Throws<ArgumentException>(() => _factory.RegisterEffect(null!, creator));
        }

        [Fact]
        public void RegisterEffect_WithEmptyEffectId_ShouldThrowArgumentException()
        {
            // Arrange
            Func<IEffect> creator = () => new SoftGlowEffect();

            // Act & Assert
            Assert.Throws<ArgumentException>(() => _factory.RegisterEffect("", creator));
            Assert.Throws<ArgumentException>(() => _factory.RegisterEffect("   ", creator));
        }

        [Fact]
        public void RegisterEffect_WithNullCreator_ShouldThrowArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => _factory.RegisterEffect("testeffect", null!));
        }

        [Fact]
        public void RegisterEffect_WithExistingId_ShouldOverwriteEffect()
        {
            // Arrange
            var testEffectId = "testeffect";
            Func<IEffect> creator1 = () => new SoftGlowEffect();
            Func<IEffect> creator2 = () => new GenerativeVisualizerEffect();

            // Act
            _factory.RegisterEffect(testEffectId, creator1);
            var effect1 = _factory.CreateEffect(testEffectId);
            
            _factory.RegisterEffect(testEffectId, creator2); // Overwrite
            var effect2 = _factory.CreateEffect(testEffectId);

            // Assert
            Assert.IsType<SoftGlowEffect>(effect1);
            Assert.IsType<GenerativeVisualizerEffect>(effect2);

            // Clean up
            effect1.Dispose();
            effect2.Dispose();
        }

        [Fact]
        public void UnregisterEffect_WithRegisteredEffect_ShouldReturnTrueAndRemoveEffect()
        {
            // Arrange
            var testEffectId = "testeffect";
            _factory.RegisterEffect(testEffectId, () => new SoftGlowEffect());
            Assert.True(_factory.IsEffectRegistered(testEffectId));

            // Act
            var result = _factory.UnregisterEffect(testEffectId);

            // Assert
            Assert.True(result);
            Assert.False(_factory.IsEffectRegistered(testEffectId));
            var availableEffects = _factory.GetAvailableEffectIds();
            Assert.DoesNotContain(testEffectId, availableEffects);
        }

        [Fact]
        public void UnregisterEffect_WithUnregisteredEffect_ShouldReturnFalse()
        {
            // Act
            var result = _factory.UnregisterEffect("nonexistent");

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void UnregisterEffect_WithNullOrEmpty_ShouldReturnFalse()
        {
            // Act & Assert
            Assert.False(_factory.UnregisterEffect(null!));
            Assert.False(_factory.UnregisterEffect(""));
            Assert.False(_factory.UnregisterEffect("   "));
        }

        [Fact]
        public void UnregisterEffect_IsCaseInsensitive()
        {
            // Arrange
            var testEffectId = "TestEffect";
            _factory.RegisterEffect(testEffectId, () => new SoftGlowEffect());
            Assert.True(_factory.IsEffectRegistered(testEffectId));

            // Act
            var result = _factory.UnregisterEffect("testeffect"); // Lower case

            // Assert
            Assert.True(result);
            Assert.False(_factory.IsEffectRegistered(testEffectId));
        }

        [Fact]
        public void UnregisterEffect_WithBuiltInEffect_ShouldRemoveIt()
        {
            // Arrange
            Assert.True(_factory.IsEffectRegistered("softglow"));

            // Act
            var result = _factory.UnregisterEffect("softglow");

            // Assert
            Assert.True(result);
            Assert.False(_factory.IsEffectRegistered("softglow"));
        }
    }
}
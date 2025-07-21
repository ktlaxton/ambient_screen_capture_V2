using Xunit;
using AmbientEffectsEngine.Services;

namespace AmbientEffectsEngine.Tests.Services;

public class SystemTrayServiceTests
{
    [Fact]
    public void SystemTrayService_CanBeInstantiated()
    {
        // Arrange & Act
        var service = new SystemTrayService();
        
        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void Initialize_ShouldNotThrow()
    {
        // Arrange
        var service = new SystemTrayService();
        
        // Act & Assert
        var exception = Record.Exception(() => service.Initialize());
        Assert.Null(exception);
    }

    [Fact]
    public void Shutdown_ShouldNotThrow()
    {
        // Arrange
        var service = new SystemTrayService();
        service.Initialize();
        
        // Act & Assert
        var exception = Record.Exception(() => service.Shutdown());
        Assert.Null(exception);
    }
}
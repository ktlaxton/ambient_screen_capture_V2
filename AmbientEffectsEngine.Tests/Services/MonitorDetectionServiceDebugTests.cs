using System;
using System.Linq;
using System.Threading.Tasks;
using AmbientEffectsEngine.Services;
using Xunit;
using Xunit.Abstractions;

namespace AmbientEffectsEngine.Tests.Services
{
    public class MonitorDetectionServiceDebugTests : IDisposable
    {
        private readonly MonitorDetectionService _monitorDetectionService;
        private readonly ITestOutputHelper _output;

        public MonitorDetectionServiceDebugTests(ITestOutputHelper output)
        {
            _monitorDetectionService = new MonitorDetectionService();
            _output = output;
        }

        [Fact]
        public async Task Debug_GetConnectedMonitorsAsync()
        {
            try
            {
                // Act
                var monitors = await _monitorDetectionService.GetConnectedMonitorsAsync();
                var monitorList = monitors.ToList();

                _output.WriteLine($"Monitor count: {monitorList.Count}");
                
                foreach (var monitor in monitorList)
                {
                    _output.WriteLine($"Monitor - Id: {monitor.Id}, Name: {monitor.Name}, IsPrimary: {monitor.IsPrimary}");
                }

                // This test is purely for debugging - let's see what we get
                Assert.True(true); // Always pass, just for output
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Exception occurred: {ex.Message}");
                _output.WriteLine($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public void Dispose()
        {
            _monitorDetectionService?.Dispose();
        }
    }
}
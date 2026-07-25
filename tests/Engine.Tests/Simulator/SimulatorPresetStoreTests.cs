#if SIMULATOR_ENABLED
using System.IO;
using AmbientFx.Simulator;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// UX redesign (preset store): name sanitization is Windows-safe, and save/list/load round-trips a
/// full-scene scenario through a real (temp) directory without ever touching the default AppData path.
/// </summary>
public sealed class SimulatorPresetStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ambientfx-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Theory]
    [InlineData("My Preset", "My Preset")]                    // spaces are fine
    [InlineData(@"a/b\c:d*e?f", "a-b-c-d-e-f")]               // invalid chars become '-'
    [InlineData("  padded  ", "padded")]
    [InlineData("trailing.dots..", "trailing.dots")]
    [InlineData("", "preset")]
    [InlineData("   ", "preset")]
    [InlineData("CON", "CON-preset")]                          // reserved device name
    [InlineData("com1", "com1-preset")]
    public void SanitizeFileName_ProducesSafeStems(string input, string expected) =>
        Assert.Equal(expected, SimulatorPresetStore.SanitizeFileName(input));

    [Fact]
    public void SanitizeFileName_CapsLengthAt60()
    {
        string result = SimulatorPresetStore.SanitizeFileName(new string('x', 200));
        Assert.Equal(60, result.Length);
    }

    [Fact]
    public void SaveListLoad_RoundTripsAFullScene()
    {
        var scene = SimulatorScenario.Fallback();
        scene.ActiveEffectId = "fire";
        scene.Monitors[0].Effect = "rain";

        Assert.True(SimulatorPresetStore.Save(scene, "Desk: three-wide", NullLogger.Instance, _dir));

        var entry = Assert.Single(SimulatorPresetStore.List(_dir));
        Assert.Equal("Desk- three-wide", entry.Name); // ':' sanitized
        Assert.Equal(entry.Name, scene.Name);         // file stem and content agree

        var loaded = SimulatorPresetStore.TryLoad(entry.Path, NullLogger.Instance);
        Assert.NotNull(loaded);
        Assert.Equal("fire", loaded!.ActiveEffectId);
        Assert.Equal("rain", loaded.Monitors[0].Effect);
    }

    [Fact]
    public void Save_SameName_Overwrites()
    {
        var first = SimulatorScenario.Fallback();
        first.ActiveEffectId = "fire";
        var second = SimulatorScenario.Fallback();
        second.ActiveEffectId = "ripple";

        Assert.True(SimulatorPresetStore.Save(first, "dup", NullLogger.Instance, _dir));
        Assert.True(SimulatorPresetStore.Save(second, "dup", NullLogger.Instance, _dir));

        var entry = Assert.Single(SimulatorPresetStore.List(_dir));
        Assert.Equal("ripple", SimulatorPresetStore.TryLoad(entry.Path, NullLogger.Instance)!.ActiveEffectId);
    }

    [Fact]
    public void List_MissingDirectory_ReturnsEmpty() =>
        Assert.Empty(SimulatorPresetStore.List(Path.Combine(_dir, "nope")));

    [Fact]
    public void TryLoad_MissingFile_ReturnsNull() =>
        Assert.Null(SimulatorPresetStore.TryLoad(Path.Combine(_dir, "ghost.json"), NullLogger.Instance));
}
#endif

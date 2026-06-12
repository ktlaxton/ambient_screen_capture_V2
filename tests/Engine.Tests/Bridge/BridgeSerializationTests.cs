using System.Text.Json;
using AmbientFx.Bridge;
using AmbientFx.Models;
using Xunit;

namespace AmbientFx.Engine.Tests.Bridge;

/// <summary>
/// Spec section 5.3: the bridge JSON schema is a versioned contract mirrored in
/// web/src/shared/bridge.ts. These tests pin the exact wire key names.
/// </summary>
public sealed class BridgeSerializationTests
{
    private static string[] SortedKeys(JsonElement obj) =>
        obj.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void FrameEnvelope_SerializesToExactSpecSchema()
    {
        var payload = new FramePayload
        {
            T = 1234567.89,
            Edges = new EdgeColors
            {
                Top = new[] { new[] { 1, 2, 3 }, new[] { 4, 5, 6 } },
                Bottom = new[] { new[] { 7, 8, 9 }, new[] { 10, 11, 12 } },
                Left = new[] { new[] { 13, 14, 15 } },
                Right = new[] { new[] { 16, 17, 18 } },
            },
            Dominant = new[] { 200, 100, 50 },
            Audio = new AudioData { Intensity = 0.5f, Bands = new[] { 0.1f, 0.9f, 0.25f } },
        };

        string json = BridgeJson.Serialize(new OutboundEnvelope<FramePayload>("frame", payload));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Envelope: exactly { type, payload }.
        Assert.Equal(new[] { "payload", "type" }, SortedKeys(root));
        Assert.Equal("frame", root.GetProperty("type").GetString());

        var pl = root.GetProperty("payload");
        Assert.Equal(new[] { "audio", "dominant", "edges", "t" }, SortedKeys(pl));
        Assert.Equal(1234567.89, pl.GetProperty("t").GetDouble());

        var edges = pl.GetProperty("edges");
        Assert.Equal(new[] { "bottom", "left", "right", "top" }, SortedKeys(edges));

        // Every edge zone color is an [r,g,b] array of 3 ints.
        foreach (string side in new[] { "top", "bottom", "left", "right" })
        {
            var arr = edges.GetProperty(side);
            Assert.Equal(JsonValueKind.Array, arr.ValueKind);
            Assert.True(arr.GetArrayLength() > 0);
            foreach (var color in arr.EnumerateArray())
            {
                Assert.Equal(3, color.GetArrayLength());
                foreach (var channel in color.EnumerateArray())
                {
                    Assert.Equal(JsonValueKind.Number, channel.ValueKind);
                    Assert.InRange(channel.GetInt32(), 0, 255);
                }
            }
        }

        Assert.Equal(2, edges.GetProperty("top").GetArrayLength());
        Assert.Equal(1, edges.GetProperty("top")[0][0].GetInt32());
        Assert.Equal(6, edges.GetProperty("top")[1][2].GetInt32());
    }

    [Fact]
    public void Frame_DominantAndAudio_MatchSpecSchema()
    {
        var payload = new FramePayload
        {
            Dominant = new[] { 200, 100, 50 },
            Audio = new AudioData { Intensity = 0.5f, Bands = new[] { 0.1f, 0.9f, 0.25f } },
        };

        string json = BridgeJson.Serialize(new OutboundEnvelope<FramePayload>("frame", payload));
        using var doc = JsonDocument.Parse(json);
        var pl = doc.RootElement.GetProperty("payload");

        var dominant = pl.GetProperty("dominant");
        Assert.Equal(3, dominant.GetArrayLength());
        Assert.Equal(200, dominant[0].GetInt32());
        Assert.Equal(100, dominant[1].GetInt32());
        Assert.Equal(50, dominant[2].GetInt32());

        var audio = pl.GetProperty("audio");
        Assert.Equal(new[] { "bands", "intensity" }, SortedKeys(audio));
        Assert.Equal(0.5, audio.GetProperty("intensity").GetDouble(), precision: 6);

        var bands = audio.GetProperty("bands");
        Assert.Equal(3, bands.GetArrayLength());
        Assert.Equal(0.1, bands[0].GetDouble(), precision: 6);
        Assert.Equal(0.9, bands[1].GetDouble(), precision: 6);
        Assert.Equal(0.25, bands[2].GetDouble(), precision: 6);
    }

    [Fact]
    public void StatusEnvelope_SerializesLevelAndMessage()
    {
        string json = BridgeJson.Serialize(new OutboundEnvelope<StatusPayload>(
            MessageTypes.Status, new StatusPayload { Level = "error", Message = "capture failed" }));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("status", root.GetProperty("type").GetString());
        var pl = root.GetProperty("payload");
        Assert.Equal(new[] { "level", "message" }, SortedKeys(pl));
        Assert.Equal("error", pl.GetProperty("level").GetString());
        Assert.Equal("capture failed", pl.GetProperty("message").GetString());
    }

    [Fact]
    public void MonitorInfo_Serialization_ExcludesHMonitorEntirely()
    {
        var monitor = new MonitorInfo
        {
            Id = @"\\.\DISPLAY1",
            Name = "Dell U2720Q",
            X = -1920,
            Y = 0,
            Width = 1920,
            Height = 1080,
            IsPrimary = true,
            HMonitor = (nint)987654321, // native handle must never cross the bridge
        };

        string json = BridgeJson.Serialize(monitor);

        Assert.DoesNotContain("hmonitor", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("987654321", json); // the handle value itself

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(
            new[] { "height", "id", "isPrimary", "name", "width", "x", "y" },
            SortedKeys(root));
        Assert.Equal(@"\\.\DISPLAY1", root.GetProperty("id").GetString());
        Assert.Equal("Dell U2720Q", root.GetProperty("name").GetString());
        Assert.Equal(-1920, root.GetProperty("x").GetInt32());
        Assert.Equal(0, root.GetProperty("y").GetInt32());
        Assert.Equal(1920, root.GetProperty("width").GetInt32());
        Assert.Equal(1080, root.GetProperty("height").GetInt32());
        Assert.True(root.GetProperty("isPrimary").GetBoolean());
    }

    [Fact]
    public void MonitorsPayload_RoundTrips_WithoutHMonitor()
    {
        var payload = new MonitorsPayload
        {
            Monitors = { new MonitorInfo { Id = @"\\.\DISPLAY1", HMonitor = 42 } },
        };

        string json = BridgeJson.Serialize(new OutboundEnvelope<MonitorsPayload>(
            MessageTypes.Monitors, payload));

        Assert.DoesNotContain("hmonitor", json, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(json);
        var monitors = doc.RootElement.GetProperty("payload").GetProperty("monitors");
        Assert.Equal(1, monitors.GetArrayLength());
        Assert.Equal(@"\\.\DISPLAY1", monitors[0].GetProperty("id").GetString());
    }
}

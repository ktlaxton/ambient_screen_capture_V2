using System.Text.Json;
using AmbientFx.Bridge;
using Xunit;

namespace AmbientFx.Engine.Tests.Bridge;

/// <summary>
/// Web -> engine command parsing (spec 5.3). Parse and PayloadAs must never throw on
/// hostile input — the bridge receives arbitrary strings from the WebView2 layer.
/// </summary>
public sealed class CommandParserTests
{
    /// <summary>Serializes the envelope exactly as web/src/shared/bridge.ts would post it.</summary>
    private static CommandEnvelope ParseEnvelope(string type, object? payload)
    {
        string json = BridgeJson.Serialize(new { type, payload });
        var env = CommandParser.Parse(json);
        Assert.NotNull(env);
        Assert.Equal(type, env!.Type);
        return env;
    }

    // ---------------------------------------------------------------- Parse

    [Fact]
    public void Parse_ValidEnvelope_ReturnsTypeAndPayload()
    {
        var env = CommandParser.Parse("""{"type":"setEnabled","payload":{"enabled":true}}""");

        Assert.NotNull(env);
        Assert.Equal("setEnabled", env!.Type);
        Assert.Equal(JsonValueKind.Object, env.Payload.ValueKind);
    }

    [Theory]
    [InlineData("garbage not json")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ truncated")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("\"just a string\"")]
    public void Parse_MalformedInput_ReturnsNull(string input)
    {
        Assert.Null(CommandParser.Parse(input));
    }

    [Fact]
    public void Parse_Null_ReturnsNull()
    {
        Assert.Null(CommandParser.Parse(null!));
    }

    [Theory]
    [InlineData("""{"payload":{"enabled":true}}""")] // no type at all
    [InlineData("""{"type":"","payload":{}}""")]      // empty type
    [InlineData("""{"type":null,"payload":{}}""")]    // null type
    public void Parse_MissingOrEmptyType_ReturnsNull(string input)
    {
        Assert.Null(CommandParser.Parse(input));
    }

    // ---------------------------------------------------------------- PayloadAs

    [Fact]
    public void PayloadAs_AbsentPayload_ReturnsDefaultInstance_NotNull()
    {
        var env = CommandParser.Parse("""{"type":"setEnabled"}""");

        Assert.NotNull(env);
        Assert.Equal(JsonValueKind.Undefined, env!.Payload.ValueKind);

        var cmd = env.PayloadAs<SetEnabledCmd>();
        Assert.NotNull(cmd);
        Assert.False(cmd!.Enabled); // default instance
    }

    [Fact]
    public void PayloadAs_ExplicitNullPayload_ReturnsDefaultInstance()
    {
        var env = CommandParser.Parse("""{"type":"setEnabled","payload":null}""");

        var cmd = env!.PayloadAs<SetEnabledCmd>();
        Assert.NotNull(cmd);
        Assert.False(cmd!.Enabled);
    }

    [Fact]
    public void PayloadAs_SetGlobal_PartialUpdate_OnlyIntensitySet()
    {
        var env = CommandParser.Parse("""{"type":"setGlobal","payload":{"intensity":0.5}}""");

        var cmd = env!.PayloadAs<SetGlobalCmd>();
        Assert.NotNull(cmd);
        Assert.Equal(0.5f, cmd!.Intensity);
        Assert.Null(cmd.Smoothing);
        Assert.Null(cmd.Brightness);
        Assert.Null(cmd.AudioSensitivity);
        Assert.Null(cmd.MaxFps);
    }

    [Fact]
    public void PayloadAs_SetEffect_MonitorIdAbsent_IsNull()
    {
        var env = CommandParser.Parse("""{"type":"setEffect","payload":{"effectId":"plasma"}}""");

        var cmd = env!.PayloadAs<SetEffectCmd>();
        Assert.NotNull(cmd);
        Assert.Null(cmd!.MonitorId); // "apply to all targets" semantics
        Assert.Equal("plasma", cmd.EffectId);
    }

    [Theory]
    [InlineData("""{"type":"setEnabled","payload":"a string, not an object"}""")]
    [InlineData("""{"type":"setEnabled","payload":12345}""")]
    [InlineData("""{"type":"setEnabled","payload":[1,2,3]}""")]
    public void PayloadAs_TypeMismatchedPayload_ReturnsNull_NoThrow(string input)
    {
        var env = CommandParser.Parse(input);
        Assert.NotNull(env);
        Assert.Null(env!.PayloadAs<SetEnabledCmd>());
    }

    // ---------------------------------------------------------------- round-trip every command type

    [Fact]
    public void RoundTrip_SetEnabled()
    {
        var env = ParseEnvelope(CommandTypes.SetEnabled, new { enabled = true });
        Assert.True(env.PayloadAs<SetEnabledCmd>()!.Enabled);
    }

    [Fact]
    public void RoundTrip_SetSourceMonitor()
    {
        var env = ParseEnvelope(CommandTypes.SetSourceMonitor, new { monitorId = @"\\.\DISPLAY1" });
        Assert.Equal(@"\\.\DISPLAY1", env.PayloadAs<SetSourceMonitorCmd>()!.MonitorId);
    }

    [Fact]
    public void RoundTrip_SetTargetMonitors()
    {
        var env = ParseEnvelope(
            CommandTypes.SetTargetMonitors,
            new { monitorIds = new[] { @"\\.\DISPLAY2", @"\\.\DISPLAY3" } });
        var cmd = env.PayloadAs<SetTargetMonitorsCmd>();
        Assert.Equal(new List<string> { @"\\.\DISPLAY2", @"\\.\DISPLAY3" }, cmd!.MonitorIds);
    }

    [Fact]
    public void RoundTrip_SetEffect()
    {
        var env = ParseEnvelope(
            CommandTypes.SetEffect, new { monitorId = @"\\.\DISPLAY2", effectId = "plasma" });
        var cmd = env.PayloadAs<SetEffectCmd>();
        Assert.Equal(@"\\.\DISPLAY2", cmd!.MonitorId);
        Assert.Equal("plasma", cmd.EffectId);
    }

    [Fact]
    public void RoundTrip_SetEffectParams()
    {
        var env = ParseEnvelope(
            CommandTypes.SetEffectParams,
            new
            {
                effectId = "plasma",
                @params = new { speed = 0.4, palette = "neon", mirrored = false },
            });

        var cmd = env.PayloadAs<SetEffectParamsCmd>();
        Assert.Equal("plasma", cmd!.EffectId);
        Assert.Equal(0.4, cmd.Params["speed"].GetDouble());
        Assert.Equal("neon", cmd.Params["palette"].GetString());
        Assert.False(cmd.Params["mirrored"].GetBoolean());
    }

    [Fact]
    public void RoundTrip_SetGlobal_AllFields()
    {
        var env = ParseEnvelope(
            CommandTypes.SetGlobal,
            new { intensity = 0.9, smoothing = 0.2, brightness = 0.8, audioSensitivity = 0.6, maxFps = 30 });

        var cmd = env.PayloadAs<SetGlobalCmd>();
        Assert.Equal(0.9f, cmd!.Intensity);
        Assert.Equal(0.2f, cmd.Smoothing);
        Assert.Equal(0.8f, cmd.Brightness);
        Assert.Equal(0.6f, cmd.AudioSensitivity);
        Assert.Equal(30, cmd.MaxFps);
    }

    [Fact]
    public void RoundTrip_SavePreset()
    {
        var env = ParseEnvelope(CommandTypes.SavePreset, new { name = "Gaming" });
        Assert.Equal("Gaming", env.PayloadAs<PresetCmd>()!.Name);
    }

    [Fact]
    public void RoundTrip_LoadPreset()
    {
        var env = ParseEnvelope(CommandTypes.LoadPreset, new { name = "Movie" });
        Assert.Equal("Movie", env.PayloadAs<PresetCmd>()!.Name);
    }

    [Fact]
    public void RoundTrip_DeletePreset()
    {
        var env = ParseEnvelope(CommandTypes.DeletePreset, new { name = "Old" });
        Assert.Equal("Old", env.PayloadAs<PresetCmd>()!.Name);
    }

    [Fact]
    public void RoundTrip_SetAutostart()
    {
        var env = ParseEnvelope(CommandTypes.SetAutostart, new { enabled = true });
        Assert.True(env.PayloadAs<SetAutostartCmd>()!.Enabled);
    }

    [Fact]
    public void RoundTrip_SetHotkey()
    {
        var env = ParseEnvelope(
            CommandTypes.SetHotkey, new { action = "toggleEnabled", keys = "Ctrl+Alt+A" });
        var cmd = env.PayloadAs<SetHotkeyCmd>();
        Assert.Equal("toggleEnabled", cmd!.Action);
        Assert.Equal("Ctrl+Alt+A", cmd.Keys);
    }

    [Fact]
    public void RoundTrip_RequestState_EmptyPayload()
    {
        var env = ParseEnvelope(CommandTypes.RequestState, new { });
        Assert.Equal(JsonValueKind.Object, env.Payload.ValueKind);
        // No DTO for requestState; any DTO deserialized from {} is a default instance.
        Assert.NotNull(env.PayloadAs<SetEnabledCmd>());
    }

    [Fact]
    public void RoundTrip_WindowCommand()
    {
        var env = ParseEnvelope(CommandTypes.WindowCommand, new { action = "minimize" });
        Assert.Equal("minimize", env.PayloadAs<WindowCommandCmd>()!.Action);
    }

    [Fact]
    public void RoundTrip_CompleteOnboarding_NoPayload()
    {
        var env = CommandParser.Parse("""{"type":"completeOnboarding"}""");
        Assert.NotNull(env);
        Assert.Equal(CommandTypes.CompleteOnboarding, env!.Type);
        Assert.NotNull(env.PayloadAs<SetEnabledCmd>()); // default instance, never null
    }

    // ---------------------------------------------------------------- contract strings

    [Fact]
    public void CommandTypeConstants_MatchSpecWireNames()
    {
        Assert.Equal("setEnabled", CommandTypes.SetEnabled);
        Assert.Equal("setSourceMonitor", CommandTypes.SetSourceMonitor);
        Assert.Equal("setTargetMonitors", CommandTypes.SetTargetMonitors);
        Assert.Equal("setEffect", CommandTypes.SetEffect);
        Assert.Equal("setEffectParams", CommandTypes.SetEffectParams);
        Assert.Equal("setGlobal", CommandTypes.SetGlobal);
        Assert.Equal("savePreset", CommandTypes.SavePreset);
        Assert.Equal("loadPreset", CommandTypes.LoadPreset);
        Assert.Equal("deletePreset", CommandTypes.DeletePreset);
        Assert.Equal("setAutostart", CommandTypes.SetAutostart);
        Assert.Equal("setHotkey", CommandTypes.SetHotkey);
        Assert.Equal("requestState", CommandTypes.RequestState);
        Assert.Equal("windowCommand", CommandTypes.WindowCommand);
        Assert.Equal("completeOnboarding", CommandTypes.CompleteOnboarding);
    }

    [Fact]
    public void MessageTypeConstants_MatchSpecWireNames()
    {
        Assert.Equal("frame", MessageTypes.Frame);
        Assert.Equal("status", MessageTypes.Status);
        Assert.Equal("config", MessageTypes.Config);
        Assert.Equal("monitors", MessageTypes.Monitors);
        Assert.Equal("windowConfig", MessageTypes.WindowConfig);
    }
}

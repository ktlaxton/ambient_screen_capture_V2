using AmbientFx.Services;
using Xunit;

namespace AmbientFx.Engine.Tests.Signal;

/// <summary>
/// FR10: pure gesture-string parsing for global hotkeys. Only the static
/// <see cref="HotkeyService.TryParseGesture"/> is exercised — no service instance is created and
/// no hotkey is ever registered with the OS.
/// </summary>
public class HotkeyGestureParserTests
{
    // Win32 RegisterHotKey modifier flags (mirrors the service's private constants).
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    // ---------- valid gestures ----------

    [Fact]
    public void TryParseGesture_CtrlAltA_ReturnsExactFlagsAndVk()
    {
        bool ok = HotkeyService.TryParseGesture("Ctrl+Alt+A", out uint modifiers, out uint virtualKey);

        Assert.True(ok);
        Assert.Equal(MOD_CONTROL | MOD_ALT, modifiers); // exactly 0x3
        Assert.Equal(0x41u, virtualKey);                // VK for 'A'
    }

    [Fact]
    public void TryParseGesture_LowercaseWinShiftF2_IsCaseInsensitive()
    {
        bool ok = HotkeyService.TryParseGesture("win+shift+f2", out uint modifiers, out uint virtualKey);

        Assert.True(ok);
        Assert.Equal(MOD_WIN | MOD_SHIFT, modifiers); // exactly 0xC
        Assert.Equal(0x71u, virtualKey);              // VK_F2
    }

    [Fact]
    public void TryParseGesture_WhitespacePaddedCtrlSpace_IsWhitespaceInsensitive()
    {
        bool ok = HotkeyService.TryParseGesture(" Ctrl + Space ", out uint modifiers, out uint virtualKey);

        Assert.True(ok);
        Assert.Equal(MOD_CONTROL, modifiers);
        Assert.Equal(0x20u, virtualKey); // VK_SPACE
    }

    [Fact]
    public void TryParseGesture_SingleFunctionKeyF12_IsValidWithNoModifiers()
    {
        bool ok = HotkeyService.TryParseGesture("F12", out uint modifiers, out uint virtualKey);

        Assert.True(ok);
        Assert.Equal(0u, modifiers);
        Assert.Equal(0x7Bu, virtualKey); // VK_F12 = 0x6F + 12
    }

    [Fact]
    public void TryParseGesture_SingleLetterKey_IsValidWithNoModifiers()
    {
        bool ok = HotkeyService.TryParseGesture("a", out uint modifiers, out uint virtualKey);

        Assert.True(ok);
        Assert.Equal(0u, modifiers);
        Assert.Equal(0x41u, virtualKey);
    }

    [Fact]
    public void TryParseGesture_ModifierAliases_ControlAndWindows_AreAccepted()
    {
        bool ok = HotkeyService.TryParseGesture("Control+Windows+5", out uint modifiers, out uint virtualKey);

        Assert.True(ok);
        Assert.Equal(MOD_CONTROL | MOD_WIN, modifiers);
        Assert.Equal(0x35u, virtualKey); // VK for '5'
    }

    [Theory]
    [InlineData("Ctrl+Shift+Z", MOD_CONTROL | MOD_SHIFT, 0x5Au)] // letter
    [InlineData("Alt+0", MOD_ALT, 0x30u)]                        // digit
    [InlineData("Ctrl+Tab", MOD_CONTROL, 0x09u)]                 // named key via Keys enum
    [InlineData("Ctrl+PageUp", MOD_CONTROL, 0x21u)]              // named key via Keys enum
    [InlineData("Shift+F24", MOD_SHIFT, 0x87u)]                  // function-key upper bound
    [InlineData("Win+D5", MOD_WIN, 0x35u)]                       // Keys.D5 digit alias
    public void TryParseGesture_ValidGesture_ReturnsExpectedModifiersAndVk(string gesture, uint expectedModifiers, uint expectedVk)
    {
        bool ok = HotkeyService.TryParseGesture(gesture, out uint modifiers, out uint virtualKey);

        Assert.True(ok, $"'{gesture}' should parse.");
        Assert.Equal(expectedModifiers, modifiers);
        Assert.Equal(expectedVk, virtualKey);
    }

    // ---------- invalid gestures ----------

    [Theory]
    [InlineData("")]                // empty
    [InlineData("   ")]             // whitespace only
    [InlineData(null)]              // null
    [InlineData("Ctrl+")]           // trailing separator, no key
    [InlineData("Ctrl+Alt")]        // modifiers only, no key
    [InlineData("NotAKey+Q")]       // unknown token
    [InlineData("Ctrl+A+B")]        // two non-modifier keys
    [InlineData("Ctrl+ControlKey")] // a modifier key cannot be the main key
    [InlineData("Shift+LWin")]      // a modifier key cannot be the main key
    [InlineData("12")]              // pure numeric token is rejected (would alias a raw enum value)
    [InlineData("F25")]             // function keys stop at F24
    [InlineData("F0")]              // function keys start at F1
    [InlineData("+")]               // separators only
    public void TryParseGesture_InvalidGesture_ReturnsFalseWithZeroedOutputs(string? gesture)
    {
        bool ok = HotkeyService.TryParseGesture(gesture!, out uint modifiers, out uint virtualKey);

        Assert.False(ok);
        Assert.Equal(0u, modifiers);
        Assert.Equal(0u, virtualKey);
    }
}

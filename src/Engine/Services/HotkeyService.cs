using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace AmbientFx.Services;

/// <summary>
/// Global hotkeys via <c>RegisterHotKey</c> on a hidden message-only window (FR10).
/// Gesture strings look like "Ctrl+Alt+A", "Win+Shift+F2", "Ctrl+Space".
/// <para>
/// Threading: <see cref="Apply"/> must be called on the WPF UI thread (the message-only
/// window is created lazily there and WM_HOTKEY arrives on its message pump), so
/// <see cref="HotkeyPressed"/> is raised on the UI thread.
/// </para>
/// </summary>
public sealed class HotkeyService : IHotkeyService
{
    private const int WM_HOTKEY = 0x0312;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;

    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly ILogger<HotkeyService> _logger;

    /// <summary>Stable id per action name: assigned once, reused across every Apply (ids must stay in 0x0000-0xBFFF).</summary>
    private readonly Dictionary<string, int> _idByAction = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _actionById = new();

    /// <summary>Ids currently registered with the OS (subset of <see cref="_idByAction"/> values).</summary>
    private readonly HashSet<int> _registeredIds = new();

    private HwndSource? _source;
    private int _nextId = 1;
    private bool _disposed;

    /// <inheritdoc />
    public event EventHandler<string>? HotkeyPressed;

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>Never throws: invalid gestures, OS conflicts (error 1409) and window-creation
    /// failures all surface as entries in the returned failed-action list + logs.</remarks>
    public IReadOnlyList<string> Apply(IReadOnlyDictionary<string, string> hotkeysByAction)
    {
        var failed = new List<string>();
        if (hotkeysByAction is null)
        {
            return failed;
        }

        if (_disposed)
        {
            _logger.LogWarning("Apply called on a disposed HotkeyService; ignored");
            return failed;
        }

        if (!TryEnsureSource())
        {
            // No sink window: every bound action fails, but the host keeps running (NFR5).
            failed.AddRange(hotkeysByAction.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key));
            return failed;
        }

        IntPtr handle = _source!.Handle;

        // Re-registering an id does NOT replace the old binding — always unregister first.
        foreach (int id in _registeredIds)
        {
            UnregisterHotKey(handle, id);
        }
        _registeredIds.Clear();

        foreach ((string action, string gesture) in hotkeysByAction)
        {
            if (string.IsNullOrWhiteSpace(gesture))
            {
                continue; // unbound
            }

            if (!TryParseGesture(gesture, out uint modifiers, out uint virtualKey))
            {
                _logger.LogWarning("Hotkey gesture '{Gesture}' for action '{Action}' is invalid", gesture, action);
                failed.Add(action);
                continue;
            }

            int id = GetOrCreateId(action);
            // MOD_NOREPEAT: holding the combo fires once, not repeatedly.
            if (RegisterHotKey(handle, id, MOD_NOREPEAT | modifiers, virtualKey))
            {
                _registeredIds.Add(id);
                _logger.LogInformation("Hotkey '{Gesture}' registered for action '{Action}'", gesture, action);
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ERROR_HOTKEY_ALREADY_REGISTERED)
                {
                    _logger.LogWarning(
                        "Hotkey '{Gesture}' for action '{Action}' is already in use by another application (error 1409)",
                        gesture, action);
                }
                else
                {
                    _logger.LogWarning(
                        "RegisterHotKey failed for '{Gesture}' (action '{Action}'), Win32 error {Error}",
                        gesture, action, error);
                }
                failed.Add(action);
            }
        }

        return failed;
    }

    /// <summary>
    /// Parses a gesture like "Ctrl+Alt+A", "Win+Shift+F2" or "Ctrl+Space" into RegisterHotKey
    /// arguments. Case- and whitespace-insensitive. Pure (no OS calls) — unit-testable.
    /// </summary>
    /// <param name="gesture">Gesture string; modifiers Ctrl/Control, Alt, Shift, Win/Windows.</param>
    /// <param name="modifiers">MOD_* flags (without MOD_NOREPEAT; the service adds that at registration).</param>
    /// <param name="virtualKey">Win32 virtual-key code of the single non-modifier key.</param>
    /// <returns>False (with zeroed outputs) for empty input, no key, two keys, or an unknown token.</returns>
    public static bool TryParseGesture(string gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        bool hasKey = false;
        foreach (string raw in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Replace(" ", string.Empty);
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= MOD_ALT;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                case "win" or "windows":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    if (hasKey || !TryParseKey(token, out virtualKey))
                    {
                        modifiers = 0;
                        virtualKey = 0;
                        return false;
                    }
                    hasKey = true;
                    break;
            }
        }

        if (!hasKey)
        {
            modifiers = 0;
            virtualKey = 0;
            return false;
        }

        return true;
    }

    /// <summary>Parses the single non-modifier key token: A-Z, 0-9, F1-F24, or any named <see cref="WinFormsKeys"/> value.</summary>
    private static bool TryParseKey(string token, out uint virtualKey)
    {
        virtualKey = 0;
        if (token.Length == 0)
        {
            return false;
        }

        // Single character: A-Z / 0-9 map directly to their VK codes.
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                virtualKey = c;
                return true;
            }
            return false;
        }

        // Function keys F1..F24 (VK_F1 = 0x70).
        if ((token[0] is 'f' or 'F') && int.TryParse(token.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
        {
            virtualKey = 0x6F + (uint)fn;
            return true;
        }

        // Named keys via the WinForms Keys enum ("Space", "Tab", "PageUp", "D5", ...).
        // Pure numeric tokens are rejected first — Enum.TryParse would treat them as raw values.
        if (token.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (!Enum.TryParse(token, ignoreCase: true, out WinFormsKeys key))
        {
            return false;
        }

        uint code = (uint)(key & WinFormsKeys.KeyCode);
        if (code == 0 || code > 0xFE)
        {
            return false;
        }

        // A modifier cannot be the gesture's main key.
        if (key is WinFormsKeys.ShiftKey or WinFormsKeys.ControlKey or WinFormsKeys.Menu
            or WinFormsKeys.LShiftKey or WinFormsKeys.RShiftKey
            or WinFormsKeys.LControlKey or WinFormsKeys.RControlKey
            or WinFormsKeys.LMenu or WinFormsKeys.RMenu
            or WinFormsKeys.LWin or WinFormsKeys.RWin)
        {
            return false;
        }

        virtualKey = code;
        return true;
    }

    /// <summary>Returns the stable registration id for an action, allocating the next sequential one on first use.</summary>
    private int GetOrCreateId(string action)
    {
        if (!_idByAction.TryGetValue(action, out int id))
        {
            id = _nextId++;
            _idByAction[action] = id;
            _actionById[id] = action;
        }
        return id;
    }

    /// <summary>Creates the hidden message-only sink window on first use. UI thread only.</summary>
    private bool TryEnsureSource()
    {
        if (_source is not null)
        {
            return true;
        }

        try
        {
            var parameters = new HwndSourceParameters("AmbientFxHotkeySink")
            {
                ParentWindow = HWND_MESSAGE, // message-only: invisible, never in the taskbar/alt-tab
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create the hotkey message window; global hotkeys are unavailable");
            _source = null;
            return false;
        }
    }

    /// <summary>WM_HOTKEY sink; runs on the UI thread that created the window.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32(); // wParam = hotkey id; lParam low word = modifiers, high word = VK
            if (_registeredIds.Contains(id) && _actionById.TryGetValue(id, out string? action))
            {
                try
                {
                    HotkeyPressed?.Invoke(this, action);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "A HotkeyPressed subscriber threw for action '{Action}'", action);
                }
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_source is not null)
        {
            foreach (int id in _registeredIds)
            {
                UnregisterHotKey(_source.Handle, id);
            }
            _registeredIds.Clear();

            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

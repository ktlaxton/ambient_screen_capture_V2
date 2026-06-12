namespace AmbientFx.Services;

/// <summary>
/// Global hotkeys via RegisterHotKey on a hidden message window.
/// Gesture strings look like "Ctrl+Alt+A" (modifiers: Ctrl, Alt, Shift, Win).
/// </summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>
    /// Unregisters everything and registers the given action->gesture map (empty gesture = unbound).
    /// Returns the actions whose registration FAILED (conflict/invalid) so the caller can toast.
    /// Must be called on the UI thread.
    /// </summary>
    IReadOnlyList<string> Apply(IReadOnlyDictionary<string, string> hotkeysByAction);

    /// <summary>Raised on the UI thread with the action name (see HotkeyActions).</summary>
    event EventHandler<string>? HotkeyPressed;
}

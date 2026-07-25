#if SIMULATOR_ENABLED
namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10 UX redesign). Pure placement math for the floating per-monitor card (and
/// the peripheral mini-card): docks the card beside its monitor without leaving the canvas.
/// Extracted static (the <see cref="SimulatorLayoutMath"/> precedent) so the flip/clamp rules are
/// unit-testable without a WPF window. Compiled out of Release.
/// </summary>
public static class SimulatorCardMath
{
    /// <summary>
    /// Places a card of (<paramref name="cardW"/> × <paramref name="cardH"/>) canvas pixels next to
    /// <paramref name="monitor"/> (already in canvas pixels, i.e. a <see cref="SimulatorLayoutMath.CanvasLayout.Place"/>
    /// result). Preference order: right of the monitor (+<paramref name="gap"/>), flip to the left on
    /// right-edge overflow, and when both sides overflow, overlay hugging the monitor's right inner
    /// edge. The top aligns with the monitor's top, clamped so the card never leaves
    /// [0, <paramref name="canvasH"/>] (a canvas shorter than the card pins to 0).
    /// </summary>
    public static SimulatorLayoutMath.Placed PlaceCard(
        SimulatorLayoutMath.Placed monitor,
        double cardW,
        double cardH,
        double canvasW,
        double canvasH,
        double gap)
    {
        double left = monitor.Left + monitor.Width + gap;
        if (left + cardW > canvasW)
        {
            double flipped = monitor.Left - gap - cardW;
            if (flipped >= 0)
            {
                left = flipped;
            }
            else
            {
                left = monitor.Left + monitor.Width - cardW - gap;
                left = System.Math.Min(left, canvasW - cardW);
                left = System.Math.Max(0, left);
            }
        }

        double top = System.Math.Max(0, System.Math.Min(monitor.Top, canvasH - cardH));

        return new SimulatorLayoutMath.Placed(left, top, cardW, cardH);
    }
}
#endif

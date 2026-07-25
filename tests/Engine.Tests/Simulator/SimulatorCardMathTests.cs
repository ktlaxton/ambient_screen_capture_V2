#if SIMULATOR_ENABLED
using AmbientFx.Simulator;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// UX redesign (floating monitor card): the pure dock-beside-the-monitor placement — right by
/// preference, flip left on overflow, overlay when both sides overflow, always clamped to the canvas.
/// </summary>
public sealed class SimulatorCardMathTests
{
    private const double CanvasW = 1200;
    private const double CanvasH = 800;
    private const double CardW = 280;
    private const double CardH = 300;
    private const double Gap = 10;

    private static SimulatorLayoutMath.Placed Place(double left, double top, double w, double h) => new(left, top, w, h);

    [Fact]
    public void PrefersTheRightSide()
    {
        var card = SimulatorCardMath.PlaceCard(Place(100, 150, 400, 300), CardW, CardH, CanvasW, CanvasH, Gap);

        Assert.Equal(510, card.Left); // 100 + 400 + 10
        Assert.Equal(150, card.Top);  // aligned with the monitor top
    }

    [Fact]
    public void FlipsLeftWhenTheRightOverflows()
    {
        var card = SimulatorCardMath.PlaceCard(Place(700, 150, 400, 300), CardW, CardH, CanvasW, CanvasH, Gap);

        Assert.Equal(410, card.Left); // 700 - 10 - 280
    }

    [Fact]
    public void OverlaysInsideWhenBothSidesOverflow()
    {
        // Monitor spans nearly the whole canvas: no room on either side.
        var card = SimulatorCardMath.PlaceCard(Place(50, 100, 1120, 600), CardW, CardH, CanvasW, CanvasH, Gap);

        Assert.Equal(880, card.Left); // 50 + 1120 - 280 - 10: hugging the monitor's right inner edge
        Assert.True(card.Left >= 0 && card.Left + card.Width <= CanvasW);
    }

    [Fact]
    public void ClampsTopIntoTheCanvas()
    {
        var above = SimulatorCardMath.PlaceCard(Place(100, -50, 400, 300), CardW, CardH, CanvasW, CanvasH, Gap);
        Assert.Equal(0, above.Top);

        var below = SimulatorCardMath.PlaceCard(Place(100, 700, 400, 300), CardW, CardH, CanvasW, CanvasH, Gap);
        Assert.Equal(CanvasH - CardH, below.Top);
    }

    [Fact]
    public void TinyCanvas_PinsToOrigin()
    {
        var card = SimulatorCardMath.PlaceCard(Place(0, 0, 100, 100), CardW, CardH, canvasW: 200, canvasH: 200, Gap);

        Assert.Equal(0, card.Top); // canvas shorter than the card: pin to 0, never negative
        Assert.True(card.Left >= 0);
    }
}
#endif

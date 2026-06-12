using AmbientFx.Bridge;

namespace AmbientFx.Processing;

/// <summary>
/// Per-zone exponential moving average over edge colors and the dominant color, to suppress
/// flicker. State is kept as floats internally and only rounded on output. The first call after
/// construction or <see cref="Reset"/> adopts the targets directly; a zone-count change resets
/// just that edge. NOT thread-safe — DataProcessingService serializes all access on its tick.
/// </summary>
public sealed class TemporalSmoother
{
    private const float MinAlpha = 0.08f;
    private const float MaxAlpha = 1f;

    /// <summary>EMA blend factor; defaults to the value for smoothing = 0.5.</summary>
    private float _alpha = ComputeAlpha(0.5f);

    // Per-edge float state, 3 channels (r,g,b) per zone: length = zones * 3. Null = no state yet.
    private float[]? _top;
    private float[]? _bottom;
    private float[]? _left;
    private float[]? _right;
    private float[]? _dominant;

    /// <summary>
    /// Sets the smoothing strength: 0 = no temporal smoothing, 1 = heaviest.
    /// alpha = clamp(1 - 0.92 * smoothing, 0.08, 1). Out-of-range inputs are clamped to [0,1].
    /// </summary>
    public void SetSmoothing(float smoothing01) => _alpha = ComputeAlpha(smoothing01);

    /// <summary>
    /// Blends the target edge colors into the running per-zone state and returns the rounded result.
    /// Edges whose zone count changed since the previous call (or that have no state yet) adopt the
    /// target directly. Always returns fresh arrays; never aliases the input.
    /// </summary>
    public EdgeColors Smooth(EdgeColors target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new EdgeColors
        {
            Top = SmoothEdge(ref _top, target.Top ?? Array.Empty<int[]>()),
            Bottom = SmoothEdge(ref _bottom, target.Bottom ?? Array.Empty<int[]>()),
            Left = SmoothEdge(ref _left, target.Left ?? Array.Empty<int[]>()),
            Right = SmoothEdge(ref _right, target.Right ?? Array.Empty<int[]>()),
        };
    }

    /// <summary>
    /// Blends the target dominant color ([r,g,b]) into the running state and returns the
    /// rounded result as a fresh 3-element array.
    /// </summary>
    public int[] SmoothDominant(int[] target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (_dominant is null)
        {
            _dominant = new float[3];
            for (int i = 0; i < 3; i++)
                _dominant[i] = Channel(target, i);
        }
        else
        {
            float a = _alpha;
            for (int i = 0; i < 3; i++)
                _dominant[i] += a * (Channel(target, i) - _dominant[i]);
        }

        return new[] { RoundChannel(_dominant[0]), RoundChannel(_dominant[1]), RoundChannel(_dominant[2]) };
    }

    /// <summary>Drops all running state; the next Smooth/SmoothDominant adopts its targets directly.</summary>
    public void Reset()
    {
        _top = null;
        _bottom = null;
        _left = null;
        _right = null;
        _dominant = null;
    }

    private int[][] SmoothEdge(ref float[]? state, int[][] target)
    {
        int zones = target.Length;

        if (state is null || state.Length != zones * 3)
        {
            // First frame for this edge, or zone count changed: adopt the target directly.
            state = new float[zones * 3];
            for (int z = 0; z < zones; z++)
            {
                state[z * 3] = Channel(target[z], 0);
                state[z * 3 + 1] = Channel(target[z], 1);
                state[z * 3 + 2] = Channel(target[z], 2);
            }
        }
        else
        {
            float a = _alpha;
            for (int z = 0; z < zones; z++)
            {
                state[z * 3] += a * (Channel(target[z], 0) - state[z * 3]);
                state[z * 3 + 1] += a * (Channel(target[z], 1) - state[z * 3 + 1]);
                state[z * 3 + 2] += a * (Channel(target[z], 2) - state[z * 3 + 2]);
            }
        }

        var output = new int[zones][];
        for (int z = 0; z < zones; z++)
        {
            output[z] = new[]
            {
                RoundChannel(state[z * 3]),
                RoundChannel(state[z * 3 + 1]),
                RoundChannel(state[z * 3 + 2]),
            };
        }

        return output;
    }

    private static float ComputeAlpha(float smoothing01)
    {
        float s = Math.Clamp(smoothing01, 0f, 1f);
        return Math.Clamp(1f - 0.92f * s, MinAlpha, MaxAlpha);
    }

    /// <summary>Defensive channel read: missing/short/null zone arrays read as 0 (black).</summary>
    private static int Channel(int[]? zone, int index) =>
        zone is not null && index < zone.Length ? zone[index] : 0;

    private static int RoundChannel(float value) =>
        Math.Clamp((int)MathF.Round(value, MidpointRounding.AwayFromZero), 0, 255);
}

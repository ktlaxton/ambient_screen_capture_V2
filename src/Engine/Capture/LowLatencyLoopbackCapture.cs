using NAudio.CoreAudioApi;

namespace AmbientFx.Capture;

/// <summary>
/// WASAPI loopback capture with a configurable buffer length.
/// <see cref="NAudio.Wave.WasapiLoopbackCapture"/> hard-codes a 100 ms buffer (~50 ms / ~20 Hz
/// DataAvailable cadence); this subclass reproduces it with a ~30 ms buffer for a ~15 ms / ~66 Hz
/// callback cadence, matching the verified pattern from the NAudio 2.3.0 release/2.x source.
/// </summary>
/// <remarks>
/// Pass a Render-flow <see cref="MMDevice"/>. One instance = one capture session: after
/// <c>Dispose()</c>, construct a fresh instance (and a fresh <see cref="MMDevice"/>) to restart.
/// <c>useEventSync</c> stays <c>false</c> — loopback event handles never signal during silence.
/// </remarks>
internal sealed class LowLatencyLoopbackCapture : WasapiCapture
{
    /// <summary>Creates a loopback capture over the given render device.</summary>
    /// <param name="renderDevice">A Render-flow endpoint (e.g. the default multimedia render device).</param>
    /// <param name="bufferMs">WASAPI buffer length in milliseconds; DataAvailable fires roughly every half of this.</param>
    public LowLatencyLoopbackCapture(MMDevice renderDevice, int bufferMs = 30)
        : base(renderDevice, useEventSync: false, audioBufferMillisecondsLength: bufferMs)
    {
    }

    /// <summary>Adds the loopback flag so the render endpoint's mix is captured.</summary>
    protected override AudioClientStreamFlags GetAudioClientStreamFlags()
        => AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
}

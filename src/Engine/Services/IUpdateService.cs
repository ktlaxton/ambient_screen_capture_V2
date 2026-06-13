namespace AmbientFx.Services;

/// <summary>
/// Auto-update via Velopack (Story 7.4). Checks the configured feed, downloads any newer
/// release in the background, and stages it to apply when the process exits — so the next
/// launch runs the new version.
/// </summary>
public interface IUpdateService
{
    /// <summary>True only when running a Velopack-installed build (dev/portable runs can't update).</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Checks <paramref name="feedUrl"/>, downloads + stages a newer release if one exists.
    /// Returns the staged version string, or null when already up to date.
    /// Throws on network/feed errors (callers decide how to surface them).
    /// </summary>
    Task<string?> CheckAndStageAsync(string feedUrl);
}

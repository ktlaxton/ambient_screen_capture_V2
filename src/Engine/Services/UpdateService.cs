using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace AmbientFx.Services;

/// <inheritdoc cref="IUpdateService"/>
public sealed class UpdateService : IUpdateService
{
    /// <summary>Used when ApplicationSettings.UpdateFeedUrl is blank.</summary>
    public const string DefaultFeedUrl = "https://github.com/ktlaxton/ambient_screen_capture_V2";

    private readonly ILogger<UpdateService> _logger;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsSupported
    {
        get
        {
            try
            {
                return CreateManager(DefaultFeedUrl).IsInstalled;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Velopack locator probe failed; updates unsupported in this run");
                return false;
            }
        }
    }

    /// <inheritdoc />
    public async Task<string?> CheckAndStageAsync(string feedUrl)
    {
        var url = string.IsNullOrWhiteSpace(feedUrl) ? DefaultFeedUrl : feedUrl.Trim();
        var manager = CreateManager(url);

        var updateInfo = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (updateInfo is null)
        {
            _logger.LogInformation("Update check: already on the latest version");
            return null;
        }

        var version = updateInfo.TargetFullRelease.Version.ToString();
        _logger.LogInformation("Update {Version} available — downloading", version);
        await manager.DownloadUpdatesAsync(updateInfo).ConfigureAwait(false);

        // Stage the apply for process exit: the next launch runs the new version (AC6).
        manager.WaitExitThenApplyUpdates(updateInfo, silent: true, restart: false);
        _logger.LogInformation("Update {Version} downloaded; it will apply on exit", version);
        return version;
    }

    /// <summary>GitHub repo URLs use the GithubSource (Releases feed); anything else is a static feed.</summary>
    private static UpdateManager CreateManager(string feedUrl) =>
        feedUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase)
            ? new UpdateManager(new GithubSource(feedUrl, accessToken: null, prerelease: false))
            : new UpdateManager(feedUrl);
}

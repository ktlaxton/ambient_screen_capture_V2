using System.IO;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Licensing;

/// <summary>
/// Holds the app's current entitlement (Story 9.1). Thin, synchronous, offline: validation
/// is pure crypto against the embedded public key. The coordinator owns persistence of the
/// key string (in ApplicationSettings); this service owns "what does it entitle".
/// </summary>
public interface ILicenseService
{
    /// <summary>The current entitlement; <see cref="LicenseInfo.Free"/> until a valid key is applied.</summary>
    LicenseInfo Current { get; }

    /// <summary>
    /// Validates and (when valid or empty) adopts a key. An empty key returns to the free
    /// edition; an invalid key leaves the current entitlement untouched and reports the error.
    /// </summary>
    LicenseInfo Apply(string? key);
}

/// <inheritdoc />
public sealed class LicenseService : ILicenseService
{
    private readonly ILogger<LicenseService> _logger;
    private readonly string _clockFloorPath;
    private readonly string _publicKey;
    private readonly object _gate = new();
    private volatile LicenseInfo _current = LicenseInfo.Free;

    /// <summary>DI ctor: the clock-floor file lives next to settings.json in %AppData%\AmbientFx.</summary>
    public LicenseService(ILogger<LicenseService> logger)
        : this(logger, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AmbientFx", ".license-clock"))
    {
    }

    /// <summary>Test ctor: arbitrary clock-floor path and (optionally) public key.</summary>
    internal LicenseService(
        ILogger<LicenseService> logger,
        string clockFloorPath,
        string? publicKeySpkiBase64 = null)
    {
        _logger = logger;
        _clockFloorPath = clockFloorPath;
        _publicKey = publicKeySpkiBase64 ?? LicenseValidator.ProductionPublicKey;
    }

    public LicenseInfo Current => _current;

    public LicenseInfo Apply(string? key)
    {
        // Rollback-resistant clock: judge expiry against max(today, highest date ever seen).
        // Winding the system clock back can only LOWER "today", never below the stored floor,
        // so a dated key can't be revived by a casual clock change. Best-effort (a local admin
        // who also edits the floor file can still defeat it — documented in MONETIZATION.md).
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        DateOnly floor = ReadClockFloor() is { } f && f > today ? f : today;

        var result = LicenseValidator.Validate(key, _publicKey, floor);
        lock (_gate)
        {
            if (result.IsValid || string.IsNullOrWhiteSpace(key))
            {
                _current = result;
                _logger.LogInformation("License applied: {Edition}{To}", result.Edition,
                    result.IsPremium ? $" (licensed to {result.LicensedTo})" : string.Empty);
            }
            else
            {
                _logger.LogWarning("License key rejected: {Error}", result.Error);
            }
        }
        AdvanceClockFloor(today); // high-water mark — only ever moves forward
        return result;
    }

    private DateOnly? ReadClockFloor()
    {
        try
        {
            return File.Exists(_clockFloorPath)
                && DateOnly.TryParse(File.ReadAllText(_clockFloorPath).Trim(), out var d)
                ? d : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the license clock floor");
            return null;
        }
    }

    private void AdvanceClockFloor(DateOnly today)
    {
        try
        {
            if (ReadClockFloor() is { } existing && existing >= today)
            {
                return; // never move the high-water mark backward
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_clockFloorPath)!);
            File.WriteAllText(_clockFloorPath, today.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist the license clock floor");
        }
    }
}

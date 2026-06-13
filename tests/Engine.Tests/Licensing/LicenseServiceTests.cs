using System.IO;
using System.Security.Cryptography;
using AmbientFx.Licensing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AmbientFx.Engine.Tests.Licensing;

/// <summary>
/// LicenseService entitlement + the monotonic clock-floor that resists casual clock-rollback on
/// dated keys (Epic 9 review fix). Uses an ephemeral keypair (no production secret in CI) via the
/// internal public-key test ctor.
/// </summary>
public sealed class LicenseServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "AmbientFxLic", Guid.NewGuid().ToString("N"));
    private readonly string _privatePem;
    private readonly string _publicSpki;

    public LicenseServiceTests()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _privatePem = ecdsa.ExportPkcs8PrivateKeyPem();
        _publicSpki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string ClockPath => Path.Combine(_dir, ".license-clock");

    private LicenseService NewService() =>
        new(NullLogger<LicenseService>.Instance, ClockPath, _publicSpki);

    private string Mint(DateOnly? expires = null) =>
        LicenseMinter.Mint(new LicensePayloadDto
        {
            Edition = LicenseEditions.Premium,
            Name = "Kirk",
            Id = "T1",
            Expires = expires,
        }, _privatePem);

    [Fact]
    public void Defaults_to_free_and_applies_a_perpetual_key()
    {
        var svc = NewService();
        Assert.False(svc.Current.IsPremium);

        var info = svc.Apply(Mint());
        Assert.True(info.IsPremium);
        Assert.True(svc.Current.IsPremium);
        Assert.Equal("Kirk", svc.Current.LicensedTo);
    }

    [Fact]
    public void An_invalid_key_leaves_the_current_entitlement_untouched()
    {
        var svc = NewService();
        svc.Apply(Mint()); // premium
        var info = svc.Apply("AFX1.bogus.key");
        Assert.False(info.IsValid);
        Assert.True(svc.Current.IsPremium); // unchanged
    }

    [Fact]
    public void An_empty_key_returns_to_free()
    {
        var svc = NewService();
        svc.Apply(Mint());
        var info = svc.Apply("");
        Assert.False(info.IsPremium);
        Assert.False(svc.Current.IsPremium);
    }

    [Fact]
    public void Applying_a_key_advances_the_clock_floor_to_today()
    {
        NewService().Apply(Mint());
        Assert.True(File.Exists(ClockPath));
        Assert.True(DateOnly.TryParse(File.ReadAllText(ClockPath).Trim(), out var floor));
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), floor);
    }

    [Fact]
    public void A_future_clock_floor_rejects_a_key_that_already_expired_relative_to_it()
    {
        // Simulate "the app has seen 2099-01-01" (e.g. it ran then), then the clock is wound back.
        File.WriteAllText(ClockPath, "2099-01-01");
        // A key that expires 2098 is past the floor → rejected even though the real clock is today.
        var info = NewService().Apply(Mint(expires: new DateOnly(2098, 1, 1)));
        Assert.False(info.IsValid);
        Assert.Contains("expired", info.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_future_clock_floor_still_honors_a_key_that_expires_later_still()
    {
        File.WriteAllText(ClockPath, "2099-01-01");
        var info = NewService().Apply(Mint(expires: new DateOnly(2099, 12, 31)));
        Assert.True(info.IsPremium);
    }

    [Fact]
    public void A_perpetual_key_is_unaffected_by_the_clock_floor()
    {
        File.WriteAllText(ClockPath, "2099-01-01");
        Assert.True(NewService().Apply(Mint()).IsPremium);
    }

    [Fact]
    public void The_clock_floor_never_moves_backward()
    {
        File.WriteAllText(ClockPath, "2099-01-01");
        NewService().Apply(Mint()); // today << 2099 → must NOT overwrite with today
        Assert.Equal("2099-01-01", File.ReadAllText(ClockPath).Trim());
    }

    [Fact]
    public void A_corrupt_clock_floor_file_is_ignored_not_fatal()
    {
        File.WriteAllText(ClockPath, "not-a-date");
        var info = NewService().Apply(Mint()); // falls back to today; never throws
        Assert.True(info.IsPremium);
    }
}

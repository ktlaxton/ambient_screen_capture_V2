using System.Security.Cryptography;
using AmbientFx.Licensing;
using Xunit;

namespace AmbientFx.Engine.Tests.Licensing;

/// <summary>
/// Offline license-key crypto (Story 9.1 / Epic 9): keys minted with an ephemeral test
/// keypair must validate against its public key and fail against any other; tampering,
/// expiry, and malformed input must all degrade to clear errors — never exceptions.
/// </summary>
public sealed class LicenseValidatorTests
{
    private readonly string _privatePem;
    private readonly string _publicSpki;

    public LicenseValidatorTests()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _privatePem = ecdsa.ExportPkcs8PrivateKeyPem();
        _publicSpki = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }

    private string MintPremium(string name = "Test User", DateOnly? expires = null) =>
        LicenseMinter.Mint(new LicensePayloadDto
        {
            Edition = LicenseEditions.Premium,
            Name = name,
            Id = "TEST-001",
            Issued = DateOnly.FromDateTime(DateTime.UtcNow),
            Expires = expires,
        }, _privatePem);

    [Fact]
    public void A_minted_key_validates_with_full_details()
    {
        var info = LicenseValidator.Validate(MintPremium("Kirk"), _publicSpki);

        Assert.True(info.IsValid);
        Assert.True(info.IsPremium);
        Assert.Equal(LicenseEditions.Premium, info.Edition);
        Assert.Equal("Kirk", info.LicensedTo);
        Assert.Equal("TEST-001", info.LicenseId);
        Assert.Null(info.Error);
    }

    [Fact]
    public void Whitespace_around_the_key_is_tolerated()
    {
        var info = LicenseValidator.Validate($"  {MintPremium()}\n", _publicSpki);
        Assert.True(info.IsPremium);
    }

    [Fact]
    public void No_key_is_the_free_edition_not_an_error()
    {
        foreach (var key in new[] { null, "", "   " })
        {
            var info = LicenseValidator.Validate(key, _publicSpki);
            Assert.False(info.IsPremium);
            Assert.Equal(LicenseEditions.FreeEdition, info.Edition);
            Assert.Null(info.Error);
        }
    }

    [Fact]
    public void A_tampered_payload_fails_signature_verification()
    {
        string key = MintPremium();
        string[] parts = key.Split('.');
        // Re-encode a modified payload (e.g. a different name) with the original signature.
        byte[] payload = LicenseValidator.FromBase64Url(parts[1]);
        payload[^5] ^= 0x01;
        string tampered = $"{parts[0]}.{LicenseValidator.ToBase64Url(payload)}.{parts[2]}";

        var info = LicenseValidator.Validate(tampered, _publicSpki);
        Assert.False(info.IsValid);
        Assert.Contains("signature", info.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_key_signed_by_a_different_private_key_is_rejected()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string foreignKey = LicenseMinter.Mint(
            new LicensePayloadDto { Edition = LicenseEditions.Premium, Name = "Forger" },
            other.ExportPkcs8PrivateKeyPem());

        Assert.False(LicenseValidator.Validate(foreignKey, _publicSpki).IsValid);
    }

    [Fact]
    public void An_expired_key_is_rejected_with_the_date()
    {
        string key = MintPremium(expires: new DateOnly(2025, 1, 1));
        var info = LicenseValidator.Validate(key, _publicSpki);
        Assert.False(info.IsValid);
        Assert.Contains("2025-01-01", info.Error);
    }

    [Fact]
    public void A_future_expiry_still_validates_and_is_reported()
    {
        var future = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1));
        var info = LicenseValidator.Validate(MintPremium(expires: future), _publicSpki);
        Assert.True(info.IsPremium);
        Assert.Equal(future, info.Expires);
    }

    [Fact]
    public void A_key_expiring_today_is_valid_for_the_whole_day()
    {
        // Boundary: expiry is inclusive of the expiry date (nowUtc > expires, not >=).
        var today = new DateOnly(2026, 6, 13);
        string key = MintPremium(expires: today);
        Assert.True(LicenseValidator.Validate(key, _publicSpki, today).IsPremium);          // on the day
        Assert.True(LicenseValidator.Validate(key, _publicSpki, today.AddDays(-1)).IsPremium); // day before
        Assert.False(LicenseValidator.Validate(key, _publicSpki, today.AddDays(1)).IsValid);   // day after
    }

    [Fact]
    public void An_oversized_key_is_rejected_without_throwing()
    {
        string huge = "AFX1." + new string('a', LicenseValidator.MaxKeyLength + 1) + ".sig";
        var info = LicenseValidator.Validate(huge, _publicSpki);
        Assert.False(info.IsValid);
        Assert.NotNull(info.Error);
    }

    [Fact]
    public void Unknown_extra_payload_fields_are_ignored_forward_compat()
    {
        // A future minter may add fields; older apps must still validate the key.
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string json = """{"v":1,"edition":"premium","name":"Fwd","tier":"galaxy","seats":5}""";
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(json);
        byte[] sig = ecdsa.SignData(payload, HashAlgorithmName.SHA256);
        string key = $"AFX1.{ToUrl(payload)}.{ToUrl(sig)}";

        var info = LicenseValidator.Validate(key, Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
        Assert.True(info.IsPremium);
        Assert.Equal("Fwd", info.LicensedTo);
    }

    private static string ToUrl(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void An_unknown_edition_is_rejected_even_when_correctly_signed()
    {
        string key = LicenseMinter.Mint(
            new LicensePayloadDto { Edition = "ultra-mega" }, _privatePem);
        var info = LicenseValidator.Validate(key, _publicSpki);
        Assert.False(info.IsValid);
        Assert.Contains("edition", info.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not a key at all")]
    [InlineData("AFX1.onlytwoparts")]
    [InlineData("WRONG.aGVsbG8.aGVsbG8")]
    [InlineData("AFX1.!!!notbase64!!!.aGVsbG8")]
    [InlineData("AFX1.aGVsbG8.aGVsbG8")] // valid base64, not JSON / not a signature
    public void Garbage_never_throws_and_never_validates(string key)
    {
        var info = LicenseValidator.Validate(key, _publicSpki);
        Assert.False(info.IsValid);
        Assert.False(info.IsPremium);
        Assert.NotNull(info.Error);
    }

    [Fact]
    public void The_embedded_production_key_rejects_test_minted_keys()
    {
        // A key signed by anything but the owner's real private key must fail in the real app.
        Assert.False(LicenseValidator.Validate(MintPremium()).IsValid);
    }
}

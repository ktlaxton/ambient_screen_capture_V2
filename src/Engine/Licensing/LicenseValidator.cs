using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AmbientFx.Licensing;

/// <summary>
/// Offline license-key verification (Epic 9 / Story 9.1). Keys are minted by the owner's
/// private ECDSA P-256 key (never in the repo or the app); the app verifies signatures
/// against the embedded public key — no license server, no account, no network.
///
/// Key format: <c>AFX1.&lt;base64url(payload-json)&gt;.&lt;base64url(P1363-signature)&gt;</c>
/// Payload: {"v":1,"edition":"premium","name":"...","id":"...","issued":"...","expires":null}
/// Pure and stateless; thread-safe.
/// </summary>
public static class LicenseValidator
{
    public const string Prefix = "AFX1";

    /// <summary>Hard cap before any allocation/parsing — real keys are &lt;300 chars; this only
    /// bounds a malformed/hostile string (a long key still can't unlock anything). DoS guard.</summary>
    public const int MaxKeyLength = 8192;

    /// <summary>SubjectPublicKeyInfo (base64) of the production signing key. The private
    /// half lives only with the owner (~\.ambientfx-dev\license-signing.pem + backups).</summary>
    public const string ProductionPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEgkj10xjHowL8GtJmrSr0MPyQZKK+UvDUGoZaiuATTYp8Sis6dfSWtWmiNSymeKAVZRDkLJvQfsx5bwCWGjqAtw==";

    /// <summary>Validates a key against the production public key, as of now (UTC).</summary>
    public static LicenseInfo Validate(string? key) =>
        Validate(key, ProductionPublicKey, DateOnly.FromDateTime(DateTime.UtcNow));

    /// <summary>Test seam: validate against an arbitrary public key (SPKI base64), as of now (UTC).</summary>
    public static LicenseInfo Validate(string? key, string publicKeySpkiBase64) =>
        Validate(key, publicKeySpkiBase64, DateOnly.FromDateTime(DateTime.UtcNow));

    /// <summary>
    /// Core validator. <paramref name="nowUtc"/> is the date to judge expiry against — the
    /// service passes a clock-rollback-resistant floor (see <see cref="LicenseService"/>), so a
    /// dated key can't be revived by winding the system clock back a day.
    /// </summary>
    public static LicenseInfo Validate(string? key, string publicKeySpkiBase64, DateOnly nowUtc)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return LicenseInfo.Free; // no key is the normal free state, not an error
        }
        if (key.Length > MaxKeyLength)
        {
            return LicenseInfo.Invalid("The license key is malformed.");
        }

        string[] parts = key.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != Prefix)
        {
            return LicenseInfo.Invalid("That doesn't look like an AmbientFx license key.");
        }

        byte[] payloadBytes, signature;
        try
        {
            payloadBytes = FromBase64Url(parts[1]);
            signature = FromBase64Url(parts[2]);
        }
        catch (FormatException)
        {
            return LicenseInfo.Invalid("The license key is malformed.");
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            if (!ecdsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256))
            {
                return LicenseInfo.Invalid("The license key's signature is not valid.");
            }
        }
        catch (CryptographicException)
        {
            return LicenseInfo.Invalid("The license key's signature is not valid.");
        }

        LicensePayloadDto? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LicensePayloadDto>(payloadBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return LicenseInfo.Invalid("The license key is malformed.");
        }

        if (payload is null || payload.Edition != LicenseEditions.Premium)
        {
            return LicenseInfo.Invalid("The license key has an unknown edition.");
        }
        if (payload.Expires is { } expires && nowUtc > expires)
        {
            return LicenseInfo.Invalid($"The license key expired on {expires:yyyy-MM-dd}.");
        }

        return new LicenseInfo
        {
            IsValid = true,
            Edition = LicenseEditions.Premium,
            LicensedTo = payload.Name ?? string.Empty,
            LicenseId = payload.Id ?? string.Empty,
            Expires = payload.Expires,
        };
    }

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static byte[] FromBase64Url(string value)
    {
        string s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }

    internal static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The signed payload inside a key. Unknown fields are ignored (forward compat).</summary>
public sealed class LicensePayloadDto
{
    public int V { get; set; } = 1;
    public string Edition { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Id { get; set; }
    public DateOnly? Issued { get; set; }
    public DateOnly? Expires { get; set; }
}

/// <summary>Result of validating a key — also the app's current entitlement state.</summary>
public sealed class LicenseInfo
{
    public bool IsValid { get; init; }

    /// <summary>A <see cref="LicenseEditions"/> value; "free" when no/invalid key.</summary>
    public string Edition { get; init; } = LicenseEditions.FreeEdition;

    public string LicensedTo { get; init; } = string.Empty;
    public string LicenseId { get; init; } = string.Empty;
    public DateOnly? Expires { get; init; }

    /// <summary>Human-readable reason when a presented key was rejected; null otherwise.</summary>
    public string? Error { get; init; }

    public bool IsPremium => IsValid && Edition == LicenseEditions.Premium;

    public static readonly LicenseInfo Free = new();

    public static LicenseInfo Invalid(string error) => new() { Error = error };
}

public static class LicenseEditions
{
    public const string FreeEdition = "free";
    public const string Premium = "premium";
}

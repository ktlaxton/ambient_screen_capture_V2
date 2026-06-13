using System.Security.Cryptography;
using System.Text.Json;

namespace AmbientFx.Licensing;

/// <summary>
/// Mints signed license keys (Story 9.1). Useless without the owner's private key — shipped
/// in the app only so the format has exactly one implementation, shared by the unit tests
/// (ephemeral keys) and the owner's tools/license/new-license.ps1 (production key).
/// </summary>
public static class LicenseMinter
{
    /// <summary>Signs a payload with a PKCS#8 PEM private key and returns the key string.</summary>
    public static string Mint(LicensePayloadDto payload, string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);

        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, LicenseValidator.JsonOptions);
        byte[] signature = ecdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

        return $"{LicenseValidator.Prefix}.{LicenseValidator.ToBase64Url(payloadBytes)}.{LicenseValidator.ToBase64Url(signature)}";
    }
}

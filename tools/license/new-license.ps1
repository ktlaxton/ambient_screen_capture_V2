<#
.SYNOPSIS
    Mint an AmbientFx Premium license key (Epic 9 / Story 9.3).

.DESCRIPTION
    Signs a license payload with the owner's PRIVATE ECDSA P-256 key and prints a key in the
    exact format LicenseValidator.cs verifies:

        AFX1.<base64url(payload-json)>.<base64url(P1363-signature)>

    The matching PUBLIC key is embedded in the app (LicenseValidator.ProductionPublicKey), so
    keys minted here validate offline in the shipped product. This script locally re-verifies
    every key it prints, so a key that prints is a key that will activate.

    Run this from your fulfillment webhook (or by hand) when a customer buys. NEVER commit or
    share the private key.

.PARAMETER Name
    Who the license is issued to (shown in the app as "Licensed to ...").

.PARAMETER Email
    Purchaser email (stored in the payload for support/lookup; not shown in the app).

.PARAMETER Id
    License id (defaults to a new GUID). Use your store's order id for traceability.

.PARAMETER ExpiresUtc
    Optional expiry date (yyyy-MM-dd) for subscription-style keys. Omit for a perpetual key.

.PARAMETER PrivateKeyPath
    PKCS#8 PEM private key. Defaults to ~\.ambientfx-dev\license-signing.pem.

.PARAMETER PublicKeyBase64
    SPKI public key to re-verify against. Defaults to the production key embedded in the app.

.EXAMPLE
    ./new-license.ps1 -Name "Jane Doe" -Email jane@example.com
.EXAMPLE
    ./new-license.ps1 -Name "Jane Doe" -Email jane@example.com -ExpiresUtc 2027-06-01 -Id ORDER-12345
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$Name,
    [string]$Email = '',
    [string]$Id = [guid]::NewGuid().ToString('N').Substring(0, 12).ToUpperInvariant(),
    [string]$ExpiresUtc = '',
    [string]$PrivateKeyPath = (Join-Path $env:USERPROFILE '.ambientfx-dev\license-signing.pem'),
    # Must equal LicenseValidator.ProductionPublicKey — re-verification catches a key mismatch.
    [string]$PublicKeyBase64 = 'MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEgkj10xjHowL8GtJmrSr0MPyQZKK+UvDUGoZaiuATTYp8Sis6dfSWtWmiNSymeKAVZRDkLJvQfsx5bwCWGjqAtw=='
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PrivateKeyPath)) {
    throw "Private key not found at $PrivateKeyPath. This must be the owner's secret signing key (back it up; never commit it)."
}

function ConvertTo-Base64Url([byte[]]$bytes) {
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

# Build the payload (camelCase; matches LicensePayloadDto under JsonSerializerDefaults.Web).
$payload = [ordered]@{ v = 1; edition = 'premium'; name = $Name }
if ($Email) { $payload.email = $Email }
$payload.id = $Id
$payload.issued = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
if ($ExpiresUtc) {
    $null = [datetime]::ParseExact($ExpiresUtc, 'yyyy-MM-dd', $null) # validate format
    $payload.expires = $ExpiresUtc
}
$json = ($payload | ConvertTo-Json -Compress)
$payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($json)

# Sign with ECDSA P-256 / SHA-256 (IEEE P1363 signature — what ECDsa.VerifyData expects).
$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
$ecdsa.ImportFromPem((Get-Content $PrivateKeyPath -Raw))
$signature = $ecdsa.SignData($payloadBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)

$key = "AFX1.$(ConvertTo-Base64Url $payloadBytes).$(ConvertTo-Base64Url $signature)"

# Re-verify against the PUBLIC key the app ships — a key that prints is a key that activates.
$verifier = [System.Security.Cryptography.ECDsa]::Create()
$verifier.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($PublicKeyBase64), [ref]0)
if (-not $verifier.VerifyData($payloadBytes, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) {
    throw 'Self-verification FAILED: the private key does not match the embedded public key. Do NOT ship this key.'
}

Write-Host "Payload : $json" -ForegroundColor DarkGray
Write-Host "Verified against the app's public key." -ForegroundColor Green
Write-Host "`nLicense key (send this to the customer):`n" -ForegroundColor Cyan
Write-Output $key

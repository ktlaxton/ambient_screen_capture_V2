# Monetizing AmbientFx

AmbientFx ships as **free + one-time Premium unlock**, activated by an **offline signed license
key** (Epic 9). No ads, no accounts, no license server. This doc is the playbook for selling it.

## Why this model
- **Offline keys** (ECDSA-signed, verified in-app against an embedded public key) mean nothing to
  host, nothing to pay for, nothing to get breached, and activation works with no internet.
- **No ads:** there's no decent desktop ad supply, and bundling an ad SDK into a screen-capture
  app is a privacy story not worth defending. Feature-limited free is the marketing instead.
- **Merchant of record** handles the part you don't want to: worldwide VAT/sales-tax, invoices,
  chargebacks, fraud.

## The license key system (recap)
- Format: `AFX1.<base64url(payload)>.<base64url(signature)>`, ECDSA P-256 / SHA-256.
- Mint with `tools/license/new-license.ps1` using the **private** key at
  `~\.ambientfx-dev\license-signing.pem`. The matching **public** key is embedded in the app
  (`LicenseValidator.ProductionPublicKey`).
- The app validates the key locally, flips to Premium, and persists the key in `settings.json`.
- Payload carries `name`, `email`, `id`, `issued`, and optional `expires` (for time-limited keys).

> **Guard the private key.** Back it up in at least two safe places (password manager + offline).
> Losing it → you can't mint new keys. Replacing it (new public key in a new app build) →
> **every key already sold stops working**. Treat it like the crown jewels.

## Storefront: Paddle vs Lemon Squeezy
Both are merchants of record (they remit tax for you) and both do license-key delivery + webhooks.

| | Paddle | Lemon Squeezy |
|---|---|---|
| Merchant of record (handles tax) | ✓ | ✓ |
| Built-in license keys | ✓ | ✓ |
| Webhooks (order events) | ✓ | ✓ |
| Onboarding for a solo dev | Heavier review | Fast |
| Fees | ~5% + 50¢ | ~5% + 50¢ |

**Recommendation:** Lemon Squeezy to launch (fastest solo onboarding); Paddle is a fine
alternative if you want their checkout/tax tooling. Either way the integration below is the same
shape.

## Fulfillment flow (buy → key)
```
Customer clicks "Upgrade to Premium"  (PURCHASE_URL in web/src/control/premium.ts)
        │
        ▼
Storefront checkout (Lemon Squeezy / Paddle)  ── handles payment + tax
        │  on success → "order paid" webhook
        ▼
Your webhook endpoint
   1. verify the webhook signature (store's signing secret)
   2. mint a key:  AFX1.…  (name/email/order-id from the payload; sign with the PRIVATE key)
   3. return / email the key to the buyer (most stores can show it on the receipt too)
        │
        ▼
Customer pastes the key into AmbientFx → License panel → Activate  → Premium (offline forever)
```

### Webhook: minting a key
Two ways to mint inside the webhook:

**A. Shell out to the script** (simplest if your webhook runs on a Windows host/runner):
```powershell
./tools/license/new-license.ps1 -Name $buyerName -Email $buyerEmail -Id $orderId
```
The last stdout line is the key.

**B. Port the signing to your serverless runtime** (≈15 lines; same PEM, same algorithm). Node sketch:
```js
import crypto from 'node:crypto';
function mintKey({ name, email, id }, privatePem) {
  const payload = Buffer.from(JSON.stringify({
    v: 1, edition: 'premium', name, email, id,
    issued: new Date().toISOString().slice(0, 10),
  }));
  const sig = crypto.sign(null, payload, { key: privatePem, dsaEncoding: 'ieee-p1363' });
  const b64url = (b) => b.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  return `AFX1.${b64url(payload)}.${b64url(sig)}`;
}
```
`dsaEncoding: 'ieee-p1363'` is required — it matches .NET `ECDsa.SignData` and the app's verify.
Keep the private PEM in a secret store (env var / secret manager), never in code.

## Pricing
- A **one-time** unlock (e.g. **$15–25**) fits the "I bought this nice utility" instinct better
  than a subscription for a desktop toy, and pairs with the perpetual (no-expiry) key default.
- If you later want recurring revenue, the key format already supports `expires` — issue dated
  keys and re-mint on renewal via the same webhook. No app change needed.

## License security model (what the offline keys do and don't defend)
- **Signature forgery:** infeasible — keys are ECDSA-P256 signed; the app only trusts its
  embedded public key. Editing `settings.json` to flip premium booleans does nothing: the engine
  gates on the *verified entitlement*, not on stored settings.
- **Clock rollback (dated keys):** the app keeps a monotonic "highest date seen" floor in
  `%AppData%\AmbientFx\.license-clock` and judges expiry against `max(today, floor)`, so winding
  the system clock back a day can't revive an expired key. **Residual limit:** a determined local
  admin who also deletes/edits that file (or patches the binary) can still defeat any purely
  offline scheme. We accept this — perpetual keys (the default) have no expiry to attack, and the
  alternative (an online check) isn't worth the privacy/ops cost at this scale.
- **Bypassing the UI:** the web layer only hides/locks controls; the engine re-enforces every
  gate, so editing the bundle or using DevTools grants nothing.

## Refunds & deactivation
- Offline keys can't be remotely revoked (the trade-off for no server). For a refund, the buyer
  keeps a working key — acceptable at this price point and refund rate.
- If abuse ever shows up in the data: add a revocation list shipped with app updates, or move to
  dated keys + a lightweight re-issue endpoint. Don't build either until the numbers justify it.

## Go-live
See `docs/RELEASE-CHECKLIST.md` for the ordered switch-flip (cert, store, purchase URL, prices,
final verification).

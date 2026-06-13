# AmbientFx Release / Go-Live Checklist

Everything technical is built; this is the ordered switch-flip to actually sell. Each item is an
owner action. Nothing here needs more code unless noted.

## 0. Pre-flight (verify the build)
- [ ] `dotnet test tests/Engine.Tests` green; `cd web && npm run build && npm test -- --run` green.
- [ ] `./build.ps1 -Version <x.y.z>` produces a signed installer (see §2 for the cert).
- [ ] Clean-machine pass: enable Windows Sandbox, run `tools/sandbox-verify/AmbientFx-CleanMachine.wsb`,
      confirm `tools/sandbox-verify/results/verify-report.txt` is all PASS (Epic 8 / Story 8.4 AC7).
- [ ] Free/Premium pass on your machine: confirm free = 1 monitor + 4 effects + locked RGB;
      paste a real key (mint via `tools/license/new-license.ps1`) → everything unlocks live.

## 1. Licensing keys (do once, carefully)
- [ ] Confirm the production keypair exists: `~\.ambientfx-dev\license-signing.pem` (private) and
      that `LicenseValidator.ProductionPublicKey` is its public half.
- [ ] **Back up the private key** in ≥2 secure places (password manager + offline). Losing it =
      no new keys; replacing it = all sold keys die.
- [ ] If you want a fresh production keypair (recommended for real sales, since the dev one was
      generated in a coding session): regenerate, paste the new public key into
      `LicenseValidator.ProductionPublicKey` **and** `tools/license/new-license.ps1`'s default,
      rebuild, and re-run the licensing tests.

## 2. Code-signing certificate (kills SmartScreen warnings — do before any public download)
- [ ] Buy either **Azure Trusted Signing** (subscription; `vpk` integrates via
      `--azureTrustedSignFile`) or an **OV code-signing certificate** (Certum / SSL.com offer
      individual-developer certs).
- [ ] Sign the release: `./build.ps1 -Version <x.y.z> -SignParams '<signtool args>'`
      (see `docs/RELEASING.md`). Verify the installer's signature is trusted (not self-signed).

## 3. Storefront (see docs/MONETIZATION.md)
- [ ] Create a Lemon Squeezy (or Paddle) account; add the AmbientFx Premium product + price.
- [ ] Stand up the fulfillment webhook: order-paid → mint a key → deliver. Store the **private
      signing key** as a secret in the webhook host.
- [ ] Host a purchase/landing page; point **`PURCHASE_URL`** in `web/src/control/premium.ts` at
      it, then `npm run build` + `dotnet build` so the app links to the real page.
- [ ] Test-buy end to end (store test mode): purchase → receive key → activate in the app.

## 4. Distribution
- [ ] Publish the signed release to GitHub Releases (the default update feed) per
      `docs/RELEASING.md` (`vpk upload github …`).
- [ ] Verify auto-update from a prior installed build picks up the new version.
- [ ] Sanity-check the LGPL/notice files ship (`build.ps1` already hard-fails if not — Story 8.4).

## 5. Launch hygiene
- [ ] Privacy note on the landing page: AmbientFx captures the screen locally; nothing is
      uploaded; license activation is offline. (True today — keep it true.)
- [ ] Decide refund policy text (offline keys can't be remotely revoked — see MONETIZATION.md).
- [ ] Pick the launch price (one-time $15–25 suggested).

## Tracked carry-overs from earlier epics
- [ ] Epic 8 / 8.3: re-verify the multi-vendor RGB path when non-Corsair hardware is available.
- [ ] Epic 8 / 8.4: the clean-VM sandbox run (folded into §0 above).

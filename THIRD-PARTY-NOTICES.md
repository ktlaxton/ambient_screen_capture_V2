# Third-Party Notices

AmbientFx bundles or depends on the following third-party components. NuGet packages not
listed here are MIT/Apache-2.0 licensed and consumed unmodified.

## RGB.NET — LGPL-2.1 (Epic 8)

- Packages: `RGB.NET.Core`, `RGB.NET.Devices.Corsair`, and the Story 8.3 vendor providers
  `RGB.NET.Devices.Asus` / `.Logitech` / `.Msi` / `.Razer` / `.SteelSeries` / `.Wooting`
  (NuGet, v3.2.0)
- Source: https://github.com/DarthAffe/RGB.NET
- License: GNU Lesser General Public License v2.1
- Note: non-Corsair providers rely on vendor software / SDK natives already installed on the
  user's system (Synapse, G HUB, Armoury Crate, …) — no vendor natives are bundled except the
  Corsair iCUE SDK below.

RGB.NET is used **unmodified** and **dynamically linked** via NuGet assemblies. The full
license text ships with the application as `licenses/LGPL-2.1.txt`, alongside this notice.

**Replacing / relinking RGB.NET (LGPL-2.1 §6):** the `RGB.NET.*.dll` files in the install
directory (`%LocalAppData%\AmbientFx\current\`) are ordinary .NET assemblies loaded at
runtime. To use your own (modified) build of RGB.NET, compile it from
https://github.com/DarthAffe/RGB.NET against the same major version (v3.x, `net8.0`
target) and replace the corresponding `RGB.NET.*.dll` files in that directory. AmbientFx
does not strong-name-pin, trim, ILMerge, or single-file-embed these assemblies — keep it
that way; `build.ps1` asserts they are present as separate files in every release build.
Note that an auto-update reinstalls the shipped assemblies, so a replacement must be
reapplied after updating.

**Story 8.4 review (2026-06-12):** dynamic linking, unmodified use, shipped license text,
and the documented replace path were confirmed against the Velopack publish output — see
`docs/stories/8.4.story.md` for the sign-off record.

## Corsair iCUE SDK — Corsair SDK license (Epic 8)

- File: `x64/iCUESDK.x64_2019.dll` (redistributable client library, v4.0.84)
- Source: https://github.com/CorsairOfficial/cue-sdk (release zip, `redist/x64`)
- The native client library that connects to the user's running iCUE service. Shipped
  per Corsair's SDK redistribution terms; checked into the repo at
  `src/Engine/native/x64/` and copied to the app output by `AmbientFx.csproj`.

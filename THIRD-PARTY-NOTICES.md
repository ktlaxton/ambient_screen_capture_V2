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

RGB.NET is used **unmodified** and **dynamically linked** via NuGet assemblies, which keeps
a closed-source application compatible with the LGPL's relinking requirement: the
`RGB.NET.*.dll` files in the install directory are plain .NET assemblies a user can replace
with their own (compatible) build. Do not ILMerge/trim/embed these assemblies.

**Story 8.4 gate:** the LGPL redistribution review must be signed off before the Velopack
installer (Story 7.4) ships these assemblies in a public release.

## Corsair iCUE SDK — Corsair SDK license (Epic 8)

- File: `x64/iCUESDK.x64_2019.dll` (redistributable client library, v4.0.84)
- Source: https://github.com/CorsairOfficial/cue-sdk (release zip, `redist/x64`)
- The native client library that connects to the user's running iCUE service. Shipped
  per Corsair's SDK redistribution terms; checked into the repo at
  `src/Engine/native/x64/` and copied to the app output by `AmbientFx.csproj`.

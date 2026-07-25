#if SIMULATOR_ENABLED
using AmbientFx.Licensing;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.4). An <see cref="ILicenseService"/> that reports a Premium
/// entitlement so the simulator exercises the gated paths (RGB peripherals, per-monitor effects,
/// unlimited targets), mirroring the browser simulator's <c>?premium=1</c>. The real
/// <see cref="LicenseService"/>/<see cref="LicenseValidator"/> gate is left completely unmodified —
/// only the entitlement <i>state</i> the simulator runs under is sim-Premium. Compiled out of Release.
/// </summary>
public sealed class SimulatorLicenseService : ILicenseService
{
    private static readonly LicenseInfo SimPremium = new()
    {
        IsValid = true,
        Edition = LicenseEditions.Premium,
        LicensedTo = "Simulator User",
    };

    public LicenseInfo Current => SimPremium;

    public LicenseInfo Apply(string? key) => SimPremium;
}
#endif

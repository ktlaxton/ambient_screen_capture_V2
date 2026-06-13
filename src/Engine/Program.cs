using Velopack;

namespace AmbientFx;

/// <summary>
/// Explicit entry point (Story 7.4): Velopack's install/update/uninstall hooks must run
/// BEFORE anything WPF — on those special invocations <c>VelopackApp.Build().Run()</c>
/// performs its work and exits the process, so the single-instance mutex, DI and windows
/// never spin up mid-install. Normal launches fall through to the regular WPF app.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}

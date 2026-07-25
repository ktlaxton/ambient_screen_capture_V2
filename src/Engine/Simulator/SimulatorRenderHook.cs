#if SIMULATOR_ENABLED
using System.IO;
using System.Runtime.InteropServices;
using AmbientFx.Models;
using AmbientFx.Services;
using AmbientFx.Simulator.Content;
using Microsoft.Extensions.Logging;

namespace AmbientFx.Simulator;

/// <summary>
/// Dev/QA only (Epic 10, Story 10.5). The headless render / automation hook: composes a scenario's
/// per-monitor synthetic content (at a <b>fixed</b> frame index) into one image laid out as the virtual
/// desktop, and writes a PNG. It is the seam a future CI snapshot suite would diff. For determinism it
/// captures the simulated frame buffers (pure <see cref="SyntheticPatterns"/> at a pinned frame index),
/// NOT the GPU-composited WebView2 output — so the same scenario yields byte-identical pixels every run,
/// free of driver/encoder nondeterminism. Compiled out of Release.
/// </summary>
public static class SimulatorRenderHook
{
    /// <summary>
    /// Composes the scenario into a tightly-packed, top-down BGRA buffer laid out as the virtual desktop,
    /// scaled to fit <paramref name="maxWidth"/>. Pure and deterministic for fixed (scenario, frameIndex,
    /// maxWidth) — no engine, no GPU, no WebView2.
    /// </summary>
    public static byte[] ComposeBgra(SimulatorScenario scenario, long frameIndex, out int width, out int height, int maxWidth = 960)
    {
        var monitors = scenario.Monitors;
        if (monitors.Count == 0)
        {
            width = 1;
            height = 1;
            return new byte[] { 0, 0, 0, 255 };
        }

        int minX = monitors.Min(m => m.X);
        int minY = monitors.Min(m => m.Y);
        int maxX = monitors.Max(m => m.X + m.Width);
        int maxY = monitors.Max(m => m.Y + m.Height);
        int spanW = Math.Max(1, maxX - minX);
        int spanH = Math.Max(1, maxY - minY);

        double scale = Math.Min(1.0, (double)maxWidth / spanW);
        width = Math.Max(1, (int)Math.Round(spanW * scale));
        height = Math.Max(1, (int)Math.Round(spanH * scale));

        var composite = new byte[checked(width * height * 4)];
        for (int i = 0; i < composite.Length; i += 4)
        {
            composite[i] = 18;
            composite[i + 1] = 18;
            composite[i + 2] = 24;
            composite[i + 3] = 255;
        }

        var relations = ComputeRelations(scenario);

        foreach (var m in monitors)
        {
            int mw = Math.Max(1, (int)Math.Round(m.Width * scale));
            int mh = Math.Max(1, (int)Math.Round(m.Height * scale));
            int ox = (int)Math.Round((m.X - minX) * scale);
            int oy = (int)Math.Round((m.Y - minY) * scale);

            var tile = new byte[mw * mh * 4];
            bool blank = string.Equals(m.Content?.Kind, SimContent.Blank, StringComparison.OrdinalIgnoreCase);
            if (!blank)
            {
                SyntheticPatterns.Fill(m.Pattern, tile, mw, mh, frameIndex);
            }
            else
            {
                for (int i = 3; i < tile.Length; i += 4) tile[i] = 255; // opaque black
            }

            for (int y = 0; y < mh; y++)
            {
                int cy = oy + y;
                if (cy < 0 || cy >= height) continue;
                for (int x = 0; x < mw; x++)
                {
                    int cx = ox + x;
                    if (cx < 0 || cx >= width) continue;
                    int sp = (y * mw + x) * 4;
                    int dp = (cy * width + cx) * 4;
                    composite[dp] = tile[sp];
                    composite[dp + 1] = tile[sp + 1];
                    composite[dp + 2] = tile[sp + 2];
                    composite[dp + 3] = 255;
                }
            }

            // Bake the REAL engine layout decision into the artifact: a border colored by
            // MonitorLayout.ComputeRelation(source, this) — deterministic, and the projection geometry a
            // snapshot suite would diff (GPU effect pixels require a live --simulator run; see SIMULATOR.md).
            relations.TryGetValue(m.Id, out var relation);
            DrawBorder(composite, width, height, ox, oy, mw, mh, RelationColor(relation));
        }

        return composite;
    }

    /// <summary>
    /// Runs the <b>real</b> engine layout math (<see cref="MonitorLayout.ComputeRelation"/>) for each
    /// monitor relative to the scenario's source, exactly as <c>EngineCoordinator.BuildWindowConfigFor</c>
    /// does. Pure and deterministic. The source monitor maps to "source".
    /// </summary>
    public static Dictionary<string, string> ComputeRelations(SimulatorScenario scenario)
    {
        var infos = scenario.ToMonitorInfos();
        string sourceId = scenario.ResolveSourceId();
        MonitorInfo? source = infos.FirstOrDefault(m => string.Equals(m.Id, sourceId, StringComparison.OrdinalIgnoreCase));

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in infos)
        {
            result[m.Id] = source is null || string.Equals(m.Id, sourceId, StringComparison.OrdinalIgnoreCase)
                ? "source"
                : MonitorLayout.ComputeRelation(source, m);
        }
        return result;
    }

    private static (byte R, byte G, byte B) RelationColor(string? relation) => relation switch
    {
        "left" => (220, 40, 40),
        "right" => (40, 200, 80),
        "above" => (60, 120, 240),
        "below" => (230, 200, 40),
        "source" => (240, 240, 240),
        _ => (110, 110, 120), // none / unknown
    };

    private static void DrawBorder(byte[] bgra, int width, int height, int ox, int oy, int mw, int mh, (byte R, byte G, byte B) color)
    {
        const int thickness = 2;
        void Set(int x, int y)
        {
            if (x < 0 || x >= width || y < 0 || y >= height) return;
            int p = (y * width + x) * 4;
            bgra[p] = color.B;
            bgra[p + 1] = color.G;
            bgra[p + 2] = color.R;
            bgra[p + 3] = 255;
        }
        for (int t = 0; t < thickness; t++)
        {
            for (int x = ox; x < ox + mw; x++)
            {
                Set(x, oy + t);
                Set(x, oy + mh - 1 - t);
            }
            for (int y = oy; y < oy + mh; y++)
            {
                Set(ox + t, y);
                Set(ox + mw - 1 - t, y);
            }
        }
    }

    /// <summary>Renders a scenario to a PNG on disk and returns the path. Deterministic pixels.</summary>
    public static string RenderComposite(SimulatorScenario scenario, long frameIndex, string outputPath, int maxWidth = 960)
    {
        byte[] bgra = ComposeBgra(scenario, frameIndex, out int w, out int h, maxWidth);
        WriteBgraPng(bgra, w, h, outputPath);
        return outputPath;
    }

    /// <summary>
    /// CLI entry: if <c>--simulator-render &lt;scenario&gt; [--out &lt;dir&gt;]</c> is present, render that
    /// scenario (curated name or JSON path) at frame 0 and return the written path; otherwise null. Used by
    /// <c>App.OnStartup</c> for a headless one-shot render.
    /// </summary>
    public static string? TryRunFromArgs(string[] args, ILogger logger)
    {
        int idx = Array.FindIndex(args, a => string.Equals(a, "--simulator-render", StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            return null;
        }

        string scenarioArg = idx + 1 < args.Length && !args[idx + 1].StartsWith("--")
            ? args[idx + 1]
            : "SIM_MONITORS";

        int outIdx = Array.FindIndex(args, a => string.Equals(a, "--out", StringComparison.OrdinalIgnoreCase));
        string outDir = outIdx >= 0 && outIdx + 1 < args.Length
            ? args[outIdx + 1]
            : Path.Combine(Path.GetTempPath(), "AmbientFx-SimRender");

        var scenario = SimulatorScenarioLibrary.Load(scenarioArg, logger);
        string safeName = string.Concat(scenario.Name.Split(Path.GetInvalidFileNameChars()));
        string outPath = Path.Combine(outDir, $"{safeName}.png");
        RenderComposite(scenario, frameIndex: 0, outPath);
        logger.LogInformation("Simulator headless render: '{Scenario}' -> {Path}", scenario.Name, outPath);
        return outPath;
    }

    private static void WriteBgraPng(byte[] bgra, int width, int height, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // GDI 32bppArgb is B,G,R,A in memory (little-endian) — same as our BGRA buffer.
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(bgra, y * width * 4, data.Scan0 + y * data.Stride, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
#endif

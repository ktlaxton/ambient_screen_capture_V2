using AmbientFx.Bridge;

namespace AmbientFx.Processing;

/// <summary>
/// Pure pixel analysis over a tightly packed 32-bit BGRA buffer (bytes B,G,R,A; top-down rows;
/// no padding; length &gt;= width * height * 4): per-edge zone color averages and a
/// saturation-weighted dominant color. Stateless, deterministic, and thread-safe.
/// </summary>
public static class EdgeZoneExtractor
{
    /// <summary>Fraction of the frame's height/width used as the sampling band along each edge.</summary>
    private const double EdgeBandFraction = 0.15;

    /// <summary>Base weight added to per-pixel saturation when computing the dominant color.</summary>
    private const double DominantBaseWeight = 0.2;

    /// <summary>Mean Rec.601 luma (0-255 scale) below which the frame counts as near-black.</summary>
    private const double NearBlackLuma = 2.0;

    /// <summary>
    /// Averages the edge bands of the frame into <paramref name="zonesPerEdge"/> contiguous zones per edge.
    /// Top/bottom bands are max(1, round(height * 0.15)) rows tall and their zones run left-to-right;
    /// left/right bands are max(1, round(width * 0.15)) columns wide and their zones run top-to-bottom.
    /// Zone i spans [i*extent/zones, (i+1)*extent/zones) so remainders distribute evenly; zones that end
    /// up empty (extent &lt; zones, e.g. a 1x1 image) yield [0,0,0].
    /// </summary>
    /// <param name="bgra">Tightly packed BGRA pixels, length &gt;= width * height * 4.</param>
    /// <param name="width">Frame width in pixels (&gt; 0).</param>
    /// <param name="height">Frame height in pixels (&gt; 0).</param>
    /// <param name="zonesPerEdge">Zone count per edge; values below 1 are clamped to 1.</param>
    /// <returns>Edge colors with each zone as [r,g,b] ints 0-255.</returns>
    /// <exception cref="ArgumentOutOfRangeException">width or height is not positive.</exception>
    /// <exception cref="ArgumentException">The buffer is smaller than width * height * 4 bytes.</exception>
    public static EdgeColors ExtractEdges(ReadOnlySpan<byte> bgra, int width, int height, int zonesPerEdge)
    {
        ValidateBuffer(bgra, width, height);
        if (zonesPerEdge < 1) zonesPerEdge = 1;

        int bandRows = Math.Min(height, Math.Max(1, (int)Math.Round(height * EdgeBandFraction, MidpointRounding.AwayFromZero)));
        int bandCols = Math.Min(width, Math.Max(1, (int)Math.Round(width * EdgeBandFraction, MidpointRounding.AwayFromZero)));

        return new EdgeColors
        {
            Top = AverageHorizontalBand(bgra, width, rowStart: 0, rowCount: bandRows, zonesPerEdge),
            Bottom = AverageHorizontalBand(bgra, width, rowStart: height - bandRows, rowCount: bandRows, zonesPerEdge),
            Left = AverageVerticalBand(bgra, width, height, colStart: 0, colCount: bandCols, zonesPerEdge),
            Right = AverageVerticalBand(bgra, width, height, colStart: width - bandCols, colCount: bandCols, zonesPerEdge),
        };
    }

    /// <summary>
    /// Computes the overall dominant color as a saturation-weighted mean over ALL pixels:
    /// weight = 0.2 + sat, where sat = (max - min) / max for max &gt; 0, else 0.
    /// Returns [0,0,0] when the frame's mean Rec.601 luma is near black (&lt; 2 on the 0-255 scale).
    /// </summary>
    /// <param name="bgra">Tightly packed BGRA pixels, length &gt;= width * height * 4.</param>
    /// <param name="width">Frame width in pixels (&gt; 0).</param>
    /// <param name="height">Frame height in pixels (&gt; 0).</param>
    /// <returns>[r,g,b] ints 0-255.</returns>
    /// <exception cref="ArgumentOutOfRangeException">width or height is not positive.</exception>
    /// <exception cref="ArgumentException">The buffer is smaller than width * height * 4 bytes.</exception>
    public static int[] ExtractDominant(ReadOnlySpan<byte> bgra, int width, int height)
    {
        ValidateBuffer(bgra, width, height);

        long pixelCount = (long)width * height;
        int len = checked(width * height * 4);

        double weightedR = 0, weightedG = 0, weightedB = 0, totalWeight = 0;
        double lumaSum = 0;

        for (int p = 0; p < len; p += 4)
        {
            int b = bgra[p];
            int g = bgra[p + 1];
            int r = bgra[p + 2];

            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            double sat = max > 0 ? (max - min) / (double)max : 0.0;
            double w = DominantBaseWeight + sat;

            weightedR += r * w;
            weightedG += g * w;
            weightedB += b * w;
            totalWeight += w;

            lumaSum += 0.299 * r + 0.587 * g + 0.114 * b;
        }

        double meanLuma = lumaSum / pixelCount;
        if (meanLuma < NearBlackLuma)
            return new int[3];

        // totalWeight >= 0.2 * pixelCount > 0, so the division is always safe.
        return new[]
        {
            RoundChannel(weightedR / totalWeight),
            RoundChannel(weightedG / totalWeight),
            RoundChannel(weightedB / totalWeight),
        };
    }

    /// <summary>Averages a horizontal band (top or bottom) into zones running left-to-right.</summary>
    private static int[][] AverageHorizontalBand(ReadOnlySpan<byte> bgra, int width, int rowStart, int rowCount, int zones)
    {
        var result = new int[zones][];
        int rowEnd = rowStart + rowCount;

        for (int z = 0; z < zones; z++)
        {
            int x0 = (int)((long)z * width / zones);
            int x1 = (int)((long)(z + 1) * width / zones);
            long count = (long)(x1 - x0) * rowCount;
            if (count == 0)
            {
                result[z] = new int[3];
                continue;
            }

            long sumR = 0, sumG = 0, sumB = 0;
            for (int y = rowStart; y < rowEnd; y++)
            {
                int rowOffset = y * width * 4;
                int pEnd = rowOffset + x1 * 4;
                for (int p = rowOffset + x0 * 4; p < pEnd; p += 4)
                {
                    sumB += bgra[p];
                    sumG += bgra[p + 1];
                    sumR += bgra[p + 2];
                }
            }

            result[z] = new[] { RoundAverage(sumR, count), RoundAverage(sumG, count), RoundAverage(sumB, count) };
        }

        return result;
    }

    /// <summary>Averages a vertical band (left or right) into zones running top-to-bottom.</summary>
    private static int[][] AverageVerticalBand(ReadOnlySpan<byte> bgra, int width, int height, int colStart, int colCount, int zones)
    {
        var result = new int[zones][];
        int colEnd = colStart + colCount;

        for (int z = 0; z < zones; z++)
        {
            int y0 = (int)((long)z * height / zones);
            int y1 = (int)((long)(z + 1) * height / zones);
            long count = (long)(y1 - y0) * colCount;
            if (count == 0)
            {
                result[z] = new int[3];
                continue;
            }

            long sumR = 0, sumG = 0, sumB = 0;
            for (int y = y0; y < y1; y++)
            {
                int rowOffset = y * width * 4;
                int pEnd = rowOffset + colEnd * 4;
                for (int p = rowOffset + colStart * 4; p < pEnd; p += 4)
                {
                    sumB += bgra[p];
                    sumG += bgra[p + 1];
                    sumR += bgra[p + 2];
                }
            }

            result[z] = new[] { RoundAverage(sumR, count), RoundAverage(sumG, count), RoundAverage(sumB, count) };
        }

        return result;
    }

    /// <summary>Integer average rounded half-up; inputs are sums of 0-255 bytes so the result stays in range.</summary>
    private static int RoundAverage(long sum, long count) => (int)((sum + count / 2) / count);

    private static int RoundChannel(double value) =>
        Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    private static void ValidateBuffer(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        long required = (long)width * height * 4;
        if (bgra.Length < required)
            throw new ArgumentException(
                $"Pixel buffer too small: need {required} bytes for {width}x{height} BGRA but got {bgra.Length}.",
                nameof(bgra));
    }
}

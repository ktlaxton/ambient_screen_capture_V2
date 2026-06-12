using AmbientFx.Processing;
using Xunit;

namespace AmbientFx.Engine.Tests.Processing;

/// <summary>
/// Builds tightly packed top-down BGRA buffers for tests. Painter receives (x, y) and
/// returns the pixel as (R, G, B); alpha is always 255.
/// </summary>
internal static class SyntheticFrames
{
    public static byte[] Create(int width, int height, Func<int, int, (byte R, byte G, byte B)> painter)
    {
        var buffer = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = painter(x, y);
                int p = (y * width + x) * 4;
                buffer[p] = b;     // B
                buffer[p + 1] = g; // G
                buffer[p + 2] = r; // R
                buffer[p + 3] = 255;
            }
        }

        return buffer;
    }

    public static byte[] Solid(int width, int height, byte r, byte g, byte b) =>
        Create(width, height, (_, _) => (r, g, b));
}

public class EdgeZoneExtractorTests
{
    private static readonly int[] Black = { 0, 0, 0 };

    // ---------- ExtractEdges: solid color ----------

    [Fact]
    public void ExtractEdges_SolidColor_EveryZoneIsExactlyThatColor()
    {
        byte[] buffer = SyntheticFrames.Solid(16, 16, r: 10, g: 20, b: 30);

        var edges = EdgeZoneExtractor.ExtractEdges(buffer, 16, 16, zonesPerEdge: 8);

        var expected = new[] { 10, 20, 30 };
        foreach (int[][] edge in new[] { edges.Top, edges.Bottom, edges.Left, edges.Right })
        {
            Assert.Equal(8, edge.Length);
            foreach (int[] zone in edge)
                Assert.Equal(expected, zone);
        }
    }

    [Fact]
    public void ExtractEdges_BgraByteOrderHonored_PureBlueMapsToRgbBlue()
    {
        // Pure blue: B byte = 255, R and G bytes = 0. Output is [r,g,b] => [0,0,255].
        byte[] buffer = SyntheticFrames.Solid(8, 8, r: 0, g: 0, b: 255);

        var edges = EdgeZoneExtractor.ExtractEdges(buffer, 8, 8, zonesPerEdge: 2);

        Assert.Equal(new[] { 0, 0, 255 }, edges.Top[0]);
        Assert.Equal(new[] { 0, 0, 255 }, edges.Left[1]);
    }

    // ---------- ExtractEdges: quadrants + left/right ordering ----------

    [Fact]
    public void ExtractEdges_QuadrantImage_ZonesMapToCorrectQuadrants()
    {
        var topLeft = (R: (byte)255, G: (byte)0, B: (byte)0);
        var topRight = (R: (byte)0, G: (byte)0, B: (byte)255);
        var bottomLeft = (R: (byte)0, G: (byte)255, B: (byte)0);
        var bottomRight = (R: (byte)255, G: (byte)255, B: (byte)0);

        byte[] buffer = SyntheticFrames.Create(16, 16, (x, y) =>
            y < 8 ? (x < 8 ? topLeft : topRight)
                  : (x < 8 ? bottomLeft : bottomRight));

        var edges = EdgeZoneExtractor.ExtractEdges(buffer, 16, 16, zonesPerEdge: 2);

        // Top/bottom run left-to-right.
        Assert.Equal(new[] { 255, 0, 0 }, edges.Top[0]);
        Assert.Equal(new[] { 0, 0, 255 }, edges.Top[1]);
        Assert.Equal(new[] { 0, 255, 0 }, edges.Bottom[0]);
        Assert.Equal(new[] { 255, 255, 0 }, edges.Bottom[1]);

        // Left/right run TOP-TO-BOTTOM: zone 0 = top half, zone 1 = bottom half.
        Assert.Equal(new[] { 255, 0, 0 }, edges.Left[0]);
        Assert.Equal(new[] { 0, 255, 0 }, edges.Left[1]);
        Assert.Equal(new[] { 0, 0, 255 }, edges.Right[0]);
        Assert.Equal(new[] { 255, 255, 0 }, edges.Right[1]);
    }

    [Fact]
    public void ExtractEdges_TopHalfBottomHalfSplit_LeftAndRightZonesRunTopToBottom()
    {
        // Top half color A, bottom half color B; 4 zones per edge => zones 0-1 = A, zones 2-3 = B.
        byte[] buffer = SyntheticFrames.Create(16, 16, (_, y) =>
            y < 8 ? ((byte)200, (byte)10, (byte)10) : ((byte)10, (byte)10, (byte)200));

        var edges = EdgeZoneExtractor.ExtractEdges(buffer, 16, 16, zonesPerEdge: 4);

        var a = new[] { 200, 10, 10 };
        var b = new[] { 10, 10, 200 };
        foreach (int[][] vertical in new[] { edges.Left, edges.Right })
        {
            Assert.Equal(a, vertical[0]);
            Assert.Equal(a, vertical[1]);
            Assert.Equal(b, vertical[2]);
            Assert.Equal(b, vertical[3]);
        }
    }

    // ---------- ExtractEdges: remainder distribution ----------

    [Fact]
    public void ExtractEdges_WidthNotDivisibleByZones_BoundariesFollowFloorFormula()
    {
        // W=10, Z=8: zone i spans [i*10/8, (i+1)*10/8) => widths 1,1,1,2,1,1,1,2.
        // Paint column x with R = x*10; expected per-zone averages below.
        byte[] buffer = SyntheticFrames.Create(10, 10, (x, _) => ((byte)(x * 10), (byte)0, (byte)0));

        var edges = EdgeZoneExtractor.ExtractEdges(buffer, 10, 10, zonesPerEdge: 8);

        var expectedR = new[] { 0, 10, 20, 35, 50, 60, 70, 85 };
        for (int z = 0; z < 8; z++)
        {
            Assert.Equal(expectedR[z], edges.Top[z][0]);
            Assert.Equal(expectedR[z], edges.Bottom[z][0]);
        }
    }

    [Fact]
    public void ExtractEdges_HeightNotDivisibleByZones_VerticalBoundariesFollowFloorFormula()
    {
        // H=10, Z=8 on the left/right edges; paint row y with R = y*10.
        byte[] buffer = SyntheticFrames.Create(10, 10, (_, y) => ((byte)(y * 10), (byte)0, (byte)0));

        var edges = EdgeZoneExtractor.ExtractEdges(buffer, 10, 10, zonesPerEdge: 8);

        var expectedR = new[] { 0, 10, 20, 35, 50, 60, 70, 85 };
        for (int z = 0; z < 8; z++)
        {
            Assert.Equal(expectedR[z], edges.Left[z][0]);
            Assert.Equal(expectedR[z], edges.Right[z][0]);
        }
    }

    [Fact]
    public void ExtractEdges_WidthAtLeastZones_NoZoneIsEmpty()
    {
        // Solid non-black image, W=10 >= Z=8: every zone must carry the color (none left black).
        byte[] buffer = SyntheticFrames.Solid(10, 10, r: 40, g: 50, b: 60);

        var edges = EdgeZoneExtractor.ExtractEdges(buffer, 10, 10, zonesPerEdge: 8);

        foreach (int[][] edge in new[] { edges.Top, edges.Bottom, edges.Left, edges.Right })
            foreach (int[] zone in edge)
                Assert.Equal(new[] { 40, 50, 60 }, zone);
    }

    // ---------- ExtractEdges: degenerate sizes ----------

    [Fact]
    public void ExtractEdges_OneByOneImage_WorksAndLastZoneCarriesThePixel()
    {
        byte[] buffer = SyntheticFrames.Solid(1, 1, r: 10, g: 20, b: 30);

        var edges = EdgeZoneExtractor.ExtractEdges(buffer, 1, 1, zonesPerEdge: 8);

        var color = new[] { 10, 20, 30 };
        foreach (int[][] edge in new[] { edges.Top, edges.Bottom, edges.Left, edges.Right })
        {
            Assert.Equal(8, edge.Length);
            // Boundary formula [i*1/8, (i+1)*1/8): zones 0-6 are empty (black), zone 7 holds the pixel.
            for (int z = 0; z < 7; z++)
                Assert.Equal(Black, edge[z]);
            Assert.Equal(color, edge[7]);
        }
    }

    [Fact]
    public void ExtractEdges_ZonesGreaterThanWidth_DefinedNonCrashingOutput()
    {
        byte[] buffer = SyntheticFrames.Solid(3, 3, r: 50, g: 60, b: 70);

        var edges = EdgeZoneExtractor.ExtractEdges(buffer, 3, 3, zonesPerEdge: 8);

        var color = new[] { 50, 60, 70 };
        foreach (int[][] edge in new[] { edges.Top, edges.Bottom, edges.Left, edges.Right })
        {
            Assert.Equal(8, edge.Length);
            int colored = 0;
            foreach (int[] zone in edge)
            {
                Assert.Equal(3, zone.Length);
                if (zone[0] == 0 && zone[1] == 0 && zone[2] == 0) continue;
                Assert.Equal(color, zone);
                colored++;
            }

            Assert.True(colored >= 1, "At least one zone must carry the image color.");
        }
    }

    [Fact]
    public void ExtractEdges_ZonesBelowOne_ClampedToOneZone()
    {
        byte[] buffer = SyntheticFrames.Solid(4, 4, r: 1, g: 2, b: 3);

        var edges = EdgeZoneExtractor.ExtractEdges(buffer, 4, 4, zonesPerEdge: 0);

        Assert.Single(edges.Top);
        Assert.Single(edges.Bottom);
        Assert.Single(edges.Left);
        Assert.Single(edges.Right);
        Assert.Equal(new[] { 1, 2, 3 }, edges.Top[0]);
    }

    // ---------- ExtractEdges: validation ----------

    [Theory]
    [InlineData(0, 4)]
    [InlineData(-1, 4)]
    [InlineData(4, 0)]
    [InlineData(4, -1)]
    public void ExtractEdges_NonPositiveDimensions_Throws(int width, int height)
    {
        var buffer = new byte[64];
        Assert.Throws<ArgumentOutOfRangeException>(() => EdgeZoneExtractor.ExtractEdges(buffer, width, height, 4));
    }

    [Fact]
    public void ExtractEdges_BufferTooSmall_ThrowsArgumentException()
    {
        var buffer = new byte[4 * 4 * 4 - 1];
        Assert.Throws<ArgumentException>(() => EdgeZoneExtractor.ExtractEdges(buffer, 4, 4, 4));
    }

    // ---------- ExtractDominant ----------

    [Fact]
    public void ExtractDominant_PureSingleColor_ReturnsThatColor()
    {
        byte[] buffer = SyntheticFrames.Solid(4, 4, r: 200, g: 50, b: 100);

        int[] dominant = EdgeZoneExtractor.ExtractDominant(buffer, 4, 4);

        Assert.Equal(new[] { 200, 50, 100 }, dominant);
    }

    [Fact]
    public void ExtractDominant_SmallVividRedOnLargeGray_RedWinsViaSaturationWeighting()
    {
        // 10 pure-red pixels (weight 1.2 each) vs 90 gray pixels (weight 0.2 each):
        // R = (10*255*1.2 + 90*128*0.2) / 30 = 178.8 -> 179; G = B = 2304/30 = 76.8 -> 77.
        byte[] buffer = SyntheticFrames.Create(10, 10, (x, _) =>
            x == 0 ? ((byte)255, (byte)0, (byte)0) : ((byte)128, (byte)128, (byte)128));

        int[] dominant = EdgeZoneExtractor.ExtractDominant(buffer, 10, 10);

        Assert.Equal(new[] { 179, 77, 77 }, dominant);
        Assert.True(dominant[0] > dominant[1], "Red must dominate despite covering only 10% of the area.");
    }

    [Fact]
    public void ExtractDominant_NearBlackImage_ReturnsBlack()
    {
        // Mean Rec.601 luma of (1,1,1) is 1.0 < 2.0 => near-black guard kicks in.
        byte[] buffer = SyntheticFrames.Solid(4, 4, r: 1, g: 1, b: 1);

        Assert.Equal(Black, EdgeZoneExtractor.ExtractDominant(buffer, 4, 4));
    }

    [Fact]
    public void ExtractDominant_AllZeroImage_ReturnsBlack()
    {
        byte[] buffer = SyntheticFrames.Solid(4, 4, r: 0, g: 0, b: 0);

        Assert.Equal(Black, EdgeZoneExtractor.ExtractDominant(buffer, 4, 4));
    }

    [Fact]
    public void ExtractDominant_JustAboveNearBlackThreshold_ReturnsActualColor()
    {
        // (3,3,3) has mean luma 3.0 > 2.0, so the real (dim) color must come through.
        byte[] buffer = SyntheticFrames.Solid(4, 4, r: 3, g: 3, b: 3);

        Assert.Equal(new[] { 3, 3, 3 }, EdgeZoneExtractor.ExtractDominant(buffer, 4, 4));
    }

    [Fact]
    public void ExtractDominant_BufferTooSmall_ThrowsArgumentException()
    {
        var buffer = new byte[8];
        Assert.Throws<ArgumentException>(() => EdgeZoneExtractor.ExtractDominant(buffer, 4, 4));
    }
}

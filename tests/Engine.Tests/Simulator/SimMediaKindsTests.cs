#if SIMULATOR_ENABLED
using AmbientFx.Simulator.Content;
using Xunit;

namespace AmbientFx.Engine.Tests.Simulator;

/// <summary>
/// Story 10.6: the "media" content source routes a picked file to the right in-box decoder — a still
/// image (WIC) or a video (MediaPlayer). These cover that classification (case-insensitive, by extension)
/// and the Browse… file-dialog filter.
/// </summary>
public sealed class SimMediaKindsTests
{
    [Theory]
    [InlineData("clip.mp4")]
    [InlineData("C:/x/movie.MOV")]
    [InlineData("a.b.webm")]
    [InlineData("trailer.MKV")]
    public void IsVideo_TrueForVideoExtensions(string path)
    {
        Assert.True(SimMediaKinds.IsVideo(path));
        Assert.False(SimMediaKinds.IsImage(path));
    }

    [Theory]
    [InlineData("photo.png")]
    [InlineData("C:/x/pic.JPG")]
    [InlineData("frame.jpeg")]
    [InlineData("art.bmp")]
    public void IsImage_TrueForImageExtensions(string path)
    {
        Assert.True(SimMediaKinds.IsImage(path));
        Assert.False(SimMediaKinds.IsVideo(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("noext")]
    [InlineData("data.txt")]
    public void Neither_ForBlankOrUnknown(string? path)
    {
        Assert.False(SimMediaKinds.IsVideo(path));
        Assert.False(SimMediaKinds.IsImage(path));
    }

    [Fact]
    public void OpenFileFilter_OffersPicturesAndVideo()
    {
        string filter = SimMediaKinds.OpenFileFilter;
        Assert.Contains("Pictures & video", filter);
        Assert.Contains("*.png", filter);
        Assert.Contains("*.mp4", filter);
        Assert.Contains("All files", filter);
    }
}
#endif

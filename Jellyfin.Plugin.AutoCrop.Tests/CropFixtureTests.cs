using Jellyfin.Plugin.AutoCrop.Configuration;

namespace Jellyfin.Plugin.AutoCrop.Tests;

/// <summary>
/// The analysis on keyframes measured from real files: the plugin's exact ffmpeg command, its stderr
/// trimmed to the stream lines and one line per keyframe. Analysed with the default settings.
/// </summary>
public class CropFixtureTests
{
    private static readonly string[] All =
    {
        "chainsaw", "chernobyl", "dune", "dunkirk", "interstellar", "neagley", "oppenheimer", "peaky", "severance", "shogun",
    };

    public static TheoryData<string> AllFixtures() => new(All);

    private sealed record Fixture(int Width, int Height, IReadOnlyList<KeyframeSample> Samples, CropResult Result)
    {
        public CropBox Full => CropBox.Full(Width, Height);
    }

    private static Fixture Load(string name)
    {
        var parser = new CropdetectParser();

        // The trimmed stderr has no "Output #0" header; the input stream has the same frame size.
        parser.AddLine("Output #0, null, to 'pipe:':");
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{name}.cropdetect.txt")))
            parser.AddLine(line);

        var config = new PluginConfiguration();
        var result = new CropResult { FrameWidth = parser.FrameWidth!.Value, FrameHeight = parser.FrameHeight!.Value, PixelAspect = parser.PixelAspect };
        CropScanner.Analyse(result, parser.Samples, new AnalyzerOptions(config.MinimumBarPercent));
        return new Fixture(result.FrameWidth, result.FrameHeight, parser.Samples, result);
    }

    private static void AssertLetterbox(int top, int bottom, int width, CropBox? box)
    {
        Assert.NotNull(box);
        Assert.Equal(0, box.X);
        Assert.Equal(width, box.Width);
        Assert.InRange(box.Y, top - 4, top + 4);
        Assert.InRange(box.Bottom, bottom - 4, bottom + 4);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void TheWholePicture_ContainsEveryKeyframe(string name)
    {
        var fixture = Load(name);

        foreach (var sample in fixture.Samples.Where(s => s.Box != null))
            Assert.True(fixture.Result.Crop!.Contains(sample.Box!), $"{name} at {sample.Time}: {sample.Box} outside {fixture.Result.Crop}");
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void RealFilms_AreNeverSuspicious_AndBothCropsAreStandardShapes(string name)
    {
        var fixture = Load(name);

        Assert.Null(fixture.Result.SuspiciousReason);
        foreach (var box in new[] { fixture.Result.Crop!, fixture.Result.TheatreCrop! })
            Assert.True(box == fixture.Full || CropAnalyzer.Snap(box, fixture.Width, fixture.Height).Box == box, $"{name}: {box} isn't snapped");
    }

    [Theory]
    [InlineData("chainsaw")]
    [InlineData("chernobyl")]
    [InlineData("dune")]
    [InlineData("neagley")]
    [InlineData("peaky")]
    [InlineData("severance")]
    public void OneShape_IsUncroppedInBothModes(string name)
    {
        var fixture = Load(name);

        Assert.Equal(fixture.Full, fixture.Result.Crop);
        Assert.Equal(fixture.Full, fixture.Result.TheatreCrop);
        Assert.False(fixture.Result.HasCrop);
    }

    [Fact]
    public void Shogun_BurnedInBars_AreCroppedInBothModes()
    {
        var fixture = Load("shogun");

        AssertLetterbox(56, 1020, 1920, fixture.Result.Crop);
        Assert.Equal(fixture.Result.Crop, fixture.Result.TheatreCrop);
    }

    /// <summary>
    /// IMAX films switch between a scope picture and the full frame. The whole picture is the full
    /// frame (nothing is ever cut); the theatre crop is the scope picture, as a regular cinema shows them.
    /// </summary>
    [Theory]
    [InlineData("interstellar", 140, 940)]
    [InlineData("dunkirk", 102, 978)]
    [InlineData("oppenheimer", 206, 1952)]
    public void Imax_PlaysWholeInTheFullFrame_AndTheatreInItsScopePicture(string name, int top, int bottom)
    {
        var fixture = Load(name);

        Assert.Equal(fixture.Full, fixture.Result.Crop);
        AssertLetterbox(top, bottom, fixture.Width, fixture.Result.TheatreCrop);
        Assert.True(fixture.Result.HasCrop);
    }
}

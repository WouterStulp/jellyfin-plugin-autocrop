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
        public IReadOnlyList<CropSegment> Timeline
            => Result.Segments ?? new List<CropSegment> { new(0, Samples.Max(s => s.Time), Result.Crop!) };

        public double Duration => Timeline[^1].End;

        public double Share(CropBox box) => Timeline.Where(s => s.Box == box).Sum(s => s.Duration) / Duration;
    }

    private static Fixture Load(string name, double? maximumInsertSeconds = null)
    {
        var parser = new CropdetectParser();

        // The trimmed stderr has no "Output #0" header; the input stream has the same frame size.
        parser.AddLine("Output #0, null, to 'pipe:':");
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{name}.cropdetect.txt")))
            parser.AddLine(line);

        var config = new PluginConfiguration();
        var result = new CropResult { FrameWidth = parser.FrameWidth!.Value, FrameHeight = parser.FrameHeight!.Value, PixelAspect = parser.PixelAspect };
        var options = new AnalyzerOptions(
            config.MinimumBarPercent, config.MinimumSegmentSeconds, MaximumInsertSeconds: maximumInsertSeconds ?? config.MaximumInsertSeconds);
        CropScanner.Analyse(result, parser.Samples, parser.DurationSeconds ?? 0, options);
        return new Fixture(result.FrameWidth, result.FrameHeight, parser.Samples, result);
    }

    private static void AssertLetterbox(int top, int bottom, int width, CropBox box)
    {
        Assert.Equal(0, box.X);
        Assert.Equal(width, box.Width);
        Assert.InRange(box.Y, top - 4, top + 4);
        Assert.InRange(box.Bottom, bottom - 4, bottom + 4);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void EveryKeyframesPicture_IsInsideItsSegment_WhenInsertsAreNeverCut(string name)
    {
        var fixture = Load(name, maximumInsertSeconds: 0);

        foreach (var sample in fixture.Samples.Where(s => s.Box != null))
        {
            var segment = fixture.Timeline.First(s => s.Start <= sample.Time && sample.Time <= s.End);
            Assert.True(segment.Box.Contains(sample.Box!), $"{name} at {sample.Time}: {sample.Box} outside {segment.Box}");
        }
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void OnlyBriefInserts_AreEverCut(string name)
    {
        var fixture = Load(name);
        var limit = new PluginConfiguration().MaximumInsertSeconds;

        // Keyframes outside their segment's box come in runs; each run is one cut insert, under the limit.
        var outside = fixture.Samples
            .Where(s => s.Box != null)
            .OrderBy(s => s.Time)
            .Where(s => !fixture.Timeline.First(t => t.Start <= s.Time && s.Time <= t.End).Box.Contains(s.Box!))
            .ToList();
        var runStart = double.NaN;
        var previous = double.NaN;
        foreach (var sample in outside)
        {
            if (double.IsNaN(runStart) || sample.Time - previous > limit)
                runStart = sample.Time;
            Assert.True(sample.Time - runStart < limit, $"{name}: picture cut from {runStart} to {sample.Time}");
            previous = sample.Time;
        }
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void RealFilms_AreNeverSuspicious(string name)
    {
        var fixture = Load(name);

        Assert.Null(fixture.Result.SuspiciousReason);
        Assert.All(fixture.Timeline, s => Assert.True(
            s.Box == CropBox.Full(fixture.Width, fixture.Height) || CropAnalyzer.Snap(s.Box, fixture.Width, fixture.Height).Box == s.Box,
            $"{name}: {s.Box} at {s.Start} isn't snapped"));
    }

    [Theory]
    [InlineData("chainsaw")]
    [InlineData("chernobyl")]
    [InlineData("dune")]
    [InlineData("neagley")]
    [InlineData("peaky")]
    [InlineData("severance")]
    public void OneShape_IsOneUncroppedSegment(string name)
    {
        var fixture = Load(name);

        Assert.Null(fixture.Result.Segments);
        Assert.Equal(CropBox.Full(fixture.Width, fixture.Height), fixture.Result.Crop);
        Assert.False(fixture.Result.HasCrop);
    }

    [Fact]
    public void Shogun_BurnedInBars_AreOneCroppedSegment()
    {
        var fixture = Load("shogun");

        Assert.Null(fixture.Result.Segments);
        AssertLetterbox(56, 1020, 1920, fixture.Result.Crop!);
    }

    [Theory]
    [InlineData("interstellar", 140, 940, 0.30, 0.10)]
    [InlineData("dunkirk", 102, 978, 0.20, 0.10)]
    [InlineData("oppenheimer", 206, 1952, 0.30, 0.10)]
    public void Imax_SwitchesBetweenItsScopeMatteAndTheFullFrame(string name, int top, int bottom, double minimumScope, double minimumFull)
    {
        var fixture = Load(name);
        var full = CropBox.Full(fixture.Width, fixture.Height);
        var segments = fixture.Result.Segments!;
        var scope = segments.Select(s => s.Box).First(b => b != full);

        Assert.InRange(segments.Count, 2, 199);
        AssertLetterbox(top, bottom, fixture.Width, scope);
        Assert.All(segments, s => Assert.True(s.Box == scope || s.Box == full, $"{s.Box} at {s.Start}"));
        // Only a brief zoom-out between two long scenes may be shorter than the minimum.
        Assert.All(segments.Where(s => s.Duration < 30), s => Assert.True(s.Box == full, $"{s.Duration} s of {s.Box} at {s.Start}"));
        Assert.True(fixture.Share(scope) > minimumScope, $"scope {fixture.Share(scope):P1}");
        Assert.True(fixture.Share(full) > minimumFull, $"full frame {fixture.Share(full):P1}");
        Assert.Equal(full, fixture.Result.Crop);
    }
}

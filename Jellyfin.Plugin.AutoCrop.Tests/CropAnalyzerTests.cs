namespace Jellyfin.Plugin.AutoCrop.Tests;

public class CropAnalyzerTests
{
    private const int W = 1920;
    private const int H = 1080;

    private static readonly AnalyzerOptions Options = new(MinimumBarPercent: 1.0, MinimumSegmentSeconds: 2.0);

    // 2.39:1 and 1.90:1 pictures centred in a 16:9 frame.
    private static readonly CropBox Scope = new(0, 138, 1920, 804);
    private static readonly CropBox Imax = new(0, 35, 1920, 1010);
    private static readonly CropBox Full = CropBox.Full(W, H);

    private static KeyframeSample K(double t, CropBox? box) => new(t, box);

    private static List<KeyframeSample> Every2s(double from, double to, CropBox? box)
    {
        var list = new List<KeyframeSample>();
        for (var t = from; t < to; t += 2)
            list.Add(K(t, box));
        return list;
    }

    private static IReadOnlyList<CropSegment> Segments(IReadOnlyList<KeyframeSample> samples, double duration, AnalyzerOptions? options = null)
        => CropAnalyzer.Segments(samples, W, H, duration, options ?? Options);

    // ----- trickplay thumbnails -----

    private static List<KeyframeSample> Thumbnails(int count, Func<int, CropBox?> box)
        => Enumerable.Range(0, count).Select(i => K(i * 10, box(i))).ToList();

    [Fact]
    public void ShowsNoBars_FullFrameThumbnailsWithDarkShots()
    {
        // Dark shots give smaller, scattered boxes; they never recur as a matte.
        var thumbnails = Thumbnails(200, i => i % 10 == 3 ? new CropBox(40 + i % 7, 20 + i % 11, 200, 100) : i % 25 == 0 ? null : CropBox.Full(320, 180));

        Assert.True(CropAnalyzer.ShowsNoBars(thumbnails, 320, 180));
    }

    [Fact]
    public void ShowsNoBars_NotWithBarsInEveryThumbnail()
    {
        Assert.False(CropAnalyzer.ShowsNoBars(Thumbnails(200, _ => new CropBox(0, 10, 320, 160)), 320, 180));
        Assert.False(CropAnalyzer.ShowsNoBars(Thumbnails(200, _ => new CropBox(0, 1, 320, 179)), 320, 180));
    }

    [Fact]
    public void ShowsNoBars_NotWithARecurringMatteBesideFullFrame()
    {
        // An IMAX film: full frame now and then, 2.39:1 in 10 of 200 thumbnails (5%).
        var thumbnails = Thumbnails(200, i => i < 10 ? new CropBox(0, 23, 320, 134) : CropBox.Full(320, 180));

        Assert.False(CropAnalyzer.ShowsNoBars(thumbnails, 320, 180));
    }

    [Fact]
    public void ShowsNoBars_NotFromTooFewThumbnails()
    {
        Assert.False(CropAnalyzer.ShowsNoBars(Thumbnails(CropAnalyzer.MinimumThumbnails - 1, _ => CropBox.Full(320, 180)), 320, 180));
        Assert.True(CropAnalyzer.ShowsNoBars(Thumbnails(CropAnalyzer.MinimumThumbnails, _ => CropBox.Full(320, 180)), 320, 180));
    }

    // ----- whole-file union -----

    [Fact]
    public void Union_TakesTheOuterBoundsOfEveryKeyframe()
    {
        var union = CropAnalyzer.Union(new[] { K(0, new CropBox(0, 60, 1920, 958)), K(2, new CropBox(0, 59, 1920, 961)) }, W, H, 1.0);

        Assert.Equal(new CropBox(0, 59, 1920, 961), union);
    }

    [Fact]
    public void Union_IgnoresFullyBlackKeyframes()
    {
        var union = CropAnalyzer.Union(new[] { K(0, null), K(2, new CropBox(0, 60, 1920, 960)), K(4, null) }, W, H, 1.0);

        Assert.Equal(new CropBox(0, 60, 1920, 960), union);
    }

    [Fact]
    public void Union_AllBlack_IsTheFullFrame()
    {
        Assert.Equal(Full, CropAnalyzer.Union(new[] { K(0, null) }, W, H, 1.0));
    }

    [Fact]
    public void Union_OneFullFrameKeyframe_MeansNoCrop()
    {
        var samples = Every2s(0, 100, Scope);
        samples.Add(K(100, Full));

        Assert.Equal(Full, CropAnalyzer.Union(samples, W, H, 1.0));
    }

    [Fact]
    public void Union_Pillarbox_CropsLeftAndRight()
    {
        var union = CropAnalyzer.Union(new[] { K(0, new CropBox(240, 0, 1440, 1080)) }, W, H, 1.0);

        Assert.Equal(new CropBox(240, 0, 1440, 1080), union);
    }

    [Theory]
    [InlineData(10, 1.0, 0)] // 10 px < 1% of 1080: noise, not a bar
    [InlineData(11, 1.0, 11)] // 11 px >= 10.8 px
    [InlineData(60, 10.0, 0)] // a higher minimum ignores real but thin bars
    public void MinimumBar_AppliesPerSide(int bar, double minimumPercent, int expectedTop)
    {
        var union = CropAnalyzer.Union(new[] { K(0, new CropBox(0, bar, W, H - bar - 60)) }, W, H, minimumPercent);

        Assert.Equal(expectedTop, union.Y);
        Assert.Equal(minimumPercent >= 10 ? H : H - 60, union.Bottom);
    }

    [Fact]
    public void MinimumBar_TwoPixelEdge_IsNotCropped()
    {
        Assert.Equal(Full, CropAnalyzer.Union(new[] { K(0, new CropBox(2, 2, W - 4, H - 4)) }, W, H, 1.0));
    }

    // ----- per-scene segments -----

    [Fact]
    public void Segments_ConstantPicture_IsOneSegment()
    {
        var segments = Segments(Every2s(0, 60, Scope), 60);

        Assert.Equal(new[] { new CropSegment(0, 60, Scope) }, segments);
    }

    [Fact]
    public void Segments_NearlyEqualBoxes_Merge()
    {
        var samples = new[] { K(0, Scope), K(2, Scope with { Y = 137, Height = 806 }), K(4, Scope with { Y = 140 }) };

        var segment = Assert.Single(Segments(samples, 6));
        Assert.Equal(137, segment.Box.Y);
        Assert.Equal(Scope.Bottom + 2, segment.Box.Bottom);
    }

    [Fact]
    public void Segments_FadeToBlack_InheritsTheNeighbouringBox()
    {
        var samples = Every2s(0, 20, Scope).Concat(Every2s(20, 30, null)).Concat(Every2s(30, 50, Scope)).ToList();

        Assert.Equal(new[] { new CropSegment(0, 50, Scope) }, Segments(samples, 50));
    }

    [Fact]
    public void Segments_AllBlack_IsOneFullFrameSegment()
    {
        Assert.Equal(new[] { new CropSegment(0, 10, Full) }, Segments(Every2s(0, 10, null), 10));
    }

    [Fact]
    public void Segments_ImaxStretches_GetTheLargerBox()
    {
        var samples = Every2s(0, 600, Scope).Concat(Every2s(600, 900, Imax)).Concat(Every2s(900, 1500, Scope)).ToList();

        var segments = Segments(samples, 1500);

        Assert.Equal(3, segments.Count);
        Assert.Equal(Scope, segments[0].Box);
        Assert.Equal(Imax, segments[1].Box);
        Assert.Equal(Scope, segments[2].Box);
    }

    [Fact]
    public void Segments_BoundaryBetweenKeyframes_IsPaddedWithTheLargerBox()
    {
        // The change happened somewhere between the last 2.39 keyframe (598) and the first 1.90 one
        // (600), and back between 898 and 900: the IMAX box must cover both gaps.
        var samples = Every2s(0, 600, Scope).Concat(Every2s(600, 900, Imax)).Concat(Every2s(900, 1500, Scope)).ToList();

        var segments = Segments(samples, 1500);

        Assert.Equal(598, segments[1].Start);
        Assert.Equal(900, segments[1].End);
        Assert.Equal(598, segments[0].End);
        Assert.Equal(900, segments[2].Start);
    }

    [Fact]
    public void Segments_NeverCropPictureSeenInsideThem()
    {
        var samples = Every2s(0, 40, Scope)
            .Concat(new[] { K(40, Imax), K(41, new CropBox(100, 0, 1720, 1080)) })
            .Concat(Every2s(42, 80, Scope))
            .Concat(Every2s(80, 120, new CropBox(0, 100, 1920, 880)))
            .ToList();

        var segments = Segments(samples, 120);

        foreach (var sample in samples.Where(s => s.Box != null))
        {
            var segment = segments.Single(s => s.Start <= sample.Time && sample.Time < s.End || sample.Time == s.End && s == segments[^1]);
            Assert.Equal(segment.Box, segment.Box.Union(sample.Box!));
        }
    }

    [Fact]
    public void Segments_ShortWideFlash_WidensTheCropInsteadOfBeingCut()
    {
        // One full-frame keyframe at 30 s inside a 2.39 film: under 2 s on its own.
        var samples = Every2s(0, 30, Scope).Append(K(30, Full)).Concat(Every2s(32, 60, Scope)).ToList();

        var segments = Segments(samples, 60);

        Assert.Contains(segments, s => s.Box == Full && s.Start <= 30 && s.End >= 30);
        Assert.DoesNotContain(segments, s => s.Duration < 2 && segments.Count > 1);
    }

    [Fact]
    public void Segments_ShortSegment_JoinsTheNeighbourThatLosesLeastZoom()
    {
        // A 1 s 2.20 stretch between a long 2.39 and a long 1.90 part goes into the 1.90 one.
        var mid = new CropBox(0, 104, 1920, 872);
        var samples = Every2s(0, 60, Scope).Append(K(60, mid)).Concat(Every2s(61, 121, Imax)).ToList();

        var segments = Segments(samples, 121, Options with { MinimumSegmentSeconds = 3 });

        Assert.Equal(2, segments.Count);
        Assert.Equal(Scope, segments[0].Box);
        Assert.Equal(Imax, segments[1].Box);
        Assert.Equal(58, segments[0].End);
    }

    [Fact]
    public void Segments_TooMany_FallBackToTheWholeFileUnion()
    {
        // 50 alternations of 20 s each.
        var samples = Enumerable.Range(0, 500).Select(i => K(i * 2, i / 10 % 2 == 0 ? Scope : Imax)).ToList();

        var segments = Segments(samples, 1000, Options with { MaxSegments = 10 });

        Assert.Equal(new[] { new CropSegment(0, 1000, Imax) }, segments);
    }

    [Fact]
    public void Segments_DarkShotsAndTitleCards_StayInTheirScenesMatte()
    {
        // A scope film whose dark shots, a title card and the credits give smaller, scattered boxes.
        var samples = Every2s(0, 600, Scope)
            .Concat(Every2s(600, 700, new CropBox(400, 300, 1100, 400)))
            .Concat(Every2s(700, 1300, Scope))
            .Concat(Every2s(1300, 1400, new CropBox(820, 500, 276, 100)))
            .Concat(Every2s(1400, 2000, Scope))
            .Concat(Every2s(2000, 2200, new CropBox(360, 380, 1193, 40)))
            .ToList();

        Assert.Equal(new[] { new CropSegment(0, 2200, Scope) }, Segments(samples, 2200));
    }

    [Fact]
    public void Segments_DarkShotInsideAnImaxScene_DoesNotSplitIt()
    {
        // A dark IMAX shot whose box fits the scope matte lands there, and the short stretch is
        // absorbed back into the IMAX scene with the default minimum length.
        var samples = Every2s(0, 600, Scope)
            .Concat(Every2s(600, 700, Imax))
            .Concat(Every2s(700, 710, new CropBox(300, 200, 1300, 600)))
            .Concat(Every2s(710, 900, Imax))
            .Concat(Every2s(900, 1500, Scope))
            .ToList();

        var segments = Segments(samples, 1500, Options with { MinimumSegmentSeconds = 30 });

        Assert.Equal(new[] { Scope, Imax, Scope }, segments.Select(s => s.Box));
    }

    [Fact]
    public void Segments_RecurringPillarbox_IsAMatte()
    {
        var pillar = new CropBox(240, 0, 1440, 1080);
        var samples = Every2s(0, 300, pillar).Concat(Every2s(300, 600, Full)).Concat(Every2s(600, 900, pillar)).ToList();

        var segments = Segments(samples, 900);

        Assert.Equal(new[] { pillar, Full, pillar }, segments.Select(s => s.Box));
    }

    [Fact]
    public void Mattes_RareOrImplausibleShapes_DontCount()
    {
        // 19 keyframes of a 2.39 picture is under the minimum of 20; a 4:1 strip is no film format.
        var strip = new CropBox(0, 300, 1920, 480);
        var boxes = Enumerable.Repeat(Full, 400).Concat(Enumerable.Repeat(Scope, 19)).Concat(Enumerable.Repeat(strip, 100)).ToList();

        Assert.Equal(new[] { Full }, CropAnalyzer.Mattes(boxes, 9, 5, Options));
    }

    [Fact]
    public void Segments_KeyframesOutOfOrder_AreSorted()
    {
        var samples = Every2s(0, 600, Scope).Concat(Every2s(600, 900, Imax)).Concat(Every2s(900, 1500, Scope)).Reverse().ToList();

        Assert.Equal(3, Segments(samples, 1500).Count);
    }

    [Fact]
    public void Segments_ThinBarsAreDroppedPerSegment()
    {
        var almostFull = new CropBox(0, 4, 1920, 1072);
        var samples = Every2s(0, 20, Scope).Concat(Every2s(20, 40, almostFull)).ToList();

        var segments = Segments(samples, 40);

        Assert.Equal(Full, segments[^1].Box);
    }
}

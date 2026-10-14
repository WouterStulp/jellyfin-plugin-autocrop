namespace Jellyfin.Plugin.AutoCrop.Tests;

public class CropAnalyzerTests
{
    private const int W = 1920;
    private const int H = 1080;

    private static readonly AnalyzerOptions Options = new(MinimumBarPercent: 1.0);

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

    // ----- the theatre crop -----

    private static CropBox Theatre(IReadOnlyList<KeyframeSample> samples) => CropAnalyzer.TheatreCrop(samples, W, H, Options);

    [Fact]
    public void TheatreCrop_IsTheNarrowestRecurringLetterbox()
    {
        // A 2.39:1 film with IMAX scenes in the full frame is cut to 2.39:1 throughout, as a regular
        // cinema shows it; the whole picture is the full frame.
        var samples = Every2s(0, 600, Scope).Concat(Every2s(600, 900, Full)).Concat(Every2s(900, 1500, Scope)).ToList();

        Assert.Equal(Scope, Theatre(samples));
        Assert.Equal(Full, CropAnalyzer.Union(samples, W, H, 1.0));
    }

    [Fact]
    public void TheatreCrop_PicksTheNarrowestOfSeveralLetterboxes()
    {
        var samples = Every2s(0, 300, Imax).Concat(Every2s(300, 600, Scope)).Concat(Every2s(600, 900, Full)).ToList();

        Assert.Equal(Scope, Theatre(samples));
    }

    [Fact]
    public void TheatreCrop_OneShape_IsTheWholePicture()
    {
        Assert.Equal(Scope, Theatre(Every2s(0, 600, Scope)));
        Assert.Equal(Full, Theatre(Every2s(0, 600, Full)));
        Assert.Equal(Full, Theatre(Array.Empty<KeyframeSample>()));
        Assert.Equal(Full, Theatre(new[] { K(0, null) }));
    }

    [Fact]
    public void TheatreCrop_IgnoresPillarboxShapes()
    {
        // A 4:3 part in a 16:9 episode: cropping to it would cut the sides of the rest, and on a wide
        // screen it only adds to the side bars.
        var pillar = new CropBox(240, 0, 1440, 1080);
        var samples = Every2s(0, 300, pillar).Concat(Every2s(300, 600, Full)).Concat(Every2s(600, 900, pillar)).ToList();

        Assert.Equal(Full, Theatre(samples));
        Assert.Contains(pillar, CropAnalyzer.Mattes(samples.Select(s => s.Box!).ToList(), 9, 5, Options));
    }

    [Fact]
    public void TheatreCrop_DarkShotsAndTitleCards_DontMakeAShape()
    {
        var samples = Every2s(0, 600, Scope)
            .Concat(new[] { K(600, new CropBox(300, 200, 1300, 600)), K(602, new CropBox(600, 400, 276, 100)), K(604, null) })
            .ToList();

        Assert.Equal(Scope, Theatre(samples));
    }

    [Fact]
    public void TheatreCrop_RareShapes_DontCount()
    {
        // 19 keyframes of 2.39:1 in a full-frame film is under the minimum of 20.
        var samples = Every2s(0, 800, Full).Concat(Enumerable.Range(0, 19).Select(i => K(800 + (i * 2), Scope))).ToList();

        Assert.Equal(Full, Theatre(samples));
    }

    [Fact]
    public void TheatreCrop_DropsThinBars()
    {
        Assert.Equal(Full, Theatre(Every2s(0, 600, new CropBox(0, 4, 1920, 1072))));
    }

    [Fact]
    public void TheatreCrop_KeyframesOutOfOrder_DontMatter()
    {
        var samples = Every2s(0, 600, Scope).Concat(Every2s(600, 900, Full)).Reverse().ToList();

        Assert.Equal(Scope, Theatre(samples));
    }

    [Fact]
    public void Mattes_RareOrImplausibleShapes_DontCount()
    {
        // 19 keyframes of a 2.39 picture is under the minimum of 20; a 4:1 strip is no film format.
        var strip = new CropBox(0, 300, 1920, 480);
        var boxes = Enumerable.Repeat(Full, 400).Concat(Enumerable.Repeat(Scope, 19)).Concat(Enumerable.Repeat(strip, 100)).ToList();

        Assert.Equal(new[] { Full }, CropAnalyzer.Mattes(boxes, 9, 5, Options));
    }

    // ----- standard aspect ratios -----

    private static void AssertSnapped(CropBox measured, CropBox snapped, double ratio)
    {
        Assert.True(snapped.Contains(measured), $"{snapped} cuts {measured}");
        Assert.Equal(ratio, (double)snapped.Width / snapped.Height, 2);
    }

    [Fact]
    public void Snap_ScopeGrowsTallerTo239()
    {
        // 2.394:1 is nearer 2.39 than 2.40; 2.39 needs 803.3 rows, so 804, one more on each side.
        var measured = new CropBox(0, 139, 1920, 802);

        var (box, suspicious) = CropAnalyzer.Snap(measured, W, H);

        Assert.Null(suspicious);
        Assert.Equal(new CropBox(0, 138, 1920, 804), box);
        AssertSnapped(measured, box, 2.39);
    }

    [Fact]
    public void Snap_4KScope_GrowsTo239()
    {
        var measured = new CropBox(0, 277, 3840, 1606);

        var (box, suspicious) = CropAnalyzer.Snap(measured, 3840, 2160);

        Assert.Null(suspicious);
        Assert.Equal(new CropBox(0, 277, 3840, 1607), box);
        AssertSnapped(measured, box, 2.39);
    }

    [Fact]
    public void Snap_GrowsWiderToExactly2To1_CentredOnTheMeasuredBox()
    {
        var measured = new CropBox(60, 60, 1800, 901);

        var (box, _) = CropAnalyzer.Snap(measured, W, H);

        Assert.Equal(new CropBox(59, 60, 1802, 901), box);
        Assert.Equal(2.0, (double)box.Width / box.Height, 3);
        AssertSnapped(measured, box, 2.0);
    }

    [Fact]
    public void Snap_2To1ThatWouldNeedMoreThanTheFrameWidth_StaysAsMeasured()
    {
        // 1920x961 is 1.998:1. Exactly 2:1 would need 1922 columns or 960 rows, and 960 rows would cut
        // one measured row: the box keeps its measured size, which is within tolerance of 2.00.
        var measured = new CropBox(0, 59, 1920, 961);

        Assert.Equal((measured, (string?)null), CropAnalyzer.Snap(measured, W, H));
    }

    [Fact]
    public void Snap_GrowingPastAnEdge_ShiftsInsideTheFrame()
    {
        var measured = new CropBox(0, 0, 1920, 802);

        var (box, _) = CropAnalyzer.Snap(measured, W, H);

        Assert.Equal(new CropBox(0, 0, 1920, 804), box);
    }

    [Theory]
    [InlineData(240, 0, 1440, 1080)] // 4:3 pillarbox
    [InlineData(0, 140, 1920, 800)] // 2.40
    [InlineData(0, 0, 1920, 1080)] // full frame, 16:9
    public void Snap_AlreadyStandard_IsUnchanged(int x, int y, int width, int height)
    {
        var measured = new CropBox(x, y, width, height);

        Assert.Equal((measured, (string?)null), CropAnalyzer.Snap(measured, W, H));
    }

    [Fact]
    public void Snap_FullFrame_IsNeverSuspicious()
    {
        // A 1.50:1 frame isn't a standard ratio, but uncropped there is nothing to doubt.
        Assert.Equal((CropBox.Full(720, 480), (string?)null), CropAnalyzer.Snap(CropBox.Full(720, 480), 720, 480));
    }

    [Fact]
    public void Snap_UsesTheDisplayAspectOfNonSquarePixels()
    {
        // A 16:9 DVD (720x480, SAR 32:27): 720x362 is 2.36:1 on screen and snaps to 2.35.
        var measured = new CropBox(0, 59, 720, 362);

        var (box, suspicious) = CropAnalyzer.Snap(measured, 720, 480, 32.0 / 27);

        Assert.Null(suspicious);
        Assert.True(box.Contains(measured));
        Assert.Equal(new CropBox(0, 58, 720, 364), box);

        // One row is 0.3% at this height, so the ratio is as close to 2.35 as whole rows allow.
        Assert.InRange(box.Width * 32.0 / 27 / box.Height, 2.34, 2.35);
    }

    [Fact]
    public void Snap_BogusStrip_IsSuspicious()
    {
        var measured = new CropBox(375, 441, 1169, 197);

        var (box, suspicious) = CropAnalyzer.Snap(measured, W, H);

        Assert.Equal(measured, box);
        Assert.Equal("Picture 5.93:1 is outside 1.25:1 to 2.90:1", suspicious);
    }

    [Fact]
    public void Snap_NoStandardRatioNearby_IsSuspicious()
    {
        // 1.55:1 lies between 1.43 and 1.66, more than 1.5% from both.
        Assert.Equal("Picture 1.55:1 is no standard aspect ratio", CropAnalyzer.Snap(new CropBox(123, 0, 1674, 1080), W, H).Suspicious);
    }

    [Fact]
    public void Snap_UnderHalfTheFrame_IsSuspicious()
    {
        Assert.StartsWith("Picture covers only 26%", CropAnalyzer.Snap(new CropBox(460, 270, 1000, 540), W, H).Suspicious);
    }

    private static CropResult Analysed(IReadOnlyList<KeyframeSample> samples)
    {
        var result = new CropResult { FrameWidth = W, FrameHeight = H };
        CropScanner.Analyse(result, samples, Options);
        return result;
    }

    [Fact]
    public void Analyse_SuspiciousWholeFile_IsNotCropped()
    {
        var result = Analysed(Every2s(0, 600, new CropBox(375, 441, 1169, 197)));

        Assert.True(result.Suspicious);
        Assert.Equal(Full, result.Crop);
        Assert.Equal(Full, result.TheatreCrop);
        Assert.False(result.HasCrop);
    }

    [Fact]
    public void Analyse_SuspiciousTheatreCrop_FallsBackToTheWholePicture()
    {
        // 1920x914 is 2.10:1, no standard ratio within 1.5%: the whole picture (the full frame) plays instead.
        var result = Analysed(Every2s(0, 600, Full).Concat(Every2s(600, 1200, new CropBox(0, 83, 1920, 914))).ToList());

        Assert.False(result.Suspicious);
        Assert.Equal(Full, result.Crop);
        Assert.Equal(Full, result.TheatreCrop);
    }

    [Fact]
    public void Analyse_SnapsBothCrops()
    {
        var scope = new CropBox(0, 139, 1920, 802);
        var result = Analysed(Every2s(0, 600, scope).Concat(Every2s(600, 900, Imax)).Concat(Every2s(900, 1500, scope)).ToList());

        // 1920x1010 is 1.901:1; 1.90 needs 1010.5 rows.
        Assert.Equal(Imax with { Height = 1011 }, result.Crop);
        Assert.Equal(Scope, result.TheatreCrop);
        Assert.True(result.HasCrop);
    }
}

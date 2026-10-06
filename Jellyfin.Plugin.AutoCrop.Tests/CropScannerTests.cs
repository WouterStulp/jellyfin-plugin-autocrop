using System.Diagnostics;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.AutoCrop.Tests;

[Collection("Plugin")]
public class CropScannerTests : IDisposable
{
    private readonly string _dir = TestPlugin.TempDir();

    public CropScannerTests()
    {
        TestPlugin.Create(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    public static TheoryData<BaseItem, bool> EligibilityCases() => new()
    {
        { new Movie { Path = "/media/film.mkv" }, true },
        { new Episode { Path = "/media/show/s01e01.mp4" }, true },
        { new Movie { Path = "/media/film.strm" }, false },
        { new Movie { Path = "https://example.invalid/film.mkv" }, false },
        { new Movie { Path = "/media/film.mkv", IsVirtualItem = true }, false },
        { new Movie { Path = "/media/film.mkv", IsPlaceHolder = true }, false },
        { new Movie { Path = "/media/film", VideoType = VideoType.BluRay }, false },
        { new Movie { Path = "" }, false },
        { new Video { Path = "/media/home-video.mkv" }, false },
    };

    [Theory]
    [MemberData(nameof(EligibilityCases))]
    public void IsEligible_OnlyLocalMovieAndEpisodeFiles(BaseItem item, bool expected)
    {
        Assert.Equal(expected, CropScanner.IsEligible(item));
    }

    [Fact]
    public void Arguments_PutHardwareDecodingBeforeTheInput()
    {
        var args = CropScanner.Arguments("/media/film.mkv", new[] { "-hwaccel", "vaapi" });

        Assert.Equal(args.ToList().IndexOf("-i") - 2, args.ToList().IndexOf("-hwaccel"));
        Assert.Equal(CropScanner.Arguments("/media/film.mkv").Count + 2, args.Count);
    }

    [Fact]
    public void HardwareDecoding_FollowsJellyfinsAcceleration()
    {
        Assert.Null(CropScanner.HardwareDecodingArguments(null));
        Assert.Null(CropScanner.HardwareDecodingArguments(new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.none }));
        Assert.Equal(
            new[] { "-hwaccel", "vaapi", "-hwaccel_device", "/dev/dri/renderD129" },
            CropScanner.HardwareDecodingArguments(new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.vaapi, VaapiDevice = "/dev/dri/renderD129" }));
        Assert.Equal(
            new[] { "-hwaccel", "cuda" },
            CropScanner.HardwareDecodingArguments(new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.nvenc }));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                new[] { "-hwaccel", "vaapi", "-hwaccel_device", "/dev/dri/renderD128" },
                CropScanner.HardwareDecodingArguments(new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.qsv, QsvDevice = string.Empty }));
        }
    }

    // ----- end to end against a real ffmpeg (AUTOCROP_FFMPEG, or ffmpeg on the PATH) -----

    private static string? Ffmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("AUTOCROP_FFMPEG");
        if (!string.IsNullOrEmpty(configured))
            return configured;

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, "ffmpeg"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// A 640x360 (16:9) clip with one keyframe per second, made of parts that each show a test
    /// pattern of the given height centred on black: (seconds, picture height).
    /// </summary>
    private string MakeClip(string ffmpeg, string name, string pixelFormat, params (int Seconds, int Height)[] parts)
        => MakeClip(ffmpeg, name, pixelFormat, Array.Empty<string>(), parts);

    private string MakeClip(string ffmpeg, string name, string pixelFormat, string[] encoderArguments, params (int Seconds, int Height)[] parts)
    {
        var path = Path.Combine(_dir, name);
        var graph = string.Join(';', parts.Select((p, i) =>
            $"testsrc2=s=640x{p.Height}:r=24:d={p.Seconds},pad=640:360:0:{(360 - p.Height) / 2}:black[v{i}]"));
        graph += ";" + string.Concat(parts.Select((_, i) => $"[v{i}]")) + $"concat=n={parts.Length}:v=1:a=0";

        RunFfmpeg(ffmpeg, new[] { "-f", "lavfi", "-i", graph, "-c:v", "libx264", "-preset", "ultrafast", "-g", "24" }
            .Concat(encoderArguments)
            .Concat(new[] { "-pix_fmt", pixelFormat, path }));
        return path;
    }

    private static void RunFfmpeg(string ffmpeg, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true };
        foreach (var argument in new[] { "-v", "error", "-y" }.Concat(arguments))
            startInfo.ArgumentList.Add(argument);

        var run = Process.Start(startInfo)!;
        var error = run.StandardError.ReadToEnd();
        run.WaitForExit();
        Assert.True(run.ExitCode == 0, error);
    }

    /// <summary>
    /// Trickplay made the way Jellyfin makes it: a 320 px wide thumbnail every 10 s, tiled into 10x10
    /// sheets 0.jpg, 1.jpg, ... (the last one padded with black cells), in a folder laid out like
    /// Jellyfin's. Returns a trickplay manager that points there, and the thumbnail count.
    /// </summary>
    private (ITrickplayManager Manager, int Thumbnails) MakeTrickplay(string ffmpeg, string clip, Movie movie)
    {
        var thumbnails = Path.Combine(_dir, "thumbnails-" + movie.Id.ToString("N"));
        var sheets = Path.Combine(_dir, "trickplay", movie.Id.ToString("N")[..2], movie.Id.ToString("N"), "320 - 10x10");
        Directory.CreateDirectory(thumbnails);
        Directory.CreateDirectory(sheets);
        RunFfmpeg(ffmpeg, new[] { "-i", clip, "-vf", "fps=1/10,scale=320:-2", Path.Combine(thumbnails, "%d.jpg") });
        var count = Directory.GetFiles(thumbnails).Length;
        RunFfmpeg(ffmpeg, new[] { "-framerate", "1", "-i", Path.Combine(thumbnails, "%d.jpg"), "-vf", "tile=10x10", "-start_number", "0", Path.Combine(sheets, "%d.jpg") });

        var manager = Substitute.For<ITrickplayManager>();
        manager.GetTrickplayResolutions(movie.Id).Returns(new Dictionary<int, TrickplayInfo>
        {
            [320] = new() { ItemId = movie.Id, Width = 320, Height = 180, TileWidth = 10, TileHeight = 10, ThumbnailCount = count, Interval = 10000 },
        });
        manager.GetTrickplayDirectory(movie, 10, 10, 320, false).Returns(sheets);
        return (manager, count);
    }

    private CropStore Store() => new(() => Path.Combine(_dir, "crops.json"), NullLogger<CropStore>.Instance);

    private static CropScanner Scanner(
        CropStore store, string? ffmpeg = null, EncodingOptions? encoding = null, ITrickplayManager? trickplay = null)
    {
        var encoder = Substitute.For<IMediaEncoder>();
        encoder.EncoderPath.Returns(ffmpeg ?? "ffmpeg-must-not-run");
        var configuration = Substitute.For<IConfigurationManager>();
        configuration.GetConfiguration("encoding").Returns(encoding);
        return new CropScanner(encoder, configuration, trickplay ?? Substitute.For<ITrickplayManager>(), store, NullLogger<CropScanner>.Instance);
    }

    private static Movie MovieAt(string path, int seconds = 0) => new()
    {
        Id = Guid.NewGuid(),
        Path = path,
        Name = Path.GetFileName(path),
        Width = 640,
        Height = 360,
        RunTimeTicks = TimeSpan.FromSeconds(seconds).Ticks,
    };

    private Task<CropResult> Scan(string ffmpeg, string path, EncodingOptions? encoding = null)
        => Scan(ffmpeg, MovieAt(path), encoding);

    private async Task<CropResult> Scan(string ffmpeg, Movie movie, EncodingOptions? encoding = null, ITrickplayManager? trickplay = null)
    {
        var path = movie.Path;
        var store = Store();
        var scanner = Scanner(store, ffmpeg, encoding, trickplay);

        var result = await scanner.ScanAsync(movie, CancellationToken.None);

        Assert.Same(result, store.GetCurrent(movie.Id, path));
        Assert.False(scanner.NeedsScan(movie));
        Assert.Equal(CropAnalyzer.Version, result.AnalysisVersion);
        if (!result.Failed)
            Assert.Equal(result.Keyframes, store.GetSamples(movie.Id)!.Value.Samples.Count);
        return result;
    }

    /// <summary>A movie on disk with a stored result from the first analysis, which had no version.</summary>
    private (Movie Movie, CropResult Result) OutdatedResult(CropStore store)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".mkv");
        File.WriteAllText(path, "video");
        var file = new FileInfo(path);
        var movie = new Movie { Id = Guid.NewGuid(), Path = path, Name = "Film" };
        var result = new CropResult
        {
            ItemId = movie.Id,
            Path = path,
            FileSize = file.Length,
            FileModifiedUtc = file.LastWriteTimeUtc,
            FrameWidth = 1920,
            FrameHeight = 1080,
            Crop = CropBox.Full(1920, 1080),
            Segments = new List<CropSegment> { new(0, 30, new CropBox(600, 400, 276, 100)), new(30, 60, CropBox.Full(1920, 1080)) },
        };
        store.Set(result);
        return (movie, result);
    }

    [Fact]
    public void Reanalyse_OutdatedResultWithKeyframes_IsRecomputedWithoutFfmpeg()
    {
        var store = Store();
        var scanner = Scanner(store);
        var (movie, _) = OutdatedResult(store);
        var scope = new CropBox(0, 138, 1920, 804);
        store.SetSamples(movie.Id, 60, Enumerable.Range(0, 30).Select(i => new KeyframeSample(i * 2, i == 10 ? new CropBox(600, 400, 276, 100) : scope)).ToList());

        var (reanalysed, withoutSamples) = scanner.Reanalyse(outdatedOnly: true);

        Assert.Equal(1, reanalysed);
        Assert.Empty(withoutSamples);
        var result = Store().Get(movie.Id)!;
        Assert.Equal(scope, result.Crop);
        Assert.Null(result.Segments);
        Assert.Equal(CropAnalyzer.Version, result.AnalysisVersion);
        Assert.False(scanner.NeedsScan(movie));
    }

    [Fact]
    public void Reanalyse_Version2Result_IsSnappedAndCheckedWithoutFfmpeg()
    {
        var store = Store();
        var scanner = Scanner(store);
        var (scope, _) = OutdatedResult(store);
        var (strip, _) = OutdatedResult(store);
        store.Get(scope.Id)!.AnalysisVersion = 2;
        store.Get(strip.Id)!.AnalysisVersion = 2;
        store.SetSamples(scope.Id, 60, Enumerable.Range(0, 30).Select(i => new KeyframeSample(i * 2, new CropBox(0, 139, 1920, 802))).ToList());
        store.SetSamples(strip.Id, 60, Enumerable.Range(0, 30).Select(i => new KeyframeSample(i * 2, new CropBox(375, 441, 1169, 197))).ToList());

        Assert.Equal(2, scanner.Reanalyse(outdatedOnly: true).Reanalysed);

        Assert.Equal(new CropBox(0, 138, 1920, 804), Store().Get(scope.Id)!.Crop);
        var suspicious = Store().Get(strip.Id)!;
        Assert.True(suspicious.Suspicious);
        Assert.Equal(CropBox.Full(1920, 1080), suspicious.Crop);
        Assert.Equal(CropAnalyzer.Version, suspicious.AnalysisVersion);
        Assert.False(scanner.NeedsScan(strip));
    }

    [Fact]
    public void Reanalyse_OutdatedResultWithoutKeyframes_NeedsAScan()
    {
        var store = Store();
        var scanner = Scanner(store);
        var (movie, result) = OutdatedResult(store);

        var (reanalysed, withoutSamples) = scanner.Reanalyse(outdatedOnly: true);

        Assert.Equal(0, reanalysed);
        Assert.Equal(movie.Id, Assert.Single(withoutSamples));
        Assert.Same(result, store.Get(movie.Id));
        Assert.True(scanner.NeedsScan(movie));
    }

    [Fact]
    public void Reanalyse_CurrentResults_OnlyWhenAskedForAll()
    {
        var store = Store();
        var scanner = Scanner(store);
        var (movie, result) = OutdatedResult(store);
        result.AnalysisVersion = CropAnalyzer.Version;
        store.SetSamples(movie.Id, 60, Enumerable.Range(0, 30).Select(i => new KeyframeSample(i * 2, new CropBox(0, 60, 1920, 960))).ToList());

        Assert.Equal(0, scanner.Reanalyse(outdatedOnly: true).Reanalysed);
        Plugin.Instance!.Configuration.MinimumBarPercent = 10;
        Assert.Equal(1, scanner.Reanalyse(outdatedOnly: false).Reanalysed);

        // 60 px is under 10% of 1080: with the new setting the bars are left alone.
        Assert.Equal(CropBox.Full(1920, 1080), store.Get(movie.Id)!.Crop);
    }

    private static void AssertBox(CropBox expected, CropBox? actual)
    {
        Assert.NotNull(actual);
        Assert.InRange(actual.X, expected.X - 2, expected.X);
        Assert.InRange(actual.Y, expected.Y - 2, expected.Y);
        Assert.InRange(actual.Right, expected.Right, expected.Right + 2);
        Assert.InRange(actual.Bottom, expected.Bottom, expected.Bottom + 2);
    }

    [SkippableTheory]
    [InlineData("yuv420p")]
    [InlineData("yuv420p10le")]
    public async Task EndToEnd_BarsOnEveryKeyframe_AreCropped(string pixelFormat)
    {
        var ffmpeg = Ffmpeg();
        Skip.If(ffmpeg == null, "ffmpeg not found");

        var result = await Scan(ffmpeg!, MakeClip(ffmpeg!, $"bars-{pixelFormat}.mp4", pixelFormat, (6, 320)));

        Assert.Null(result.Error);
        Assert.Equal(640, result.FrameWidth);
        Assert.Equal(360, result.FrameHeight);
        Assert.Equal(6, result.Keyframes);
        AssertBox(new CropBox(0, 20, 640, 320), result.Crop);
        Assert.Null(result.Segments);
    }

    [SkippableFact]
    public async Task EndToEnd_OneFullFramePart_MeansNoStaticCrop()
    {
        var ffmpeg = Ffmpeg();
        Skip.If(ffmpeg == null, "ffmpeg not found");

        // Each part lasts longer than the 30 s minimum segment length.
        var result = await Scan(ffmpeg!, MakeClip(ffmpeg!, "fullframe.mp4", "yuv420p", (40, 320), (40, 360), (40, 320)));

        Assert.Equal(CropBox.Full(640, 360), result.Crop);
        Assert.Equal(3, result.Segments!.Count);
        Assert.Equal(CropBox.Full(640, 360), result.Segments[1].Box);
        Assert.True(result.HasCrop);
    }

    [SkippableFact]
    public async Task EndToEnd_ScopeAndImaxParts_GetTheirOwnSegments()
    {
        var ffmpeg = Ffmpeg();
        Skip.If(ffmpeg == null, "ffmpeg not found");

        // 2.39:1 (640x268), 1.90:1 (640x336) and 2.39:1 again, 40 s each: longer than the 30 s minimum.
        var result = await Scan(ffmpeg!, MakeClip(ffmpeg!, "imax.mp4", "yuv420p", (40, 268), (40, 336), (40, 268)));

        var segments = result.Segments!;
        Assert.Equal(3, segments.Count);
        AssertBox(new CropBox(0, 46, 640, 268), segments[0].Box);
        AssertBox(new CropBox(0, 12, 640, 336), segments[1].Box);
        AssertBox(new CropBox(0, 46, 640, 268), segments[2].Box);
        Assert.Equal(39, segments[1].Start, 1);
        Assert.Equal(80, segments[1].End, 1);
        Assert.Equal(120, segments[2].End, 1);
        AssertBox(new CropBox(0, 12, 640, 336), result.Crop);
    }

    [SkippableFact]
    public async Task EndToEnd_UnreadableFile_IsRecordedAsFailed()
    {
        var ffmpeg = Ffmpeg();
        Skip.If(ffmpeg == null, "ffmpeg not found");
        var path = Path.Combine(_dir, "broken.mkv");
        File.WriteAllText(path, "this is not a video");

        var result = await Scan(ffmpeg!, path);

        Assert.True(result.Failed);
        Assert.StartsWith("ffmpeg exited with code", result.Error);
    }

    [SkippableFact]
    public async Task EndToEnd_GpuThatCantDecode_FallsBackToTheCpu()
    {
        var ffmpeg = Ffmpeg();
        Skip.If(ffmpeg == null, "ffmpeg not found");
        var gone = new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.vaapi, VaapiDevice = "/dev/dri/does-not-exist" };

        var result = await Scan(ffmpeg!, MakeClip(ffmpeg!, "fallback.mp4", "yuv420p", (6, 320)), gone);

        Assert.Null(result.Error);
        AssertBox(new CropBox(0, 20, 640, 320), result.Crop);
    }

    // ----- trickplay pre-check -----

    [Fact]
    public void TrickplayArguments_UntileTheSheetsInOrder()
    {
        var args = CropScanner.TrickplayArguments("/config/trickplay/f4/x/320 - 10x10", 10, 10);

        Assert.Equal(Path.Combine("/config/trickplay/f4/x/320 - 10x10", "%d.jpg"), args[args.ToList().IndexOf("-i") + 1]);
        Assert.StartsWith("untile=10x10,cropdetect=", args[args.ToList().IndexOf("-vf") + 1]);
        Assert.Contains(Path.Combine("/media/100%%", "%d.jpg"), CropScanner.TrickplayArguments("/media/100%", 10, 10));
    }

    [Theory]
    [InlineData(144, 10000, 1440, true)]
    [InlineData(145, 10000, 1440, true)]
    [InlineData(150, 10000, 1440, true)]
    [InlineData(151, 10000, 1440, false)]
    [InlineData(144, 10000, 2880, false)]
    [InlineData(30, 10000, 330, true)]
    [InlineData(30, 10000, 400, false)]
    [InlineData(30, 10000, 0, false)]
    public void TrickplayMatchesRuntime_Within10PercentAnd60Seconds(int count, int interval, double runtime, bool expected)
    {
        Assert.Equal(expected, CropScanner.TrickplayMatchesRuntime(count, interval, runtime));
    }

    private CropResult TrickplayResult(CropStore store, CropBox thumbnail)
    {
        var (movie, result) = OutdatedResult(store);
        result.AnalysisVersion = CropAnalyzer.Version;
        result.AnalysisSource = AnalysisSources.Trickplay;
        result.Crop = CropBox.Full(1920, 1080);
        result.Segments = null;
        store.SetSamples(movie.Id, 600, Enumerable.Range(0, 60).Select(i => new KeyframeSample(i * 10, thumbnail)).ToList(), (320, 180));
        return result;
    }

    [Fact]
    public void Reanalyse_TrickplayResultThatStillShowsNoBars_IsKept()
    {
        var store = Store();
        var result = TrickplayResult(store, CropBox.Full(320, 180));

        var (reanalysed, needScan) = Scanner(store).Reanalyse(outdatedOnly: false);

        Assert.Equal(1, reanalysed);
        Assert.Empty(needScan);
        Assert.Equal(AnalysisSources.Trickplay, store.Get(result.ItemId)!.AnalysisSource);
    }

    [Fact]
    public void Reanalyse_TrickplayResultThatNoLongerPasses_GetsTheExactScan()
    {
        var store = Store();
        var result = TrickplayResult(store, new CropBox(0, 10, 320, 160));

        var (reanalysed, needScan) = Scanner(store).Reanalyse(outdatedOnly: false);

        Assert.Equal(0, reanalysed);
        Assert.Equal(result.ItemId, Assert.Single(needScan));
        Assert.Null(store.Get(result.ItemId));
    }

    [SkippableFact]
    public async Task EndToEnd_CleanTrickplay_SettlesTheFileWithoutAKeyframeScan()
    {
        var ffmpeg = Ffmpeg();
        Skip.If(ffmpeg == null, "ffmpeg not found");
        var movie = MovieAt(MakeClip(ffmpeg!, "clean.mp4", "yuv420p", (255, 360)), 255);
        var (trickplay, thumbnails) = MakeTrickplay(ffmpeg!, movie.Path, movie);

        var result = await Scan(ffmpeg!, movie, trickplay: trickplay);

        Assert.Equal(AnalysisSources.Trickplay, result.AnalysisSource);
        Assert.Equal(CropBox.Full(640, 360), result.Crop);
        Assert.Null(result.Segments);
        Assert.False(result.HasCrop);

        // One sheet of 100 cells, of which only the real thumbnails count; the rest is black padding.
        Assert.InRange(thumbnails, 25, 27);
        Assert.Equal(thumbnails, result.Keyframes);
        var stored = Store().GetSamples(movie.Id)!.Value;
        Assert.Equal((320, 180), stored.ThumbnailSize);
        Assert.Equal((thumbnails - 1) * 10, stored.Samples[^1].Time);
    }

    [SkippableFact]
    public async Task EndToEnd_TrickplayWithBars_GetsTheExactCrop()
    {
        var ffmpeg = Ffmpeg();
        Skip.If(ffmpeg == null, "ffmpeg not found");
        var movie = MovieAt(MakeClip(ffmpeg!, "bars.mp4", "yuv420p", (255, 320)), 255);
        var (trickplay, _) = MakeTrickplay(ffmpeg!, movie.Path, movie);

        var result = await Scan(ffmpeg!, movie, trickplay: trickplay);

        Assert.Equal(AnalysisSources.Keyframes, result.AnalysisSource);
        AssertBox(new CropBox(0, 20, 640, 320), result.Crop);
    }

    [SkippableFact]
    public async Task EndToEnd_TrickplayOfAShapeSwitchingFilm_GetsTheExactScan()
    {
        var ffmpeg = Ffmpeg();
        Skip.If(ffmpeg == null, "ffmpeg not found");

        // The full-frame part makes the union full frame; the 2.39:1 parts are a per-scene matte.
        var movie = MovieAt(MakeClip(ffmpeg!, "imax.mp4", "yuv420p", (90, 268), (90, 360), (90, 268)), 270);
        var (trickplay, _) = MakeTrickplay(ffmpeg!, movie.Path, movie);

        var result = await Scan(ffmpeg!, movie, trickplay: trickplay);

        Assert.Equal(AnalysisSources.Keyframes, result.AnalysisSource);
        Assert.Equal(3, result.Segments!.Count);
        AssertBox(new CropBox(0, 46, 640, 268), result.Segments[0].Box);
    }

    [SkippableTheory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("disabled")]
    public async Task EndToEnd_UnusableTrickplay_GetsTheExactScan(string problem)
    {
        var ffmpeg = Ffmpeg();
        Skip.If(ffmpeg == null, "ffmpeg not found");
        var movie = MovieAt(MakeClip(ffmpeg!, "clean.mp4", "yuv420p", (255, 360)), 255);
        var (trickplay, _) = MakeTrickplay(ffmpeg!, movie.Path, movie);
        if (problem == "missing")
            trickplay = Substitute.For<ITrickplayManager>();
        else if (problem == "stale")
            movie.RunTimeTicks = TimeSpan.FromMinutes(45).Ticks;
        else
            Plugin.Instance!.Configuration.UseTrickplay = false;

        var result = await Scan(ffmpeg!, movie, trickplay: trickplay);

        Assert.Equal(AnalysisSources.Keyframes, result.AnalysisSource);
        Assert.Equal(CropBox.Full(640, 360), result.Crop);
    }

    // ----- one scan per item -----

    [Fact]
    public async Task ScanAsync_ItemScannedInTheMeantime_IsNotScannedAgain()
    {
        var store = Store();
        var (movie, result) = OutdatedResult(store);
        result.AnalysisVersion = CropAnalyzer.Version;

        // The scanner's ffmpeg doesn't exist: running it would record a failure instead.
        Assert.Same(result, await Scanner(store).ScanAsync(movie, CancellationToken.None));
        Assert.Same(result, store.Get(movie.Id));
    }

    [SkippableFact]
    public async Task EndToEnd_TaskAndQueueOnTheSameItem_ScanItOnce()
    {
        var ffmpeg = Ffmpeg();
        Skip.If(ffmpeg == null, "ffmpeg not found");
        var store = Store();
        var scanner = Scanner(store, ffmpeg);
        var movie = MovieAt(MakeClip(ffmpeg!, "once.mp4", "yuv420p", (6, 320)));

        var results = await Task.WhenAll(scanner.ScanAsync(movie, CancellationToken.None), scanner.ScanAsync(movie, CancellationToken.None));

        Assert.Same(results[0], results[1]);
        Assert.Equal(AnalysisSources.Keyframes, results[0].AnalysisSource);
    }

    // ----- files with too few keyframes -----

    [SkippableFact]
    public async Task EndToEnd_SparseKeyframes_AreMeasuredEveryTwoSeconds()
    {
        var ffmpeg = Ffmpeg();
        Skip.If(ffmpeg == null, "ffmpeg not found");

        // Only the first frame is a keyframe: one sample in 130 seconds.
        var result = await Scan(ffmpeg!, MakeClip(ffmpeg!, "sparse.mp4", "yuv420p", OneKeyframe, (130, 320)));

        Assert.Equal(AnalysisSources.Frames, result.AnalysisSource);
        Assert.InRange(result.Keyframes, 60, 70);
        AssertBox(new CropBox(0, 20, 640, 320), result.Crop);
    }

    [Theory]
    [InlineData(AnalysisSources.Keyframes, 3, 1440, true)] // an Evangelion remux: 3 keyframes in 24 minutes
    [InlineData(null, 3, 1440, true)] // from before sources were stored
    [InlineData(AnalysisSources.Keyframes, 24, 1440, false)] // one a minute is enough
    [InlineData(AnalysisSources.Keyframes, 1, 100, false)] // under 2 minutes
    [InlineData(AnalysisSources.Frames, 3, 1440, false)] // already measured every 2 seconds
    public void Reanalyse_KeyframeResultWithTooFewKeyframes_IsMeasuredAgain(string? source, int keyframes, double duration, bool expected)
    {
        var store = Store();
        var scanner = Scanner(store);
        var (movie, result) = OutdatedResult(store);
        result.AnalysisSource = source;
        result.Keyframes = keyframes;
        store.SetSamples(movie.Id, duration, Enumerable.Range(0, keyframes).Select(i => new KeyframeSample(i * 2, new CropBox(0, 60, 1920, 960))).ToList());

        var (reanalysed, needScan) = scanner.Reanalyse(outdatedOnly: true);

        Assert.Equal(expected ? 0 : 1, reanalysed);
        Assert.Equal(expected, needScan.Contains(movie.Id));
        Assert.Equal(expected, store.Get(movie.Id) == null);
        Assert.Equal(expected, scanner.NeedsScan(movie));
    }

    [SkippableTheory]
    [InlineData("yuv420p", "nv12")]
    [InlineData("nv12", "nv12")]
    [InlineData("yuv420p10le", "p010le")]
    [InlineData("p010le", "p010le")]
    [InlineData("yuv422p10le", null)]
    [InlineData("yuv420p12le", null)]
    [InlineData(null, null)]
    public void QsvFramesDecoding_DownloadsTheSurfaceFormat(string? pixelFormat, string? download)
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "QSV decoding is Linux only");
        var qsv = new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.qsv, QsvDevice = "/dev/dri/renderD129" };

        var frames = CropScanner.QsvFramesDecoding(qsv, pixelFormat);

        Assert.Equal(download, frames?.Download);
        if (frames is var (decoding, _))
        {
            Assert.Equal(
                new[] { "-init_hw_device", "vaapi=va:/dev/dri/renderD129", "-init_hw_device", "qsv=qs@va", "-hwaccel", "qsv", "-hwaccel_output_format", "qsv" },
                decoding);
        }
    }

    [Fact]
    public void QsvFramesDecoding_OnlyForQsv()
    {
        Assert.Null(CropScanner.QsvFramesDecoding(null, "yuv420p"));
        Assert.Null(CropScanner.QsvFramesDecoding(new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.vaapi }, "yuv420p"));
        Assert.Null(CropScanner.QsvFramesDecoding(new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.nvenc }, "yuv420p"));
    }

    [Fact]
    public void Arguments_HardwareDownload_SelectsOnTheGpuThenDownloads()
    {
        var args = CropScanner.Arguments("/media/film.mkv", new[] { "-hwaccel", "qsv" }, everyTwoSeconds: true, hardwareDownload: "p010le");

        Assert.Equal(
            @"select='isnan(prev_selected_t)+gte(t-prev_selected_t\,2)',hwdownload,format=p010le,cropdetect=limit=0.094:round=2:reset=1:skip=0",
            args[args.ToList().IndexOf("-vf") + 1]);
        Assert.True(args.ToList().IndexOf("-hwaccel") < args.ToList().IndexOf("-i"));
    }

    [Theory]
    [InlineData(150, 300, true)]
    [InlineData(135, 300, true)]
    [InlineData(134, 300, false)]
    [InlineData(38, 300, false)] // the broken VAAPI surface pass
    [InlineData(0, 0, false)]
    public void EnoughFrames_AtLeast90PercentOfOnePerTwoSeconds(int samples, double duration, bool expected)
    {
        Assert.Equal(expected, CropScanner.EnoughFrames(samples, duration));
    }

    /// <summary>
    /// An ffmpeg that answers QSV runs itself with <paramref name="qsvSamples"/> 2.39:1 samples (from a
    /// 640x360 frame) and passes everything else to the real one. Every call is logged.
    /// </summary>
    private (string Path, string Log) FakeQsvFfmpeg(string ffmpeg, int qsvSamples)
    {
        var script = Path.Combine(_dir, "ffmpeg-qsv.sh");
        var log = Path.Combine(_dir, "ffmpeg-calls.log");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            echo "$*" >> '{{log}}'
            case " $* " in
              *" -hwaccel qsv "*)
                {
                  echo "Output #0, null, to 'pipe:':"
                  echo "  Stream #0:0: Video: wrapped_avframe, nv12, 640x360 [SAR 1:1 DAR 16:9]"
                  i=0
                  while [ $i -lt {{qsvSamples}} ]; do
                    echo "[Parsed_cropdetect_3 @ 0x1] x1:0 x2:639 y1:46 y2:313 w:640 h:268 x:0 y:46 pts:$i t:$((i * 2)).000000"
                    i=$((i + 1))
                  done
                } >&2
                exit 0;;
            esac
            exec '{{ffmpeg}}' "$@"
            """);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (script, log);
    }

    private static readonly string[] OneKeyframe = { "-g", "100000", "-keyint_min", "100000", "-sc_threshold", "0", "-x264-params", "scenecut=0" };

    [SkippableTheory]
    [InlineData(65, true)]
    [InlineData(10, false)]
    public async Task EndToEnd_SparseKeyframesOnQsv_UseQsvUnlessItLosesFrames(int qsvSamples, bool qsvUsed)
    {
        var ffmpeg = Ffmpeg();
        Skip.If(ffmpeg == null, "ffmpeg not found");
        Skip.IfNot(OperatingSystem.IsLinux(), "QSV decoding is Linux only");
        var (fake, log) = FakeQsvFfmpeg(ffmpeg!, qsvSamples);
        var clip = MakeClip(ffmpeg!, "sparse-qsv.mp4", "yuv420p", OneKeyframe, (130, 320));
        Plugin.Instance!.Configuration.HardwareDecoding = true;
        var qsv = new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.qsv, QsvDevice = "/dev/dri/does-not-exist" };

        var result = await Scan(fake, MovieAt(clip), qsv);

        Assert.Equal(AnalysisSources.Frames, result.AnalysisSource);
        Assert.Contains(File.ReadAllLines(log), call => call.Contains("-hwaccel qsv", StringComparison.Ordinal) && call.Contains("hwdownload,format=nv12", StringComparison.Ordinal));
        if (qsvUsed)
        {
            Assert.Equal(qsvSamples, result.Keyframes);
            AssertBox(new CropBox(0, 46, 640, 268), result.Crop);
        }
        else
        {
            // The real ffmpeg measured it after the short QSV run: the VAAPI device is gone too, so the CPU.
            Assert.InRange(result.Keyframes, 60, 70);
            AssertBox(new CropBox(0, 20, 640, 320), result.Crop);
        }
    }

    [SkippableFact]
    public async Task EndToEnd_SparseKeyframesOnFailingQsv_FallBackToTheUsualPath()
    {
        var ffmpeg = Ffmpeg();
        Skip.If(ffmpeg == null, "ffmpeg not found");
        Skip.IfNot(OperatingSystem.IsLinux(), "QSV decoding is Linux only");
        var qsv = new EncodingOptions { HardwareAccelerationType = HardwareAccelerationType.qsv, QsvDevice = "/dev/dri/does-not-exist" };

        var result = await Scan(ffmpeg!, MakeClip(ffmpeg!, "sparse-qsv-fails.mp4", "yuv420p", OneKeyframe, (130, 320)), qsv);

        Assert.Null(result.Error);
        Assert.Equal(AnalysisSources.Frames, result.AnalysisSource);
        Assert.InRange(result.Keyframes, 60, 70);
        AssertBox(new CropBox(0, 20, 640, 320), result.Crop);
    }

    [Fact]
    public void Arguments_EveryTwoSeconds_DecodesEveryFrame()
    {
        var args = CropScanner.Arguments("/media/film.mkv", everyTwoSeconds: true);

        Assert.DoesNotContain("-skip_frame", args);
        Assert.StartsWith("select=", args[args.ToList().IndexOf("-vf") + 1]);
    }
}

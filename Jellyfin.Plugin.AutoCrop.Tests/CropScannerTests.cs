using System.Diagnostics;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaEncoding;
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
    {
        var path = Path.Combine(_dir, name);
        var graph = string.Join(';', parts.Select((p, i) =>
            $"testsrc2=s=640x{p.Height}:r=24:d={p.Seconds},pad=640:360:0:{(360 - p.Height) / 2}:black[v{i}]"));
        graph += ";" + string.Concat(parts.Select((_, i) => $"[v{i}]")) + $"concat=n={parts.Length}:v=1:a=0";

        var run = Process.Start(new ProcessStartInfo(ffmpeg)
        {
            ArgumentList = { "-v", "error", "-y", "-f", "lavfi", "-i", graph, "-c:v", "libx264", "-g", "24", "-pix_fmt", pixelFormat, path },
            RedirectStandardError = true,
        })!;
        var error = run.StandardError.ReadToEnd();
        run.WaitForExit();
        Assert.True(run.ExitCode == 0, error);
        return path;
    }

    private CropStore Store() => new(() => Path.Combine(_dir, "crops.json"), NullLogger<CropStore>.Instance);

    private static CropScanner Scanner(CropStore store, string? ffmpeg = null, EncodingOptions? encoding = null)
    {
        var encoder = Substitute.For<IMediaEncoder>();
        encoder.EncoderPath.Returns(ffmpeg ?? "ffmpeg-must-not-run");
        var configuration = Substitute.For<IConfigurationManager>();
        configuration.GetConfiguration("encoding").Returns(encoding);
        return new CropScanner(encoder, configuration, store, NullLogger<CropScanner>.Instance);
    }

    private async Task<CropResult> Scan(string ffmpeg, string path, EncodingOptions? encoding = null)
    {
        var store = Store();
        var scanner = Scanner(store, ffmpeg, encoding);
        var movie = new Movie { Id = Guid.NewGuid(), Path = path, Name = Path.GetFileName(path) };

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
}

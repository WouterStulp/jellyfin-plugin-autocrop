using System.Diagnostics;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaEncoding;
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

    private async Task<CropResult> Scan(string ffmpeg, string path)
    {
        var encoder = Substitute.For<IMediaEncoder>();
        encoder.EncoderPath.Returns(ffmpeg);
        var store = new CropStore(() => Path.Combine(_dir, "crops.json"), NullLogger<CropStore>.Instance);
        var scanner = new CropScanner(encoder, store, NullLogger<CropScanner>.Instance);
        var movie = new Movie { Id = Guid.NewGuid(), Path = path, Name = Path.GetFileName(path) };

        var result = await scanner.ScanAsync(movie, CancellationToken.None);

        Assert.Same(result, store.GetCurrent(movie.Id, path));
        Assert.False(scanner.NeedsScan(movie));
        return result;
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

        var result = await Scan(ffmpeg!, MakeClip(ffmpeg!, "fullframe.mp4", "yuv420p", (4, 320), (3, 360), (4, 320)));

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

        // 2.39:1 (640x268) for 6 s, 1.90:1 (640x336) for 4 s, 2.39:1 again for 6 s.
        var result = await Scan(ffmpeg!, MakeClip(ffmpeg!, "imax.mp4", "yuv420p", (6, 268), (4, 336), (6, 268)));

        var segments = result.Segments!;
        Assert.Equal(3, segments.Count);
        AssertBox(new CropBox(0, 46, 640, 268), segments[0].Box);
        AssertBox(new CropBox(0, 12, 640, 336), segments[1].Box);
        AssertBox(new CropBox(0, 46, 640, 268), segments[2].Box);
        Assert.Equal(5, segments[1].Start, 1);
        Assert.Equal(10, segments[1].End, 1);
        Assert.Equal(16, segments[2].End, 1);
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
}

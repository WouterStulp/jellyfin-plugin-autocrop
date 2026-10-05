using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AutoCrop.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoCrop;

/// <summary>
/// Measures the picture area of a file with one ffmpeg cropdetect pass over all of its keyframes,
/// unless Jellyfin's trickplay thumbnails already show there are no bars. One scan at a time across
/// the scheduled task and the new-item queue, so a NAS isn't flooded.
/// </summary>
public class CropScanner
{
    private readonly IMediaEncoder _mediaEncoder;
    private const string Cropdetect = "cropdetect=limit=0.094:round=2:reset=1:skip=0";

    private readonly IConfigurationManager _configurationManager;
    private readonly ITrickplayManager _trickplayManager;
    private readonly CropStore _store;
    private readonly ILogger<CropScanner> _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public CropScanner(
        IMediaEncoder mediaEncoder,
        IConfigurationManager configurationManager,
        ITrickplayManager trickplayManager,
        CropStore store,
        ILogger<CropScanner> logger)
    {
        _mediaEncoder = mediaEncoder;
        _configurationManager = configurationManager;
        _trickplayManager = trickplayManager;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Movies and episodes backed by a local video file. Skips virtual items, placeholders, disc
    /// folders (ISO, DVD, Blu-ray), .strm shortcuts and remote paths.
    /// </summary>
    public static bool IsEligible(BaseItem item)
        => item is Movie or Episode
            && item is Video { IsVirtualItem: false, IsPlaceHolder: false, VideoType: VideoType.VideoFile }
            && !string.IsNullOrEmpty(item.Path)
            && Path.IsPathFullyQualified(item.Path)
            && !string.Equals(Path.GetExtension(item.Path), ".strm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Eligible, on disk, and without a result for the file as it is now (failed ones included) from
    /// the current analysis. An outdated result that had stored keyframes was already re-analysed by
    /// <see cref="Reanalyse"/>, so what is left needs ffmpeg again.
    /// </summary>
    public bool NeedsScan(BaseItem item)
        => IsEligible(item)
            && File.Exists(item.Path)
            && _store.GetCurrent(item.Id, item.Path) is not { AnalysisVersion: >= CropAnalyzer.Version };

    internal static AnalyzerOptions Options()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        return new AnalyzerOptions(config.MinimumBarPercent, config.MinimumSegmentSeconds);
    }

    /// <summary>Fills in a result's crop and segments from its keyframes.</summary>
    internal static void Analyse(CropResult result, IReadOnlyList<KeyframeSample> samples, double durationSeconds, AnalyzerOptions options)
    {
        var segments = CropAnalyzer.Segments(samples, result.FrameWidth, result.FrameHeight, durationSeconds, options);
        result.Keyframes = samples.Count;
        result.Crop = CropAnalyzer.Union(samples, result.FrameWidth, result.FrameHeight, options.MinimumBarPercent);
        result.Segments = segments.Count > 1 ? segments.ToList() : null;
        result.AnalysisVersion = CropAnalyzer.Version;
    }

    /// <summary>
    /// Recomputes scan results from their stored keyframes with the current analysis and settings,
    /// without ffmpeg. With <paramref name="outdatedOnly"/>, only results from an older analysis.
    /// Results settled by trickplay are checked against the same rule again; one that no longer passes
    /// is dropped, so it gets the exact scan. Returns how many were recomputed and the items that need
    /// a scan: those without stored keyframes and the dropped trickplay results.
    /// </summary>
    public (int Reanalysed, IReadOnlyList<Guid> NeedScan) Reanalyse(bool outdatedOnly)
    {
        var options = Options();
        var updates = new List<(CropResult Current, CropResult? Updated)>();
        var needScan = new List<Guid>();
        foreach (var result in _store.All().Where(r => !r.Failed && (!outdatedOnly || r.AnalysisVersion < CropAnalyzer.Version)))
        {
            var stored = _store.GetSamples(result.ItemId);
            if (result.AnalysisSource == AnalysisSources.Trickplay)
            {
                if (stored is { ThumbnailSize: { } size } && CropAnalyzer.ShowsNoBars(stored.Value.Samples, size.Width, size.Height))
                {
                    var still = result.Copy();
                    still.AnalysisVersion = CropAnalyzer.Version;
                    updates.Add((result, still));
                }
                else
                {
                    updates.Add((result, null));
                    needScan.Add(result.ItemId);
                }

                continue;
            }

            if (stored == null)
            {
                needScan.Add(result.ItemId);
                continue;
            }

            var updated = result.Copy();
            Analyse(updated, stored.Value.Samples, stored.Value.DurationSeconds, options);
            updates.Add((result, updated));
        }

        _store.Replace(updates);
        var reanalysed = updates.Count(u => u.Updated != null);
        if (updates.Count > 0 || needScan.Count > 0)
            _logger.LogInformation("AutoCrop re-analysed {Count} result(s); {Missing} need a scan", reanalysed, needScan.Count);

        return (reanalysed, needScan);
    }

    internal static IReadOnlyList<string> Arguments(string path, IReadOnlyList<string>? hardwareDecoding = null) => new[]
    {
        "-hide_banner", "-nostats", "-nostdin",
        "-skip_frame", "nokey",
    }.Concat(hardwareDecoding ?? Array.Empty<string>()).Concat(new[]
    {
        "-i", path,
        // V (capital) skips cover art and other attached pictures.
        "-map", "0:V:0",
        // A fractional limit is scaled to the pixel format's bit depth: 24/255 for 8-bit, and the
        // same relative level for 10-bit, whose black sits at 64 rather than 16. skip=0 keeps the
        // first keyframes, which cropdetect would otherwise ignore.
        "-vf", Cropdetect,
        "-an", "-sn", "-dn",
        "-f", "null", "-",
    }).ToArray();

    /// <summary>
    /// One pass over the trickplay sheets 0.jpg, 1.jpg, ... in a folder: each sheet is split into its
    /// thumbnails, which cropdetect measures in order.
    /// </summary>
    internal static IReadOnlyList<string> TrickplayArguments(string directory, int tileWidth, int tileHeight) => new[]
    {
        "-hide_banner", "-nostats", "-nostdin",
        "-f", "image2", "-framerate", "1", "-start_number", "0",
        "-i", Path.Combine(directory.Replace("%", "%%", StringComparison.Ordinal), "%d.jpg"),
        "-vf", $"untile={tileWidth}x{tileHeight},{Cropdetect}",
        "-f", "null", "-",
    };

    /// <summary>
    /// Trickplay made for an earlier version of the file can't be trusted: its thumbnails must cover
    /// the runtime to within 10% and 60 seconds.
    /// </summary>
    internal static bool TrickplayMatchesRuntime(int thumbnailCount, int intervalMs, double runtimeSeconds)
        => runtimeSeconds > 0
            && Math.Abs((thumbnailCount * intervalMs / 1000.0) - runtimeSeconds) <= Math.Min(60, runtimeSeconds * 0.1);

    /// <summary>
    /// Decoder arguments for the GPU Jellyfin itself is set up to use. Decoding is bit-exact, so the
    /// GPU finds the same picture as the CPU, just faster (3x on HEVC with an Intel iGPU). Frames come
    /// back to system memory for cropdetect. On Linux, QSV sits on top of VAAPI, so both use VAAPI.
    /// </summary>
    internal static IReadOnlyList<string>? HardwareDecodingArguments(EncodingOptions? encoding)
    {
        if (encoding == null)
            return null;

        static string Device(string? configured) => string.IsNullOrWhiteSpace(configured) ? "/dev/dri/renderD128" : configured;

        return encoding.HardwareAccelerationType switch
        {
            HardwareAccelerationType.qsv when OperatingSystem.IsLinux()
                => new[] { "-hwaccel", "vaapi", "-hwaccel_device", Device(encoding.QsvDevice ?? encoding.VaapiDevice) },
            HardwareAccelerationType.vaapi => new[] { "-hwaccel", "vaapi", "-hwaccel_device", Device(encoding.VaapiDevice) },
            HardwareAccelerationType.nvenc => new[] { "-hwaccel", "cuda" },
            HardwareAccelerationType.videotoolbox => new[] { "-hwaccel", "videotoolbox" },
            _ => null,
        };
    }

    public async Task<CropResult> ScanAsync(BaseItem item, CancellationToken cancellationToken)
    {
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await MeasureAsync(item, cancellationToken).ConfigureAwait(false);
            _store.Set(result);
            return result;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task<CropResult> MeasureAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var path = item.Path;
        var file = new FileInfo(path);
        var result = new CropResult
        {
            ItemId = item.Id,
            Path = path,
            ScannedAtUtc = DateTime.UtcNow,
            AnalysisVersion = CropAnalyzer.Version,
        };

        try
        {
            result.FileSize = file.Length;
            result.FileModifiedUtc = file.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Error = $"Unreadable file: {ex.Message}";
            _logger.LogWarning("AutoCrop could not read {Path}: {Message}", path, ex.Message);
            return result;
        }

        var watch = Stopwatch.StartNew();
        if ((Plugin.Instance?.Configuration.UseTrickplay ?? true)
            && await TrickplayShowsNoBarsAsync(item, result, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "AutoCrop scanned {Name}: no bars in {Thumbnails} trickplay thumbnails in {Seconds:0.0}s, skipped the keyframe scan",
                item.Name, result.Keyframes, watch.Elapsed.TotalSeconds);
            return result;
        }

        var hardware = Plugin.Instance?.Configuration.HardwareDecoding == false
            ? null
            : HardwareDecodingArguments(_configurationManager.GetConfiguration("encoding") as EncodingOptions);
        var (parser, error) = await DetectAsync(path, hardware, cancellationToken).ConfigureAwait(false);
        result.AnalysisSource = AnalysisSources.Keyframes;
        result.Error = error;
        var width = parser.FrameWidth ?? item.Width;
        var height = parser.FrameHeight ?? item.Height;
        if (result.Error == null && (width <= 0 || height <= 0))
            result.Error = "Unknown frame size";

        if (result.Error != null)
        {
            _logger.LogWarning("AutoCrop could not scan {Path}: {Error}", path, result.Error);
            return result;
        }

        result.FrameWidth = width;
        result.FrameHeight = height;
        Analyse(result, parser.Samples, parser.DurationSeconds ?? 0, Options());
        _store.SetSamples(item.Id, parser.DurationSeconds ?? 0, parser.Samples);

        _logger.LogInformation(
            "AutoCrop scanned {Name}: {Keyframes} {Source} in {Seconds:0}s, frame {Width}x{Height}, picture {Crop}, {Segments} segment(s)",
            item.Name, result.Keyframes, result.AnalysisSource, watch.Elapsed.TotalSeconds, width, height, result.Crop, result.Segments?.Count ?? 1);
        return result;
    }

    private async Task<(CropdetectParser Parser, string? Error)> DetectAsync(
        string path, IReadOnlyList<string>? hardware, CancellationToken cancellationToken)
    {
        var parser = new CropdetectParser();
        var (exitCode, lastLine) = await RunFfmpegAsync(Arguments(path, hardware), parser, cancellationToken).ConfigureAwait(false);

        // A GPU that can't decode this codec or profile must not fail the scan: measure it on the CPU.
        if (hardware != null && (exitCode != 0 || parser.Samples.Count == 0))
        {
            _logger.LogInformation("AutoCrop: GPU decoding failed for {Path} ({LastLine}), measuring on the CPU", path, lastLine);
            parser = new CropdetectParser();
            (exitCode, lastLine) = await RunFfmpegAsync(Arguments(path), parser, cancellationToken).ConfigureAwait(false);
        }

        var error = exitCode != 0 ? $"ffmpeg exited with code {exitCode}: {lastLine}"
            : parser.Samples.Count == 0 ? "ffmpeg reported no cropdetect output"
            : null;
        return (parser, error);
    }

    /// <summary>
    /// Settles the item from its trickplay thumbnails when they prove there is nothing to crop, and
    /// fills in the result. False for anything else (bars, possible mattes, no or stale trickplay, a
    /// failed ffmpeg), which then gets the exact scan.
    /// </summary>
    private async Task<bool> TrickplayShowsNoBarsAsync(BaseItem item, CropResult result, CancellationToken cancellationToken)
    {
        try
        {
            var trickplay = await TrickplayThumbnailsAsync(item, result.FileModifiedUtc, cancellationToken).ConfigureAwait(false);
            if (trickplay is not ({ } thumbnails, var width, var height)
                || item.Width <= 0
                || item.Height <= 0
                || !CropAnalyzer.ShowsNoBars(thumbnails, width, height))
                return false;

            result.FrameWidth = item.Width;
            result.FrameHeight = item.Height;
            result.Crop = CropBox.Full(item.Width, item.Height);
            result.Segments = null;
            result.Keyframes = thumbnails.Count;
            result.AnalysisSource = AnalysisSources.Trickplay;
            _store.SetSamples(item.Id, TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds, thumbnails, (width, height));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Also a Jellyfin whose trickplay API differs from the SDK: the exact scan always works.
            _logger.LogWarning("AutoCrop could not read the trickplay of {Path}: {Message}", item.Path, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The thumbnails of the largest trickplay resolution, at thumbnail scale, timed by their index,
    /// with the thumbnail size. Null when there is no complete, current trickplay.
    /// </summary>
    private async Task<(IReadOnlyList<KeyframeSample> Thumbnails, int Width, int Height)?> TrickplayThumbnailsAsync(
        BaseItem item, DateTime fileModifiedUtc, CancellationToken cancellationToken)
    {
        var resolutions = await _trickplayManager.GetTrickplayResolutions(item.Id).ConfigureAwait(false);
        var info = resolutions?.Values.MaxBy(i => i.Width);
        if (info is not { ThumbnailCount: > 0, Interval: > 0, TileWidth: > 0, TileHeight: > 0 })
            return null;

        var runtime = TimeSpan.FromTicks(item.RunTimeTicks ?? 0).TotalSeconds;
        if (!TrickplayMatchesRuntime(info.ThumbnailCount, info.Interval, runtime))
        {
            _logger.LogInformation(
                "AutoCrop: trickplay of {Path} ({Count} x {Interval} ms) doesn't match its runtime of {Runtime:0}s",
                item.Path, info.ThumbnailCount, info.Interval, runtime);
            return null;
        }

        // In the metadata folder, or beside the file with "Save trickplay images next to media".
        var directory = new[] { false, true }
            .Select(saveWithMedia => _trickplayManager.GetTrickplayDirectory(item, info.TileWidth, info.TileHeight, info.Width, saveWithMedia))
            .FirstOrDefault(dir => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "0.jpg")));
        if (directory == null || File.GetLastWriteTimeUtc(Path.Combine(directory, "0.jpg")) < fileModifiedUtc)
            return null;

        var parser = new CropdetectParser();
        var (exitCode, lastLine) = await RunFfmpegAsync(
            TrickplayArguments(directory, info.TileWidth, info.TileHeight), parser, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0 || parser.Samples.Count < info.ThumbnailCount)
        {
            _logger.LogInformation(
                "AutoCrop could not read the trickplay of {Path}: {Count} of {Expected} thumbnails, {LastLine}",
                item.Path, parser.Samples.Count, info.ThumbnailCount, lastLine);
            return null;
        }

        // The last sheet is padded with black cells past ThumbnailCount; those aren't thumbnails.
        var thumbnails = parser.Samples
            .Take(info.ThumbnailCount)
            .Select((s, i) => s with { Time = i * info.Interval / 1000.0 })
            .ToList();
        return (thumbnails, parser.FrameWidth ?? info.Width, parser.FrameHeight ?? info.Height);
    }

    private async Task<(int ExitCode, string LastLine)> RunFfmpegAsync(
        IReadOnlyList<string> arguments, CropdetectParser parser, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        try
        {
            // Idle maps to nice 19 on Linux: playback and transcodes always come first.
            process.PriorityClass = ProcessPriorityClass.Idle;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            _logger.LogDebug("Could not lower ffmpeg's priority: {Message}", ex.Message);
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });

        var lastLine = string.Empty;
        string? line;
        while ((line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            parser.AddLine(line);
            if (line.Length > 0)
                lastLine = line;
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, lastLine);
    }
}

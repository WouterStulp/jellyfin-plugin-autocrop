using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoCrop;

/// <summary>
/// Measures the picture area of a file with one ffmpeg cropdetect pass over all of its keyframes.
/// One scan at a time across the scheduled task and the new-item queue, so a NAS isn't flooded.
/// </summary>
public class CropScanner
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly CropStore _store;
    private readonly ILogger<CropScanner> _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public CropScanner(IMediaEncoder mediaEncoder, CropStore store, ILogger<CropScanner> logger)
    {
        _mediaEncoder = mediaEncoder;
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

    /// <summary>Eligible, on disk, and without a result for the file as it is now (failed ones included).</summary>
    public bool NeedsScan(BaseItem item)
        => IsEligible(item) && File.Exists(item.Path) && _store.GetCurrent(item.Id, item.Path) == null;

    internal static IReadOnlyList<string> Arguments(string path) => new[]
    {
        "-hide_banner", "-nostats", "-nostdin",
        "-skip_frame", "nokey",
        "-i", path,
        // V (capital) skips cover art and other attached pictures.
        "-map", "0:V:0",
        // A fractional limit is scaled to the pixel format's bit depth: 24/255 for 8-bit, and the
        // same relative level for 10-bit, whose black sits at 64 rather than 16. skip=0 keeps the
        // first keyframes, which cropdetect would otherwise ignore.
        "-vf", "cropdetect=limit=0.094:round=2:reset=1:skip=0",
        "-an", "-sn", "-dn",
        "-f", "null", "-",
    };

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
        var parser = new CropdetectParser();
        var (exitCode, lastLine) = await RunFfmpegAsync(path, parser, cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
            result.Error = $"ffmpeg exited with code {exitCode}: {lastLine}";
        else if (parser.Samples.Count == 0)
            result.Error = "ffmpeg reported no cropdetect output";

        var width = parser.FrameWidth ?? item.Width;
        var height = parser.FrameHeight ?? item.Height;
        if (result.Error == null && (width <= 0 || height <= 0))
            result.Error = "Unknown frame size";

        if (result.Error != null)
        {
            _logger.LogWarning("AutoCrop could not scan {Path}: {Error}", path, result.Error);
            return result;
        }

        var config = Plugin.Instance?.Configuration;
        var options = new AnalyzerOptions(config?.MinimumBarPercent ?? 1.0, config?.MinimumSegmentSeconds ?? 2.0);
        var segments = CropAnalyzer.Segments(parser.Samples, width, height, parser.DurationSeconds ?? 0, options);

        result.FrameWidth = width;
        result.FrameHeight = height;
        result.Keyframes = parser.Samples.Count;
        result.Crop = CropAnalyzer.Union(parser.Samples, width, height, options.MinimumBarPercent);
        result.Segments = segments.Count > 1 ? segments.ToList() : null;

        _logger.LogInformation(
            "AutoCrop scanned {Name}: {Keyframes} keyframes in {Seconds:0}s, frame {Width}x{Height}, picture {Crop}, {Segments} segment(s)",
            item.Name, result.Keyframes, watch.Elapsed.TotalSeconds, width, height, result.Crop, segments.Count);
        return result;
    }

    private async Task<(int ExitCode, string LastLine)> RunFfmpegAsync(
        string path, CropdetectParser parser, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in Arguments(path))
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

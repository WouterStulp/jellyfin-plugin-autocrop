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
using MediaBrowser.Model.Configuration;
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
    private readonly IConfigurationManager _configurationManager;
    private readonly CropStore _store;
    private readonly ILogger<CropScanner> _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public CropScanner(
        IMediaEncoder mediaEncoder, IConfigurationManager configurationManager, CropStore store, ILogger<CropScanner> logger)
    {
        _mediaEncoder = mediaEncoder;
        _configurationManager = configurationManager;
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
    /// Returns how many were recomputed and the items that have no stored keyframes.
    /// </summary>
    public (int Reanalysed, IReadOnlyList<Guid> WithoutSamples) Reanalyse(bool outdatedOnly)
    {
        var options = Options();
        var updates = new List<(CropResult Current, CropResult Updated)>();
        var withoutSamples = new List<Guid>();
        foreach (var result in _store.All().Where(r => !r.Failed && (!outdatedOnly || r.AnalysisVersion < CropAnalyzer.Version)))
        {
            var stored = _store.GetSamples(result.ItemId);
            if (stored == null)
            {
                withoutSamples.Add(result.ItemId);
                continue;
            }

            var updated = result.Copy();
            Analyse(updated, stored.Value.Samples, stored.Value.DurationSeconds, options);
            updates.Add((result, updated));
        }

        _store.Replace(updates);
        if (updates.Count > 0 || withoutSamples.Count > 0)
            _logger.LogInformation("AutoCrop re-analysed {Count} result(s); {Missing} have no stored keyframes", updates.Count, withoutSamples.Count);

        return (updates.Count, withoutSamples);
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
        "-vf", "cropdetect=limit=0.094:round=2:reset=1:skip=0",
        "-an", "-sn", "-dn",
        "-f", "null", "-",
    }).ToArray();

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
        var parser = new CropdetectParser();
        var hardware = Plugin.Instance?.Configuration.HardwareDecoding == false
            ? null
            : HardwareDecodingArguments(_configurationManager.GetConfiguration("encoding") as EncodingOptions);
        var (exitCode, lastLine) = await RunFfmpegAsync(Arguments(path, hardware), parser, cancellationToken).ConfigureAwait(false);

        // A GPU that can't decode this codec or profile must not fail the scan: measure it on the CPU.
        if (hardware != null && (exitCode != 0 || parser.Samples.Count == 0))
        {
            _logger.LogInformation("AutoCrop: GPU decoding failed for {Path} ({LastLine}), measuring on the CPU", path, lastLine);
            parser = new CropdetectParser();
            (exitCode, lastLine) = await RunFfmpegAsync(Arguments(path), parser, cancellationToken).ConfigureAwait(false);
        }

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

        result.FrameWidth = width;
        result.FrameHeight = height;
        Analyse(result, parser.Samples, parser.DurationSeconds ?? 0, Options());
        _store.SetSamples(item.Id, parser.DurationSeconds ?? 0, parser.Samples);

        _logger.LogInformation(
            "AutoCrop scanned {Name}: {Keyframes} keyframes in {Seconds:0}s, frame {Width}x{Height}, picture {Crop}, {Segments} segment(s)",
            item.Name, result.Keyframes, watch.Elapsed.TotalSeconds, width, height, result.Crop, result.Segments?.Count ?? 1);
        return result;
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

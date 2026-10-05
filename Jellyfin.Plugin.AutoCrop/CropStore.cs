using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoCrop;

/// <summary>
/// Scan results, one JSON file in the plugin's data folder. Every change rewrites the file through a
/// temp file and a move, so a crash mid-write leaves the previous version intact. The raw keyframe
/// bounds of each scan sit beside it in samples/{itemId}.json.gz, so a new analysis or new settings
/// can be applied without running ffmpeg again.
/// </summary>
public class CropStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _lock = new();
    private readonly Func<string> _path;
    private readonly ILogger<CropStore> _logger;
    private Dictionary<Guid, CropResult>? _results;

    // The plugin instance (and its data folder) may not exist yet when DI builds this.
    public CropStore(ILogger<CropStore> logger)
        : this(() => System.IO.Path.Combine(Plugin.Instance!.DataFolderPath, "crops.json"), logger)
    {
    }

    internal CropStore(Func<string> path, ILogger<CropStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public CropResult? Get(Guid itemId)
    {
        lock (_lock)
            return Load().GetValueOrDefault(itemId);
    }

    /// <summary>The stored result, but only while the file is still the one it was measured on.</summary>
    public CropResult? GetCurrent(Guid itemId, string? path)
    {
        var result = Get(itemId);
        return result != null && IsCurrent(result, path) ? result : null;
    }

    public IReadOnlyList<CropResult> All()
    {
        lock (_lock)
            return Load().Values.ToList();
    }

    public void Set(CropResult result)
    {
        lock (_lock)
        {
            Load()[result.ItemId] = result;
            Save();
        }
    }

    public void Remove(Guid itemId)
    {
        lock (_lock)
        {
            if (Load().Remove(itemId))
                Save();
        }
    }

    /// <summary>Drops results and stored keyframes for items that are no longer in the library.</summary>
    public void RemoveAllExcept(IReadOnlySet<Guid> itemIds)
    {
        lock (_lock)
        {
            var results = Load();
            var stale = results.Keys.Where(id => !itemIds.Contains(id)).ToList();
            foreach (var id in stale)
            {
                results.Remove(id);
                DeleteSamples(id);
            }

            if (stale.Count > 0)
                Save();
        }
    }

    /// <summary>
    /// Stores each updated result, or removes it when Updated is null, but only while the result it was
    /// computed from is still the stored one, so a re-analysis never overwrites a scan that finished in
    /// the meantime. One write for all.
    /// </summary>
    public void Replace(IReadOnlyList<(CropResult Current, CropResult? Updated)> updates)
    {
        lock (_lock)
        {
            var results = Load();
            var changed = false;
            foreach (var (current, updated) in updates)
            {
                if (!ReferenceEquals(results.GetValueOrDefault(current.ItemId), current))
                    continue;

                if (updated == null)
                    results.Remove(current.ItemId);
                else
                    results[current.ItemId] = updated;
                changed = true;
            }

            if (changed)
                Save();
        }
    }

    /// <summary>
    /// Stores a scan's keyframes as rows of [t, x1, x2, y1, y2] in cropdetect's inclusive bounds, or
    /// [t] for a fully black keyframe, plus the file's duration. Trickplay thumbnails are stored the
    /// same way with their <paramref name="thumbnailSize"/>, which marks them as thumbnail scale.
    /// </summary>
    public void SetSamples(
        Guid itemId, double durationSeconds, IReadOnlyList<KeyframeSample> samples, (int Width, int Height)? thumbnailSize = null)
    {
        var path = SamplesPath(itemId);
        var tmp = path + ".tmp";
        var file = new SamplesFile(
            durationSeconds,
            samples.Select(s => s.Box is { } b ? new[] { s.Time, b.X, b.Right - 1, b.Y, b.Bottom - 1 } : new[] { s.Time }).ToList(),
            thumbnailSize?.Width,
            thumbnailSize?.Height);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            using (var stream = File.Create(tmp))
            using (var gzip = new GZipStream(stream, CompressionLevel.SmallestSize))
                JsonSerializer.Serialize(gzip, file, JsonOptions);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save keyframes to {Path}", path);
        }
    }

    /// <summary>
    /// The stored keyframes of an item and its duration, or null when there are none. ThumbnailSize is
    /// set when they are trickplay thumbnails rather than video frames.
    /// </summary>
    public (double DurationSeconds, IReadOnlyList<KeyframeSample> Samples, (int Width, int Height)? ThumbnailSize)? GetSamples(Guid itemId)
    {
        var path = SamplesPath(itemId);
        try
        {
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            var file = JsonSerializer.Deserialize<SamplesFile>(gzip, JsonOptions);
            if (file?.Keyframes == null)
                return null;

            var samples = file.Keyframes
                .Select(k => new KeyframeSample(
                    k[0],
                    k.Length < 5 ? null : new CropBox((int)k[1], (int)k[3], (int)k[2] - (int)k[1] + 1, (int)k[4] - (int)k[3] + 1)))
                .ToList();
            var thumbnailSize = file is { ThumbnailWidth: { } w, ThumbnailHeight: { } h } ? (w, h) : ((int, int)?)null;
            return (file.Duration, samples, thumbnailSize);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read keyframes from {Path}", path);
            return null;
        }
    }

    internal static bool IsCurrent(CropResult result, string? path)
    {
        if (string.IsNullOrEmpty(path) || !string.Equals(result.Path, path, StringComparison.Ordinal))
            return false;

        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length == result.FileSize && file.LastWriteTimeUtc == result.FileModifiedUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string SamplesPath(Guid itemId)
        => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path())!, "samples", itemId.ToString("N") + ".json.gz");

    private void DeleteSamples(Guid itemId)
    {
        try
        {
            File.Delete(SamplesPath(itemId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete the keyframes of {ItemId}: {Message}", itemId, ex.Message);
        }
    }

    private Dictionary<Guid, CropResult> Load()
    {
        if (_results != null)
            return _results;

        var path = _path();
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<CropResult>>(File.ReadAllText(path), JsonOptions);
                _results = (list ?? new List<CropResult>()).ToDictionary(r => r.ItemId);
                return _results;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // The results are a cache of what ffmpeg measured; losing them only costs a rescan.
            _logger.LogError(ex, "Could not read {Path}, starting with no scan results", path);
        }

        _results = new Dictionary<Guid, CropResult>();
        return _results;
    }

    private void Save()
    {
        var path = _path();
        var tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(tmp, JsonSerializer.Serialize(_results!.Values.ToList(), JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save scan results to {Path}", path);
        }
    }

    private sealed record SamplesFile(double Duration, List<double[]> Keyframes, int? ThumbnailWidth = null, int? ThumbnailHeight = null);
}

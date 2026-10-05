using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoCrop;

/// <summary>
/// Scan results, one JSON file in the plugin's data folder. Every change rewrites the file through a
/// temp file and a move, so a crash mid-write leaves the previous version intact.
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

    /// <summary>Drops results for items that are no longer in the library.</summary>
    public void RemoveAllExcept(IReadOnlySet<Guid> itemIds)
    {
        lock (_lock)
        {
            var results = Load();
            var stale = results.Keys.Where(id => !itemIds.Contains(id)).ToList();
            foreach (var id in stale)
                results.Remove(id);
            if (stale.Count > 0)
                Save();
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
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AutoCrop;

/// <summary>
/// The scan result for one item. Path, size and modification time identify the file it was measured
/// on, so a replaced or re-encoded file is scanned again. A failed scan is kept too, with its reason,
/// so it shows in the dashboard and isn't retried every night until the file changes.
/// </summary>
public sealed class CropResult
{
    public Guid ItemId { get; set; }

    public string Path { get; set; } = string.Empty;

    public long FileSize { get; set; }

    public DateTime FileModifiedUtc { get; set; }

    public int FrameWidth { get; set; }

    public int FrameHeight { get; set; }

    /// <summary>The whole-file picture area, used in static mode and as the fallback.</summary>
    public CropBox? Crop { get; set; }

    /// <summary>The per-scene timeline; null when the whole file is one segment.</summary>
    public List<CropSegment>? Segments { get; set; }

    public int Keyframes { get; set; }

    public DateTime ScannedAtUtc { get; set; }

    public string? Error { get; set; }

    /// <summary>The <see cref="CropAnalyzer.Version"/> that produced Crop and Segments; 0 for results from before versions.</summary>
    public int AnalysisVersion { get; set; }

    /// <summary>What the result was measured on, one of <see cref="AnalysisSources"/>; null for results from before sources.</summary>
    public string? AnalysisSource { get; set; }

    [JsonIgnore]
    public bool Failed => Error != null;

    [JsonIgnore]
    public bool IsPerScene => !Failed && Segments is { Count: > 1 };

    [JsonIgnore]
    public bool HasCrop
    {
        get
        {
            if (Failed || Crop == null)
                return false;

            var full = CropBox.Full(FrameWidth, FrameHeight);
            return Crop != full || (Segments?.Any(s => s.Box != full) ?? false);
        }
    }

    public CropResult Copy() => (CropResult)MemberwiseClone();
}

public static class AnalysisSources
{
    /// <summary>Jellyfin's trickplay thumbnails showed no bars, so the file wasn't decoded. Never a crop.</summary>
    public const string Trickplay = "trickplay";

    public const string Keyframes = "keyframes";

    /// <summary>A frame every 2 seconds, for files with too few keyframes to measure.</summary>
    public const string Frames = "frames";
}

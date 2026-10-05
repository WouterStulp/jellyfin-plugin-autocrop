using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AutoCrop;

/// <summary>
/// Reads ffmpeg's stderr line by line during a cropdetect pass (reset=1, so every line describes one
/// keyframe on its own) and collects the per-keyframe bounds, the frame size and the duration.
/// </summary>
public sealed class CropdetectParser
{
    private static readonly Regex BoundsLine = new(
        @"x1:(-?\d+)\s+x2:(-?\d+)\s+y1:(-?\d+)\s+y2:(-?\d+)(?:.*?\bt:(\S+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FrameSize = new(@"\b(\d{2,5})x(\d{2,5})\b", RegexOptions.Compiled);

    private static readonly Regex DurationLine = new(
        @"^\s*Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly List<KeyframeSample> _samples = new();
    private bool _inOutputSection;

    public IReadOnlyList<KeyframeSample> Samples => _samples;

    /// <summary>Size of the frames cropdetect saw, from the output stream ffmpeg reports.</summary>
    public int? FrameWidth { get; private set; }

    public int? FrameHeight { get; private set; }

    public double? DurationSeconds { get; private set; }

    public void AddLine(string line)
    {
        var bounds = BoundsLine.Match(line);
        if (bounds.Success)
        {
            AddSample(bounds);
            return;
        }

        if (line.StartsWith("Output #0", StringComparison.Ordinal))
        {
            _inOutputSection = true;
            return;
        }

        if (_inOutputSection && FrameWidth == null)
        {
            var video = line.IndexOf("Video:", StringComparison.Ordinal);
            var size = video < 0 ? Match.Empty : FrameSize.Match(line, video);
            if (size.Success)
            {
                FrameWidth = int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture);
                FrameHeight = int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture);
            }

            return;
        }

        if (DurationSeconds == null)
        {
            var duration = DurationLine.Match(line);
            if (duration.Success)
            {
                DurationSeconds = (int.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture) * 3600)
                    + (int.Parse(duration.Groups[2].Value, CultureInfo.InvariantCulture) * 60)
                    + double.Parse(duration.Groups[3].Value, CultureInfo.InvariantCulture);
            }
        }
    }

    private void AddSample(Match bounds)
    {
        var x1 = Int(bounds, 1);
        var x2 = Int(bounds, 2);
        var y1 = Int(bounds, 3);
        var y2 = Int(bounds, 4);

        // cropdetect prints t:nan when a frame has no timestamp; keep the timeline ordered regardless.
        var previous = _samples.Count > 0 ? _samples[^1].Time : 0;
        var time = bounds.Groups[5].Success
            && double.TryParse(bounds.Groups[5].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
            && double.IsFinite(t)
                ? Math.Max(t, previous)
                : previous;

        var box = x2 <= x1 || y2 <= y1 ? null : new CropBox(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
        _samples.Add(new KeyframeSample(time, box));
    }

    private static int Int(Match match, int group)
        => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
}

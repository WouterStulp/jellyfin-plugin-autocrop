using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AutoCrop;

public sealed record AnalyzerOptions(
    double MinimumBarPercent,
    double MinimumMatteShare = 0.05,
    int MinimumMatteKeyframes = 20);

/// <summary>
/// Turns per-keyframe cropdetect bounds into the two crops of a file: the whole picture, which never
/// cuts any of it, and the theatre crop, the narrowest letterbox shape the file keeps coming back to.
/// </summary>
public static class CropAnalyzer
{
    /// <summary>
    /// Bumped whenever the analysis changes, so stored results are recomputed from their keyframes.
    /// </summary>
    public const int Version = 6;

    /// <summary>Fewer trickplay thumbnails with picture than this prove nothing.</summary>
    internal const int MinimumThumbnails = 20;

    /// <summary>Display aspect ratios that films and series are made in.</summary>
    internal static readonly double[] StandardRatios =
    {
        4.0 / 3, 1.375, 1.43, 1.66, 16.0 / 9, 1.85, 1.90, 2.00, 2.20, 2.35, 2.39, 2.40, 2.55, 2.76,
    };

    /// <summary>
    /// Snaps a measured crop outward to the nearest standard display aspect ratio: the long side keeps
    /// its extent, the short side grows evenly around the box (shifted inside the frame at an edge), so
    /// no measured picture is ever cut. When the frame is too small for that, the box stays as measured.
    /// Returns why the box can't be a real picture shape instead when it is suspicious: no standard
    /// ratio within 1.5%, a ratio outside 1.25-2.90, or less than half the frame. The full frame is
    /// never suspicious. <paramref name="pixelAspect"/> is the stream's sample aspect ratio (SAR).
    /// </summary>
    public static (CropBox Box, string? Suspicious) Snap(CropBox box, int frameWidth, int frameHeight, double pixelAspect = 1)
    {
        if (box == CropBox.Full(frameWidth, frameHeight) || box.Width <= 0 || box.Height <= 0)
            return (box, null);

        var ratio = box.Width * pixelAspect / box.Height;
        if (ratio is < 1.25 or > 2.90)
            return (box, FormattableString.Invariant($"Picture {ratio:0.00}:1 is outside 1.25:1 to 2.90:1"));

        var standard = StandardRatios.MinBy(r => Math.Abs(ratio - r) / r);
        if (Math.Abs(ratio - standard) / standard > 0.015)
            return (box, FormattableString.Invariant($"Picture {ratio:0.00}:1 is no standard aspect ratio"));

        var snapped = box;
        if (ratio < standard)
        {
            var width = (int)Math.Ceiling((box.Height * standard / pixelAspect) - 1e-6);
            if (width <= frameWidth)
                snapped = box with { X = Grow(box.X, box.Width, width, frameWidth), Width = width };
        }
        else
        {
            var height = (int)Math.Ceiling((box.Width * pixelAspect / standard) - 1e-6);
            if (height <= frameHeight)
                snapped = box with { Y = Grow(box.Y, box.Height, height, frameHeight), Height = height };
        }

        if (snapped.Area * 2 < (long)frameWidth * frameHeight)
            return (box, FormattableString.Invariant($"Picture covers only {100.0 * snapped.Area / ((long)frameWidth * frameHeight):0}% of the frame"));

        return (snapped, null);
    }

    // The new start of a side grown from length to size, centred on the old one and kept in the frame.
    private static int Grow(int start, int length, int size, int frame)
        => Math.Clamp(start - ((size - length) / 2), 0, frame - size);

    /// <summary>
    /// The whole picture: the union over every keyframe that isn't fully black. A row or column that
    /// is black in all of them is a burned-in bar; one full-frame shot means no crop.
    /// </summary>
    public static CropBox Union(IEnumerable<KeyframeSample> samples, int frameWidth, int frameHeight, double minimumBarPercent)
    {
        CropBox? union = null;
        foreach (var box in samples.Select(s => s.Box).OfType<CropBox>())
            union = union == null ? box : union.Union(box);

        return union == null
            ? CropBox.Full(frameWidth, frameHeight)
            : DropThinBars(union, frameWidth, frameHeight, minimumBarPercent);
    }

    /// <summary>
    /// The theatre crop: the narrowest letterbox shape (full width, bars above and below) among the
    /// shapes the file keeps coming back to, which wider shots are cut to, as a regular cinema shows
    /// an IMAX film. The whole picture when there is none. A pillarbox shape never counts: on a wide
    /// screen it only adds to the side bars, and cropping to it would cut the sides of everything else.
    /// </summary>
    public static CropBox TheatreCrop(IReadOnlyList<KeyframeSample> samples, int frameWidth, int frameHeight, AnalyzerOptions options)
    {
        var boxes = samples.Select(s => s.Box).OfType<CropBox>().ToList();
        if (boxes.Count == 0)
            return CropBox.Full(frameWidth, frameHeight);

        var toleranceX = Math.Max(4, frameWidth / 200);
        var toleranceY = Math.Max(4, frameHeight / 200);
        var mattes = Mattes(boxes, toleranceX, toleranceY, options);
        var union = mattes[^1];
        var letterbox = mattes.FirstOrDefault(m => m != union && m.X - union.X <= toleranceX && union.Right - m.Right <= toleranceX);
        return DropThinBars(letterbox ?? union, frameWidth, frameHeight, options.MinimumBarPercent);
    }

    /// <summary>
    /// Whether trickplay thumbnails prove there is nothing to crop: together they show picture in
    /// every row and column of the thumbnail, and no letterbox or pillarbox recurs in enough of them
    /// (5%, at least 5) to be a shape of its own. This only ever concludes "no crop"; every crop comes
    /// from the exact scan.
    /// </summary>
    public static bool ShowsNoBars(IReadOnlyList<KeyframeSample> thumbnails, int width, int height)
    {
        var boxes = thumbnails.Select(s => s.Box).OfType<CropBox>().ToList();
        if (boxes.Count < MinimumThumbnails || !boxes.Aggregate((a, b) => a.Union(b)).Contains(CropBox.Full(width, height)))
            return false;

        // One thumbnail pixel of tolerance: that is about 6 video pixels, under the 1% minimum bar.
        var options = new AnalyzerOptions(0, MinimumMatteShare: 0.05, MinimumMatteKeyframes: 5);
        return Mattes(boxes, 1, 1, options).Count == 1;
    }

    /// <summary>
    /// Keeps a bar only when it is at least <paramref name="minimumBarPercent"/> of that dimension,
    /// so compression noise along the edge doesn't turn into a 2-pixel crop.
    /// </summary>
    public static CropBox DropThinBars(CropBox box, int frameWidth, int frameHeight, double minimumBarPercent)
    {
        var minX = frameWidth * minimumBarPercent / 100;
        var minY = frameHeight * minimumBarPercent / 100;

        var left = Math.Clamp(box.X, 0, frameWidth);
        var top = Math.Clamp(box.Y, 0, frameHeight);
        var right = Math.Clamp(box.Right, left, frameWidth);
        var bottom = Math.Clamp(box.Bottom, top, frameHeight);

        if (left < minX) left = 0;
        if (top < minY) top = 0;
        if (frameWidth - right < minX) right = frameWidth;
        if (frameHeight - bottom < minY) bottom = frameHeight;

        return new CropBox(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// The picture shapes the file really uses, smallest first. Always the whole-file union, plus
    /// every letterbox (full width, equal bars top and bottom) or pillarbox (full height, equal bars
    /// left and right) that recurs in enough keyframes. Dark shots and credits give scattered,
    /// asymmetric boxes that never recur often enough to count. Each matte is the union of its
    /// cluster, so it never cuts picture any of its keyframes showed.
    /// </summary>
    internal static IReadOnlyList<CropBox> Mattes(IReadOnlyList<CropBox> boxes, int toleranceX, int toleranceY, AnalyzerOptions options)
    {
        var union = boxes.Aggregate((a, b) => a.Union(b));
        var mattes = new List<CropBox> { union };
        var needed = Math.Max(options.MinimumMatteKeyframes, options.MinimumMatteShare * boxes.Count);

        var letterbox = boxes.Where(b => b.X - union.X <= toleranceX
            && union.Right - b.Right <= toleranceX
            && Math.Abs((b.Y - union.Y) - (union.Bottom - b.Bottom)) <= toleranceY);
        AddClusters(mattes, letterbox, b => (b.Y, b.Bottom), toleranceY, needed, toleranceX, toleranceY);

        var pillarbox = boxes.Where(b => b.Y - union.Y <= toleranceY
            && union.Bottom - b.Bottom <= toleranceY
            && Math.Abs((b.X - union.X) - (union.Right - b.Right)) <= toleranceX);
        AddClusters(mattes, pillarbox, b => (b.X, b.Right), toleranceX, needed, toleranceX, toleranceY);

        return mattes.OrderBy(m => m.Area).ToList();
    }

    // Takes the most common edge pair as a seed, gathers every box within tolerance of it, and repeats
    // until the next cluster is too small to be a real matte.
    private static void AddClusters(
        List<CropBox> mattes,
        IEnumerable<CropBox> candidates,
        Func<CropBox, (int Start, int End)> edges,
        int tolerance,
        double needed,
        int toleranceX,
        int toleranceY)
    {
        var left = candidates.ToList();
        while (left.Count >= needed)
        {
            var seed = left.GroupBy(edges).MaxBy(g => g.Count())!.Key;
            bool Near(CropBox box) => Math.Abs(edges(box).Start - seed.Start) <= tolerance && Math.Abs(edges(box).End - seed.End) <= tolerance;

            var cluster = left.Where(Near).ToList();
            if (cluster.Count < needed)
                return;

            left.RemoveAll(Near);
            var matte = cluster.Aggregate((a, b) => a.Union(b));
            var aspect = (double)matte.Width / matte.Height;
            if (aspect is >= 1.30 and <= 2.80 && !mattes.Any(m => m.IsCloseTo(matte, toleranceX, toleranceY)))
                mattes.Add(matte);
        }
    }
}

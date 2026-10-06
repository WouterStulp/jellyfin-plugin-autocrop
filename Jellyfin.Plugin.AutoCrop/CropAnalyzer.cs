using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AutoCrop;

public sealed record AnalyzerOptions(
    double MinimumBarPercent,
    double MinimumSegmentSeconds,
    int MaxSegments = 200,
    double MinimumMatteShare = 0.05,
    int MinimumMatteKeyframes = 20);

/// <summary>
/// Turns per-keyframe cropdetect bounds into the picture area of a file. Every rule here only ever
/// grows a box: a row or column that shows picture in any keyframe a box covers is never cut.
/// </summary>
public static class CropAnalyzer
{
    /// <summary>
    /// Bumped whenever the analysis changes, so stored results are recomputed from their keyframes.
    /// </summary>
    public const int Version = 3;

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

    /// <summary>
    /// Snaps every segment like <see cref="Snap"/>; a suspicious one gets the whole-file
    /// <paramref name="crop"/>, which contains every keyframe. Neighbours that end up equal are joined.
    /// </summary>
    public static IReadOnlyList<CropSegment> SnapSegments(
        IReadOnlyList<CropSegment> segments, CropBox crop, int frameWidth, int frameHeight, double pixelAspect = 1)
        => MergeSimilar(
            segments.Select(s => s with { Box = Snap(s.Box, frameWidth, frameHeight, pixelAspect) is (var box, null) ? box : crop }).ToList(),
            0,
            0);

    // The new start of a side grown from length to size, centred on the old one and kept in the frame.
    private static int Grow(int start, int length, int size, int frame)
        => Math.Clamp(start - ((size - length) / 2), 0, frame - size);

    /// <summary>
    /// The whole-file picture area: the union over every keyframe that isn't fully black. A row or
    /// column that is black in all of them is a burned-in bar; one full-frame shot means no crop.
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
    /// Whether trickplay thumbnails prove there is nothing to crop: together they show picture in
    /// every row and column of the thumbnail, and no letterbox or pillarbox recurs in enough of them
    /// (5%, at least 5) to be a per-scene matte. This only ever concludes "no crop"; every crop comes
    /// from the exact scan.
    /// </summary>
    public static bool ShowsNoBars(IReadOnlyList<KeyframeSample> thumbnails, int width, int height)
    {
        var boxes = thumbnails.Select(s => s.Box).OfType<CropBox>().ToList();
        if (boxes.Count < MinimumThumbnails || !boxes.Aggregate((a, b) => a.Union(b)).Contains(CropBox.Full(width, height)))
            return false;

        // One thumbnail pixel of tolerance: that is about 6 video pixels, under the 1% minimum bar.
        var options = new AnalyzerOptions(0, 0, MinimumMatteShare: 0.05, MinimumMatteKeyframes: 5);
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
    /// The per-scene timeline. Every keyframe gets the smallest of the file's mattes that contains its
    /// box, so a dark shot or a title card, whose box is smaller than the real picture, lands in the
    /// matte of its scene instead of becoming a picture shape of its own. Consecutive keyframes with
    /// the same matte form a run; the stretch between two runs, where the real change happened
    /// somewhere between two keyframes, gets the union of both. Segments shorter than the minimum
    /// are absorbed into a neighbour by union.
    /// </summary>
    public static IReadOnlyList<CropSegment> Segments(
        IReadOnlyList<KeyframeSample> samples, int frameWidth, int frameHeight, double durationSeconds, AnalyzerOptions options)
    {
        var end = Math.Max(durationSeconds, samples.Count > 0 ? samples.Max(s => s.Time) : 0);
        var picture = samples.Where(s => s.Box != null).OrderBy(s => s.Time).ToList();
        if (picture.Count == 0)
            return new[] { new CropSegment(0, end, CropBox.Full(frameWidth, frameHeight)) };

        var toleranceX = Math.Max(4, frameWidth / 200);
        var toleranceY = Math.Max(4, frameHeight / 200);
        var mattes = Mattes(picture.Select(s => s.Box!).ToList(), toleranceX, toleranceY, options);

        var runs = new List<(double First, double Last, CropBox Box)>();
        foreach (var sample in picture)
        {
            var matte = mattes.First(m => m.Contains(sample.Box!));
            if (runs.Count > 0 && runs[^1].Box == matte)
                runs[^1] = (runs[^1].First, sample.Time, matte);
            else
                runs.Add((sample.Time, sample.Time, matte));
        }

        var segments = new List<CropSegment>();
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            if (i > 0 && run.First > runs[i - 1].Last)
                segments.Add(new CropSegment(runs[i - 1].Last, run.First, runs[i - 1].Box.Union(run.Box)));

            var start = i == 0 ? 0 : run.First;
            var stop = i == runs.Count - 1 ? end : run.Last;
            segments.Add(new CropSegment(start, stop, run.Box));
        }

        segments = MergeSimilar(segments, toleranceX, toleranceY);
        AbsorbShortSegments(segments, options.MinimumSegmentSeconds, toleranceX, toleranceY);

        segments = MergeSimilar(
            segments.Select(s => s with { Box = DropThinBars(s.Box, frameWidth, frameHeight, options.MinimumBarPercent) }).ToList(),
            0,
            0);

        if (segments.Count > options.MaxSegments)
            return new[] { new CropSegment(0, end, segments.Select(s => s.Box).Aggregate((a, b) => a.Union(b))) };

        return segments;
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

    private static List<CropSegment> MergeSimilar(List<CropSegment> segments, int toleranceX, int toleranceY)
    {
        var merged = new List<CropSegment>();
        foreach (var segment in segments)
        {
            if (merged.Count > 0 && merged[^1].Box.IsCloseTo(segment.Box, toleranceX, toleranceY))
                merged[^1] = new CropSegment(merged[^1].Start, segment.End, merged[^1].Box.Union(segment.Box));
            else
                merged.Add(segment);
        }

        return merged;
    }

    private static void AbsorbShortSegments(List<CropSegment> segments, double minimumSeconds, int toleranceX, int toleranceY)
    {
        while (segments.Count > 1)
        {
            var shortest = -1;
            for (var i = 0; i < segments.Count; i++)
            {
                if (segments[i].Duration < minimumSeconds && (shortest < 0 || segments[i].Duration < segments[shortest].Duration))
                    shortest = i;
            }

            if (shortest < 0)
                return;

            // Join the neighbour that loses the least zoom, so a bridge between a narrow and a wide
            // scene widens the wide scene instead of the narrow one.
            var neighbour = shortest == 0 ? 1
                : shortest == segments.Count - 1 ? shortest - 1
                : ZoomLost(segments[shortest], segments[shortest - 1]) <= ZoomLost(segments[shortest], segments[shortest + 1])
                    ? shortest - 1
                    : shortest + 1;

            var first = Math.Min(shortest, neighbour);
            var a = segments[first];
            var b = segments[first + 1];
            segments[first] = new CropSegment(a.Start, b.End, a.Box.Union(b.Box));
            segments.RemoveAt(first + 1);

            var merged = MergeSimilar(segments, toleranceX, toleranceY);
            segments.Clear();
            segments.AddRange(merged);
        }
    }

    private static double ZoomLost(CropSegment a, CropSegment b)
    {
        var union = a.Box.Union(b.Box).Area;
        return ((union - a.Box.Area) * a.Duration) + ((union - b.Box.Area) * b.Duration);
    }
}

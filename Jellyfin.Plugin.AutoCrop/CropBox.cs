using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AutoCrop;

/// <summary>A rectangle in frame pixels. Right and Bottom are exclusive.</summary>
public sealed record CropBox(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height)
{
    [JsonIgnore]
    public int Right => X + Width;

    [JsonIgnore]
    public int Bottom => Y + Height;

    [JsonIgnore]
    public long Area => (long)Width * Height;

    public static CropBox Full(int frameWidth, int frameHeight) => new(0, 0, frameWidth, frameHeight);

    public CropBox Union(CropBox other)
    {
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new CropBox(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public bool Contains(CropBox other)
        => X <= other.X && Y <= other.Y && Right >= other.Right && Bottom >= other.Bottom;

    public override string ToString() => $"{Width}x{Height}+{X}+{Y}";

    public bool IsCloseTo(CropBox other, int toleranceX, int toleranceY)
        => Math.Abs(X - other.X) <= toleranceX
            && Math.Abs(Right - other.Right) <= toleranceX
            && Math.Abs(Y - other.Y) <= toleranceY
            && Math.Abs(Bottom - other.Bottom) <= toleranceY;
}

/// <summary>A stretch of the file, in seconds from its start, that shows one picture shape.</summary>
public sealed record CropSegment(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("box")] CropBox Box)
{
    [JsonIgnore]
    public double Duration => End - Start;
}

/// <summary>One keyframe's cropdetect bounds. Box is null for a fully black keyframe.</summary>
public sealed record KeyframeSample(double Time, CropBox? Box);

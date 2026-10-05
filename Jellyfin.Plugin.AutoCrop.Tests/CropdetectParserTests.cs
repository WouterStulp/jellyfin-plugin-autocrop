namespace Jellyfin.Plugin.AutoCrop.Tests;

public class CropdetectParserTests
{
    // Trimmed from a real `ffmpeg -skip_frame nokey ... cropdetect=...:reset=1` run.
    private const string Stderr = """
        Input #0, matroska,webm, from '/media/show/S01E01.mkv':
          Duration: 00:50:12.48, start: 0.000000, bitrate: 4210 kb/s
          Stream #0:0: Video: h264 (High), yuv420p(tv, bt709, progressive), 1920x1080 [SAR 1:1 DAR 16:9], 23.98 fps
          Stream #0:1: Video: mjpeg (Baseline), yuvj420p(pc), 600x900, 90k tbr (attached pic)
        Stream mapping:
          Stream #0:0 -> #0:0 (h264 (native) -> wrapped_avframe (native))
        [Parsed_cropdetect_0 @ 0x55d1] x1:0 x2:1919 y1:60 y2:1019 w:1920 h:960 x:0 y:60 pts:0 t:0.000000 limit:0.094000 crop=1920:960:0:60
        Output #0, null, to 'pipe:':
          Metadata:
            encoder         : Lavf61.7.100
          Stream #0:0: Video: wrapped_avframe, yuv420p(tv, bt709, progressive), 1920x1080 [SAR 1:1 DAR 16:9], q=2-31, 200 kb/s, 23.98 fps
        [Parsed_cropdetect_0 @ 0x55d1] x1:1919 x2:0 y1:1079 y2:0 w:0 h:0 x:0 y:0 pts:2002 t:2.002000 limit:0.094000 crop=-1904:-1072:1912:1076
        [Parsed_cropdetect_0 @ 0x55d1] x1:2 x2:1917 y1:59 y2:1019 w:1916 h:960 x:2 y:60 pts:4171 t:4.171000 limit:0.094000 crop=1916:960:2:60
        [Parsed_cropdetect_0 @ 0x55d1] x1:0 x2:1919 y1:62 y2:1017 w:1920 h:956 x:0 y:62 pts:NOPTS t:nan limit:0.094000 crop=1920:956:0:62
        """;

    private static CropdetectParser Parse(string text)
    {
        var parser = new CropdetectParser();
        foreach (var line in text.Split('\n'))
            parser.AddLine(line.TrimEnd('\r'));
        return parser;
    }

    [Fact]
    public void ReadsEveryKeyframeWithItsTime()
    {
        var parser = Parse(Stderr);

        Assert.Equal(4, parser.Samples.Count);
        Assert.Equal(new KeyframeSample(0, new CropBox(0, 60, 1920, 960)), parser.Samples[0]);
        Assert.Equal(4.171, parser.Samples[2].Time, 3);
        Assert.Equal(new CropBox(2, 59, 1916, 961), parser.Samples[2].Box);
    }

    [Fact]
    public void FullyBlackKeyframe_HasNoBox()
    {
        var sample = Parse(Stderr).Samples[1];

        Assert.Null(sample.Box);
        Assert.Equal(2.002, sample.Time, 3);
    }

    [Fact]
    public void MissingTimestamp_KeepsThePreviousTime()
    {
        Assert.Equal(4.171, Parse(Stderr).Samples[3].Time, 3);
    }

    [Fact]
    public void FrameSize_ComesFromTheOutputStream_NotTheCoverArt()
    {
        var parser = Parse(Stderr);

        Assert.Equal(1920, parser.FrameWidth);
        Assert.Equal(1080, parser.FrameHeight);
    }

    [Fact]
    public void Duration_IsParsed()
    {
        Assert.Equal(3012.48, Parse(Stderr).DurationSeconds!.Value, 2);
    }

    [Fact]
    public void Arguments_DecodeOnlyKeyframesOfTheMainVideoStream()
    {
        var args = string.Join(' ', CropScanner.Arguments("/m/a b.mkv"));

        Assert.Contains("-skip_frame nokey -i /m/a b.mkv -map 0:V:0", args);
        Assert.Contains("cropdetect=limit=0.094:round=2:reset=1:skip=0", args);
        Assert.EndsWith("-f null -", args);
    }
}

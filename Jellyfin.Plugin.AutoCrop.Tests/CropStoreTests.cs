using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AutoCrop.Tests;

public class CropStoreTests : IDisposable
{
    private readonly string _dir = TestPlugin.TempDir();
    private readonly string _storePath;
    private readonly string _video;

    public CropStoreTests()
    {
        _storePath = Path.Combine(_dir, "data", "crops.json");
        _video = Path.Combine(_dir, "movie.mkv");
        File.WriteAllText(_video, "not really a video");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private CropStore NewStore() => new(() => _storePath, NullLogger<CropStore>.Instance);

    private CropResult ResultFor(string path, Guid? id = null)
    {
        var file = new FileInfo(path);
        return new CropResult
        {
            ItemId = id ?? Guid.NewGuid(),
            Path = path,
            FileSize = file.Length,
            FileModifiedUtc = file.LastWriteTimeUtc,
            FrameWidth = 1920,
            FrameHeight = 1080,
            Crop = new CropBox(0, 60, 1920, 960),
            Segments = new List<CropSegment> { new(0, 10, new CropBox(0, 60, 1920, 960)), new(10, 20, CropBox.Full(1920, 1080)) },
            ScannedAtUtc = DateTime.UtcNow,
        };
    }

    [Fact]
    public void Set_PersistsAndReloadsEverything()
    {
        var result = ResultFor(_video);
        NewStore().Set(result);

        var loaded = NewStore().GetCurrent(result.ItemId, _video);

        Assert.NotNull(loaded);
        Assert.Equal(result.Crop, loaded.Crop);
        Assert.Equal(result.Segments, loaded.Segments);
        Assert.Equal(result.FileModifiedUtc, loaded.FileModifiedUtc);
    }

    [Fact]
    public void GetCurrent_FileSizeChanged_IsInvalid()
    {
        var store = NewStore();
        var result = ResultFor(_video);
        store.Set(result);
        var modified = File.GetLastWriteTimeUtc(_video);

        File.AppendAllText(_video, "more bytes");
        File.SetLastWriteTimeUtc(_video, modified);

        Assert.Null(store.GetCurrent(result.ItemId, _video));
        Assert.NotNull(store.Get(result.ItemId));
    }

    [Fact]
    public void GetCurrent_ModificationTimeChanged_IsInvalid()
    {
        var store = NewStore();
        var result = ResultFor(_video);
        store.Set(result);

        File.SetLastWriteTimeUtc(_video, result.FileModifiedUtc.AddMinutes(1));

        Assert.Null(store.GetCurrent(result.ItemId, _video));
    }

    [Fact]
    public void GetCurrent_DifferentPathOrMissingFile_IsInvalid()
    {
        var store = NewStore();
        var result = ResultFor(_video);
        store.Set(result);

        Assert.Null(store.GetCurrent(result.ItemId, _video + ".other"));
        File.Delete(_video);
        Assert.Null(store.GetCurrent(result.ItemId, _video));
    }

    [Fact]
    public void Write_GoesThroughATempFileThatIsMovedIntoPlace()
    {
        var store = NewStore();
        store.Set(ResultFor(_video));
        var before = File.ReadAllText(_storePath);

        // A crash mid-write leaves a torn temp file behind; the real file must be untouched by it.
        File.WriteAllText(_storePath + ".tmp", "{ torn");
        Assert.Single(NewStore().All());

        store.Set(ResultFor(_video));

        Assert.False(File.Exists(_storePath + ".tmp"));
        Assert.NotEqual(before, File.ReadAllText(_storePath));
        Assert.Equal(2, NewStore().All().Count);
    }

    [Fact]
    public void CorruptFile_StartsEmptyInsteadOfThrowing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        File.WriteAllText(_storePath, "not json");

        Assert.Empty(NewStore().All());
    }

    [Fact]
    public void RemoveAllExcept_DropsItemsThatLeftTheLibrary()
    {
        var store = NewStore();
        var keep = ResultFor(_video);
        store.Set(keep);
        store.Set(ResultFor(_video));

        store.RemoveAllExcept(new HashSet<Guid> { keep.ItemId });

        Assert.Equal(keep.ItemId, Assert.Single(NewStore().All()).ItemId);
    }

    [Fact]
    public void Replace_WithoutAnUpdate_RemovesTheResult()
    {
        var store = NewStore();
        var result = ResultFor(_video);
        store.Set(result);

        store.Replace(new (CropResult, CropResult?)[] { (result, null) });

        Assert.Null(NewStore().Get(result.ItemId));
    }

    [Fact]
    public void Samples_KeepTheirThumbnailScale()
    {
        var store = NewStore();
        var id = Guid.NewGuid();

        store.SetSamples(id, 600, new[] { new KeyframeSample(0, new CropBox(0, 0, 320, 180)) }, (320, 180));

        Assert.Equal((320, 180), NewStore().GetSamples(id)!.Value.ThumbnailSize);
    }

    [Fact]
    public void Samples_RoundTripCompactlyBesideTheResults()
    {
        var store = NewStore();
        var id = Guid.NewGuid();
        var samples = new List<KeyframeSample>
        {
            new(0, new CropBox(0, 60, 1920, 960)),
            new(2.002, null),
            new(4.171, new CropBox(2, 59, 1916, 961)),
        };

        store.SetSamples(id, 3012.48, samples);
        var loaded = NewStore().GetSamples(id);

        Assert.NotNull(loaded);
        Assert.Equal(3012.48, loaded.Value.DurationSeconds);
        Assert.Equal(samples, loaded.Value.Samples);
        Assert.Null(loaded.Value.ThumbnailSize);
        Assert.True(File.Exists(Path.Combine(_dir, "data", "samples", id.ToString("N") + ".json.gz")));
        Assert.False(File.Exists(_storePath));
        Assert.Null(store.GetSamples(Guid.NewGuid()));
    }

    [Fact]
    public void RemoveAllExcept_DeletesTheKeyframesToo()
    {
        var store = NewStore();
        var keep = ResultFor(_video);
        var gone = ResultFor(_video);
        store.Set(keep);
        store.Set(gone);
        store.SetSamples(keep.ItemId, 10, new[] { new KeyframeSample(0, null) });
        store.SetSamples(gone.ItemId, 10, new[] { new KeyframeSample(0, null) });

        store.RemoveAllExcept(new HashSet<Guid> { keep.ItemId });

        Assert.NotNull(store.GetSamples(keep.ItemId));
        Assert.Null(store.GetSamples(gone.ItemId));
    }

    [Fact]
    public void Replace_SkipsResultsThatChangedSinceTheyWereRead()
    {
        var store = NewStore();
        var stale = ResultFor(_video);
        var current = ResultFor(_video);
        store.Set(stale);
        store.Set(current);
        var rescanned = ResultFor(_video, stale.ItemId);
        store.Set(rescanned);

        store.Replace(new (CropResult, CropResult?)[] { (stale, stale.Copy()), (current, current.Copy()) });

        Assert.Same(rescanned, store.Get(stale.ItemId));
        Assert.NotSame(current, store.Get(current.ItemId));
        Assert.Equal(current.Crop, NewStore().Get(current.ItemId)!.Crop);
    }

    [Fact]
    public void HasCrop_FullFrameUnionWithCroppedSegments_StillCounts()
    {
        var result = ResultFor(_video);
        result.Crop = CropBox.Full(1920, 1080);

        Assert.True(result.HasCrop);
        Assert.True(result.IsPerScene);
    }

    [Fact]
    public void HasCrop_FailedOrFullFrame_IsFalse()
    {
        var full = ResultFor(_video);
        full.Crop = CropBox.Full(1920, 1080);
        full.Segments = null;
        var failed = ResultFor(_video);
        failed.Error = "ffmpeg exited with code 1";

        Assert.False(full.HasCrop);
        Assert.False(failed.HasCrop);
        Assert.False(failed.IsPerScene);
    }
}

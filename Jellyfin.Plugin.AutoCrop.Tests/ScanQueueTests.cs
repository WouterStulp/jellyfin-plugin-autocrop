using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.AutoCrop.Tests;

[Collection("Plugin")]
public class ScanQueueTests : IAsyncLifetime
{
    private readonly string _dir = TestPlugin.TempDir();
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
    private readonly List<Guid> _processed = new();
    private readonly object _lock = new();
    private ScanQueue _queue = null!;
    private int _running;
    private int _maxRunning;

    public async Task InitializeAsync()
    {
        TestPlugin.Create(_dir);
        var store = new CropStore(() => Path.Combine(_dir, "crops.json"), NullLogger<CropStore>.Instance);
        var scanner = new CropScanner(Substitute.For<IMediaEncoder>(), store, NullLogger<CropScanner>.Instance);
        _queue = new ScanQueue(_library, scanner, NullLogger<ScanQueue>.Instance)
        {
            SettleDelay = TimeSpan.Zero,
            ProcessOverride = async (id, ct) =>
            {
                var now = Interlocked.Increment(ref _running);
                lock (_lock)
                    _maxRunning = Math.Max(_maxRunning, now);
                await Task.Delay(30, ct);
                lock (_lock)
                    _processed.Add(id);
                Interlocked.Decrement(ref _running);
            },
        };
        await _queue.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _queue.StopAsync(CancellationToken.None);
        _queue.Dispose();
        Directory.Delete(_dir, true);
    }

    private async Task WaitForProcessed(int count)
    {
        for (var i = 0; i < 200; i++)
        {
            lock (_lock)
            {
                if (_processed.Count >= count)
                    return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Only {_processed.Count} of {count} items were processed");
    }

    private static Movie MovieAt(string path) => new() { Id = Guid.NewGuid(), Path = path };

    [Fact]
    public async Task ProcessesItemsOneAtATime_InOrder()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
            _queue.Enqueue(id);

        await WaitForProcessed(ids.Count);

        Assert.Equal(1, _maxRunning);
        Assert.Equal(ids, _processed);
    }

    [Fact]
    public async Task DuplicateWhileWaiting_IsQueuedOnce()
    {
        var id = Guid.NewGuid();
        _queue.SettleDelay = TimeSpan.FromMilliseconds(100);

        Assert.True(_queue.Enqueue(id));
        Assert.False(_queue.Enqueue(id));
        await WaitForProcessed(1);
        await Task.Delay(100);

        Assert.Single(_processed);
    }

    [Fact]
    public async Task SettleDelay_HoldsNewItemsBack()
    {
        _queue.SettleDelay = TimeSpan.FromMilliseconds(300);
        _queue.Enqueue(Guid.NewGuid());

        await Task.Delay(100);
        Assert.Empty(_processed);
        await WaitForProcessed(1);
    }

    [Fact]
    public async Task ItemAddedAndUpdated_QueueMoviesAndEpisodes()
    {
        var added = MovieAt("/media/a.mkv");
        var updated = MovieAt("/media/b.mkv");

        _library.ItemAdded += Raise.Event<EventHandler<ItemChangeEventArgs>>(_library, new ItemChangeEventArgs { Item = added });
        _library.ItemUpdated += Raise.Event<EventHandler<ItemChangeEventArgs>>(_library, new ItemChangeEventArgs { Item = updated });
        await WaitForProcessed(2);

        Assert.Equal(new[] { added.Id, updated.Id }, _processed);
    }

    [Fact]
    public async Task ItemAdded_IgnoresAudioStrmRemoteAndVirtualItems()
    {
        var items = new BaseItem[]
        {
            new Audio { Id = Guid.NewGuid(), Path = "/media/song.flac" },
            MovieAt("/media/film.strm"),
            MovieAt("https://example.invalid/film.mkv"),
            new Movie { Id = Guid.NewGuid(), Path = "/media/missing.mkv", IsVirtualItem = true },
            new Folder { Id = Guid.NewGuid(), Path = "/media" },
        };

        foreach (var item in items)
            _library.ItemAdded += Raise.Event<EventHandler<ItemChangeEventArgs>>(_library, new ItemChangeEventArgs { Item = item });
        await Task.Delay(150);

        Assert.Empty(_processed);
        Assert.Equal(0, _queue.PendingCount);
    }

    [Fact]
    public async Task Disabled_IgnoresLibraryEvents()
    {
        Plugin.Instance!.Configuration.Enabled = false;

        _library.ItemAdded += Raise.Event<EventHandler<ItemChangeEventArgs>>(_library, new ItemChangeEventArgs { Item = MovieAt("/media/a.mkv") });
        await Task.Delay(100);

        Assert.Empty(_processed);
    }
}

using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.AutoCrop.Tests;

public class LibraryTitlesTests : IAsyncLifetime
{
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
    private readonly IServerApplicationHost _host = Substitute.For<IServerApplicationHost>();
    private readonly Movie _dune = new() { Id = Guid.NewGuid(), Name = "Dune", Path = "/media/dune.mkv" };
    private LibraryTitles _titles = null!;

    public async Task InitializeAsync()
    {
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { _dune });
        _titles = new LibraryTitles(_library, _host, NullLogger<LibraryTitles>.Instance);
        await _titles.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _titles.StopAsync(CancellationToken.None);
        _titles.Dispose();
    }

    private void Fire(string change, BaseItem item)
    {
        var args = new ItemChangeEventArgs { Item = item };
        if (change == "added")
            _library.ItemAdded += Raise.Event<EventHandler<ItemChangeEventArgs>>(_library, args);
        else if (change == "updated")
            _library.ItemUpdated += Raise.Event<EventHandler<ItemChangeEventArgs>>(_library, args);
        else
            _library.ItemRemoved += Raise.Event<EventHandler<ItemChangeEventArgs>>(_library, args);
    }

    [Fact]
    public async Task IsBuiltInTheBackground_OnceJellyfinHasStarted()
    {
        await Task.Delay(1500);
        _library.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());

        _host.CoreStartupHasCompleted.Returns(true);
        for (var i = 0; i < 50 && _library.ReceivedCalls().All(c => c.GetMethodInfo().Name != nameof(ILibraryManager.GetItemList)); i++)
            await Task.Delay(100);

        _library.Received(1).GetItemList(Arg.Any<InternalItemsQuery>());
        Assert.Equal("Dune", _titles.Get()[_dune.Id]);
        _library.Received(1).GetItemList(Arg.Any<InternalItemsQuery>());
    }

    [Fact]
    public void AddedEpisode_IsListedWithoutAnotherQuery()
    {
        _titles.Get();
        var episode = new Episode { Id = Guid.NewGuid(), Path = "/media/show/s01e02.mkv", SeriesName = "Severance", ParentIndexNumber = 1, IndexNumber = 2 };

        Fire("added", episode);

        Assert.Equal("Severance · S01E02", _titles.Get()[episode.Id]);
        _library.Received(1).GetItemList(Arg.Any<InternalItemsQuery>());
    }

    [Fact]
    public void UpdatedItem_GetsItsNewTitle()
    {
        var before = _titles.Get();
        _dune.Name = "Dune: Part One";

        Fire("updated", _dune);

        Assert.Equal("Dune: Part One", _titles.Get()[_dune.Id]);
        Assert.Equal("Dune", before[_dune.Id]);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("no longer eligible")]
    public void RemovedOrIneligibleItem_IsDropped(string change)
    {
        _titles.Get();
        if (change == "removed")
        {
            Fire("removed", _dune);
        }
        else
        {
            _dune.IsVirtualItem = true;
            Fire("updated", _dune);
        }

        Assert.False(_titles.Get().ContainsKey(_dune.Id));
    }

    [Fact]
    public void EventsBeforeTheFirstBuild_DontQueryTheLibrary()
    {
        Fire("added", new Movie { Id = Guid.NewGuid(), Name = "Up", Path = "/media/up.mkv" });

        _library.DidNotReceive().GetItemList(Arg.Any<InternalItemsQuery>());
        Assert.Single(_titles.Get());
    }

    [Fact]
    public void OtherItems_AreIgnored()
    {
        _titles.Get();

        Fire("added", new Folder { Id = Guid.NewGuid(), Name = "Films", Path = "/media" });

        Assert.Single(_titles.Get());
    }
}

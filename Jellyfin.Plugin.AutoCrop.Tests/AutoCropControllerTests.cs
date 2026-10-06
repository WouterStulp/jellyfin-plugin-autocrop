using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AutoCrop.Api;
using Jellyfin.Plugin.AutoCrop.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.AutoCrop.Tests;

[Collection("Plugin")]
public class AutoCropControllerTests : IDisposable
{
    private readonly string _dir = TestPlugin.TempDir();
    private readonly ILibraryManager _library = Substitute.For<ILibraryManager>();
    private readonly IUserManager _users = Substitute.For<IUserManager>();
    private readonly User _alice = new("alice", "test-provider", "test-reset");
    private readonly CropStore _store;
    private readonly CropScanner _scanner;
    private readonly ScanQueue _queue;
    private readonly LibraryTitles _titles;

    public AutoCropControllerTests()
    {
        TestPlugin.Create(_dir);
        _titles = new LibraryTitles(_library, Substitute.For<IServerApplicationHost>(), NullLogger<LibraryTitles>.Instance);
        _users.GetUserById(_alice.Id).Returns(_alice);
        _store = new CropStore(() => Path.Combine(_dir, "crops.json"), NullLogger<CropStore>.Instance);
        _scanner = new CropScanner(Substitute.For<IMediaEncoder>(), Substitute.For<MediaBrowser.Common.Configuration.IConfigurationManager>(), Substitute.For<MediaBrowser.Controller.Trickplay.ITrickplayManager>(), _store, NullLogger<CropScanner>.Instance);
        _queue = new ScanQueue(_library, _scanner, NullLogger<ScanQueue>.Instance);
    }

    public void Dispose()
    {
        _queue.Dispose();
        Directory.Delete(_dir, true);
    }

    private AutoCropController Controller(Guid? userId)
    {
        var identity = userId == null
            ? new ClaimsIdentity()
            : new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", userId.Value.ToString("N")) }, "Test");
        return new AutoCropController(_library, _users, _store, _scanner, _queue, _titles)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } },
        };
    }

    /// <summary>A movie on disk with a stored scan result, visible to alice unless stated otherwise.</summary>
    private Movie ScannedMovie(CropBox crop, List<CropSegment>? segments = null, bool visibleToAlice = true, string name = "Film")
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".mkv");
        File.WriteAllText(path, "video");
        var movie = new Movie { Id = Guid.NewGuid(), Path = path, Name = name };
        var file = new FileInfo(path);
        _store.Set(new CropResult
        {
            ItemId = movie.Id,
            Path = path,
            FileSize = file.Length,
            FileModifiedUtc = file.LastWriteTimeUtc,
            FrameWidth = 1920,
            FrameHeight = 1080,
            Crop = crop,
            Segments = segments,
            ScannedAtUtc = DateTime.UtcNow,
        });
        _library.GetItemById(movie.Id).Returns(movie);
        _library.GetItemById<BaseItem>(movie.Id, _alice).Returns(visibleToAlice ? movie : null);
        return movie;
    }

    private static JsonElement Json(ActionResult result)
        => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public void GetItem_ReturnsCropSegmentsAndPlayerSettings()
    {
        var segments = new List<CropSegment>
        {
            new(0, 600, new CropBox(0, 138, 1920, 804)),
            new(600, 900, new CropBox(0, 35, 1920, 1010)),
        };
        var movie = ScannedMovie(new CropBox(0, 35, 1920, 1010), segments);
        Plugin.Instance!.Configuration.DefaultMode = "static";
        Plugin.Instance.Configuration.TransitionMs = 0;

        var json = Json(Controller(_alice.Id).GetItem(movie.Id));

        Assert.Equal(1920, json.GetProperty("frameWidth").GetInt32());
        Assert.Equal(35, json.GetProperty("crop").GetProperty("y").GetInt32());
        Assert.Equal(2, json.GetProperty("segments").GetArrayLength());
        Assert.Equal(600, json.GetProperty("segments")[1].GetProperty("start").GetDouble());
        Assert.Equal("static", json.GetProperty("defaultMode").GetString());
        Assert.Equal(0, json.GetProperty("transitionMs").GetInt32());
        Assert.True(json.GetProperty("zoomStyledSubtitles").GetBoolean());
    }

    [Fact]
    public void GetItem_StyledSubtitleZoom_CanBeTurnedOff()
    {
        var movie = ScannedMovie(new CropBox(0, 60, 1920, 960));
        Plugin.Instance!.Configuration.ZoomStyledSubtitles = false;

        Assert.False(Json(Controller(_alice.Id).GetItem(movie.Id)).GetProperty("zoomStyledSubtitles").GetBoolean());
    }

    private Episode ScannedEpisode(Guid seriesId, Guid libraryId)
    {
        var movie = ScannedMovie(new CropBox(0, 60, 1920, 960));
        var episode = new Episode { Id = movie.Id, Path = movie.Path, SeriesId = seriesId, SeriesName = "Black Clover" };
        _library.GetItemById(episode.Id).Returns(episode);
        _library.GetItemById<BaseItem>(episode.Id, _alice).Returns(episode);
        _library.GetCollectionFolders(episode).Returns(new List<Folder> { new CollectionFolder { Id = libraryId } });
        return episode;
    }

    [Fact]
    public void GetItem_DefaultMode_IsTheItemsThenSeriesThenLibraryThenServerMode()
    {
        var series = Guid.NewGuid();
        var library = Guid.NewGuid();
        var episode = ScannedEpisode(series, library);
        var config = Plugin.Instance!.Configuration;
        string Mode() => Json(Controller(_alice.Id).GetItem(episode.Id)).GetProperty("defaultMode").GetString()!;

        config.DefaultMode = "static";
        Assert.Equal("static", Mode());
        config.ModeOverrides.Add(new ModeOverride { Id = library, Mode = "off" });
        Assert.Equal("off", Mode());
        config.ModeOverrides.Add(new ModeOverride { Id = series, Mode = "per-scene" });
        Assert.Equal("per-scene", Mode());
        config.ModeOverrides.Add(new ModeOverride { Id = episode.Id, Mode = "static" });
        Assert.Equal("static", Mode());
    }

    [Fact]
    public void GetItem_RemembersViewerModesPerSeries_OrPerMovie()
    {
        var series = Guid.NewGuid();
        var episode = ScannedEpisode(series, Guid.NewGuid());
        var movie = ScannedMovie(new CropBox(0, 60, 1920, 960));

        Assert.Equal(series.ToString("N"), Json(Controller(_alice.Id).GetItem(episode.Id)).GetProperty("seriesId").GetString());
        Assert.Equal(movie.Id.ToString("N"), Json(Controller(_alice.Id).GetItem(movie.Id)).GetProperty("seriesId").GetString());
    }

    [Fact]
    public void SetMode_StoresAndReplacesTheOverride_ClearModeRemovesIt()
    {
        var movie = ScannedMovie(new CropBox(0, 60, 1920, 960));
        var controller = Controller(_alice.Id);

        Assert.IsType<NoContentResult>(controller.SetMode(movie.Id, "off"));
        Assert.IsType<NoContentResult>(controller.SetMode(movie.Id, "static"));
        var stored = Assert.Single(Plugin.Instance!.Configuration.ModeOverrides);
        Assert.Equal((movie.Id, "static"), (stored.Id, stored.Mode));
        Assert.Equal("static", Json(controller.GetResults()).GetProperty("items")[0].GetProperty("mode").GetString());

        Assert.IsType<NoContentResult>(controller.ClearMode(movie.Id));
        Assert.Empty(Plugin.Instance.Configuration.ModeOverrides);
    }

    [Fact]
    public void SetMode_AcceptsLibrariesAndSeries_RejectsUnknownItemsAndModes()
    {
        var library = new CollectionFolder { Id = Guid.NewGuid() };
        var series = new Series { Id = Guid.NewGuid() };
        _library.GetItemById(library.Id).Returns(library);
        _library.GetItemById(series.Id).Returns(series);
        var controller = Controller(_alice.Id);

        Assert.IsType<NoContentResult>(controller.SetMode(library.Id, "off"));
        Assert.IsType<NoContentResult>(controller.SetMode(series.Id, "static"));
        Assert.IsType<NotFoundResult>(controller.SetMode(Guid.NewGuid(), "off"));
        Assert.IsType<BadRequestResult>(controller.SetMode(series.Id, "inherit"));
        Assert.IsType<BadRequestResult>(controller.SetMode(series.Id, null));
        Assert.Equal(2, Plugin.Instance!.Configuration.ModeOverrides.Count);
    }

    [Fact]
    public void Libraries_AreTheMovieTvAndMixedOnes_WithTheirMode()
    {
        var films = Guid.NewGuid();
        var shows = Guid.NewGuid();
        var mixed = Guid.NewGuid();
        _library.GetVirtualFolders().Returns(new List<VirtualFolderInfo>
        {
            new() { Name = "Films", ItemId = films.ToString(), CollectionType = CollectionTypeOptions.movies },
            new() { Name = "Anime", ItemId = shows.ToString(), CollectionType = CollectionTypeOptions.tvshows },
            new() { Name = "Mixed", ItemId = mixed.ToString(), CollectionType = null },
            new() { Name = "Music", ItemId = Guid.NewGuid().ToString(), CollectionType = CollectionTypeOptions.music },
        });
        Plugin.Instance!.Configuration.ModeOverrides.Add(new ModeOverride { Id = shows, Mode = "static" });

        var libraries = Json(Controller(_alice.Id).GetLibraries()).EnumerateArray().ToList();

        Assert.Equal(new[] { "Films", "Anime", "Mixed" }, libraries.Select(l => l.GetProperty("name").GetString()));
        Assert.Equal(shows.ToString("N"), libraries[1].GetProperty("id").GetString());
        Assert.Equal("static", libraries[1].GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, libraries[0].GetProperty("mode").ValueKind);
    }

    [Fact]
    public void Results_SeriesFilter_ListsSeriesWithTheirEpisodesAndMode()
    {
        var clover = Guid.NewGuid();
        var episodes = Enumerable.Range(1, 3).Select(i => new Episode
        {
            Id = Guid.NewGuid(), Path = $"/media/clover/{i}.mkv", SeriesId = clover, SeriesName = "Black Clover", ParentIndexNumber = 1, IndexNumber = i,
        }).ToList();
        var film = new Movie { Id = Guid.NewGuid(), Name = "Dune", Path = "/media/dune.mkv" };
        foreach (var item in episodes.Cast<BaseItem>().Append(film))
            _store.Set(new CropResult { ItemId = item.Id, Path = item.Path, FrameWidth = 1920, FrameHeight = 1080, Crop = item == episodes[0] ? new CropBox(0, 60, 1920, 960) : CropBox.Full(1920, 1080) });
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(episodes.Cast<BaseItem>().Append(film).ToList());
        Plugin.Instance!.Configuration.ModeOverrides.Add(new ModeOverride { Id = clover, Mode = "static" });

        var page = Json(Controller(_alice.Id).GetResults(filter: "series", search: "clover"));

        var row = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(clover.ToString("N"), row.GetProperty("itemId").GetString());
        Assert.Equal("Black Clover", row.GetProperty("title").GetString());
        Assert.Equal("series", row.GetProperty("kind").GetString());
        Assert.Equal(3, row.GetProperty("episodes").GetInt32());
        Assert.Equal(1, row.GetProperty("withBars").GetInt32());
        Assert.Equal("static", row.GetProperty("mode").GetString());
    }

    [Fact]
    public void GetItem_ItemTheCallerCannotSee_Is404()
    {
        var movie = ScannedMovie(new CropBox(0, 60, 1920, 960), visibleToAlice: false);

        Assert.IsType<NotFoundResult>(Controller(_alice.Id).GetItem(movie.Id));
    }

    [Fact]
    public void GetItem_NoOrUnknownUser_IsForbidden()
    {
        var movie = ScannedMovie(new CropBox(0, 60, 1920, 960));

        Assert.IsType<ForbidResult>(Controller(null).GetItem(movie.Id));
        Assert.IsType<ForbidResult>(Controller(Guid.NewGuid()).GetItem(movie.Id));
    }

    [Fact]
    public void GetItem_NoCropNeeded_Is404()
    {
        var movie = ScannedMovie(CropBox.Full(1920, 1080));

        Assert.IsType<NotFoundResult>(Controller(_alice.Id).GetItem(movie.Id));
    }

    [Fact]
    public void GetItem_NotScannedOrFileChanged_Is404()
    {
        var movie = ScannedMovie(new CropBox(0, 60, 1920, 960));
        File.AppendAllText(movie.Path, "re-encoded");

        Assert.IsType<NotFoundResult>(Controller(_alice.Id).GetItem(movie.Id));
        Assert.IsType<NotFoundResult>(Controller(_alice.Id).GetItem(Guid.NewGuid()));
    }

    [Fact]
    public void GetItem_PluginDisabled_Is404()
    {
        var movie = ScannedMovie(new CropBox(0, 60, 1920, 960));
        Plugin.Instance!.Configuration.Enabled = false;

        Assert.IsType<NotFoundResult>(Controller(_alice.Id).GetItem(movie.Id));
    }

    [Fact]
    public void Rescan_DropsTheResultAndQueuesTheItem()
    {
        var movie = ScannedMovie(new CropBox(0, 60, 1920, 960));

        Assert.IsType<AcceptedResult>(Controller(_alice.Id).Rescan(movie.Id));

        Assert.Null(_store.Get(movie.Id));
        Assert.Equal(1, _queue.PendingCount);
    }

    [Fact]
    public void Rescan_UnknownItem_Is404()
    {
        Assert.IsType<NotFoundResult>(Controller(_alice.Id).Rescan(Guid.NewGuid()));
    }

    [Fact]
    public void Reanalyse_RecomputesFromKeyframesAndQueuesItemsWithout()
    {
        var withSamples = ScannedMovie(CropBox.Full(1920, 1080));
        var keyframes = Enumerable.Range(0, 30).Select(i => new KeyframeSample(i * 2, new CropBox(0, 60, 1920, 960))).ToList();
        _store.SetSamples(withSamples.Id, 60, keyframes);
        var withoutSamples = ScannedMovie(new CropBox(0, 60, 1920, 960));

        var json = Json(Controller(_alice.Id).Reanalyse());

        Assert.Equal(1, json.GetProperty("reanalysed").GetInt32());
        Assert.Equal(1, json.GetProperty("rescanning").GetInt32());
        Assert.Equal(new CropBox(0, 60, 1920, 960), _store.Get(withSamples.Id)!.Crop);
        Assert.Equal(CropAnalyzer.Version, _store.Get(withSamples.Id)!.AnalysisVersion);
        Assert.Equal(1, _queue.PendingCount);
        Assert.NotNull(_store.Get(withoutSamples.Id));
    }

    [Fact]
    public void Stats_CountsEveryCategory()
    {
        ScannedMovie(new CropBox(0, 60, 1920, 960));
        ScannedMovie(CropBox.Full(1920, 1080));
        var perScene = new List<CropSegment> { new(0, 10, new CropBox(0, 138, 1920, 804)), new(10, 20, CropBox.Full(1920, 1080)) };
        ScannedMovie(CropBox.Full(1920, 1080), perScene);
        _store.Set(new CropResult { ItemId = Guid.NewGuid(), Path = "/x.mkv", Error = "ffmpeg exited with code 1" });
        var unscanned = new Movie { Id = Guid.NewGuid(), Path = "/media/new.mkv" };
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(new List<BaseItem> { unscanned });

        var json = Json(Controller(_alice.Id).GetStats());

        Assert.Equal(3, json.GetProperty("scanned").GetInt32());
        Assert.Equal(2, json.GetProperty("withBars").GetInt32());
        Assert.Equal(1, json.GetProperty("perScene").GetInt32());
        Assert.Equal(1, json.GetProperty("failed").GetInt32());
        Assert.Equal(1, json.GetProperty("pending").GetInt32());
    }

    [Fact]
    public void Results_FiltersSearchesAndPages()
    {
        ScannedMovie(new CropBox(0, 60, 1920, 960), name: "Dune");
        ScannedMovie(new CropBox(0, 138, 1920, 804), name: "Dunkirk");
        ScannedMovie(CropBox.Full(1920, 1080), name: "Up");
        var episode = new Episode { Id = Guid.NewGuid(), SeriesName = "Severance", ParentIndexNumber = 1, IndexNumber = 2 };
        _library.GetItemById(episode.Id).Returns(episode);
        _store.Set(new CropResult { ItemId = episode.Id, Path = "/e.mkv", Error = "Unreadable file" });
        var controller = Controller(_alice.Id);

        var bars = Json(controller.GetResults(filter: "bars"));
        var search = Json(controller.GetResults(search: "dun", limit: 1));
        var failed = Json(controller.GetResults(filter: "failed"));

        Assert.Equal(2, bars.GetProperty("total").GetInt32());
        Assert.Equal(2, search.GetProperty("total").GetInt32());
        Assert.Equal(1, search.GetProperty("items").GetArrayLength());
        var row = failed.GetProperty("items")[0];
        Assert.Equal("Severance · S01E02", row.GetProperty("title").GetString());
        Assert.Equal("failed", row.GetProperty("status").GetString());
        Assert.Equal("Unreadable file", row.GetProperty("error").GetString());
    }

    [Fact]
    public void Results_TakeTitlesFromOneLibraryQuery_NotALookupPerResult()
    {
        var movies = Enumerable.Range(0, 30).Select(i => new Movie { Id = Guid.NewGuid(), Name = $"Film {i:00}", Path = $"/media/film{i}.mkv" }).ToList();
        foreach (var movie in movies)
            _store.Set(new CropResult { ItemId = movie.Id, Path = movie.Path, FrameWidth = 1920, FrameHeight = 1080, Crop = CropBox.Full(1920, 1080), ScannedAtUtc = DateTime.UtcNow });
        _library.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(movies.Cast<BaseItem>().ToList());
        var controller = Controller(_alice.Id);

        var page = Json(controller.GetResults(limit: 25));
        var search = Json(controller.GetResults(search: "Film 07"));
        Json(controller.GetStats());

        Assert.Equal(25, page.GetProperty("items").GetArrayLength());
        Assert.Equal("Film 07", search.GetProperty("items")[0].GetProperty("title").GetString());
        _library.Received(1).GetItemList(Arg.Any<InternalItemsQuery>());
        _library.DidNotReceive().GetItemById(Arg.Any<Guid>());
    }

    [Theory]
    [InlineData("Dune", "Dune (2021)")]
    [InlineData("Dune (2021)", "Dune (2021)")]
    public void Results_MovieTitle_HasTheYearOnce(string name, string expected)
    {
        var movie = ScannedMovie(new CropBox(0, 60, 1920, 960), name: name);
        movie.ProductionYear = 2021;

        var row = Json(Controller(_alice.Id).GetResults()).GetProperty("items")[0];

        Assert.Equal(expected, row.GetProperty("title").GetString());
    }

    [Fact]
    public void Results_ReportAspectRatio()
    {
        ScannedMovie(new CropBox(0, 60, 1920, 960));

        var row = Json(Controller(_alice.Id).GetResults()).GetProperty("items")[0];

        Assert.Equal(2.0, row.GetProperty("aspect").GetDouble());
        Assert.Equal("bars", row.GetProperty("status").GetString());
    }

    [Fact]
    public void Suspicious_IsItsOwnStatusFilterAndCount_AndNeverCropped()
    {
        var movie = ScannedMovie(CropBox.Full(1920, 1080), name: "Strip");
        _store.Get(movie.Id)!.SuspiciousReason = "Picture 5.93:1 is outside 1.25:1 to 2.90:1";
        ScannedMovie(CropBox.Full(1920, 1080), name: "Up");
        var controller = Controller(_alice.Id);

        var row = Json(controller.GetResults(filter: "suspicious")).GetProperty("items");

        Assert.Equal(1, row.GetArrayLength());
        Assert.Equal("suspicious", row[0].GetProperty("status").GetString());
        Assert.Equal("Picture 5.93:1 is outside 1.25:1 to 2.90:1", row[0].GetProperty("suspiciousReason").GetString());
        Assert.Equal(1, Json(controller.GetResults(filter: "no-bars")).GetProperty("total").GetInt32());
        Assert.Equal(1, Json(controller.GetStats()).GetProperty("suspicious").GetInt32());
        Assert.IsType<NotFoundResult>(controller.GetItem(movie.Id));
    }

    [Theory]
    [InlineData(nameof(AutoCropController.GetItem), null)]
    [InlineData(nameof(AutoCropController.Rescan), "RequiresElevation")]
    [InlineData(nameof(AutoCropController.Reanalyse), "RequiresElevation")]
    [InlineData(nameof(AutoCropController.GetStats), "RequiresElevation")]
    [InlineData(nameof(AutoCropController.GetResults), "RequiresElevation")]
    [InlineData(nameof(AutoCropController.GetLibraries), "RequiresElevation")]
    [InlineData(nameof(AutoCropController.SetMode), "RequiresElevation")]
    [InlineData(nameof(AutoCropController.ClearMode), "RequiresElevation")]
    public void Endpoints_RequireTheRightAuthorization(string action, string? policy)
    {
        var method = typeof(AutoCropController).GetMethod(action)!;

        var authorize = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(policy, authorize.Policy);
        Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Null(typeof(AutoCropController).GetCustomAttribute<AllowAnonymousAttribute>());
    }

    [Fact]
    public void Script_IsServedAnonymously()
    {
        var method = typeof(AutoCropController).GetMethod(nameof(AutoCropController.GetScript))!;

        Assert.NotNull(method.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.IsType<FileStreamResult>(Controller(null).GetScript());
    }

    [Fact]
    public void Script_IsRevalidatedOnEveryLoad()
    {
        var controller = Controller(null);

        controller.GetScript();

        Assert.Equal("no-cache", controller.Response.Headers.CacheControl.ToString());
    }
}

using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AutoCrop.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
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

    public AutoCropControllerTests()
    {
        TestPlugin.Create(_dir);
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
        return new AutoCropController(_library, _users, _store, _scanner, _queue)
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

    [Theory]
    [InlineData(nameof(AutoCropController.GetItem), null)]
    [InlineData(nameof(AutoCropController.Rescan), "RequiresElevation")]
    [InlineData(nameof(AutoCropController.Reanalyse), "RequiresElevation")]
    [InlineData(nameof(AutoCropController.GetStats), "RequiresElevation")]
    [InlineData(nameof(AutoCropController.GetResults), "RequiresElevation")]
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
}

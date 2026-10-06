using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.AutoCrop.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AutoCrop.Api;

[ApiController]
[Route("AutoCrop")]
[Produces(MediaTypeNames.Application.Json)]
public class AutoCropController : ControllerBase
{
    // Jellyfin's administrator policy (Jellyfin.Api.Constants.Policies.RequiresElevation).
    internal const string AdminPolicy = "RequiresElevation";

    private static readonly object OverridesGate = new();

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly CropStore _store;
    private readonly CropScanner _scanner;
    private readonly ScanQueue _queue;
    private readonly LibraryTitles _titles;

    public AutoCropController(
        ILibraryManager libraryManager, IUserManager userManager, CropStore store, CropScanner scanner, ScanQueue queue, LibraryTitles titles)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _store = store;
        _scanner = scanner;
        _queue = queue;
        _titles = titles;
    }

    /// <summary>
    /// The crop for an item the caller can see, for the web player, with the mode it plays with (its
    /// own, its series', its library's or the server default) and the series (or the movie itself) a
    /// viewer's own mode is remembered for. 404 when there is no result, the file changed since the
    /// scan, no crop is needed, or the item isn't visible to the caller.
    /// </summary>
    [HttpGet("Items/{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetItem([FromRoute] Guid itemId)
    {
        var claim = User.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value;
        var user = Guid.TryParse(claim, out var userId) ? _userManager.GetUserById(userId) : null;
        if (user == null)
            return Forbid();

        var config = Plugin.Instance?.Configuration;
        if (config is { Enabled: false })
            return NotFound();

        // Jellyfin's own visibility check: parental rating, library access, tags.
        var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        var result = item == null ? null : _store.GetCurrent(item.Id, item.Path);
        if (result is not { HasCrop: true })
            return NotFound();

        return Ok(new
        {
            itemId = result.ItemId.ToString("N"),
            frameWidth = result.FrameWidth,
            frameHeight = result.FrameHeight,
            crop = result.Crop,
            segments = result.Segments,
            defaultMode = CropModes.Effective(config?.ModeOverrides ?? new List<ModeOverride>(), config?.DefaultMode, Scopes(item!)),
            seriesId = (item is Episode { SeriesId: var series } && series != Guid.Empty ? series : item!.Id).ToString("N"),
            transitionMs = config?.TransitionMs ?? 300,
        });
    }

    [HttpPost("Items/{itemId}/Rescan")]
    [Authorize(Policy = AdminPolicy)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Rescan([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item == null || !CropScanner.IsEligible(item))
            return NotFound();

        _store.Remove(itemId);
        _queue.Enqueue(itemId, settle: false);
        return Accepted();
    }

    /// <summary>The movie, TV and mixed libraries with their mode (null: the server default), for the settings.</summary>
    [HttpGet("Libraries")]
    [Authorize(Policy = AdminPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetLibraries()
        => Ok(_libraryManager.GetVirtualFolders()
            .Where(f => f.CollectionType is null or CollectionTypeOptions.movies or CollectionTypeOptions.tvshows or CollectionTypeOptions.mixed)
            .Select(f => (Folder: f, Id: Guid.TryParse(f.ItemId, out var id) ? id : Guid.Empty))
            .Where(x => x.Id != Guid.Empty)
            .Select(x => new { id = x.Id.ToString("N"), name = x.Folder.Name, mode = ModeOf(x.Id) }));

    /// <summary>Sets the mode of a library, series, movie or episode.</summary>
    [HttpPost("Modes/{id}")]
    [Authorize(Policy = AdminPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SetMode([FromRoute] Guid id, [FromQuery] string? mode)
    {
        if (!CropModes.IsValid(mode))
            return BadRequest();

        var item = _libraryManager.GetItemById(id);
        if (item is not (CollectionFolder or Series) && !(item != null && CropScanner.IsEligible(item)))
            return NotFound();

        UpdateOverrides(overrides =>
        {
            overrides.RemoveAll(o => o.Id == id);
            overrides.Add(new ModeOverride { Id = id, Mode = mode! });
        });
        return NoContent();
    }

    /// <summary>Clears the mode of a library, series, movie or episode, so it inherits again.</summary>
    [HttpDelete("Modes/{id}")]
    [Authorize(Policy = AdminPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult ClearMode([FromRoute] Guid id)
    {
        UpdateOverrides(overrides => overrides.RemoveAll(o => o.Id == id));
        return NoContent();
    }

    /// <summary>
    /// Recomputes every result from its stored keyframes with the current settings, without ffmpeg.
    /// Items scanned before keyframes were kept, and trickplay results that no longer pass, are queued
    /// for a scan.
    /// </summary>
    [HttpPost("Reanalyse")]
    [Authorize(Policy = AdminPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult Reanalyse()
    {
        var (reanalysed, withoutSamples) = _scanner.Reanalyse(outdatedOnly: false);
        foreach (var itemId in withoutSamples)
            _queue.Enqueue(itemId, settle: false);

        return Ok(new { reanalysed, rescanning = withoutSamples.Count });
    }

    [HttpGet("Stats")]
    [Authorize(Policy = AdminPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetStats()
    {
        var results = _store.All();
        var scanned = results.Where(r => !r.Failed).ToList();
        var known = results.Select(r => r.ItemId).ToHashSet();
        var pending = _titles.Get().Keys.Count(id => !known.Contains(id));

        return Ok(new
        {
            scanned = scanned.Count,
            withBars = scanned.Count(r => r.HasCrop),
            perScene = scanned.Count(r => r.IsPerScene),
            byTrickplay = scanned.Count(r => r.AnalysisSource == AnalysisSources.Trickplay),
            suspicious = scanned.Count(r => r.Suspicious),
            failed = results.Count - scanned.Count,
            pending,
            queued = _queue.PendingCount,
        });
    }

    /// <summary>
    /// Scan results for the dashboard table, newest first, filtered, searched and paged. The "series"
    /// filter lists series instead, by name, with their scanned episodes and mode.
    /// </summary>
    [HttpGet("Results")]
    [Authorize(Policy = AdminPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetResults(
        [FromQuery] string? filter = null,
        [FromQuery] string? search = null,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 25)
    {
        limit = Math.Clamp(limit, 1, 200);
        var titles = _titles.Get();
        if (filter == "series")
            return Ok(SeriesPage(titles, search, startIndex, limit));

        string TitleOf(CropResult r) => titles.TryGetValue(r.ItemId, out var entry) ? entry.Title : Title(r);

        var rows = _store.All()
            .Where(r => MatchesFilter(r, filter))
            .Where(r => string.IsNullOrWhiteSpace(search) || TitleOf(r).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.ScannedAtUtc)
            .ToList();

        return Ok(new
        {
            total = rows.Count,
            items = rows.Skip(Math.Max(0, startIndex)).Take(limit).Select(r => (Result: r, Title: TitleOf(r))).Select(x => new
            {
                itemId = x.Result.ItemId.ToString("N"),
                title = x.Title,
                status = Status(x.Result),
                error = x.Result.Error,
                suspiciousReason = x.Result.Suspicious ? x.Result.SuspiciousReason : null,
                frameWidth = x.Result.FrameWidth,
                frameHeight = x.Result.FrameHeight,
                crop = x.Result.Crop,
                aspect = x.Result.Crop is { Height: > 0 } c ? Math.Round((double)c.Width / c.Height, 2) : (double?)null,
                segments = x.Result.Segments,
                scannedAt = x.Result.ScannedAtUtc,
                source = x.Result.Failed ? null : x.Result.AnalysisSource ?? AnalysisSources.Keyframes,
                mode = ModeOf(x.Result.ItemId),
            }),
        });
    }

    [HttpGet("Web/autocrop.js")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetScript()
    {
        var stream = typeof(AutoCropController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.AutoCrop.Web.autocrop.js");
        if (stream == null)
            return NotFound();

        // Revalidate on every load, so a plugin update reaches browsers that cached the old script.
        Response.Headers.CacheControl = "no-cache";
        return File(stream, "application/javascript");
    }

    internal static string Status(CropResult result)
        => result.Failed ? "failed"
            : result.Suspicious ? "suspicious"
            : result.IsPerScene ? "per-scene"
            : result.HasCrop ? "bars"
            : "no-bars";

    private static bool MatchesFilter(CropResult result, string? filter) => filter switch
    {
        "bars" => result.HasCrop,
        "per-scene" => result.IsPerScene,
        "no-bars" => !result.Failed && !result.HasCrop && !result.Suspicious,
        "suspicious" => result.Suspicious,
        "failed" => result.Failed,
        _ => true,
    };

    private static string? ModeOf(Guid id)
        => Plugin.Instance?.Configuration.ModeOverrides.FirstOrDefault(o => o.Id == id)?.Mode;

    // A new list each time, so a playback request reading the old one never sees it change halfway.
    private static void UpdateOverrides(Action<List<ModeOverride>> change)
    {
        var plugin = Plugin.Instance;
        if (plugin == null)
            return;

        lock (OverridesGate)
        {
            var overrides = plugin.Configuration.ModeOverrides.ToList();
            change(overrides);
            plugin.Configuration.ModeOverrides = overrides;
            plugin.SaveConfiguration();
        }
    }

    private object SeriesPage(IReadOnlyDictionary<Guid, LibraryEntry> titles, string? search, int startIndex, int limit)
    {
        var series = _store.All()
            .Where(r => !r.Failed)
            .Select(r => (Result: r, Entry: titles.GetValueOrDefault(r.ItemId)))
            .Where(x => x.Entry is { SeriesId: not null })
            .GroupBy(x => x.Entry!.SeriesId!.Value)
            .Select(g => (Id: g.Key, Title: g.First().Entry!.SeriesName ?? string.Empty, Episodes: g.Count(), WithBars: g.Count(x => x.Result.HasCrop)))
            .Where(s => string.IsNullOrWhiteSpace(search) || s.Title.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new
        {
            total = series.Count,
            items = series.Skip(Math.Max(0, startIndex)).Take(limit).Select(s => new
            {
                itemId = s.Id.ToString("N"),
                title = s.Title,
                kind = "series",
                episodes = s.Episodes,
                withBars = s.WithBars,
                mode = ModeOf(s.Id),
            }),
        };
    }

    // An item's own id, its series and its libraries, looked up only as far as needed.
    private IEnumerable<Guid> Scopes(BaseItem item)
    {
        yield return item.Id;
        if (item is Episode { SeriesId: var series } && series != Guid.Empty)
            yield return series;

        foreach (var library in _libraryManager.GetCollectionFolders(item))
            yield return library.Id;
    }

    // Only for results whose item isn't in the cached library list, e.g. one that was just removed.
    private string Title(CropResult result)
        => _libraryManager.GetItemById(result.ItemId) is { } item ? LibraryTitles.TitleOf(item) : Path.GetFileNameWithoutExtension(result.Path);
}

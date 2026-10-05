using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly CropStore _store;
    private readonly ScanQueue _queue;

    public AutoCropController(ILibraryManager libraryManager, IUserManager userManager, CropStore store, ScanQueue queue)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _store = store;
        _queue = queue;
    }

    /// <summary>
    /// The crop for an item the caller can see, for the web player. 404 when there is no result, the
    /// file changed since the scan, no crop is needed, or the item isn't visible to the caller.
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
            defaultMode = CropModes.Normalize(config?.DefaultMode),
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

    [HttpGet("Stats")]
    [Authorize(Policy = AdminPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetStats()
    {
        var results = _store.All();
        var scanned = results.Where(r => !r.Failed).ToList();
        var known = results.Select(r => r.ItemId).ToHashSet();
        var pending = DetectBlackBarsTask.LibraryVideos(_libraryManager).Count(i => !known.Contains(i.Id));

        return Ok(new
        {
            scanned = scanned.Count,
            withBars = scanned.Count(r => r.HasCrop),
            perScene = scanned.Count(r => r.IsPerScene),
            failed = results.Count - scanned.Count,
            pending,
            queued = _queue.PendingCount,
        });
    }

    /// <summary>Scan results for the dashboard table, newest first, filtered, searched and paged.</summary>
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
        var rows = _store.All()
            .Where(r => MatchesFilter(r, filter))
            .Select(r => (Result: r, Title: Title(r)))
            .Where(x => string.IsNullOrWhiteSpace(search) || x.Title.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Result.ScannedAtUtc)
            .ToList();

        return Ok(new
        {
            total = rows.Count,
            items = rows.Skip(Math.Max(0, startIndex)).Take(limit).Select(x => new
            {
                itemId = x.Result.ItemId.ToString("N"),
                title = x.Title,
                status = Status(x.Result),
                error = x.Result.Error,
                frameWidth = x.Result.FrameWidth,
                frameHeight = x.Result.FrameHeight,
                crop = x.Result.Crop,
                aspect = x.Result.Crop is { Height: > 0 } c ? Math.Round((double)c.Width / c.Height, 2) : (double?)null,
                segments = x.Result.Segments,
                scannedAt = x.Result.ScannedAtUtc,
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
        return stream == null ? NotFound() : File(stream, "application/javascript");
    }

    internal static string Status(CropResult result)
        => result.Failed ? "failed"
            : result.IsPerScene ? "per-scene"
            : result.HasCrop ? "bars"
            : "no-bars";

    private static bool MatchesFilter(CropResult result, string? filter) => filter switch
    {
        "bars" => result.HasCrop,
        "per-scene" => result.IsPerScene,
        "no-bars" => !result.Failed && !result.HasCrop,
        "failed" => result.Failed,
        _ => true,
    };

    private string Title(CropResult result)
    {
        var item = _libraryManager.GetItemById(result.ItemId);
        return item switch
        {
            Episode episode => string.Format(
                CultureInfo.InvariantCulture,
                "{0} · S{1:00}E{2:00}",
                episode.SeriesName,
                episode.ParentIndexNumber ?? 0,
                episode.IndexNumber ?? 0),
            Movie movie when movie.ProductionYear.HasValue => $"{movie.Name} ({movie.ProductionYear})",
            not null => item.Name,
            null => Path.GetFileNameWithoutExtension(result.Path),
        };
    }
}

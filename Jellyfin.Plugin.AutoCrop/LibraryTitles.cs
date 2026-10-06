using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoCrop;

/// <summary>A video's dashboard title, and for an episode its series.</summary>
public sealed record LibraryEntry(string Title, Guid? SeriesId = null, string? SeriesName = null)
{
    internal static LibraryEntry Of(BaseItem item)
        => item is Episode { SeriesId: var id } episode && id != Guid.Empty
            ? new LibraryEntry(LibraryTitles.TitleOf(item), id, episode.SeriesName)
            : new LibraryEntry(LibraryTitles.TitleOf(item));
}

/// <summary>
/// Dashboard titles for every eligible video, from one library query instead of a lookup per result.
/// Built in the background once Jellyfin has started, so the first dashboard visit is instant, and kept
/// current from the library's item events. A long expiry catches anything the events missed.
/// </summary>
public sealed class LibraryTitles : IHostedService, IDisposable
{
    internal static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<LibraryTitles> _logger;
    private readonly object _gate = new();
    private (DateTime BuiltAt, ImmutableDictionary<Guid, LibraryEntry> Titles)? _cache;
    private CancellationTokenSource? _stopping;

    public LibraryTitles(ILibraryManager libraryManager, IServerApplicationHost applicationHost, ILogger<LibraryTitles> logger)
    {
        _libraryManager = libraryManager;
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>A snapshot: later library changes don't affect a dictionary already handed out.</summary>
    public IReadOnlyDictionary<Guid, LibraryEntry> Get()
    {
        lock (_gate)
        {
            if (_cache is { } cached && DateTime.UtcNow - cached.BuiltAt < MaxAge)
                return cached.Titles;

            // ponytail: built under the lock, so item events wait for the ~1 s query; only at startup and every 30 min.
            var titles = DetectBlackBarsTask.LibraryVideos(_libraryManager).ToImmutableDictionary(i => i.Id, LibraryEntry.Of);
            _cache = (DateTime.UtcNow, titles);
            return titles;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemRemoved;
        _ = Task.Run(() => WarmUpAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _stopping?.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _stopping?.Dispose();

    internal static string TitleOf(BaseItem item)
        => item switch
        {
            Episode episode => string.Format(
                CultureInfo.InvariantCulture,
                "{0} · S{1:00}E{2:00}",
                episode.SeriesName,
                episode.ParentIndexNumber ?? 0,
                episode.IndexNumber ?? 0),
            Movie { ProductionYear: { } year } movie when !movie.Name.EndsWith($"({year})", StringComparison.Ordinal)
                => $"{movie.Name} ({year})",
            _ => item.Name,
        };

    // Hosted services start before Jellyfin's own startup has finished; the library is only queried after.
    private async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_applicationHost.CoreStartupHasCompleted)
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);

            Get();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Server shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoCrop could not load the library titles; the dashboard loads them on first use");
        }
    }

    // Raised for every metadata refresh as well: a rename shows at once, an item that stops being eligible goes.
    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        var item = e.Item;
        lock (_gate)
        {
            if (_cache is not { } cached)
                return;

            var titles = CropScanner.IsEligible(item) ? cached.Titles.SetItem(item.Id, LibraryEntry.Of(item)) : cached.Titles.Remove(item.Id);
            _cache = (cached.BuiltAt, titles);
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        lock (_gate)
        {
            if (_cache is { } cached)
                _cache = (cached.BuiltAt, cached.Titles.Remove(e.Item.Id));
        }
    }
}

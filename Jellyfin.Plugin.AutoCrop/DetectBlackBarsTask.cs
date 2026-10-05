using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoCrop;

public class DetectBlackBarsTask : IScheduledTask
{
    public const string TaskKey = "AutoCropDetectBlackBars";

    private readonly ILibraryManager _libraryManager;
    private readonly CropScanner _scanner;
    private readonly CropStore _store;
    private readonly ILogger<DetectBlackBarsTask> _logger;

    public DetectBlackBarsTask(ILibraryManager libraryManager, CropScanner scanner, CropStore store, ILogger<DetectBlackBarsTask> logger)
    {
        _libraryManager = libraryManager;
        _scanner = scanner;
        _store = store;
        _logger = logger;
    }

    public string Name => "Detect black bars";

    public string Key => TaskKey;

    public string Description => "Measures burned-in black bars in every movie and episode that hasn't been scanned yet or whose file changed.";

    public string Category => "AutoCrop";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = new TimeSpan(2, 0, 0).Ticks,
        },
    };

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!(Plugin.Instance?.Configuration.Enabled ?? true))
            return;

        var items = LibraryVideos(_libraryManager);

        // An empty list is more likely a library glitch than an emptied library; keep the results.
        if (items.Count > 0)
            _store.RemoveAllExcept(items.Select(i => i.Id).ToHashSet());

        var todo = items.Where(_scanner.NeedsScan).ToList();
        _logger.LogInformation("AutoCrop: {Count} of {Total} videos need a scan", todo.Count, items.Count);

        for (var i = 0; i < todo.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _scanner.ScanAsync(todo[i], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "AutoCrop could not scan {Path}", todo[i].Path);
            }

            progress.Report(100.0 * (i + 1) / todo.Count);
        }
    }

    /// <summary>Every eligible movie and episode in the library.</summary>
    internal static IReadOnlyList<BaseItem> LibraryVideos(ILibraryManager libraryManager)
        => libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        }).Where(CropScanner.IsEligible).ToList();
}

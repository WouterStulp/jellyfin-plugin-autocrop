using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoCrop;

/// <summary>
/// Scans new and changed movies and episodes in the background, one at a time. Each item waits a
/// short settle delay first, so a file that is still being copied or imported isn't measured early.
/// </summary>
public sealed class ScanQueue : IHostedService, IDisposable
{
    internal static readonly TimeSpan DefaultSettleDelay = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly CropScanner _scanner;
    private readonly ILogger<ScanQueue> _logger;
    private readonly Channel<(Guid ItemId, DateTime DueUtc)> _channel = Channel.CreateUnbounded<(Guid, DateTime)>();
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();
    private CancellationTokenSource? _stopping;
    private Task? _worker;

    public ScanQueue(ILibraryManager libraryManager, CropScanner scanner, ILogger<ScanQueue> logger)
    {
        _libraryManager = libraryManager;
        _scanner = scanner;
        _logger = logger;
    }

    internal TimeSpan SettleDelay { get; set; } = DefaultSettleDelay;

    /// <summary>Test-only replacement for the per-item scan. Production never assigns it.</summary>
    internal Func<Guid, CancellationToken, Task>? ProcessOverride { get; set; }

    public int PendingCount => _pending.Count;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _worker = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _stopping?.Cancel();
        if (_worker != null)
            await Task.WhenAny(_worker, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Queues an item unless it is already waiting. Returns false for a duplicate.</summary>
    public bool Enqueue(Guid itemId, bool settle = true)
    {
        if (!_pending.TryAdd(itemId, 0))
            return false;

        var due = DateTime.UtcNow + (settle ? SettleDelay : TimeSpan.Zero);
        return _channel.Writer.TryWrite((itemId, due));
    }

    // Raised for every metadata refresh as well; the cheap checks are here, the file check in the worker.
    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if ((Plugin.Instance?.Configuration.Enabled ?? true) && CropScanner.IsEligible(e.Item))
            Enqueue(e.Item.Id);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (itemId, due) in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var wait = due - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

                // Off the pending list before the scan, so a change during the scan queues it again.
                _pending.TryRemove(itemId, out _);
                try
                {
                    await (ProcessOverride ?? ProcessAsync)(itemId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "AutoCrop could not scan item {ItemId}", itemId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Server shutting down.
        }
    }

    private async Task ProcessAsync(Guid itemId, CancellationToken cancellationToken)
    {
        if (!(Plugin.Instance?.Configuration.Enabled ?? true))
            return;

        var item = _libraryManager.GetItemById(itemId);
        if (item != null && _scanner.NeedsScan(item))
            await _scanner.ScanAsync(item, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _stopping?.Dispose();
}

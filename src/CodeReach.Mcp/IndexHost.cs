using System.Diagnostics;
using CodeReach.Core;
using CodeReach.Core.Facts;
using CodeReach.Core.Graph;
using CodeReach.Core.Queries;
using CodeReach.Core.Tools;
using CodeReach.Roslyn;

namespace CodeReach.Mcp;

/// <summary>
/// Owns the index of one solution. A fresh snapshot is served without loading Roslyn; the workspace is
/// opened only when files changed, and released again after an idle period.
/// </summary>
internal sealed class IndexHost : IIndexHost, IDisposable
{
    private readonly HostOptions _options;
    private readonly SnapshotStore _store;
    private readonly Lock _state = new();
    private readonly string _root;
    private RoslynIndexer? _indexer;
    private volatile CodeGraph? _graph;
    private StampSet? _stamps;
    private Task? _refresh;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private Timer? _idle;
    private long _generation;
    private bool _snapshotTried;
    private DateTime _lastSweep = DateTime.MinValue;
    private DateTime _lastWork = DateTime.UtcNow;
    private string _origin = "nothing built yet";
    private string _progress = "idle";
    private string? _pending;
    private string? _failure;

    private readonly TimeSpan _patience;

    /// <param name="patience">How long a query waits for a running refresh before it answers from the previous index or refuses.</param>
    public IndexHost(HostOptions options, TimeSpan? patience = null)
    {
        _patience = patience ?? TimeSpan.FromSeconds(Limits.WaitForFreshSeconds);
        _options = options;
        _store = new SnapshotStore(options);
        _root = Path.GetDirectoryName(options.SolutionPath)!;
    }

    public CodeReachConfig Config => _options.Config;

    /// <summary>True while a Roslyn workspace is held in memory.</summary>
    public bool WorkspaceLoaded => _indexer is not null;

    public async Task<GraphLease> AcquireAsync(CancellationToken cancel)
    {
        Task? refresh;
        lock (_state)
        {
            LoadSnapshotOnce();
            if (_refresh is null or { IsCompleted: true } && (_graph is null || SweepDue()))
            {
                StartRefresh(force: false);
            }

            refresh = _refresh;
        }

        if (refresh is { IsCompleted: false })
        {
            await Task.WhenAny(refresh, Task.Delay(_patience, cancel)).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
        }

        lock (_state)
        {
            var running = _refresh is { IsCompleted: false };
            if (_graph is { } graph)
            {
                var notice = running ? $"STALE: {_pending}; refresh in progress ({_progress}). The answer below is from the previous index."
                    : _failure is not null ? $"STALE: the last refresh failed ({_failure}). The answer below is from the previous index."
                    : null;
                return new GraphLease(graph, notice);
            }

            throw running
                ? new ToolRefusal(Codes.IndexBuilding, $"the first index of this solution is being built ({_progress}). A large solution takes about a minute; call again shortly.")
                : new ToolRefusal(Codes.SolutionLoadFailed, _failure ?? "the index could not be built.");
        }
    }

    public async Task<string> ReindexAsync(bool full, CancellationToken cancel)
    {
        Task refresh;
        CodeGraph? before;
        lock (_state)
        {
            LoadSnapshotOnce();
            before = _graph;
            if (_refresh is null or { IsCompleted: true })
            {
                StartRefresh(force: true, full);
            }

            refresh = _refresh ?? Task.CompletedTask;
        }

        await refresh.WaitAsync(cancel).ConfigureAwait(false);
        var after = _graph ?? throw new ToolRefusal(Codes.SolutionLoadFailed, _failure ?? "the index could not be built.");
        return before is null || ReferenceEquals(before, after)
            ? $"Index is up to date: {after.Facts.Documents.Length} documents, {after.Refs.Count} references. {_origin}"
            : $"Index refreshed: documents {before.Facts.Documents.Length} -> {after.Facts.Documents.Length}, references {before.Refs.Count} -> {after.Refs.Count}, "
                + $"endpoints {before.Endpoints.Count} -> {after.Endpoints.Count}. {_origin}";
    }

    public string DescribeState()
    {
        lock (_state)
        {
            var state = _refresh is { IsCompleted: false } ? $"refreshing ({_progress})" : _graph is null ? "not built" : "fresh";
            return $"state: {state}; {_origin}; workspace {(WorkspaceLoaded ? "loaded" : "not loaded")}; snapshot {_store.FilePath}";
        }
    }

    public Task<string> GitDiffAsync(string? baseRef, CancellationToken cancel) => GitRunner.DiffAsync(_root, baseRef, cancel);

    /// <summary>Starts refreshing as soon as files change, and releases the workspace when nothing happens for a while.</summary>
    public void Watch()
    {
        try
        {
            _debounce = new Timer(_ => Poke(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(_root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName };
            FileSystemEventHandler changed = (_, e) =>
            {
                if (FileStamps.IsRelevant(e.FullPath))
                {
                    _debounce.Change(Limits.WatcherDebounceMilliseconds, Timeout.Infinite);
                }
            };
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (_, e) => changed(_, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"[codereach] file watching is off ({exception.Message}); the index still refreshes when a tool is called.");
        }

        _idle = new Timer(_ => ReleaseIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>Waits for a refresh that is still running, so a one-shot command leaves a complete snapshot behind.</summary>
    public async Task SettleAsync()
    {
        if (_refresh is { } refresh)
        {
            await refresh.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
        _idle?.Dispose();
        _indexer?.Dispose();
    }

    private IEnumerable<string> ProjectDirectories(IndexFacts facts) =>
        facts.Projects.Select(p => Path.GetDirectoryName(Path.GetFullPath(p.Path, facts.Root))).OfType<string>().Distinct();

    private bool SweepDue() => DateTime.UtcNow - _lastSweep > TimeSpan.FromSeconds(Limits.FreshnessSweepSeconds);

    private void LoadSnapshotOnce()
    {
        if (_snapshotTried || _graph is not null)
        {
            return;
        }

        _snapshotTried = true;
        var watch = Stopwatch.StartNew();
        if (_store.Load() is var (facts, stamps) && facts.SolutionPath == _options.SolutionPath)
        {
            _stamps = stamps;
            _graph = new CodeGraph(facts, ++_generation, DateTimeOffset.UtcNow);
            _origin = $"loaded from snapshot in {watch.ElapsedMilliseconds} ms";
        }
    }

    private void Poke()
    {
        lock (_state)
        {
            if (_refresh is null or { IsCompleted: true } && _graph is not null)
            {
                StartRefresh(force: true);
            }
        }
    }

    /// <summary>Must be called under the lock. Decides what a refresh has to do and starts it, or does nothing when the index is fresh.</summary>
    private void StartRefresh(bool force, bool full = false)
    {
        _lastSweep = DateTime.UtcNow;
        var previous = _graph?.Facts;
        FileChanges? changes = null;
        if (previous is not null && _stamps is not null && !full)
        {
            changes = FileStamps.Compare(_stamps, _root, ProjectDirectories(previous));
            if (!changes.Any)
            {
                _stamps = changes.Current;
                _failure = force ? null : _failure;
                return;
            }
        }

        var rebuild = previous is null || changes is null || changes.BuildInputs.Count > 0;
        _pending = changes?.Describe() ?? (previous is null ? "first build" : "full rebuild requested");
        _progress = "starting";
        _refresh = Task.Run(() => RefreshAsync(previous, rebuild ? null : changes));
    }

    private async Task RefreshAsync(IndexFacts? previous, FileChanges? changes)
    {
        var watch = Stopwatch.StartNew();
        var progress = new Progress<IndexProgress>(p => _progress = p.Done > 0 ? $"{p.Stage}, {p.Done} documents" : p.Stage);
        try
        {
            IndexFacts facts;
            StampSet stamps;
            if (changes is null || previous is null)
            {
                _indexer?.Dispose();
                _indexer = null;
                _indexer = await RoslynIndexer.OpenAsync(_options.SolutionPath, _options.Config, progress, CancellationToken.None).ConfigureAwait(false);

                // Stamps are taken before any source text is read: a later edit can only look newer than the index.
                stamps = FileStamps.Take(_root, _indexer.ProjectDirectories);
                facts = await _indexer.IndexAllAsync(progress, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                stamps = changes.Current;
                _indexer ??= await RoslynIndexer.OpenAsync(_options.SolutionPath, _options.Config, progress, CancellationToken.None).ConfigureAwait(false);
                facts = await _indexer.UpdateAsync(previous, new SourceChanges(changes.Changed, changes.Added, changes.Removed), progress, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            var graph = new CodeGraph(facts, Interlocked.Increment(ref _generation), DateTimeOffset.UtcNow);
            lock (_state)
            {
                _graph = graph;
                _stamps = stamps;
                _failure = null;
                _lastWork = DateTime.UtcNow;
                _origin = changes is null
                    ? $"built from source in {watch.Elapsed.TotalSeconds:F1}s ({facts.Documents.Length} documents)"
                    : $"refreshed in {watch.Elapsed.TotalSeconds:F1}s after {changes.Describe()}; {_indexer.LastExtracted} documents re-extracted";
                _progress = "idle";
            }

            _store.Save(facts, stamps);
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"[codereach] refresh failed: {exception}").ConfigureAwait(false);
            lock (_state)
            {
                _failure = $"{_options.SolutionPath} could not be indexed: {exception.Message} Check that `dotnet build` works there, then call reindex.";
                _progress = "idle";
            }
        }
    }

    private void ReleaseIfIdle()
    {
        lock (_state)
        {
            var idle = DateTime.UtcNow - _lastWork > TimeSpan.FromMinutes(Limits.IdleUnloadMinutes);
            if (_indexer is null || !idle || _refresh is { IsCompleted: false })
            {
                return;
            }

            _indexer.Dispose();
            _indexer = null;
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BroidShot.Core;

namespace BroidShot.Modules.Media;

internal enum DownloadState { Queued, Running, Completed, Failed, Cancelled }

/// <summary>One row of the download queue. Property changes are raised through the queue's UI poster.</summary>
internal sealed class DownloadItem : INotifyPropertyChanged
{
    private readonly Action<Action> _post;
    private DownloadState _state = DownloadState.Queued;
    private double? _fraction;
    private string _status = "Sırada";
    private string _speed = "";
    private string _eta = "";
    private string? _filePath;

    internal DownloadItem(DownloadRequest request, Action<Action> post)
    {
        Request = request;
        _post = post;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DownloadRequest Request { get; }
    public string Title => Request.Title;
    public string PresetText => YtDlpArguments.PresetOptions.FirstOrDefault(o => o.Preset == Request.Preset)?.Label ?? Request.Preset.ToString();
    internal CancellationTokenSource Cancellation { get; set; } = new();
    internal ToolJob? Job { get; set; }

    public DownloadState State => _state;
    public double? Fraction => _fraction;
    public double Percent => (_fraction ?? 0) * 100;
    public bool IsIndeterminate => _state == DownloadState.Running && _fraction is null;
    public string StatusText => _status;
    public string SpeedText => _speed;
    public string EtaText => _eta;
    public string? FilePath => _filePath;
    public string DetailText => string.Join("  ·  ", new[] { _status, _speed, _eta }.Where(s => s.Length > 0));

    public bool CanCancel => _state is DownloadState.Queued or DownloadState.Running;
    public bool CanRetry => _state is DownloadState.Failed or DownloadState.Cancelled;
    public bool CanOpen => _state == DownloadState.Completed && _filePath is not null;
    public bool IsFinished => _state is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled;

    internal void SetState(DownloadState state, string status, string? filePath = null)
    {
        _state = state;
        _status = status;
        if (state != DownloadState.Running) { _speed = ""; _eta = ""; }
        if (state == DownloadState.Completed) _fraction = 1;
        if (state == DownloadState.Queued) _fraction = null;
        if (filePath is not null) _filePath = filePath;
        Raise(nameof(State), nameof(StatusText), nameof(SpeedText), nameof(EtaText), nameof(DetailText), nameof(Fraction), nameof(Percent),
            nameof(IsIndeterminate), nameof(CanCancel), nameof(CanRetry), nameof(CanOpen), nameof(IsFinished), nameof(FilePath));
    }

    internal void SetProgress(DownloadProgress progress)
    {
        if (_state != DownloadState.Running) return;
        _fraction = progress.IsPostProcessing ? null : progress.Fraction;
        _status = progress.Describe();
        _speed = progress.IsPostProcessing ? "" : progress.SpeedText;
        _eta = progress.IsPostProcessing ? "" : progress.EtaText;
        Raise(nameof(Fraction), nameof(Percent), nameof(IsIndeterminate), nameof(StatusText), nameof(SpeedText), nameof(EtaText), nameof(DetailText));
    }

    private void Raise(params string[] names) => _post(() =>
    {
        foreach (string name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    });
}

/// <summary>
/// FIFO download queue with a configurable number of parallel downloads. Each running item is an
/// <see cref="IJobTracker"/> job; cancelling the job (shell) or the item (page) kills the process tree.
/// </summary>
internal sealed class DownloadQueue
{
    public delegate Task<string?> DownloadWork(DownloadRequest request, Action<DownloadProgress> progress, CancellationToken cancellationToken);

    private readonly object _gate = new();
    private readonly DownloadWork _work;
    private readonly IJobTracker _jobs;
    private readonly Action<Action> _post;
    private readonly ObservableCollection<DownloadItem> _items = new();
    private readonly List<DownloadItem> _pending = new();
    private int _maxParallel;
    private int _running;
    private bool _shutdown;

    /// <param name="post">Runs an action on the UI thread (synchronously in tests).</param>
    public DownloadQueue(DownloadWork work, IJobTracker jobs, Action<Action> post, int maxParallel)
    {
        _work = work ?? throw new ArgumentNullException(nameof(work));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _maxParallel = MediaSettings.ClampParallel(maxParallel);
        Items = new ReadOnlyObservableCollection<DownloadItem>(_items);
    }

    public ReadOnlyObservableCollection<DownloadItem> Items { get; }

    /// <summary>Raised (on the poster) when an item finished successfully.</summary>
    public event Action<DownloadItem>? ItemCompleted;

    public int MaxParallel
    {
        get { lock (_gate) return _maxParallel; }
        set { lock (_gate) _maxParallel = MediaSettings.ClampParallel(value); Pump(); }
    }

    public int RunningCount { get { lock (_gate) return _running; } }
    public int QueuedCount { get { lock (_gate) return _pending.Count; } }

    public DownloadItem Enqueue(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var item = new DownloadItem(request, _post);
        lock (_gate)
        {
            if (_shutdown) throw new InvalidOperationException("Kuyruk kapatıldı.");
            _pending.Add(item);
        }
        _post(() => _items.Insert(0, item));
        Pump();
        return item;
    }

    public void Cancel(DownloadItem item)
    {
        bool wasQueued;
        lock (_gate)
        {
            wasQueued = _pending.Remove(item);
            if (!wasQueued && item.State != DownloadState.Running) return;
        }
        if (wasQueued) item.SetState(DownloadState.Cancelled, "İptal edildi");
        else
        {
            try { item.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public void Retry(DownloadItem item)
    {
        lock (_gate)
        {
            if (_shutdown || !item.CanRetry) return;
            item.Cancellation.Dispose();
            item.Cancellation = new CancellationTokenSource();
            item.SetState(DownloadState.Queued, "Sırada");
            _pending.Add(item);
        }
        Pump();
    }

    public void Remove(DownloadItem item)
    {
        if (!item.IsFinished) Cancel(item);
        lock (_gate) _pending.Remove(item);
        _post(() => _items.Remove(item));
    }

    public void ClearFinished()
    {
        _post(() =>
        {
            foreach (DownloadItem item in _items.Where(i => i.IsFinished).ToArray()) _items.Remove(item);
        });
    }

    /// <summary>Cancels queued and running downloads; no new ones start afterwards.</summary>
    public void CancelAll()
    {
        DownloadItem[] queued;
        lock (_gate)
        {
            _shutdown = true;
            queued = _pending.ToArray();
            _pending.Clear();
        }
        foreach (DownloadItem item in queued) item.SetState(DownloadState.Cancelled, "İptal edildi");
        foreach (DownloadItem item in Items.ToArray().Where(i => i.State == DownloadState.Running))
        {
            try { item.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void Pump()
    {
        var toStart = new List<DownloadItem>();
        lock (_gate)
        {
            while (!_shutdown && _running < _maxParallel && _pending.Count > 0)
            {
                DownloadItem next = _pending[0];
                _pending.RemoveAt(0);
                _running++;
                next.SetState(DownloadState.Running, "Başlatılıyor…");
                toStart.Add(next);
            }
        }
        foreach (DownloadItem item in toStart) _ = RunItemAsync(item);
    }

    private async Task RunItemAsync(DownloadItem item)
    {
        ToolJob job = _jobs.Start(ShortTitle(item.Title));
        item.Job = job;
        CancellationToken token = item.Cancellation.Token;
        using CancellationTokenRegistration fromJob = job.Token.Register(() => { try { item.Cancellation.Cancel(); } catch (ObjectDisposedException) { } });
        try
        {
            await Task.Yield();
            string? path = await _work(item.Request, p =>
            {
                item.SetProgress(p);
                job.Report(p.IsPostProcessing ? null : p.Fraction, p.Describe() + (p.SpeedText.Length > 0 ? " · " + p.SpeedText : ""));
            }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            item.SetState(DownloadState.Completed, path is null ? "Tamamlandı" : "Tamamlandı · " + Path.GetFileName(path), path);
            job.Complete(path is null ? "Tamamlandı" : Path.GetFileName(path));
            _post(() => ItemCompleted?.Invoke(item));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            item.SetState(DownloadState.Cancelled, "İptal edildi");
            job.Cancelled();
        }
        catch (Exception ex)
        {
            bool expected = ex is MediaException or ExternalToolException or ToolConsentRequiredException;
            string message = ex is ToolConsentRequiredException
                ? "Önce yt-dlp ve ffmpeg kurulumunu onayla (Bilgi al)."
                : expected ? ex.Message : "Beklenmeyen hata: " + ex.Message;
            item.SetState(DownloadState.Failed, message);
            job.Fail(message);
            if (!expected) ErrorLog.Write("media", "İndirme başarısız", ex);
        }
        finally
        {
            item.Job = null;
            lock (_gate) _running--;
            Pump();
        }
    }

    private static string ShortTitle(string title) => "İndiriliyor: " + (title.Length > 60 ? title[..60] + "…" : title);
}

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace BroidShot.Core;

public enum ToolJobState { Running, Completed, Failed, Cancelled }

/// <summary>Background jobs shown in the shell's job list.</summary>
public interface IJobTracker
{
    /// <summary>Running and recently finished jobs (UI thread collection).</summary>
    ReadOnlyObservableCollection<ToolJob> Jobs { get; }

    /// <summary>Adds a running job. Report progress on it and finish it with Complete/Fail/Cancelled.</summary>
    ToolJob Start(string title, bool canCancel = true);

    /// <summary>
    /// Runs <paramref name="work"/> as a job: completion, cancellation (OperationCanceledException after
    /// the job token was cancelled) and failure update the job automatically. Returns the final state;
    /// exceptions are recorded on the job and logged, not rethrown.
    /// </summary>
    Task<ToolJobState> RunAsync(string title, Func<ToolJob, CancellationToken, Task> work, bool canCancel = true);
}

/// <summary>One job row. Thread-safe: Report/Complete/Fail may be called from any thread.</summary>
public sealed class ToolJob : INotifyPropertyChanged
{
    private readonly JobTracker _owner;
    private readonly CancellationTokenSource _cancellation = new();
    private double? _progress;
    private string? _status;
    private ToolJobState _state = ToolJobState.Running;

    internal ToolJob(JobTracker owner, string title, bool canCancel)
    {
        _owner = owner;
        Title = title;
        CanCancel = canCancel;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }
    public bool CanCancel { get; }
    public CancellationToken Token => _cancellation.Token;
    /// <summary>0..1, or null while indeterminate.</summary>
    public double? Progress => _progress;
    public bool IsIndeterminate => _progress is null && _state == ToolJobState.Running;
    /// <summary>0..100 for progress bars (0 while indeterminate).</summary>
    public double Percent => (_progress ?? 0) * 100;
    public string? Status => _status;
    public ToolJobState State => _state;
    public bool IsRunning => _state == ToolJobState.Running;
    public bool IsCancellationRequested => _cancellation.IsCancellationRequested;
    /// <summary>True while a cancel button should be offered.</summary>
    public bool CanCancelNow => CanCancel && IsRunning && !_cancellation.IsCancellationRequested;

    /// <summary>Updates progress (clamped to 0..1; null = indeterminate) and optionally the status line.</summary>
    public void Report(double? progress, string? status = null)
    {
        if (!IsRunning) return;
        _progress = progress is { } value ? Math.Clamp(double.IsNaN(value) ? 0 : value, 0, 1) : null;
        if (status is not null) _status = status;
        Changed(nameof(Progress), nameof(Percent), nameof(IsIndeterminate), nameof(Status));
    }

    /// <summary>Requests cancellation; the work observes <see cref="Token"/> and then calls <see cref="Cancelled"/> (RunAsync does it automatically).</summary>
    public void Cancel()
    {
        if (!CanCancel || !IsRunning) return;
        try { _cancellation.Cancel(); } catch (ObjectDisposedException) { }
        _status = "İptal ediliyor…";
        Changed(nameof(Status), nameof(IsCancellationRequested), nameof(CanCancelNow));
    }

    public void Complete(string? status = null) => Finish(ToolJobState.Completed, status ?? "Tamamlandı", 1);
    public void Fail(string message) => Finish(ToolJobState.Failed, message, _progress);
    public void Cancelled() => Finish(ToolJobState.Cancelled, "İptal edildi", _progress);

    private void Finish(ToolJobState state, string status, double? progress)
    {
        if (!IsRunning) return;
        _state = state;
        _status = status;
        _progress = progress;
        Changed(nameof(State), nameof(IsRunning), nameof(CanCancelNow), nameof(Status), nameof(Progress), nameof(Percent), nameof(IsIndeterminate));
        _owner.OnFinished(this);
    }

    private void Changed(params string[] names) => _owner.Post(() =>
    {
        foreach (string name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    });
}

public sealed class JobTracker : IJobTracker
{
    private readonly ObservableCollection<ToolJob> _jobs = new();
    private readonly Dispatcher? _dispatcher;

    /// <param name="dispatcher">Collection changes are marshalled here; null updates synchronously (tests).</param>
    public JobTracker(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher;
        Jobs = new ReadOnlyObservableCollection<ToolJob>(_jobs);
    }

    public ReadOnlyObservableCollection<ToolJob> Jobs { get; }

    /// <summary>How long a finished job stays visible. Zero removes it immediately.</summary>
    public TimeSpan FinishedJobLinger { get; set; } = TimeSpan.FromSeconds(5);

    public ToolJob Start(string title, bool canCancel = true)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("İş başlığı boş olamaz.", nameof(title));
        var job = new ToolJob(this, title, canCancel);
        Post(() => _jobs.Add(job));
        return job;
    }

    public async Task<ToolJobState> RunAsync(string title, Func<ToolJob, CancellationToken, Task> work, bool canCancel = true)
    {
        ArgumentNullException.ThrowIfNull(work);
        ToolJob job = Start(title, canCancel);
        try
        {
            await work(job, job.Token).ConfigureAwait(true);
            job.Complete(); // no-op when the work already finished the job itself
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested) { job.Cancelled(); }
        catch (Exception ex)
        {
            ErrorLog.Write("jobs", "İş başarısız: " + title, ex);
            job.Fail(ex.Message);
        }
        return job.State;
    }

    internal void OnFinished(ToolJob job)
    {
        if (FinishedJobLinger <= TimeSpan.Zero) { Post(() => _jobs.Remove(job)); return; }
        _ = RemoveLaterAsync(job, FinishedJobLinger);
    }

    private async Task RemoveLaterAsync(ToolJob job, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        Post(() => _jobs.Remove(job));
    }

    internal void Post(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess()) action();
        else if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(action);
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BroidShot.Modules.Media;

/// <summary>A process to start: executable plus an argument list (never a concatenated command line).</summary>
internal sealed record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, string? WorkingDirectory = null);

/// <summary>Process execution behind an interface so tests can replace yt-dlp with a fake.</summary>
internal interface IProcessRunner
{
    /// <summary>
    /// Runs the process to completion, delivering each output line as it arrives (any thread).
    /// When <paramref name="cancellationToken"/> is cancelled the whole process tree is killed and
    /// <see cref="OperationCanceledException"/> is thrown. Returns the exit code.
    /// </summary>
    Task<int> RunAsync(ProcessSpec spec, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken);

    /// <summary>Kills every running process tree started by this runner (application shutdown).</summary>
    void KillAll();
}

internal sealed class SystemProcessRunner : IProcessRunner
{
    private readonly object _gate = new();
    private readonly HashSet<Process> _running = new();

    public async Task<int> RunAsync(ProcessSpec spec, Action<string> onOutput, Action<string> onError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = spec.WorkingDirectory ?? System.IO.Path.GetDirectoryName(spec.FileName) ?? ""
        };
        foreach (string argument in spec.Arguments) info.ArgumentList.Add(argument);
        foreach (KeyValuePair<string, string> pair in spec.Environment) info.Environment[pair.Key] = pair.Value;

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var outputDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) => { if (e.Data is null) outputDone.TrySetResult(); else Safe(onOutput, e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is null) errorDone.TrySetResult(); else Safe(onError, e.Data); };

        try { process.Start(); }
        catch (Win32Exception ex) { throw new MediaException("Araç başlatılamadı: " + System.IO.Path.GetFileName(spec.FileName), ex); }
        lock (_gate) _running.Add(process);
        try
        {
            try { process.StandardInput.Close(); } catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using (cancellationToken.Register(() => Kill(process)))
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAny(Task.WhenAll(outputDone.Task, errorDone.Task), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally
        {
            lock (_gate) _running.Remove(process);
        }
    }

    public void KillAll()
    {
        Process[] processes;
        lock (_gate) processes = new List<Process>(_running).ToArray();
        foreach (Process process in processes) Kill(process);
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException) { }
    }

    private static void Safe(Action<string> handler, string line)
    {
        try { handler(line); }
        catch (Exception ex) { Core.ErrorLog.Write("media", "Süreç çıktısı işlenemedi", ex); }
    }
}

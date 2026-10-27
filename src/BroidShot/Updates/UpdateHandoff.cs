using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BroidShot.Core;

namespace BroidShot.Updates;

internal static class UpdateHandoff
{
    private static string PlanPath => Path.Combine(AppUpdateService.UpdatesRoot, "pending.json");
    private static string HealthPath => Path.Combine(AppUpdateService.UpdatesRoot, "healthy.txt");
    private static string HelperReadyPath => Path.Combine(AppUpdateService.UpdatesRoot, "helper-ready.txt");

    internal static async Task ScheduleAsync(string source, string target, string[] args)
    {
        await Task.Run(() => { PackageManifest.Load(source); PackageManifest.Load(target); });
        Directory.CreateDirectory(AppUpdateService.UpdatesRoot);
        var plan = new UpdatePlan
        {
            SourceDirectory = Path.GetFullPath(source), InstallDirectory = Path.GetFullPath(target), ProcessId = Environment.ProcessId,
            Arguments = args.Where(a => a is "--autostart" or "--background").ToArray()
        };
        File.Delete(HealthPath);
        File.Delete(HelperReadyPath);
        File.WriteAllText(PlanPath, JsonSerializer.Serialize(plan));
        var start = new ProcessStartInfo(Path.Combine(source, "BroidShot.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add(PlanPath);
        Process helperProcess;
        try { helperProcess = Process.Start(start) ?? throw new IOException("Güncelleme yardımcısı başlatılamadı."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            File.WriteAllText(Path.Combine(AppUpdateService.UpdatesRoot, "failed-version.txt"), PackageManifest.Load(source).Version);
            File.Delete(PlanPath);
            throw new ExternalToolException("Güncelleme yardımcısı başlatılamadı; mevcut sürüm korundu.", ex);
        }
        using var helper = helperProcess;
        var watch = Stopwatch.StartNew();
        while (!helper.HasExited && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (File.Exists(HelperReadyPath) && File.ReadAllText(HelperReadyPath) == plan.Token) return;
            await Task.Delay(100);
        }
        if (!helper.HasExited) { helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
        File.WriteAllText(Path.Combine(AppUpdateService.UpdatesRoot, "failed-version.txt"), PackageManifest.Load(source).Version);
        File.Delete(PlanPath);
        throw new ExternalToolException("Yeni sürümün yardımcısı açılamadı. Mevcut sürüm korundu; yeni bir sürüm yayımlanana kadar tekrar kurulmayacak.");
    }

    internal static bool IsHealthLaunch(string[] args)
    {
        int flag = Array.IndexOf(args, "--updated");
        if (flag < 0 || flag + 1 >= args.Length) return false;
        try
        {
            UpdatePlan? plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(PlanPath));
            return plan is not null && plan.Token == args[flag + 1]
                && Path.GetFullPath(plan.InstallDirectory).TrimEnd('\\').Equals(AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    internal static void Acknowledge(string[] args)
    {
        if (IsHealthLaunch(args)) File.WriteAllText(HealthPath, args[Array.IndexOf(args, "--updated") + 1]);
    }

    internal static async Task<int> RunHelperAsync(string path)
    {
        UpdatePlan? plan = null;
        bool validatedTarget = false;
        try
        {
            if (!Path.GetFullPath(path).Equals(Path.GetFullPath(PlanPath), StringComparison.OrdinalIgnoreCase)) return 2;
            plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(path)) ?? throw new InvalidDataException("Güncelleme planı boş.");
            if (!Path.GetFullPath(plan.SourceDirectory).TrimEnd('\\').Equals(AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFullPath(plan.SourceDirectory).StartsWith(Path.GetFullPath(AppUpdateService.UpdatesRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || plan.Arguments.Any(a => a is not ("--autostart" or "--background")) || !Guid.TryParseExact(plan.Token, "N", out _))
                throw new InvalidDataException("Güncelleme planı geçersiz.");
            PackageManifest.Load(plan.InstallDirectory);
            validatedTarget = true;
            File.WriteAllText(HelperReadyPath, plan.Token);
            try
            {
                using var old = Process.GetProcessById(plan.ProcessId);
                if (!string.Equals(old.MainModule?.FileName, Path.Combine(plan.InstallDirectory, "BroidShot.exe"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Güncellenecek süreç eşleşmiyor.");
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try { await old.WaitForExitAsync(exitTimeout.Token); }
                catch (OperationCanceledException) { old.Kill(entireProcessTree: true); await old.WaitForExitAsync(); }
            }
            catch (ArgumentException) { } // The launching application has already exited.

            bool ok = await UpdateInstaller.ApplyAsync(plan, AppUpdateService.UpdatesRoot, async (folder, token) =>
            {
                using Process process = Start(folder, plan, "--updated", plan.Token);
                var clock = Stopwatch.StartNew();
                while (!process.HasExited && clock.Elapsed < TimeSpan.FromSeconds(90))
                {
                    if (File.Exists(HealthPath) && File.ReadAllText(HealthPath) == plan.Token) return true;
                    await Task.Delay(200, token);
                }
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(token); }
                return false;
            }, CancellationToken.None);
            if (!ok)
            {
                File.WriteAllText(Path.Combine(AppUpdateService.UpdatesRoot, "failed-version.txt"), PackageManifest.Load(plan.SourceDirectory).Version);
                Start(plan.InstallDirectory, plan, "--update-recovered").Dispose();
            }
            File.Delete(PlanPath);
            File.Delete(HealthPath);
            File.Delete(HelperReadyPath);
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("updater", "Güncelleme uygulanamadı; önceki sürüm korunuyor.", ex);
            if (validatedTarget && plan is not null && File.Exists(Path.Combine(plan.InstallDirectory, "BroidShot.exe")))
                try { Start(plan.InstallDirectory, plan, "--update-recovered").Dispose(); } catch (Exception restart) { ErrorLog.Write("updater", "Yeniden başlatılamadı.", restart); }
            return 1;
        }
    }

    private static Process Start(string folder, UpdatePlan plan, params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(folder, "BroidShot.exe")) { UseShellExecute = false, WorkingDirectory = folder };
        foreach (string argument in args.Concat(plan.Arguments)) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new IOException("BroidShot başlatılamadı.");
    }
}

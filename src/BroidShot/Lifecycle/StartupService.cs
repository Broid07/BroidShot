using System;
using System.IO;
using Microsoft.Win32;

namespace BroidShot.Lifecycle;

public interface IStartupEntryStore
{
    string? Read();
    void Write(string command);
    void Delete();
}

/// <summary>Manages this user's BroidShot Run entry only, on an explicit settings change.</summary>
public sealed class StartupService
{
    private readonly IStartupEntryStore _store;
    private readonly Func<string> _commandFactory;

    public StartupService(IStartupEntryStore? store = null, Func<string>? commandFactory = null)
    {
        _store = store ?? new RegistryStartupEntryStore();
        _commandFactory = commandFactory ?? CurrentLaunchCommand;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_store.Read());

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            string command = _commandFactory();
            if (string.IsNullOrWhiteSpace(command) || command.Length > 260)
                throw new InvalidOperationException("Uygulamanın klasör yolu Windows başlangıcı için çok uzun. Daha kısa bir klasöre taşıyıp tekrar dene.");
            _store.Write(command);
        }
        else _store.Delete();
        if (IsEnabled != enabled) throw new IOException("Windows başlangıç ayarı doğrulanamadı.");
    }

    public static string BuildLaunchCommand(string executablePath, string? hostedAssemblyPath = null)
    {
        static string QuotePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Contains('"') || path.Contains('\r') || path.Contains('\n'))
                throw new ArgumentException("Başlangıç için geçerli bir tam dosya yolu gerekiyor.", nameof(path));
            return '"' + path + '"';
        }
        string command = QuotePath(executablePath);
        if (hostedAssemblyPath != null) command += " " + QuotePath(hostedAssemblyPath);
        return command + " --autostart";
    }

    private static string CurrentLaunchCommand()
    {
        string processPath = Environment.ProcessPath ?? throw new InvalidOperationException("Uygulamanın çalıştırılabilir dosyası bulunamadı.");
        bool hosted = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        return BuildLaunchCommand(processPath, hosted ? typeof(StartupService).Assembly.Location : null);
    }
}

internal sealed class RegistryStartupEntryStore : IStartupEntryStore
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BroidShot";

    public string? Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
    public void Write(string command)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true)
            ?? throw new IOException("Windows başlangıç ayarına erişilemedi.");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
    public void Delete()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

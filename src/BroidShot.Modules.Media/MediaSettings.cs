using System;
using System.IO;
using System.Runtime.InteropServices;

namespace BroidShot.Modules.Media;

/// <summary>Saved to &lt;DataRoot&gt;/modules/media/settings.json. New properties must have defaults.</summary>
public sealed class MediaSettings
{
    public const string DefaultFileNameTemplate = "%(title)s [%(id)s].%(ext)s";

    /// <summary>Null or empty = Downloads\BroidShot.</summary>
    public string? OutputFolder { get; set; }
    public string FileNameTemplate { get; set; } = DefaultFileNameTemplate;
    public int MaxParallelDownloads { get; set; } = 2;
    public bool ClipboardToasts { get; set; } = true;
    public bool UseBrowserCookies { get; set; }
    public string CookieBrowser { get; set; } = "edge";
    public DownloadPreset LastPreset { get; set; } = DownloadPreset.VideoBest;
    public DateTimeOffset? LastYtDlpUpdateCheck { get; set; }

    public string ResolveOutputFolder() =>
        string.IsNullOrWhiteSpace(OutputFolder) ? DefaultOutputFolder() : OutputFolder!;

    public string? CookieBrowserOrNull() => UseBrowserCookies && YtDlpArguments.IsSupportedBrowser(CookieBrowser) ? CookieBrowser : null;

    public static string DefaultOutputFolder() => Path.Combine(DownloadsFolder(), "BroidShot");

    public static int ClampParallel(int value) => Math.Clamp(value, 1, 6);

    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    private static string DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out IntPtr pointer) == 0)
            {
                try { if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } path) return path; }
                finally { Marshal.FreeCoTaskMem(pointer); }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException) { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}

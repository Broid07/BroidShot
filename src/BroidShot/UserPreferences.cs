using System;
using System.IO;
using System.Text.Json;

namespace BroidShot;

public sealed class UserPreferences
{
    public bool KeepHistory { get; set; } = true;
    public bool RunInBackground { get; set; } = true;
    public bool StartInTrayOnWindowsStartup { get; set; } = true;
    public bool F12Enabled { get; set; } = true;
    public int HotkeyPreset { get; set; }
    /// <summary>Command palette shortcut in HotkeyGesture text form ("Ctrl+Alt+K"); empty disables it.</summary>
    public string PaletteHotkey { get; set; } = DefaultPaletteHotkey;
    public const string DefaultPaletteHotkey = "Ctrl+Alt+K";
    public string ExportFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    private static string FilePath => Path.Combine(DataPaths.Root, "settings.json");
    public static UserPreferences Load()
    {
        try { return JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, FilePath, true);
    }
}

using System;
using System.IO;
using System.Text.RegularExpressions;

namespace BroidShot.Core;

/// <summary>
/// Locations under the BroidShot data root. BROIDSHOT_DATA_DIR overrides the default
/// %LOCALAPPDATA%\BroidShot folder (tests and portable setups use it).
/// </summary>
public static partial class DataPaths
{
    public const string DataDirectoryVariable = "BROIDSHOT_DATA_DIR";

    public static string Root => Path.GetFullPath(Environment.GetEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } custom
        ? custom : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BroidShot"));

    /// <summary>&lt;Root&gt;/modules</summary>
    public static string ModulesRoot => Path.Combine(Root, "modules");

    /// <summary>&lt;Root&gt;/tools — external tools such as yt-dlp and ffmpeg.</summary>
    public static string ToolsRoot => Path.Combine(Root, "tools");

    /// <summary>&lt;Root&gt;/errors.log — shared with the application's crash log.</summary>
    public static string ErrorLog => Path.Combine(Root, "errors.log");

    /// <summary>&lt;Root&gt;/modules/&lt;id&gt; (not created).</summary>
    public static string ModuleFolder(string moduleId) => Path.Combine(ModulesRoot, ValidateId(moduleId, nameof(moduleId)));

    /// <summary>Module and tool ids become folder names, so they are restricted to a safe ASCII set.</summary>
    public static string ValidateId(string id, string parameterName = "id")
    {
        if (id is null || !SafeId().IsMatch(id))
            throw new ArgumentException("Kimlik yalnız küçük harf, rakam, '.', '_' ve '-' içerebilir (en fazla 64 karakter).", parameterName);
        return id;
    }

    public static bool IsValidId(string? id) => id is not null && SafeId().IsMatch(id);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex SafeId();
}

using System;
using System.IO;

namespace BroidShot.Core;

/// <summary>Appends diagnostic lines to &lt;DataRoot&gt;/errors.log. Never throws.</summary>
public static class ErrorLog
{
    private static readonly object Gate = new();

    public static void Write(string source, string message, Exception? exception = null)
    {
        try
        {
            string root = DataPaths.Root;
            Directory.CreateDirectory(root);
            string line = $"{DateTimeOffset.Now:O} [{source}] {message}{(exception is null ? "" : " " + exception)}\n";
            lock (Gate) File.AppendAllText(DataPaths.ErrorLog, line);
        }
        catch { }
    }
}

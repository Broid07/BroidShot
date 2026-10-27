using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BroidShot.Modules.Utilities;

internal enum RenameCase { None, Lower, Upper, Title }

/// <summary>What to do to every file name. Steps run in this order: find/replace, prefix/suffix, pattern, numbering tokens, case, extension.</summary>
internal sealed class RenameOptions
{
    public string Find { get; set; } = "";
    public string Replace { get; set; } = "";
    public bool UseRegex { get; set; }
    public bool IgnoreCase { get; set; } = true;
    public string Prefix { get; set; } = "";
    public string Suffix { get; set; } = "";
    /// <summary>Optional whole-name template; {name} is the name so far. Empty keeps the name.</summary>
    public string Pattern { get; set; } = "";
    public int NumberStart { get; set; } = 1;
    public int NumberStep { get; set; } = 1;
    public RenameCase Case { get; set; }
    /// <summary>"jpg" or ".jpg"; empty keeps the extension.</summary>
    public string NewExtension { get; set; } = "";
}

internal enum RenameStatus { Unchanged, Ready, Conflict, Invalid }

internal sealed class RenameEntry
{
    public RenameEntry(string sourcePath, string newName)
    {
        SourcePath = sourcePath;
        OldName = Path.GetFileName(sourcePath);
        NewName = newName;
        Directory = Path.GetDirectoryName(sourcePath) ?? "";
    }

    public string SourcePath { get; }
    public string Directory { get; }
    public string OldName { get; }
    public string NewName { get; set; }
    public string TargetPath => Path.Combine(Directory, NewName);
    public RenameStatus Status { get; set; }
    public string? Message { get; set; }
}

internal sealed class RenameException : Exception
{
    public RenameException(string message, Exception? inner = null) : base(message, inner) { }
}

internal sealed record BatchApplyResult(int Applied, string? Error);

internal static class RenameEngine
{
    private static readonly Regex NumberToken = new(@"\{n(?::([^{}]*))?\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Computes new names, statuses and conflicts without touching the disk (apart from existence checks).</summary>
    public static List<RenameEntry> Preview(IReadOnlyList<string> files, RenameOptions options)
    {
        Regex? regex = BuildRegex(options);
        var entries = new List<RenameEntry>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            string path = files[i];
            string name = Path.GetFileName(path);
            string newName = BuildName(name, i, options, regex);
            entries.Add(new RenameEntry(path, newName));
        }
        MarkStatuses(entries);
        return entries;
    }

    private static Regex? BuildRegex(RenameOptions o)
    {
        if (!o.UseRegex || o.Find.Length == 0) return null;
        try
        {
            return new Regex(o.Find, (o.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            throw new RenameException("Düzenli ifade geçersiz: " + ex.Message, ex);
        }
    }

    /// <summary>The new file name (with extension) for the file at position <paramref name="index"/>.</summary>
    public static string BuildName(string oldName, int index, RenameOptions o, Regex? regex = null)
    {
        string extension = Path.GetExtension(oldName);
        string baseName = Path.GetFileNameWithoutExtension(oldName);
        if (oldName.StartsWith('.') && extension.Length == oldName.Length) { baseName = oldName; extension = ""; }

        // 1. find / replace
        if (o.Find.Length > 0)
        {
            try
            {
                baseName = o.UseRegex
                    ? (regex ?? BuildRegex(o)!).Replace(baseName, o.Replace)
                    : Replace(baseName, o.Find, o.Replace, o.IgnoreCase);
            }
            catch (RegexMatchTimeoutException) { throw new RenameException("Düzenli ifade çok uzun sürdü; daha basit bir ifade dene."); }
            catch (ArgumentException ex) { throw new RenameException("Değiştirme metni geçersiz: " + ex.Message, ex); }
        }
        // 2. prefix / suffix
        baseName = o.Prefix + baseName + o.Suffix;
        // 3. whole-name pattern
        if (o.Pattern.Length > 0) baseName = Replace(o.Pattern, "{name}", baseName, true);
        // 4. numbering tokens {n} / {n:000}
        long number = (long)o.NumberStart + (long)index * o.NumberStep;
        baseName = NumberToken.Replace(baseName, m => FormatNumber(number, m.Groups[1].Success ? m.Groups[1].Value : null, m.Value));
        // 5. case
        baseName = o.Case switch
        {
            RenameCase.Lower => baseName.ToLower(Turkish),
            RenameCase.Upper => baseName.ToUpper(Turkish),
            RenameCase.Title => TitleCase(baseName),
            _ => baseName
        };
        // 6. extension
        string newExtension = extension;
        if (o.NewExtension.Trim().Length > 0)
        {
            string e = o.NewExtension.Trim().TrimStart('.');
            newExtension = e.Length == 0 ? "" : "." + e;
        }
        return baseName + newExtension;
    }

    private static string FormatNumber(long number, string? format, string original)
    {
        try { return string.IsNullOrEmpty(format) ? number.ToString(CultureInfo.InvariantCulture) : number.ToString(format, CultureInfo.InvariantCulture); }
        catch (FormatException) { return original; }
    }

    private static string Replace(string text, string find, string replacement, bool ignoreCase) =>
        text.Replace(find, replacement, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string TitleCase(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool start = true;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c)) { builder.Append(start ? char.ToUpper(c, Turkish) : char.ToLower(c, Turkish)); start = false; }
            else { builder.Append(c); if (c is not ('\'' or '’')) start = true; }
        }
        return builder.ToString();
    }

    /// <summary>Why a file name cannot be used, or null.</summary>
    public static string? ValidateName(string name)
    {
        if (name.Length == 0 || Path.GetFileNameWithoutExtension(name).Length == 0 && Path.GetExtension(name).Length == 0) return "Ad boş olamaz.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Ad geçersiz karakter içeriyor.";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "Ad nokta veya boşlukla bitemez.";
        string stem = name.Contains('.') ? name[..name.IndexOf('.')] : name;
        if (Reserved.Contains(stem.TrimEnd())) return "Bu ad Windows tarafından ayrılmış.";
        if (name.Length > 255) return "Ad çok uzun.";
        return null;
    }

    private static void MarkStatuses(List<RenameEntry> entries)
    {
        foreach (RenameEntry e in entries)
        {
            string? problem = ValidateName(e.NewName);
            if (problem is not null) { e.Status = RenameStatus.Invalid; e.Message = problem; }
            else if (string.Equals(e.NewName, e.OldName, StringComparison.Ordinal)) { e.Status = RenameStatus.Unchanged; e.Message = null; }
            else { e.Status = RenameStatus.Ready; e.Message = null; }
        }

        // Duplicate targets inside the batch (unchanged files keep their name, so they take part too).
        foreach (var group in entries.Where(e => e.Status != RenameStatus.Invalid)
                     .GroupBy(e => e.TargetPath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            foreach (RenameEntry e in group.Where(e => e.Status == RenameStatus.Ready))
            {
                e.Status = RenameStatus.Conflict;
                e.Message = "Aynı yeni ad başka bir dosyada da var.";
            }
        }

        // Targets that already exist on disk and are not themselves renamed away by this batch.
        var movingAway = new HashSet<string>(entries.Where(e => e.Status == RenameStatus.Ready).Select(e => e.SourcePath), StringComparer.OrdinalIgnoreCase);
        foreach (RenameEntry e in entries.Where(e => e.Status == RenameStatus.Ready))
        {
            bool sameFile = string.Equals(e.SourcePath, e.TargetPath, StringComparison.OrdinalIgnoreCase);
            if (sameFile || movingAway.Contains(e.TargetPath)) continue;
            if (File.Exists(e.TargetPath) || System.IO.Directory.Exists(e.TargetPath))
            {
                e.Status = RenameStatus.Conflict;
                e.Message = "Bu adla bir dosya zaten var.";
            }
        }
    }

    /// <summary>
    /// Renames every Ready entry. Refuses when any entry is a Conflict or Invalid. Chains and swaps (a→b, b→a) and case-only
    /// renames go through temporary names. A failure rolls back what was already done. Success is recorded in the journal.
    /// </summary>
    public static BatchApplyResult Apply(IReadOnlyList<RenameEntry> entries, RenameJournal journal)
    {
        if (entries.Any(e => e.Status is RenameStatus.Conflict or RenameStatus.Invalid))
            return new BatchApplyResult(0, "Çakışan veya geçersiz adlar var; önce bunları gider.");
        var ready = entries.Where(e => e.Status == RenameStatus.Ready).ToList();
        if (ready.Count == 0) return new BatchApplyResult(0, null);

        // The disk may have changed since the preview.
        var movingAway = new HashSet<string>(ready.Select(e => e.SourcePath), StringComparer.OrdinalIgnoreCase);
        foreach (RenameEntry e in ready)
        {
            if (!File.Exists(e.SourcePath)) return new BatchApplyResult(0, $"“{e.OldName}” artık yok. Önizlemeyi yenile.");
            bool sameFile = string.Equals(e.SourcePath, e.TargetPath, StringComparison.OrdinalIgnoreCase);
            if (!sameFile && !movingAway.Contains(e.TargetPath) && (File.Exists(e.TargetPath) || System.IO.Directory.Exists(e.TargetPath)))
                return new BatchApplyResult(0, $"“{e.NewName}” adı artık kullanımda. Önizlemeyi yenile.");
        }

        var done = new List<JournalMove>();
        var temporaries = new List<(RenameEntry Entry, string Temp)>();
        try
        {
            var entangled = new List<RenameEntry>();
            foreach (RenameEntry e in ready)
            {
                bool caseOnly = string.Equals(e.SourcePath, e.TargetPath, StringComparison.OrdinalIgnoreCase);
                if (caseOnly || movingAway.Contains(e.TargetPath)) entangled.Add(e);
                else
                {
                    File.Move(e.SourcePath, e.TargetPath, false);
                    done.Add(new JournalMove { From = e.SourcePath, To = e.TargetPath });
                }
            }
            foreach (RenameEntry e in entangled)
            {
                string temp = Path.Combine(e.Directory, "." + Guid.NewGuid().ToString("N") + ".broidtmp");
                File.Move(e.SourcePath, temp, false);
                temporaries.Add((e, temp));
            }
            foreach ((RenameEntry e, string temp) in temporaries.ToList())
            {
                File.Move(temp, e.TargetPath, false);
                temporaries.Remove((e, temp));
                done.Add(new JournalMove { From = e.SourcePath, To = e.TargetPath });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Put everything back: temporaries first, then completed moves in reverse.
            foreach ((RenameEntry e, string temp) in temporaries)
                try { if (File.Exists(temp) && !File.Exists(e.SourcePath)) File.Move(temp, e.SourcePath); } catch { }
            RenameJournal.Revert(new JournalBatch { Moves = done });
            return new BatchApplyResult(0, "Yeniden adlandırma başarısız oldu, değişiklikler geri alındı: " + ex.Message);
        }

        journal.Append(new JournalBatch
        {
            Kind = BatchKind.Rename,
            Root = ready[0].Directory,
            Moves = done
        });
        return new BatchApplyResult(done.Count, null);
    }
}

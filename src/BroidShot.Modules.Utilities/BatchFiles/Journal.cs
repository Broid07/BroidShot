using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BroidShot.Core;

namespace BroidShot.Modules.Utilities;

internal enum BatchKind { Rename, Organize }

internal sealed class JournalMove
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>One applied batch: every file that was moved or renamed, so it can be put back.</summary>
internal sealed class JournalBatch
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public BatchKind Kind { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
    public string Root { get; set; } = "";
    public List<JournalMove> Moves { get; set; } = new();
    /// <summary>Folders the batch created (organize). They are removed on undo only if they are empty.</summary>
    public List<string> CreatedFolders { get; set; } = new();

    public string Describe() =>
        $"{(Kind == BatchKind.Rename ? "Yeniden adlandırma" : "Klasör düzenleme")}: {Moves.Count} dosya, {Time:HH:mm}";
}

internal sealed class JournalFile
{
    public int Version { get; set; } = 1;
    public List<JournalBatch> Batches { get; set; } = new();
}

internal sealed record UndoResult(int Restored, IReadOnlyList<string> Skipped);

/// <summary>
/// Persistent record of the last batches (rename and organize) so the most recent one can be undone, even after a restart.
/// Undo only moves files back; it never overwrites and never deletes files (it removes folders the batch created if they are empty).
/// </summary>
internal sealed class RenameJournal
{
    public const int MaxBatches = 20;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IncludeFields = false };
    private readonly object _gate = new();
    private readonly string _path;

    public RenameJournal(string path) { _path = path; }

    public string FilePath => _path;

    private JournalFile Read()
    {
        try
        {
            if (!File.Exists(_path)) return new JournalFile();
            return JsonSerializer.Deserialize<JournalFile>(File.ReadAllText(_path), Options) ?? new JournalFile();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            ErrorLog.Write("batchfiles", "Geri alma günlüğü okunamadı.", ex);
            return new JournalFile();
        }
    }

    private void Write(JournalFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, Options));
        File.Move(temp, _path, true);
    }

    public IReadOnlyList<JournalBatch> Batches
    {
        get { lock (_gate) return Read().Batches.ToList(); }
    }

    public JournalBatch? Last
    {
        get { lock (_gate) return Read().Batches.LastOrDefault(); }
    }

    public void Append(JournalBatch batch)
    {
        lock (_gate)
        {
            JournalFile file = Read();
            file.Batches.Add(batch);
            if (file.Batches.Count > MaxBatches) file.Batches.RemoveRange(0, file.Batches.Count - MaxBatches);
            Write(file);
        }
    }

    /// <summary>Undoes the most recent batch. Returns null when there is nothing to undo.</summary>
    public UndoResult? UndoLast()
    {
        lock (_gate)
        {
            JournalFile file = Read();
            JournalBatch? batch = file.Batches.LastOrDefault();
            if (batch is null) return null;
            UndoResult result = Revert(batch);
            file.Batches.RemoveAll(b => b.Id == batch.Id);
            Write(file);
            return result;
        }
    }

    /// <summary>
    /// Moves the files of a batch back. Each file goes through a temporary name first, so swaps and chains can be undone; a file whose
    /// old name is now used by something else (that this batch did not move) is skipped, never overwritten.
    /// </summary>
    internal static UndoResult Revert(JournalBatch batch)
    {
        int restored = 0;
        var skipped = new List<string>();

        var candidates = new List<JournalMove>();
        foreach (JournalMove move in Enumerable.Reverse(batch.Moves))
        {
            if (File.Exists(move.To)) candidates.Add(move);
            else skipped.Add($"{Path.GetFileName(move.To)}: dosya artık yok veya taşınmış.");
        }

        var vacated = new HashSet<string>(candidates.Select(m => m.To), StringComparer.OrdinalIgnoreCase);
        var staged = new List<(JournalMove Move, string Temp)>();
        foreach (JournalMove move in candidates)
        {
            bool caseOnly = string.Equals(move.From, move.To, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && !vacated.Contains(move.From) && (File.Exists(move.From) || Directory.Exists(move.From)))
            {
                skipped.Add($"{Path.GetFileName(move.From)}: eski ad başka bir dosya tarafından kullanılıyor.");
                continue;
            }
            try
            {
                string temp = Path.Combine(Path.GetDirectoryName(move.To)!, "." + Guid.NewGuid().ToString("N") + ".broidtmp");
                File.Move(move.To, temp, false);
                staged.Add((move, temp));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{Path.GetFileName(move.To)}: {ex.Message}");
            }
        }

        foreach ((JournalMove move, string temp) in staged)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(move.From)!);
                File.Move(temp, move.From, false);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{Path.GetFileName(move.From)}: {ex.Message}");
                try { File.Move(temp, move.To, false); } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
            }
        }

        foreach (string folder in Enumerable.Reverse(batch.CreatedFolders))
        {
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new UndoResult(restored, skipped);
    }
}

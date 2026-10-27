using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BroidShot.Modules.Utilities;

internal enum FileCategory { Images, Videos, Documents, Archives, Music, Other }

internal static class FileCategories
{
    public static readonly FileCategory[] All = { FileCategory.Images, FileCategory.Videos, FileCategory.Documents, FileCategory.Archives, FileCategory.Music, FileCategory.Other };

    public static string FolderName(FileCategory category) => category switch
    {
        FileCategory.Images => "Görseller",
        FileCategory.Videos => "Videolar",
        FileCategory.Documents => "Belgeler",
        FileCategory.Archives => "Arşivler",
        FileCategory.Music => "Müzik",
        _ => "Diğer"
    };

    private static readonly Dictionary<string, FileCategory> Map = Build();

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory category, string list)
        {
            foreach (string e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries)) map["." + e] = category;
        }
        Add(FileCategory.Images, "jpg jpeg jpe png gif bmp webp tif tiff heic heif avif svg ico psd raw cr2 nef arw dng jfif");
        Add(FileCategory.Videos, "mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp ts mts m2ts vob");
        Add(FileCategory.Documents, "pdf doc docx xls xlsx ppt pptx txt rtf odt ods odp csv md epub mobi tex pages numbers key");
        Add(FileCategory.Archives, "zip rar 7z tar gz tgz bz2 xz iso cab");
        Add(FileCategory.Music, "mp3 wav flac aac ogg m4a wma opus mid midi aiff");
        return map;
    }

    public static FileCategory Classify(string fileName) =>
        Map.TryGetValue(Path.GetExtension(fileName), out FileCategory category) ? category : FileCategory.Other;
}

internal sealed class OrganizeEntry
{
    public OrganizeEntry(string sourcePath, FileCategory category, string targetPath)
    {
        SourcePath = sourcePath;
        Category = category;
        TargetPath = targetPath;
    }

    public string SourcePath { get; }
    public FileCategory Category { get; }
    public string TargetPath { get; }
    public string FileName => Path.GetFileName(SourcePath);
    public string TargetFolderName => FileCategories.FolderName(Category);
    /// <summary>True when a numeric suffix was added because the name was taken.</summary>
    public bool Renamed => !string.Equals(Path.GetFileName(TargetPath), FileName, StringComparison.Ordinal);
}

internal static class OrganizeEngine
{
    /// <summary>
    /// Plans moving the files directly inside <paramref name="folder"/> into Görseller / Videolar / Belgeler / Arşivler / Müzik / Diğer
    /// sub-folders of that same folder. Hidden and system files and sub-folders are left alone; a name already taken in the target
    /// folder gets " (2)", " (3)" ... so nothing is overwritten.
    /// </summary>
    public static List<OrganizeEntry> Preview(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Klasör bulunamadı.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<OrganizeEntry>();
        foreach (string file in Directory.EnumerateFiles(root).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
        {
            FileAttributes attributes = File.GetAttributes(file);
            if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            if (Path.GetExtension(file).Equals(".broidtmp", StringComparison.OrdinalIgnoreCase)) continue;
            FileCategory category = FileCategories.Classify(file);
            string targetFolder = Path.Combine(root, FileCategories.FolderName(category));
            string target = UniquePath(Path.Combine(targetFolder, Path.GetFileName(file)), taken);
            taken.Add(target);
            entries.Add(new OrganizeEntry(file, category, target));
        }
        return entries;
    }

    private static string UniquePath(string desired, HashSet<string> taken)
    {
        if (!taken.Contains(desired) && !File.Exists(desired) && !Directory.Exists(desired)) return desired;
        string directory = Path.GetDirectoryName(desired)!;
        string stem = Path.GetFileNameWithoutExtension(desired), extension = Path.GetExtension(desired);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!taken.Contains(candidate) && !File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Creates the category folders it needs, moves the files (never overwriting) and records the batch. A failure rolls back.</summary>
    public static BatchApplyResult Apply(IReadOnlyList<OrganizeEntry> entries, string folder, RenameJournal journal)
    {
        if (entries.Count == 0) return new BatchApplyResult(0, null);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var created = new List<string>();
        var done = new List<JournalMove>();
        try
        {
            foreach (OrganizeEntry entry in entries)
            {
                // Moves stay inside the chosen folder.
                string targetFolder = Path.GetDirectoryName(entry.TargetPath)!;
                if (!string.Equals(Path.GetDirectoryName(targetFolder), root, StringComparison.OrdinalIgnoreCase))
                    return Fail("Hedef klasör seçilen klasörün dışında.", done, created);
                if (!File.Exists(entry.SourcePath)) return Fail($"“{entry.FileName}” artık yok. Önizlemeyi yenile.", done, created);
                if (!Directory.Exists(targetFolder))
                {
                    Directory.CreateDirectory(targetFolder);
                    created.Add(targetFolder);
                }
                File.Move(entry.SourcePath, entry.TargetPath, false);
                done.Add(new JournalMove { From = entry.SourcePath, To = entry.TargetPath });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail("Klasör düzenlenemedi: " + ex.Message, done, created);
        }
        journal.Append(new JournalBatch { Kind = BatchKind.Organize, Root = root, Moves = done, CreatedFolders = created });
        return new BatchApplyResult(done.Count, null);
    }

    private static BatchApplyResult Fail(string message, List<JournalMove> done, List<string> created)
    {
        RenameJournal.Revert(new JournalBatch { Moves = done, CreatedFolders = created });
        return new BatchApplyResult(0, message + (done.Count > 0 ? " Yapılan taşımalar geri alındı." : ""));
    }
}

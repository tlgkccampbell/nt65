namespace Norristown.Cli;

/// <summary>
/// The record nt65 keeps, under an output directory, of the files it wrote there. An output
/// whose module has gone from the program is deleted on the next build, and only a file the
/// record names is ever deleted, so nothing nt65 did not write is touched.
/// </summary>
public static class OutputManifest
{
    /// <summary>What the record is called, in the output directory.</summary>
    public const string Name = ".nt65-outputs";

    /// <summary>How the file system tells two paths apart, which is not how nt65 tells two names apart.</summary>
    private static readonly StringComparer Names = FilePaths.Comparer;

    /// <summary>
    /// Deletes what the record in <paramref name="directory"/> names and <paramref name="written"/>
    /// does not, with any directory that leaves empty, and records <paramref name="written"/>.
    /// Paths are relative to <paramref name="root"/>, with <c>/</c> separators.
    /// </summary>
    /// <returns>The files deleted.</returns>
    public static IReadOnlyList<string> Update(string root, string directory, IReadOnlyCollection<string> written)
    {
        var record = Path.Combine(root, directory, Name);
        var before = File.Exists(record)
            ? File.ReadAllLines(record).Where(line => line.Length > 0).ToList()
            : [];
        var now = written.Order(StringComparer.Ordinal).ToList();

        // Whether a recorded path is one of these is a question about files rather than about
        // names, so it is asked the way the file system would answer it: a module renamed
        // `Gfx` to `gfx` writes the same file on Windows and a different one elsewhere, and
        // deleting the one just written would be the worst answer either way.
        var kept = now.ToHashSet(Names);

        var deleted = new List<string>();
        foreach (var path in before.Where(path => !kept.Contains(path)))
        {
            var file = Path.Combine(root, path);
            if (!File.Exists(file))
                continue;
            File.Delete(file);
            deleted.Add(path);
            var top = Path.GetFullPath(Path.Combine(root, directory));
            for (var parent = Path.GetDirectoryName(Path.GetFullPath(file));
                parent is not null && parent.Length > top.Length && !Directory.EnumerateFileSystemEntries(parent).Any();
                parent = Path.GetDirectoryName(parent))
            {
                Directory.Delete(parent);
            }
        }

        // Whether the record needs rewriting depends only on its text, which nt65 itself wrote,
        // so it is compared ordinally, as nt65 compares its own text.
        if (!before.SequenceEqual(now, StringComparer.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(record)!);
            var beside = $"{record}.{Environment.ProcessId}.tmp";
            File.WriteAllText(beside, string.Concat(now.Select(path => path + "\n")));
            File.Move(beside, record, overwrite: true);
        }
        return deleted;
    }
}

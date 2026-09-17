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
        var kept = now.ToHashSet(StringComparer.OrdinalIgnoreCase);

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

        if (!before.SequenceEqual(now, StringComparer.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(record)!);
            File.WriteAllText(record, string.Concat(now.Select(path => path + "\n")));
        }
        return deleted;
    }
}

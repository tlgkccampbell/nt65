using System.Collections.Concurrent;
using Norristown.Processor;
using Norristown.Project;
using Norristown.Semantics;
using Norristown.Standard;

namespace Norristown.Emit;

/// <summary>
/// Represents the addresses ld65 gave the program's zero-page data in the last build, as the
/// debug file that build wrote with <c>--dbgfile</c> records them.
/// <para>
/// nt65 does not run ld65, and so does not know what the build script names the debug file. The
/// newest <c>.dbg</c> under the project's <c>out</c> directory is taken to be the one. ld65 names
/// each value as the <c>.s</c> spells it, and the line map beside each <c>.s</c> gives the source
/// path of every name it defines, so a value is matched to its symbol by that path and the source
/// the module was compiled from. Those are the names the emitter itself wrote, so nothing here
/// works out a linker name again.
/// </para>
/// <para>
/// An editor asks after every edit, and a debug file can be megabytes, so each debug file is read
/// once for each length and time it was written, and the table read from it is kept. Matching the
/// table to the program's symbols is cheap, and is done on every call.
/// </para>
/// </summary>
public sealed class BuiltAddresses
{
    // The number of debug files whose tables are kept. A workspace normally holds a project or
    // two, each with one build, so a few are enough.
    private const int Kept = 4;

    // The table read from each debug file, by its full path, with the length and time it had.
    private static readonly ConcurrentDictionary<string, Read> read = new(FilePaths.Comparer);

    private BuiltAddresses(string debugFilePath, DateTime builtAtUtc, bool isStale, IReadOnlyDictionary<Symbol, long> addresses)
    {
        DebugFilePath = debugFilePath;
        BuiltAtUtc = builtAtUtc;
        IsStale = isStale;
        Addresses = addresses;
    }

    /// <summary>Gets the full path of the debug file the addresses were read from.</summary>
    public string DebugFilePath { get; }

    /// <summary>Gets the time, in UTC, at which the debug file was last written.</summary>
    public DateTime BuiltAtUtc { get; }

    /// <summary>
    /// Gets a value indicating whether a source of the program was saved after the debug file was
    /// written, so that the addresses may no longer be where the program puts its data.
    /// </summary>
    public bool IsStale { get; }

    /// <summary>
    /// Gets the address of each zero-page data declaration that the debug file gives one, keyed by
    /// the symbol as <see cref="ProgramModel.Current"/> returns it. A declaration the debug file
    /// does not give an address, such as one added since the build, is absent.
    /// </summary>
    public IReadOnlyDictionary<Symbol, long> Addresses { get; }

    /// <summary>
    /// Returns the addresses the last build gave the zero-page data of the program that
    /// <paramref name="analysis"/> describes, or null when there is no build to read them from.
    /// That is the case when the project has no <c>out</c>, when nothing under it is a
    /// <c>.dbg</c>, and when the newest <c>.dbg</c> is not an ld65 debug file that can be read.
    /// </summary>
    /// <param name="analysis">The analysis of the program.</param>
    /// <param name="project">
    /// The settings the program is built with, in the configuration being built, whose <c>out</c>
    /// names where the build writes.
    /// </param>
    /// <param name="root">The project root on disk, which is the directory that holds <c>nt65.json</c>.</param>
    public static BuiltAddresses? Of(ProgramAnalysis analysis, ProjectSettings project, string root)
    {
        if (project.Out is not { } output || Newest(Path.GetFullPath(output, root)) is not { } file)
            return null;
        if (TableOf(file, root) is not { } table)
            return null;

        var addresses = new Dictionary<Symbol, long>();
        var segments = analysis.Program.Segments;
        var stale = false;
        foreach (var analyzed in analysis.Files)
        {
            var tree = analyzed.Model.Tree;
            if (StandardModules.IsStandard(tree.Path) || Full(tree.Path, root) is not { } source)
                continue;
            stale |= WrittenAfter(source, file.LastWriteTimeUtc);
            if (!table.TryGetValue(source, out var named))
                continue;
            foreach (var symbol in analyzed.Model.Symbols)
            {
                if (symbol.Tree != tree || symbol.Kind != SymbolKind.Data || !symbol.IsReachableByPath)
                    continue;
                if (symbol.Segment is not { } segment || segments.Find(segment) is not { Size: AddressSize.ZeroPage })
                    continue;
                if (named.TryGetValue(symbol.PathName, out var address))
                    addresses[analysis.Program.Current(symbol)] = address;
            }
        }
        return new BuiltAddresses(file.FullName, file.LastWriteTimeUtc, stale, addresses);
    }

    /// <summary>
    /// Returns the debug file written last under <paramref name="directory"/>, or null when there
    /// is none or the directory cannot be read.
    /// </summary>
    private static FileInfo? Newest(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return null;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            return new DirectoryInfo(directory).EnumerateFiles("*.dbg", options)
                .MaxBy(file => file.LastWriteTimeUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the table read from <paramref name="file"/>, reading the file only when it is not
    /// kept or has changed since it was read, or null when the file cannot be read.
    /// </summary>
    private static IReadOnlyDictionary<string, Dictionary<string, long>>? TableOf(FileInfo file, string root)
    {
        if (read.TryGetValue(file.FullName, out var kept)
            && kept.Length == file.Length && kept.WrittenUtc == file.LastWriteTimeUtc)
        {
            return kept.Table;
        }

        var table = Parse(file.FullName, root);
        if (read.Count >= Kept && !read.ContainsKey(file.FullName))
            read.Clear();

        // A file that could not be read is kept too, so that a foreign `.dbg` is not read again
        // after every edit.
        read[file.FullName] = new Read(file.Length, file.LastWriteTimeUtc, table);
        return table;
    }

    /// <summary>
    /// Reads the debug file at <paramref name="path"/> and returns, for each source, the address
    /// of each label it defines, keyed by the label's source path. Returns null when the file
    /// cannot be read or is not an ld65 debug file.
    /// </summary>
    private static Dictionary<string, Dictionary<string, long>>? Parse(string path, string root)
    {
        if (Text(path) is not { } text)
            return null;

        // ca65 records a `.s` path relative to the directory the build ran in. That is normally
        // the project root, so the map is looked for there first and then beside the debug file.
        var beside = Path.GetDirectoryName(path) ?? root;
        if (!DebugFile.TryValues(text, source => Map(source, root, beside), out var values, out _))
            return null;

        var table = new Dictionary<string, Dictionary<string, long>>(FilePaths.Comparer);
        foreach (var (name, type, value, lines) in values)
        {
            if (type != "lab" || lines is null || !lines.Names.TryGetValue(name, out var named))
                continue;

            // The modules a module places share its `.s`, and so its map. Each name's path starts
            // with its own module, so listing it under every source of the map matches no other.
            foreach (var (source, _) in lines.Sources)
            {
                if (Full(source, root) is not { } full)
                    continue;
                if (!table.TryGetValue(full, out var labels))
                    table[full] = labels = new Dictionary<string, long>(StringComparer.Ordinal);
                labels[named] = value;
            }
        }
        return table;
    }

    /// <summary>
    /// Returns the text of the line map beside <paramref name="source"/>, resolving the source
    /// against each of <paramref name="directories"/> in turn, or null when no directory has one.
    /// </summary>
    private static string? Map(string source, params string[] directories)
    {
        foreach (var directory in directories)
        {
            if (Full(source, directory) is { } full && Text(full + LineMap.Extension) is { } text)
                return text;
        }
        return null;
    }

    /// <summary>
    /// Returns the text of the file at <paramref name="path"/>, or null when it is missing or
    /// cannot be read. ld65 may be writing the file while it is read, so writers are not shut out.
    /// </summary>
    private static string? Text(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns <paramref name="path"/> as a full path, taking it as relative to
    /// <paramref name="directory"/> unless it is rooted, or null when it is not a valid path.
    /// </summary>
    private static string? Full(string path, string directory)
    {
        try
        {
            return Path.GetFullPath(path, directory);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>Returns whether the file at <paramref name="path"/> was written after <paramref name="time"/>.</summary>
    private static bool WrittenAfter(string path, DateTime time)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) > time;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Represents the table read from one debug file, with the length and time it had then.</summary>
    /// <param name="Length">The length of the file when it was read.</param>
    /// <param name="WrittenUtc">The time, in UTC, at which the file had last been written when it was read.</param>
    /// <param name="Table">The table read from the file, or null when it could not be read.</param>
    private sealed record Read(long Length, DateTime WrittenUtc, IReadOnlyDictionary<string, Dictionary<string, long>>? Table);
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Norristown.Tests.Oracle;

/// <summary>
/// Runs ca65 built from the pinned cc65 commit (scripts/cc65.commit), and nothing else: not
/// whatever is on PATH, and not a build that reports another commit. The ld65 and cc65 beside
/// it are the same build.
/// </summary>
internal sealed partial class Ca65Oracle
{
    private static readonly Lazy<Ca65Oracle> pinned = new(() => new Ca65Oracle(
        Repo.Path(".cache", "cc65", "bin", OperatingSystem.IsWindows() ? "ca65.exe" : "ca65"),
        File.ReadAllText(Repo.Path("scripts", "cc65.commit")).Trim(),
        Repo.Path(".cache", "oracle")));

    private readonly string ca65;
    private readonly string ld65;
    private readonly string cc65;
    private readonly string commit;

    // The bytes of the assembler itself. The commit says which source it was built from and
    // not what came out of the build, so a cached result is keyed on the binary that produced
    // it: a rebuilt ca65 that answers differently must not be believed on an old entry.
    private readonly string binary;
    private readonly string? cacheDirectory;

    public Ca65Oracle(string ca65Path, string pinnedCommit, string? cacheDirectory)
    {
        if (!File.Exists(ca65Path))
            throw new InvalidOperationException($"ca65 not found at {ca65Path}; run scripts/build-cc65.ps1");
        var (_, output) = Execute(ca65Path, ["--version"], workingDirectory: null);
        CheckVersion(output, pinnedCommit);
        ca65 = ca65Path;
        ld65 = Path.Combine(Path.GetDirectoryName(ca65Path) ?? "", OperatingSystem.IsWindows() ? "ld65.exe" : "ld65");
        cc65 = Path.Combine(Path.GetDirectoryName(ca65Path) ?? "", OperatingSystem.IsWindows() ? "cc65.exe" : "cc65");
        commit = pinnedCommit;
        binary = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ca65Path)));
        this.cacheDirectory = cacheDirectory;
    }

    /// <summary>The pinned build, checked once per test run.</summary>
    public static Ca65Oracle Pinned => pinned.Value;

    /// <summary>Throws unless the <c>ca65 --version</c> output names <paramref name="pinnedCommit"/>.</summary>
    public static void CheckVersion(string versionOutput, string pinnedCommit)
    {
        var m = GitVersion().Match(versionOutput);
        if (!m.Success || !pinnedCommit.StartsWith(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"refusing to use ca65: it reports \"{versionOutput.Trim()}\", but the pinned cc65 commit is " +
                $"{pinnedCommit}; run scripts/build-cc65.ps1");
        }
    }

    /// <summary>
    /// Byte counts per line of the included file (listing level 2). A ca65 listing row is
    /// <c>AAAAAAr L  BB BB BB BB  source</c>: address and relocation flag, include level,
    /// up to four bytes in a 13-column field, then the source text with trailing blanks
    /// removed. Bytes past the fourth go on continuation rows with no source text; a blank
    /// source line also has no text, but never has bytes.
    /// </summary>
    public static int[] ParseListing(string listing, int sourceLines)
    {
        var counts = new List<int>();
        foreach (var row in listing.ReplaceLineEndings("\n").Split('\n'))
        {
            var m = ListingRow().Match(row);
            if (!m.Success || m.Groups["level"].Value != "2")
                continue;
            var byteCount = m.Groups["bytes"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            var text = m.Groups["text"].Value;
            if (text.Length == 0 && byteCount > 0 && counts.Count > 0)
                counts[^1] += byteCount;
            else
                counts.Add(byteCount);
        }
        // ca65 lists one extra empty row for the end of the included file.
        if (counts.Count < sourceLines)
            throw new InvalidOperationException($"listing has {counts.Count} rows for {sourceLines} source lines");
        return [.. counts.Take(sourceLines)];
    }

    /// <summary>
    /// Assembles <paramref name="source"/> with <c>ca65 -g -l</c>. Clean results are cached
    /// by content. <paramref name="alongside"/> are files the source needs, such as the binary
    /// an <c>.incbin</c> names. Both paths are relative to one working tree and may name
    /// directories, so a relative path in the source finds what it would in a real build.
    /// </summary>
    public AssemblyResult Assemble(
        string fileName, string source, IReadOnlyList<(string Name, byte[] Content)>? alongside = null)
    {
        var seed = new StringBuilder(commit).Append('\0').Append(binary)
            .Append('\0').Append(fileName).Append('\0').Append(source);
        foreach (var (name, content) in alongside ?? [])
            seed.Append('\0').Append(name).Append('\0').Append(Convert.ToHexStringLower(SHA256.HashData(content)));
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString())));
        var cached = cacheDirectory is null ? null : Path.Combine(cacheDirectory, key + ".txt");
        if (cached is not null && File.Exists(cached))
            return new AssemblyResult(true, "", [.. File.ReadAllText(cached).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)]);

        var work = Directory.CreateTempSubdirectory("nt65-ca65-");
        try
        {
            // The listing shows at most 12 bytes per line unless told otherwise, and only a
            // directive can tell it. A wrapper sets that and includes the file unchanged, so
            // ca65's messages keep the file's own name and line numbers.
            WriteText(work.FullName, fileName, source);
            foreach (var (name, content) in alongside ?? [])
                WriteBytes(work.FullName, name, content);
            var directory = Path.GetDirectoryName(Path.Combine(work.FullName, fileName))!;
            File.WriteAllText(Path.Combine(directory, "oracle-wrapper.s"),
                $".listbytes unlimited\n.include \"{Path.GetFileName(fileName)}\"\n");

            // Default warning level. -W2 is unusable: it warns that ca65's own predefined
            // CPU_* symbols are unused, even for an empty file.
            var (exitCode, output) = Execute(ca65,
                ["-g", "-l", "oracle-wrapper.lst", "-o", "oracle-wrapper.o", "oracle-wrapper.s"], directory);
            var succeeded = exitCode == 0 && output.Trim().Length == 0;
            if (!succeeded)
                return new AssemblyResult(false, output.Trim(), []);

            var sourceLines = source.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Length;
            var listing = File.ReadAllText(Path.Combine(directory, "oracle-wrapper.lst"));
            var bytes = ParseListing(listing, sourceLines);

            if (cached is not null)
            {
                Directory.CreateDirectory(cacheDirectory!);
                var temp = cached + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(temp, string.Join(",", bytes));
                File.Move(temp, cached, overwrite: true);
            }
            return new AssemblyResult(true, "", bytes);
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Assembles each file and links them with ld65 against <paramref name="config"/>. This
    /// is the check that nt65 output is an object file like any other: it links against a
    /// module written by hand, and a linker assertion in it is a link error. Paths are
    /// relative to one working tree, as for <see cref="Assemble"/>, and each file's own
    /// directory is searched for what it includes.
    /// </summary>
    public LinkResult Link(
        string config, IReadOnlyList<(string Name, string Source)> files,
        IReadOnlyList<(string Name, byte[] Content)>? alongside = null, bool debugFile = false,
        Func<string, IReadOnlyList<string>>? options = null)
    {
        var work = Directory.CreateTempSubdirectory("nt65-ld65-");
        try
        {
            File.WriteAllText(Path.Combine(work.FullName, "oracle-link.cfg"), config);
            foreach (var (name, content) in alongside ?? [])
                WriteBytes(work.FullName, name, content);
            var objects = new List<string>();
            foreach (var (name, source) in files)
            {
                WriteText(work.FullName, name, source);
                var target = Path.ChangeExtension(name, ".o");
                var include = Path.GetDirectoryName(name) is { Length: > 0 } directory ? directory : ".";
                var (code, assembled) = Execute(ca65,
                    ["-g", .. options?.Invoke(name) ?? [], "-I", include, "-o", target, name], work.FullName);
                var said = Said(assembled);
                if (code != 0 || said.Length > 0)
                    return new LinkResult(false, $"ca65 on {name}:\n{said}", []);
                objects.Add(target);
            }

            string[] dbg = debugFile ? ["--dbgfile", "linked.dbg"] : [];
            var (exitCode, output) = Execute(ld65,
                ["-C", "oracle-link.cfg", "-o", "linked.bin", .. dbg, .. objects.Order(StringComparer.Ordinal)],
                work.FullName);
            if (exitCode != 0 || output.Trim().Length > 0)
                return new LinkResult(false, output.Trim(), []);

            var binary = Path.Combine(work.FullName, "linked.bin");
            var debug = Path.Combine(work.FullName, "linked.dbg");
            return new LinkResult(true, "", File.Exists(binary) ? File.ReadAllBytes(binary) : [])
            {
                DebugFile = File.Exists(debug) ? File.ReadAllText(debug) : "",
            };
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Compiles the C file <paramref name="source"/> with cc65 for no particular target, finding
    /// <paramref name="headers"/> beside it and cc65's own headers where the build put them.
    /// </summary>
    /// <returns>What cc65 said, which is empty when it compiled cleanly.</returns>
    public string CompileC(string source, IReadOnlyList<(string Name, string Text)> headers)
    {
        var work = Directory.CreateTempSubdirectory("nt65-cc65-");
        try
        {
            foreach (var (name, text) in headers)
                WriteText(work.FullName, name, text);
            var include = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(cc65)!)!, "include");
            var (code, said) = Execute(cc65, ["-t", "none", "-I", ".", "-I", include, "-o", "compiled.s", source], work.FullName);
            return code == 0 && said.Trim().Length == 0 ? "" : said.Trim() + (code == 0 ? "" : $"\n(exit code {code})");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    /// <summary>
    /// What ca65 said about the file it was given. A defined symbol nothing in the module
    /// refers to is left out: that is the whole of what <c>-W2</c> adds beyond the default
    /// level, and it is said about ca65's own predefined <c>CPU_65816</c> and the rest for
    /// every file there is, about any symbol a <c>-D</c> defined, and about a label nt65 wrote
    /// for a linker configuration to place, which nothing in the module can refer to by
    /// design. The other half of the level — a symbol imported and never used — stays, because
    /// nt65 imports only what a file uses and one that turned up would be an nt65 bug.
    /// </summary>
    private static string Said(string output) =>
        string.Join('\n', output.ReplaceLineEndings("\n").Split('\n')
            .Where(line => !line.Contains("is defined but never used", StringComparison.Ordinal))
            .Where(line => line.Trim().Length > 0));

    private static void WriteText(string root, string name, string text)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void WriteBytes(string root, string name, byte[] content)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static (int ExitCode, string Output) Execute(string exe, string[] arguments, string? workingDirectory)
    {
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr.Result);
    }

    [GeneratedRegex(@"Git ([0-9a-fA-F]{7,40})")]
    private static partial Regex GitVersion();

    [GeneratedRegex(@"^[0-9A-F]{6}[r ] (?<level>\d+)(?:[+ ] (?<bytes>.{0,13})(?<text>.*))?$")]
    private static partial Regex ListingRow();
}

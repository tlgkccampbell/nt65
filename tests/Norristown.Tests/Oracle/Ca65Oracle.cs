using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Norristown.Tests.Oracle;

/// <param name="Messages">Everything ca65 printed. Success means ca65 exited cleanly and printed nothing.</param>
/// <param name="LineBytes">Bytes ca65 generated for each source line; <c>LineBytes[0]</c> is line 1.</param>
internal sealed record AssemblyResult(bool Succeeded, string Messages, IReadOnlyList<int> LineBytes);

/// <summary>
/// Runs ca65 built from the pinned cc65 commit (scripts/cc65.commit), and nothing else: not
/// whatever is on PATH, and not a build that reports another commit.
/// </summary>
internal sealed partial class Ca65Oracle
{
    private static readonly Lazy<Ca65Oracle> pinned = new(() => new Ca65Oracle(
        Repo.Path(".cache", "cc65", "bin", OperatingSystem.IsWindows() ? "ca65.exe" : "ca65"),
        File.ReadAllText(Repo.Path("scripts", "cc65.commit")).Trim(),
        Repo.Path(".cache", "oracle")));

    /// <summary>The pinned build, checked once per test run.</summary>
    public static Ca65Oracle Pinned => pinned.Value;

    private readonly string ca65;
    private readonly string commit;
    private readonly string? cacheDirectory;

    public Ca65Oracle(string ca65Path, string pinnedCommit, string? cacheDirectory)
    {
        if (!File.Exists(ca65Path))
            throw new InvalidOperationException($"ca65 not found at {ca65Path}; run scripts/build-cc65.ps1");
        var (_, output) = Execute(ca65Path, ["--version"], workingDirectory: null);
        CheckVersion(output, pinnedCommit);
        ca65 = ca65Path;
        commit = pinnedCommit;
        this.cacheDirectory = cacheDirectory;
    }

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

    /// <summary>Assembles <paramref name="source"/> with <c>ca65 -g -l</c>. Clean results are cached by content.</summary>
    public AssemblyResult Assemble(string fileName, string source)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(commit + "\0" + source)));
        var cached = cacheDirectory is null ? null : Path.Combine(cacheDirectory, key + ".txt");
        if (cached is not null && File.Exists(cached))
            return new AssemblyResult(true, "", [.. File.ReadAllText(cached).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)]);

        var work = Directory.CreateTempSubdirectory("nt65-ca65-");
        try
        {
            // The listing shows at most 12 bytes per line unless told otherwise, and only a
            // directive can tell it. A wrapper sets that and includes the file unchanged, so
            // ca65's messages keep the file's own name and line numbers.
            File.WriteAllText(Path.Combine(work.FullName, fileName), source);
            File.WriteAllText(Path.Combine(work.FullName, "oracle-wrapper.s"),
                $".listbytes unlimited\n.include \"{fileName}\"\n");

            // Default warning level. -W2 is unusable: it warns that ca65's own predefined
            // CPU_* symbols are unused, even for an empty file.
            var (exitCode, output) = Execute(ca65,
                ["-g", "-l", "oracle-wrapper.lst", "-o", "oracle-wrapper.o", "oracle-wrapper.s"], work.FullName);
            var succeeded = exitCode == 0 && output.Trim().Length == 0;
            if (!succeeded)
                return new AssemblyResult(false, output.Trim(), []);

            var sourceLines = source.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Length;
            var listing = File.ReadAllText(Path.Combine(work.FullName, "oracle-wrapper.lst"));
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

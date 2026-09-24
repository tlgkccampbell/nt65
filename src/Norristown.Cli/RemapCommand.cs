using Norristown.Emit;

namespace Norristown.Cli;

/// <summary>
/// Implements <c>nt65 remap-dbg</c>, which makes the debug file ld65 wrote name the
/// <c>.nt65</c> sources as well as the <c>.s</c> files that were assembled. It uses the line maps
/// nt65 wrote beside those files.
/// <para>
/// It runs after the link, and needs nothing but the debug file. That file names every <c>.s</c>
/// the program was built from, and each <c>.s</c> nt65 wrote has its line map beside it. A
/// <c>.s</c> with no line map was not written by nt65, and is left alone.
/// </para>
/// </summary>
public static class RemapCommand
{
    /// <summary>
    /// Rewrites the debug file <paramref name="arguments"/> names, in place unless <c>--out</c>
    /// gives somewhere else, and returns the exit code.
    /// </summary>
    public static ExitCode Run(IReadOnlyList<string> arguments, string directory, TextWriter error)
    {
        string? file = null, target = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case "--out" when i + 1 < arguments.Count:
                    target = arguments[++i];
                    break;
                case "--out":
                    error.WriteLine("nt65: `--out` needs a file to write");
                    return ExitCode.UsageError;
                default:
                    if (arguments[i].StartsWith('-') || file is not null)
                    {
                        error.WriteLine(arguments[i].StartsWith('-')
                            ? $"nt65: `{arguments[i]}` is not an option"
                            : "nt65: `remap-dbg` takes one debug file, and was given more than one");
                        return ExitCode.UsageError;
                    }
                    file = arguments[i];
                    break;
            }
        }
        if (file is null)
        {
            error.WriteLine("nt65: `remap-dbg` needs the debug file ld65 wrote with `--dbgfile`");
            return ExitCode.UsageError;
        }

        var path = Path.GetFullPath(file, directory);
        if (!File.Exists(path))
        {
            error.WriteLine($"{file}: error: file not found");
            return ExitCode.InputError;
        }

        // ca65 records a `.s` path relative to the directory the build ran in. That is normally
        // this directory, so the map is looked for here first and then beside the debug file.
        var beside = Path.GetDirectoryName(path) ?? directory;
        if (!DebugFile.TryRemap(File.ReadAllText(path), source => Map(source, directory, beside), out var remapped, out var problem))
        {
            error.WriteLine($"{file}: error: {problem}");
            return ExitCode.InputError;
        }
        File.WriteAllText(target is null ? path : Path.GetFullPath(target, directory), remapped);
        return ExitCode.Success;
    }

    /// <summary>
    /// Returns the line map beside <paramref name="source"/>, resolving the source against each
    /// of <paramref name="directories"/> in turn, or null when no directory has one.
    /// </summary>
    private static string? Map(string source, params string[] directories)
    {
        foreach (var directory in directories)
        {
            var path = Path.GetFullPath(source, directory) + LineMap.Extension;
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        return null;
    }
}

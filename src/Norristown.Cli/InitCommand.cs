using Norristown.Processor;
using Norristown.Project;

namespace Norristown.Cli;

/// <summary>
/// <c>nt65 init</c>: writes the two files a program is, an <c>nt65.json</c> and a
/// <c>src/main.nt65</c>, so that the first thing a newcomer runs is a build that works rather
/// than a project file copied out of a document.
/// <para>
/// It refuses to overwrite either, and writes neither when it would have to, so running it in a
/// directory that already holds a program leaves that program alone.
/// </para>
/// </summary>
public static class InitCommand
{
    private const string Source = """
        ; The program. `nt65 build` writes its ca65 under the project's `out`, for ca65 to
        ; assemble and ld65 to link.
        .module main

        .export main

        .segment CODE
        .proc main {
            rts
        }

        """;

    /// <summary>
    /// Writes a project into the directory <paramref name="arguments"/> names, or into
    /// <paramref name="directory"/>, and returns the exit code: 0 when it wrote them, 1 when one
    /// was there already, 2 when the command is wrong.
    /// </summary>
    public static int Run(IReadOnlyList<string> arguments, string directory, TextWriter output, TextWriter error)
    {
        var cpu = Cpu.Mos6502;
        string? where = null;
        for (var i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case "--cpu":
                    var value = i + 1 < arguments.Count ? arguments[++i] : null;
                    if (value is null || CpuNames.Parse(value) is not { } named)
                    {
                        error.WriteLine($"nt65: --cpu takes {CpuNames.Listed.Replace("`", "", StringComparison.Ordinal)}");
                        error.WriteLine(CommandLine.SeeHelp);
                        return 2;
                    }
                    cpu = named;
                    break;
                default:
                    if (arguments[i].StartsWith('-') || where is not null)
                    {
                        error.WriteLine(arguments[i].StartsWith('-')
                            ? $"nt65: `{arguments[i]}` is not an option"
                            : "nt65: init takes one directory");
                        error.WriteLine(CommandLine.SeeHelp);
                        return 2;
                    }
                    where = arguments[i];
                    break;
            }
        }

        var root = Path.GetFullPath(where ?? ".", directory);
        var project = Path.Combine(root, ProjectFile.Name);
        var main = Path.Combine(root, "src", "main.nt65");

        // Both are checked before either is written, so a directory that holds one of them is
        // left exactly as it was.
        foreach (var existing in (ReadOnlySpan<string>)[project, main])
        {
            if (File.Exists(existing))
            {
                error.WriteLine($"nt65: {ProjectRoot.Shown(directory, existing)} exists already");
                return 1;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(main)!);
        File.WriteAllText(project, Describing(cpu));
        File.WriteAllText(main, Source.ReplaceLineEndings("\n"));
        output.WriteLine(ProjectRoot.Shown(directory, project));
        output.WriteLine(ProjectRoot.Shown(directory, main));
        return 0;
    }

    /// <summary>The project file as <c>init</c> writes it: the three keys a program needs.</summary>
    private static string Describing(Cpu cpu) => $$"""
        {
          "cpu": "{{CpuNames.Spell(cpu)}}",
          "files": ["src/**/*.nt65"],
          "out": "build"
        }

        """.ReplaceLineEndings("\n");
}

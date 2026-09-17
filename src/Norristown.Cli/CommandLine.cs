using Norristown.Project;

namespace Norristown.Cli;

/// <summary>What <c>nt65 build</c> was asked for, read from its arguments.</summary>
/// <param name="Project">The project file <c>--project</c> names, or null to look for one.</param>
/// <param name="Configuration">The named configuration <c>--config</c> chooses, or null for the project's own settings.</param>
/// <param name="Cpu">The processor <c>--cpu</c> names, or null.</param>
/// <param name="Defines">What <c>-D</c> adds or overrides, as written.</param>
/// <param name="Out">The output directory <c>--out</c> names, or null for the project's.</param>
/// <param name="DependencyFile">Where <c>--depfile</c> writes make-style dependencies, or null.</param>
/// <param name="Header">Where <c>--c-header</c> writes a C header, or null.</param>
/// <param name="Files">The source files named, which are the ones to write output for.</param>
public sealed record CommandLine(
    string? Project,
    string? Configuration,
    Cpu? Cpu,
    IReadOnlyList<string> Defines,
    string? Out,
    string? DependencyFile,
    string? Header,
    IReadOnlyList<string> Files)
{
    /// <summary>How the command is used, as <c>--help</c> prints it.</summary>
    public const string Usage = """
        usage: nt65 build [options] [<file.nt65>...]
               nt65 --help | --version

        Builds the program nt65.json describes, found in this directory or the nearest one above it.
        Naming files builds the whole program and writes output only for those files.

        options:
          --project <file>      the project file, or the directory that holds nt65.json
          --config <name>       a named configuration from the project's `configurations`
          --cpu <cpu>           6502, 65sc02, r65c02, 65c02 or 65816
          -D NAME[=value]       adds a define, or overrides one; NAME may be a module's `.config`
          --out <dir>           where output goes, instead of the project's `out`
          --depfile <file>      writes make-style dependencies of every output
          --c-header <file>     writes a C header of what the program exports
        """;

    /// <summary>
    /// Reads the arguments after <c>build</c>, or returns null with <paramref name="problem"/>
    /// saying what is wrong with them.
    /// </summary>
    public static CommandLine? Parse(IReadOnlyList<string> arguments, out string? problem)
    {
        problem = null;
        string? project = null, configuration = null, output = null, dependencies = null, header = null;
        Cpu? cpu = null;
        var defines = new List<string>();
        var files = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var value = i + 1 < arguments.Count ? arguments[i + 1] : null;
            switch (argument)
            {
                case "--project" or "--config" or "--out" or "--depfile" or "--c-header" or "-D" when value is null:
                    problem = argument == "-D" ? "-D takes NAME or NAME=value" : $"{argument} takes a value";
                    return null;
                case "--project":
                    project = value;
                    break;
                case "--config":
                    configuration = value;
                    break;
                case "--out":
                    output = value;
                    break;
                case "--depfile":
                    dependencies = value;
                    break;
                case "--c-header":
                    header = value;
                    break;
                case "-D":
                    defines.Add(value!);
                    break;
                case "--cpu":
                    if (value is null || CpuNames.Parse(value) is not { } named)
                    {
                        problem = $"--cpu takes {CpuNames.Listed.Replace("`", "", StringComparison.Ordinal)}";
                        return null;
                    }
                    cpu = named;
                    break;
                default:
                    if (argument.StartsWith('-'))
                    {
                        problem = $"`{argument}` is not an option";
                        return null;
                    }
                    files.Add(argument);
                    continue;
            }
            i++;
        }
        return new CommandLine(project, configuration, cpu, defines, output, dependencies, header, files);
    }
}

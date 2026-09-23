using Norristown.Processor;

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
/// <param name="Check">Whether <c>--check</c> asked for the report without the output.</param>
/// <param name="Watch">Whether <c>--watch</c> asked to build again whenever the program changes.</param>
/// <param name="Json">Whether <c>--json</c> asked for the diagnostics as JSON on standard output.</param>
/// <param name="Stdout">Whether <c>--stdout</c> asked for one file's output on standard output.</param>
public sealed record CommandLine(
    string? Project,
    string? Configuration,
    Cpu? Cpu,
    IReadOnlyList<string> Defines,
    string? Out,
    string? DependencyFile,
    string? Header,
    IReadOnlyList<string> Files,
    bool Check = false,
    bool Watch = false,
    bool Json = false,
    bool Stdout = false)
{
    /// <summary>How the command is used, as <c>--help</c> prints it.</summary>
    public const string Usage = """
        usage: nt65 build [options] [<file.nt65>...]
               nt65 init [<dir>] [--cpu <cpu>]
               nt65 fmt [--check] [<file.nt65>...]
               nt65 remap-dbg <file.dbg> [--out <file>]
               nt65 explain [<diagnostic> | --markdown]
               nt65 lsp
               nt65 import-inc <file.inc> [-o <file.nt65>] [--module <name>]
               nt65 --help | --version

        Builds the program nt65.json describes, found in this directory or the nearest one above it.
        Naming files builds the whole program and writes output only for those files.

        `init` writes an nt65.json and a src/main.nt65 that builds, in this directory or the one
        named, and refuses to overwrite either.

        `fmt` writes files in the one layout nt65 sources are written in, or with `--check`
        lists the ones that are not in it already and exits 1. Named nothing, it formats every
        file the project names.

        `remap-dbg` runs after the link: it makes the debug file ld65 wrote name the `.nt65`
        sources as well as the `.s` files, from the `.s.lines` map beside each one, in place
        unless `--out` gives somewhere else.

        `explain` says what a diagnostic is about, which its one line has no room for; the name
        is the one in brackets after the message. Named nothing, it lists them; given
        `--markdown`, it writes them all as one Markdown page.

        `lsp` serves the language server on standard input and output, for an editor that speaks
        LSP; it takes nothing else, and what it says about itself goes to standard error.

        `import-inc` writes an nt65 module of constants from a ca65 include file of them, once,
        for a person to read and keep. It writes to standard output unless `-o` names a file.
        A line it cannot convert is written out as a comment saying so and counted on standard
        error. nt65 reads no ca65 at build time, and this does not change that.

        options:
          --project <file>      the project file, or the directory that holds nt65.json
          --config <name>       a named configuration from the project's `configurations`
          --cpu <cpu>           6502, 6502x, 65sc02, r65c02, 65c02 or 65816
          -D NAME[=value]       adds a define, or overrides one; NAME may be a module's `.config`
          --out <dir>           where output goes, instead of the project's `out`
          --depfile <file>      writes make-style dependencies of every output
          --c-header <file>     writes a C header of what the program exports
          --check               reports what is wrong and writes nothing
          --stdout              writes the named file's ca65 to standard output and no files
          --watch               builds again whenever the program changes, until interrupted
          --json                writes one JSON object per diagnostic to standard output
        """;

    /// <summary>
    /// The line printed after an error about the command line. Printing the whole usage text
    /// after a one-line error would bury the error, so this points at <c>--help</c> instead.
    /// </summary>
    public const string SeeHelp = "see `nt65 --help`";

    /// <summary>
    /// Reads the arguments after <c>build</c>, or returns null with <paramref name="problem"/>
    /// saying what is wrong with them.
    /// </summary>
    public static CommandLine? Parse(IReadOnlyList<string> arguments, out string? problem)
    {
        problem = null;
        string? project = null, configuration = null, output = null, dependencies = null, header = null;
        Cpu? cpu = null;
        bool check = false, watch = false, json = false, stdout = false;
        var defines = new List<string>();
        var files = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            var value = i + 1 < arguments.Count ? arguments[i + 1] : null;
            switch (argument)
            {
                case "--project" or "--config" or "--out" or "--depfile" or "--c-header" or "-D" when value is null:
                    problem = argument == "-D" ? "`-D` needs NAME or NAME=value after it" : $"`{argument}` needs a value after it";
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

                // A flag takes no value, so `continue` skips the `i++` below that steps over a value.
                case "--check":
                    check = true;
                    continue;
                case "--watch":
                    watch = true;
                    continue;
                case "--json":
                    json = true;
                    continue;
                case "--stdout":
                    stdout = true;
                    continue;
                case "--cpu":
                    if (value is null || CpuNames.Parse(value) is not { } named)
                    {
                        problem = value is null
                            ? $"`--cpu` needs a processor: {CpuNames.Listed.Replace("`", "", StringComparison.Ordinal)}"
                            : $"`{value}` is not a processor nt65 knows; `--cpu` takes {CpuNames.Listed.Replace("`", "", StringComparison.Ordinal)}";
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
        return new CommandLine(project, configuration, cpu, defines, output, dependencies, header, files,
            check, watch, json, stdout);
    }
}

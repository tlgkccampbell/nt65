using System.Reflection;

namespace Norristown.Cli;

/// <summary>What nt65 can be asked to do, and which of them an argument list asks for.</summary>
public static class Commands
{
    /// <summary>
    /// Runs nt65 with <paramref name="arguments"/> from <paramref name="directory"/>, and returns
    /// the exit code: 0 when it did what it was asked, 1 when its input (the program or a file) is
    /// wrong, 2 when the command line is.
    /// </summary>
    public static int Run(
        string[] arguments, string directory, TextWriter output, TextWriter error,
        bool colour = false, CancellationToken cancellation = default)
    {
        switch (arguments)
        {
            case ["--help" or "-h"] or ["build", "--help" or "-h"] or ["init", "--help" or "-h"]
                or ["fmt", "--help" or "-h"] or ["remap-dbg", "--help" or "-h"]
                or ["explain", "--help" or "-h"] or ["lsp", "--help" or "-h"]
                or ["import-inc", "--help" or "-h"]:
                output.WriteLine(CommandLine.Usage);
                return 0;
            case ["--version"]:
                output.WriteLine($"nt65 {Version()}");
                return 0;
            case ["build", .. var rest]:
                if (CommandLine.Parse(rest, out var problem) is not { } command)
                    return Wrong(error, problem!);
                return command.Watch
                    ? WatchCommand.Run(command, Path.GetFullPath(directory), output, error, colour, cancellation)
                    : BuildCommand.Build(command, Path.GetFullPath(directory), output, error, colour);
            case ["init", .. var chosen]:
                return InitCommand.Run(chosen, Path.GetFullPath(directory), output, error);
            case ["fmt", .. var asked]:
                return FormatCommand.Run(asked, Path.GetFullPath(directory), output, error);
            case ["remap-dbg", .. var given]:
                return RemapCommand.Run(given, Path.GetFullPath(directory), error);
            case ["explain", .. var about]:
                return ExplainCommand.Run(about, output, error);
            case ["lsp"]:
                return LspCommand.Run(error);
            case ["lsp", var unexpected, ..]:
                return Wrong(error, $"`lsp` takes no arguments, but was given `{unexpected}`");
            case ["import-inc", .. var converted]:
                return ImportIncCommand.Run(converted, Path.GetFullPath(directory), output, error);

            // An unrecognised first argument: print a one-line error and a pointer to --help,
            // rather than the whole usage text, which nobody asked for and which buries the error.
            case [var word, ..]:
                return Wrong(error, word.StartsWith('-')
                    ? $"`{word}` is not an option"
                    : $"`{word}` is not a command");
        }

        // Nothing was asked for, so the usage text is the answer.
        error.WriteLine(CommandLine.Usage);
        return 2;
    }

    /// <summary>Reports what is wrong with the command line and how to see the usage text, and returns 2.</summary>
    private static int Wrong(TextWriter error, string problem)
    {
        error.WriteLine($"nt65: {problem}");
        error.WriteLine(CommandLine.SeeHelp);
        return 2;
    }

    /// <summary>The version nt65 was built as.</summary>
    private static string Version() =>
        typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";
}

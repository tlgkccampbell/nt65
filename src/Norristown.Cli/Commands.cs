using System.Reflection;
using Norristown.LanguageServer;

namespace Norristown.Cli;

/// <summary>
/// Lists what nt65 can be asked to do, and chooses which of those commands an argument list asks
/// for.
/// </summary>
public static class Commands
{
    /// <summary>
    /// Runs nt65 with <paramref name="arguments"/> from <paramref name="directory"/>, and returns
    /// the exit code.
    /// </summary>
    public static ExitCode Run(
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
                return ExitCode.Success;
            case ["--version"]:
                output.WriteLine($"nt65 {Version()}");
                return ExitCode.Success;
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
                // The language server's own entry point is served here too, so that any editor
                // that speaks LSP can start it by running nt65 itself. Every other command is
                // synchronous, so this one waits for the server to exit. The server's own exit
                // codes are 0, 1 and 70, so they pass through unchanged.
                return (ExitCode)Server.ServeStandardStreamsAsync(error).GetAwaiter().GetResult();
            case ["lsp", var unexpected, ..]:
                return Wrong(error, $"`lsp` takes no arguments, but was given `{unexpected}`");
            case ["import-inc", .. var converted]:
                return ImportIncCommand.Run(converted, Path.GetFullPath(directory), output, error);

            // An unrecognised first argument prints a one-line error and a pointer to --help,
            // rather than the whole usage text, which nobody asked for and which buries the error.
            case [var word, ..]:
                return Wrong(error, word.StartsWith('-')
                    ? $"`{word}` is not an option"
                    : $"`{word}` is not a command");
        }

        // Nothing was asked for, so the usage text is the answer.
        error.WriteLine(CommandLine.Usage);
        return ExitCode.UsageError;
    }

    /// <summary>Reports what is wrong with the command line and how to see the usage text, and returns <see cref="ExitCode.UsageError"/>.</summary>
    private static ExitCode Wrong(TextWriter error, string problem)
    {
        error.WriteLine($"nt65: {problem}");
        error.WriteLine(CommandLine.SeeHelp);
        return ExitCode.UsageError;
    }

    /// <summary>Returns the version nt65 was built as.</summary>
    private static string Version() =>
        typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";
}

using System.Reflection;

namespace Norristown.Cli;

/// <summary>What nt65 can be asked to do, and which of them an argument list asks for.</summary>
public static class Commands
{
    /// <summary>
    /// Runs nt65 with <paramref name="arguments"/> from <paramref name="directory"/>, and returns
    /// the exit code: 0 when it did what it was asked, 1 when what it was given is wrong, 2 when
    /// the command is.
    /// </summary>
    public static int Run(string[] arguments, string directory, TextWriter output, TextWriter error)
    {
        switch (arguments)
        {
            case ["--help" or "-h"] or ["build", "--help" or "-h"] or ["remap-dbg", "--help" or "-h"]:
                output.WriteLine(CommandLine.Usage);
                return 0;
            case ["--version"]:
                output.WriteLine($"nt65 {Version()}");
                return 0;
            case ["build", .. var rest]:
                if (CommandLine.Parse(rest, out var problem) is { } command)
                    return BuildCommand.Build(command, Path.GetFullPath(directory), error);
                error.WriteLine($"nt65: {problem}");
                break;
            case ["remap-dbg", .. var given]:
                return RemapCommand.Run(given, Path.GetFullPath(directory), error);
        }
        error.WriteLine(CommandLine.Usage);
        return 2;
    }

    /// <summary>The version nt65 was built as.</summary>
    private static string Version() =>
        typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";
}

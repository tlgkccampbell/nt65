using Norristown.Cli;

namespace Norristown.Tests.Cli;

/// <summary>
/// Runs the command line in process, with its output captured, the way each command's tests
/// drive it.
/// </summary>
internal static class Nt65
{
    /// <summary>
    /// Runs nt65 in <paramref name="directory"/> and returns its exit code with everything it
    /// printed, standard output first and standard error after it.
    /// </summary>
    public static (ExitCode Code, string Printed) Run(string directory, params string[] arguments)
    {
        var (code, output, error) = Apart(directory, false, arguments);
        return (code, output + error);
    }

    /// <summary>
    /// Runs nt65 in <paramref name="directory"/> with standard output and standard error kept
    /// apart, so that a test can check which stream a message goes to, and with color on or off
    /// as <paramref name="color"/> says.
    /// </summary>
    public static (ExitCode Code, string Output, string Error) Apart(
        string directory, bool color, params string[] arguments)
    {
        var output = new StringWriter { NewLine = "\n" };
        var error = new StringWriter { NewLine = "\n" };
        var code = Commands.Run(arguments, directory, output, error, color, cancellation: TestTimeout.Token());
        return (code, output.ToString(), error.ToString());
    }
}

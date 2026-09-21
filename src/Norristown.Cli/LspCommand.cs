using Norristown.LanguageServer;

namespace Norristown.Cli;

/// <summary>
/// Serves the language server over stdio, so that an editor which speaks LSP starts it from
/// the same tool as the command rather than from inside the VS Code extension.
/// </summary>
internal static class LspCommand
{
    /// <summary>
    /// Answers LSP on standard input and output until the client says to stop, and returns the
    /// exit code the protocol asks for.
    /// </summary>
    public static int Run(TextWriter error)
    {
        // Standard output carries the protocol, so anything human-readable goes to standard
        // error, which an editor shows in the server's output channel. NT65_SERVER_LOG names a
        // file that receives the same lines, for looking at a server an editor started.
        var logPath = Environment.GetEnvironmentVariable("NT65_SERVER_LOG");
        using var log = new ServerLog(error, string.IsNullOrEmpty(logPath) ? null : logPath);

        log.Write($"Norristown language server starting (pid {Environment.ProcessId})");
        var exit = Server
            .RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), log)
            .GetAwaiter().GetResult();
        log.Write($"Norristown language server exiting ({exit})");
        return exit;
    }
}

using Norristown.LanguageServer;

// LSP over stdio. Stdout carries the protocol, so anything human-readable goes to stderr,
// which VS Code shows in the server's output channel. NT65_SERVER_LOG names a file that
// receives the same lines, for looking at a server started by an editor.
var logPath = Environment.GetEnvironmentVariable("NT65_SERVER_LOG");
using var log = new ServerLog(Console.Error, string.IsNullOrEmpty(logPath) ? null : logPath);

log.Write($"Norristown language server starting (pid {Environment.ProcessId})");
await Server.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), log);
log.Write("Norristown language server exiting");

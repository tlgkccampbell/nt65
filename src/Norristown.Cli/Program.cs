using System.Text;
using Norristown.Cli;

// Paths and messages may hold any character, and the OEM code page Windows gives a console by
// default cannot show most of them, so nt65 writes UTF-8. The encoding has no byte order mark,
// which would otherwise start what a script reads from nt65. A process with no console at all
// cannot change its code page, and keeps the default.
try
{
    Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
catch (IOException)
{
}

// Colour highlights a diagnostic's severity, and is used only when a person is likely reading
// standard error: a redirected stream is another program's input, and a non-empty NO_COLOR
// means the user has asked for no colour.
var colour = !Console.IsErrorRedirected
    && Environment.GetEnvironmentVariable("NO_COLOR") is null or "";

// Ctrl+C cancels the token rather than killing the process, so that `--watch` stops cleanly
// instead of in the middle of writing a file.
using var interrupted = new CancellationTokenSource();
Console.CancelKeyPress += (_, stopping) =>
{
    stopping.Cancel = true;
    interrupted.Cancel();
};

// This is the top-level exception handler. An exception that reaches here is a bug in nt65
// rather than a problem with the program being built, and the report says so. It names what
// threw and which program nt65 was building, and prints the stack trace to include in a bug
// report.
try
{
    return (int)Commands.Run(args, Environment.CurrentDirectory, Console.Out, Console.Error, colour, interrupted.Token);
}
catch (Exception e)
{
    Console.Error.WriteLine($"nt65: internal error: {e.GetType().Name}: {e.Message}");
    if (Building.Program is { } program)
        Console.Error.WriteLine($"nt65: while building {program}");
    Console.Error.WriteLine("nt65: this is a bug in nt65; please report it with the program and the stack below");
    Console.Error.WriteLine(e);
    return (int)ExitCode.InternalError;
}

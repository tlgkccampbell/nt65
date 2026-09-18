using Norristown.Cli;

// Colour marks what a diagnostic is, and only where somebody is reading it: a redirected
// stream is something else's input, and NO_COLOR is the person saying they do not want it.
var colour = !Console.IsErrorRedirected
    && Environment.GetEnvironmentVariable("NO_COLOR") is null or "";

// Ctrl+C ends `--watch` the way its own last line says it does, rather than killing the
// process in the middle of writing a file.
using var interrupted = new CancellationTokenSource();
Console.CancelKeyPress += (_, stopping) =>
{
    stopping.Cancel = true;
    interrupted.Cancel();
};

return Commands.Run(args, Environment.CurrentDirectory, Console.Out, Console.Error, colour, interrupted.Token);

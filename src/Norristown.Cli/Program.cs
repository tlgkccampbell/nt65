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

// The handler of last resort. Anything that reaches here is a bug in nt65 rather than
// something wrong with the program being built, and says so: what threw, which program nt65
// was reading, and the stack to report it with. 70 is what a tool exits with when it failed at
// its own end, which is what this is.
try
{
    return Commands.Run(args, Environment.CurrentDirectory, Console.Out, Console.Error, colour, interrupted.Token);
}
catch (Exception e)
{
    Console.Error.WriteLine($"nt65: internal error: {e.GetType().Name}: {e.Message}");
    if (Building.Program is { } program)
        Console.Error.WriteLine($"nt65: while building {program}");
    Console.Error.WriteLine("nt65: this is a bug in nt65; please report it with the program and the stack below");
    Console.Error.WriteLine(e);
    return 70;
}

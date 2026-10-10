namespace Norristown.LanguageServer;

/// <summary>
/// Records what a server does, one line at a time. Every line goes to <paramref name="writer"/>,
/// which is standard error when a process serves, and a copy with a timestamp goes to the file at
/// <paramref name="filePath"/> when one is given, for looking at a server an editor started.
/// </summary>
/// <param name="writer">The writer that receives every line.</param>
/// <param name="filePath">The file that receives a timestamped copy of every line, or null for none.</param>
internal sealed class ServerLog(TextWriter writer, string? filePath = null) : IDisposable
{
    private readonly Lock gate = new();
    private readonly StreamWriter? file = filePath is null
        ? null
        : new StreamWriter(filePath, append: true) { AutoFlush = true, NewLine = "\n" };

    /// <summary>Writes one line and flushes it, so that it is seen before the server does anything else.</summary>
    public void Write(string message)
    {
        lock (gate)
        {
            writer.WriteLine(message);
            writer.Flush();
            file?.WriteLine($"{DateTime.UtcNow:O} {message}");
        }
    }

    /// <summary>Closes the log file, if there is one.</summary>
    public void Dispose() => file?.Dispose();
}

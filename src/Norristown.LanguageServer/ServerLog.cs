namespace Norristown.LanguageServer;

internal sealed class ServerLog(TextWriter writer, string? filePath = null) : IDisposable
{
    private readonly Lock gate = new();
    private readonly StreamWriter? file = filePath is null
        ? null
        : new StreamWriter(filePath, append: true) { AutoFlush = true, NewLine = "\n" };

    public void Write(string message)
    {
        lock (gate)
        {
            writer.WriteLine(message);
            writer.Flush();
            file?.WriteLine($"{DateTime.UtcNow:O} {message}");
        }
    }

    public void Dispose() => file?.Dispose();
}

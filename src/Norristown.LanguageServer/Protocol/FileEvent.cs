namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a file that changed on disk.</summary>
/// <param name="Uri">The file's URI.</param>
/// <param name="Type">Whether the file was created, changed or deleted.</param>
internal sealed record FileEvent(string Uri, FileChangeType Type);

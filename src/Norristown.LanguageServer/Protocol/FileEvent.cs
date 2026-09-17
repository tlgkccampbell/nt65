namespace Norristown.LanguageServer.Protocol;

/// <summary>One file that changed on disk.</summary>
/// <param name="Uri">The file.</param>
/// <param name="Type">Whether it was created, changed or deleted.</param>
internal sealed record FileEvent(string Uri, FileChangeType Type);

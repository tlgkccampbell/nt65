namespace Norristown.LanguageServer.Protocol;

/// <summary>How the server completes.</summary>
/// <param name="TriggerCharacters">What, typed, asks for completion without the programmer asking.</param>
internal sealed record CompletionOptions(IReadOnlyList<string> TriggerCharacters);

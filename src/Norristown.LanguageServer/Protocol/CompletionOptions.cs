namespace Norristown.LanguageServer.Protocol;

/// <summary>How the server completes.</summary>
/// <param name="TriggerCharacters">What, typed, asks for completion without the programmer asking.</param>
/// <param name="ResolveProvider">
/// Whether the comment above a declaration is fetched for the one item the caret is on rather
/// than sent with every item. A file's names carry a paragraph each, and a list of hundreds
/// would be mostly prose nobody is reading.
/// </param>
internal sealed record CompletionOptions(IReadOnlyList<string> TriggerCharacters, bool ResolveProvider = false);

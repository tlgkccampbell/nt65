namespace Norristown.LanguageServer.Protocol;

/// <summary>Describes how the server provides completion.</summary>
/// <param name="TriggerCharacters">
/// The characters that, when typed, request completion without the programmer asking.
/// </param>
/// <param name="ResolveProvider">
/// Whether the comment above a declaration is fetched only for the item the caret is on, rather
/// than sent with every item. Each name in a file carries a paragraph of comment, and a list of
/// hundreds would be mostly prose nobody reads.
/// </param>
internal sealed record CompletionOptions(IReadOnlyList<string> TriggerCharacters, bool ResolveProvider = false);

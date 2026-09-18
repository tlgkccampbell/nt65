namespace Norristown.LanguageServer;

/// <summary>
/// One thing that could be written at the caret, before it becomes a protocol item. They are
/// gathered by the name the client filters on, so a name reached two ways is offered once.
/// </summary>
/// <param name="Kind">What it is, which picks its icon.</param>
/// <param name="Detail">A short description beside it, or null.</param>
/// <param name="Text">What choosing it writes.</param>
/// <param name="Documentation">The comment above the declaration it names, or null.</param>
internal readonly record struct Suggestion(
    Protocol.CompletionItemKind Kind, string? Detail, string Text, string? Documentation = null);

namespace Norristown.LanguageServer.Protocol;

/// <summary>One thing that could be written at the caret.</summary>
/// <param name="Label">What the client lists, which is also what it filters on.</param>
/// <param name="Kind">What it is, which picks its icon.</param>
/// <param name="Detail">A short description beside it, or null.</param>
/// <param name="TextEdit">What choosing it writes, over the part of a name already typed.</param>
/// <param name="Command">
/// What to run once it is written, or null. An item that writes more than its own name leaves
/// the caret where something else may go, and asks the client for that list straight away.
/// </param>
internal sealed record CompletionItem(
    string Label, CompletionItemKind Kind, string? Detail, TextEdit TextEdit, Command? Command = null);

namespace Norristown.LanguageServer.Protocol;

/// <summary>One thing that could be written at the caret.</summary>
/// <param name="Label">What the client lists, which is also what it filters on.</param>
/// <param name="Kind">What it is, which picks its icon.</param>
/// <param name="Detail">A short description beside it, or null.</param>
/// <param name="TextEdit">What choosing it writes, over the part of a name already typed.</param>
/// <param name="Command">
/// What to run once it is written, or null. An item that inserts more than its own name leaves
/// the caret where something else may be written, and asks the client to open the completion
/// list there straight away.
/// </param>
/// <param name="Documentation">The comment above the declaration it names, or null.</param>
/// <param name="SortText">
/// What the client sorts on instead of the label: how near the item's declaration is to the
/// caret, since the one meant is nearly always the nearest.
/// </param>
/// <param name="InsertTextFormat">
/// Whether the inserted text is a snippet with tab stops, as for block openers; null for plain
/// text, which every client understands.
/// </param>
internal sealed record CompletionItem(
    string Label, CompletionItemKind Kind, string? Detail, TextEdit TextEdit, Command? Command = null,
    MarkupContent? Documentation = null, string? SortText = null, InsertTextFormat? InsertTextFormat = null);

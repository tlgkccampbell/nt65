namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents an item the client can insert at the caret.</summary>
/// <param name="Label">The text the client lists, which is also the text it filters on.</param>
/// <param name="Kind">The kind of item, which determines its icon.</param>
/// <param name="Detail">A short description shown beside the item, or null.</param>
/// <param name="TextEdit">
/// The edit applied when the item is chosen, which replaces the part of a name already typed.
/// </param>
/// <param name="Command">
/// The command to run after the item is inserted, or null. An item that inserts more than its own
/// name leaves the caret where more may be typed, and asks the client to open the completion list
/// there immediately.
/// </param>
/// <param name="Documentation">The comment above the declaration the item names, or null.</param>
/// <param name="SortText">
/// The text the client sorts on instead of the label. It encodes how near the item's declaration
/// is to the caret, because the intended item is nearly always the nearest.
/// </param>
/// <param name="InsertTextFormat">
/// Whether the inserted text is a snippet with tab stops, as for block openers, or null for plain
/// text, which every client understands.
/// </param>
internal sealed record CompletionItem(
    string Label, CompletionItemKind Kind, string? Detail, TextEdit TextEdit, Command? Command = null,
    MarkupContent? Documentation = null, string? SortText = null, InsertTextFormat? InsertTextFormat = null);

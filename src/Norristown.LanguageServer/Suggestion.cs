namespace Norristown.LanguageServer;

/// <summary>
/// Represents an item that could be inserted at the caret, before it becomes a protocol item.
/// Suggestions are gathered by the name the client filters on, so a name reached two ways is
/// offered once.
/// </summary>
/// <param name="Source">Where the item comes from, which decides how completion reshapes it.</param>
/// <param name="Kind">The kind of item, which determines its icon.</param>
/// <param name="Detail">A short description shown beside the item, or null.</param>
/// <param name="Text">The text that choosing the item inserts.</param>
/// <param name="Documentation">The comment above the declaration the item names, or null.</param>
/// <param name="Band">
/// How near the named item is to the caret, for sorting. Names in scope come first, in the order
/// the binder reaches them, then modules, then the language's own words, then instructions. The
/// intended item is nearly always the nearest.
/// </param>
/// <param name="Order">
/// The item's position within its band. Items within one band that share an order sort
/// alphabetically.
/// </param>
/// <param name="IsSnippet">
/// Whether the inserted text is a snippet with tab stops, which only a block opener's is.
/// </param>
internal readonly record struct Suggestion(
    SuggestionSource Source,
    Protocol.CompletionItemKind Kind,
    string? Detail,
    string Text,
    string? Documentation = null,
    int Band = Suggestion.Keyword,
    int Order = 0,
    bool IsSnippet = false)
{
    /// <summary>A name the caret can reach as it is written, nearest first.</summary>
    public const int InScope = 1;

    /// <summary>A module, whose members are reached by writing a path into it.</summary>
    public const int Module = 2;

    /// <summary>
    /// A word of the language itself, such as a directive, a signature item, or a number's prefix
    /// character.
    /// </summary>
    public const int Keyword = 3;

    /// <summary>
    /// An instruction. Every CPU has far more instructions than could be meant at any one caret, so
    /// they sort last.
    /// </summary>
    public const int Instruction = 4;

    /// <summary>
    /// Returns the text the client sorts on, which orders by the band, then the position within
    /// it, then the label.
    /// </summary>
    public string SortText(string label) =>
        $"{Band}{Order.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)}{label}";
}

namespace Norristown.LanguageServer;

/// <summary>
/// One thing that could be written at the caret, before it becomes a protocol item. They are
/// gathered by the name the client filters on, so a name reached two ways is offered once.
/// </summary>
/// <param name="Kind">What it is, which picks its icon.</param>
/// <param name="Detail">A short description beside it, or null.</param>
/// <param name="Text">What choosing it writes.</param>
/// <param name="Documentation">The comment above the declaration it names, or null.</param>
/// <param name="Band">
/// How near the thing it names is to the caret, for sorting: names in scope first, in the order
/// the binder reaches them, then modules, then the language's own words, then instructions.
/// The one meant is nearly always the nearest.
/// </param>
/// <param name="Order">Where it stands within its band; everything within one band that shares an order is alphabetical.</param>
/// <param name="IsSnippet">Whether what it writes is a snippet with tab stops, which only a block opener is.</param>
internal readonly record struct Suggestion(
    Protocol.CompletionItemKind Kind,
    string? Detail,
    string Text,
    string? Documentation = null,
    int Band = Suggestion.Spelled,
    int Order = 0,
    bool IsSnippet = false)
{
    /// <summary>A name the caret can reach as it is written, nearest first.</summary>
    public const int InScope = 1;

    /// <summary>A module, whose members are reached by writing a path into it.</summary>
    public const int Module = 2;

    /// <summary>A word of the language itself: a directive, a signature item, or a number's prefix character.</summary>
    public const int Spelled = 3;

    /// <summary>An instruction; every CPU has far more of these than could be meant at any one caret, so they sort last.</summary>
    public const int Instruction = 4;

    /// <summary>What the client sorts on: the band, then the place within it, then the label.</summary>
    public string SortText(string label) =>
        $"{Band}{Order.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)}{label}";
}

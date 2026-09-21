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
/// How near what it names is to the caret: the names in scope first, in the order the binder
/// reaches them, then the modules, then the words the language spells, then the instructions.
/// The thing meant is nearly always the nearest.
/// </param>
/// <param name="Order">Where it stands within its band; everything within one band that shares an order is alphabetical.</param>
/// <param name="IsSnippet">Whether what it writes has stops in it, which only a block opener does.</param>
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

    /// <summary>A module, which a path has to be walked into.</summary>
    public const int Module = 2;

    /// <summary>A word the language spells: a directive, a signature item, the mark a number starts with.</summary>
    public const int Spelled = 3;

    /// <summary>An instruction, of which every CPU has more than anyone means at one caret.</summary>
    public const int Instruction = 4;

    /// <summary>What the client sorts on: the band, then the place within it, then the label.</summary>
    public string SortText(string label) =>
        $"{Band}{Order.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)}{label}";
}

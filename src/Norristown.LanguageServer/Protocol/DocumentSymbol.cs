namespace Norristown.LanguageServer.Protocol;

/// <summary>One entry in the outline, with the entries inside it.</summary>
/// <param name="Name">The name as the source writes it.</param>
/// <param name="Detail">A short rendering of the rest of the declaration, or null.</param>
/// <param name="Kind">What it declares.</param>
/// <param name="Range">Everything the declaration covers, a block's body included.</param>
/// <param name="SelectionRange">Just the name, which is what the client reveals. Inside <see cref="Range"/>.</param>
/// <param name="Children">Declarations inside it, or null.</param>
internal sealed record DocumentSymbol(
    string Name,
    string? Detail,
    SymbolKind Kind,
    Range Range,
    Range SelectionRange,
    IReadOnlyList<DocumentSymbol>? Children);

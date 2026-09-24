namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents an entry in the document outline, with the entries nested inside it.</summary>
/// <param name="Name">The name as it appears in the source.</param>
/// <param name="Detail">A short rendering of the rest of the declaration, or null.</param>
/// <param name="Kind">The kind of declaration.</param>
/// <param name="Range">The whole range of the declaration, including a block's body.</param>
/// <param name="SelectionRange">
/// The range of the name alone, which the client reveals. It lies inside <see cref="Range"/>.
/// </param>
/// <param name="Children">The declarations nested inside this one, or null.</param>
internal sealed record DocumentSymbol(
    string Name,
    string? Detail,
    SymbolKind Kind,
    Range Range,
    Range SelectionRange,
    IReadOnlyList<DocumentSymbol>? Children);

namespace Norristown.LanguageServer.Protocol;

/// <summary>Represents a routine in a call hierarchy.</summary>
/// <param name="Name">The name as it appears in the source.</param>
/// <param name="Kind">The kind of symbol, which determines its icon.</param>
/// <param name="Uri">The file that declares the routine.</param>
/// <param name="Range">The whole range of the declaration, including its body.</param>
/// <param name="SelectionRange">The range of the name alone, which the client reveals and selects.</param>
/// <param name="Detail">The module and the enclosing scopes, or null.</param>
internal sealed record CallHierarchyItem(
    string Name, SymbolKind Kind, string Uri, Range Range, Range SelectionRange, string? Detail = null);

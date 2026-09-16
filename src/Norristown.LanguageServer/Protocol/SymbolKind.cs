namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// What a document symbol is, as LSP numbers it. Only the kinds nt65 uses are listed; the
/// client picks the icon from the number.
/// </summary>
internal enum SymbolKind
{
    /// <summary>A segment block.</summary>
    Module = 2,

    /// <summary>A scope.</summary>
    Namespace = 3,

    /// <summary>A label.</summary>
    Field = 8,

    /// <summary>A routine.</summary>
    Function = 12,

    /// <summary>A constant or address alias.</summary>
    Constant = 14,
}

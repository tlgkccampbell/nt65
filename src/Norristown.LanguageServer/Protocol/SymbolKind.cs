namespace Norristown.LanguageServer.Protocol;

/// <summary>
/// Specifies the kind of a document symbol, as LSP numbers it. Only the kinds nt65 uses are
/// listed, and the client picks the icon from the number.
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

    /// <summary>A data declaration.</summary>
    Variable = 13,

    /// <summary>A constant or address alias.</summary>
    Constant = 14,

    /// <summary>An enum, a struct or a union.</summary>
    Struct = 23,
}

namespace Norristown.Semantics;

/// <summary>
/// What a written name means: the symbol it refers to, or the module it names, or nothing at
/// all. A module is not a declaration — it is only the way to one — so a name that stops at a
/// module gives the module's path rather than a symbol.
/// </summary>
/// <param name="Symbol">The symbol the name refers to, or null.</param>
/// <param name="Module">The module, or the start of one's name, when the name is one.</param>
public readonly record struct SymbolInfo(Symbol? Symbol, string? Module = null)
{
    /// <summary>A name that means nothing here.</summary>
    public static SymbolInfo None => default;

    /// <summary>Whether the name means nothing at all.</summary>
    public bool IsNone => Symbol is null && Module is null;
}

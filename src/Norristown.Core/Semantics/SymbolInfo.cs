namespace Norristown.Semantics;

/// <summary>
/// Represents what a name in the source means, which is the symbol it refers to, the module it
/// names, or nothing at all. A module is not a declaration, only the way to one, so a name that
/// stops at a module gives the module's path rather than a symbol.
/// </summary>
/// <param name="Symbol">The symbol the name refers to, or null.</param>
/// <param name="Module">
/// The module, or the start of a module's name, when the name refers to one.
/// </param>
public readonly record struct SymbolInfo(Symbol? Symbol, string? Module = null)
{
    /// <summary>Gets the information for a name that means nothing at this point.</summary>
    public static SymbolInfo None => default;

    /// <summary>Gets a value indicating whether the name means nothing at all.</summary>
    public bool IsNone => Symbol is null && Module is null;
}

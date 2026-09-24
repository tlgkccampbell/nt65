namespace Norristown.Semantics;

/// <summary>
/// Represents what a part of a name resolved to, which is a symbol, a module or the start of a
/// module's name.
/// </summary>
/// <param name="Symbol">The symbol, or null for a module path.</param>
/// <param name="Module">The module path, when the name resolved to one.</param>
/// <param name="IsAlias">Whether the name used is the alias a <c>.use ... as</c> gave the symbol.</param>
/// <param name="From">The module whose <c>.use module::*</c> brought the symbol in, when one did.</param>
internal readonly record struct Place(Symbol? Symbol, string? Module = null, bool IsAlias = false, string? From = null)
{
    /// <summary>Gets the place for a name that means nothing and has already been reported.</summary>
    public static Place Reported => default;

    /// <summary>Gets a value indicating whether this is <see cref="Reported"/>.</summary>
    public bool IsReported => Symbol is null && Module is null;

    /// <summary>
    /// Gets this place as the <see cref="SymbolInfo"/> given to a consumer, which carries only the
    /// symbol and the module.
    /// </summary>
    public SymbolInfo Means => new(Symbol, Module);
}

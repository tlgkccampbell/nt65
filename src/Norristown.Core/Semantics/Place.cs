namespace Norristown.Semantics;

/// <summary>What a part of a name resolved to: a symbol, or a module or the start of one's name.</summary>
/// <param name="Symbol">The symbol, or null for a module path.</param>
/// <param name="Module">The module path, when it is one.</param>
/// <param name="IsAlias">Whether the name was written as the name a <c>.use ... as</c> gave the symbol.</param>
/// <param name="From">The module whose <c>.use module::*</c> brought the symbol in, when one did.</param>
internal readonly record struct Place(Symbol? Symbol, string? Module = null, bool IsAlias = false, string? From = null)
{
    /// <summary>A name that means nothing, which has been reported as such.</summary>
    public static Place Reported => default;

    /// <summary>Whether this is <see cref="Reported"/>.</summary>
    public bool IsReported => Symbol is null && Module is null;

    /// <summary>The same as an answer to a consumer, which is told the symbol and the module and no more.</summary>
    public SymbolInfo Means => new(Symbol, Module);
}

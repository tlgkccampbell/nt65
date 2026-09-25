using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents one name that a file's <c>.use</c> brings in. It records what the name refers to
/// and where the item that brought it in appears, which an editor needs in order to report a
/// name that nothing uses.
/// </summary>
/// <param name="Symbol">The symbol the name refers to, or null when it names a module.</param>
/// <param name="Module">The module path, when the name refers to a module.</param>
/// <param name="At">The span of the name in the item, which is where the item is reported.</param>
/// <param name="IsExported">
/// Whether it is an <c>.export .use</c>, which re-exports the name rather than using it, so
/// it is not reported as unused when this module never refers to it.
/// </param>
public readonly record struct BroughtName(Symbol? Symbol, string? Module, TextSpan At, bool IsExported)
{
    /// <summary>
    /// Gets what the name resolves to, which is what a lookup of it in the file returns. A name
    /// whose item leads nowhere has neither a symbol nor a module, and resolves to
    /// <see cref="Resolution.Reported"/>.
    /// </summary>
    internal Resolution Resolved => new(Symbol, Module);
}

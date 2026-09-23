using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// One name a file's <c>.use</c> brings in: what it refers to, and where the item that brought
/// it in is written, which an editor needs in order to report a name that nothing uses.
/// </summary>
/// <param name="Symbol">The symbol it names, or null when it names a module.</param>
/// <param name="Module">The module path, when it is one.</param>
/// <param name="At">The name the item writes, which is where the item is reported.</param>
/// <param name="IsExported">
/// Whether it is an <c>.export .use</c>, which re-exports the name rather than using it, so
/// it is not reported as unused when this module never refers to it.
/// </param>
public readonly record struct BroughtName(Symbol? Symbol, string? Module, TextSpan At, bool IsExported);

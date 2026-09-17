using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// One name a file's <c>.use</c> brings in: what it stands for, and where the item that brought
/// it is written, which is what an editor needs to say that nothing names it.
/// </summary>
/// <param name="Symbol">The symbol it names, or null when it names a module.</param>
/// <param name="Module">The module path, when it is one.</param>
/// <param name="At">The name the item writes, which is where the item is reported.</param>
/// <param name="IsExported">
/// Whether it is an <c>.export .use</c>, which re-exports the name rather than using it: what
/// this module names it for is another module's business.
/// </param>
public readonly record struct BroughtName(Symbol? Symbol, string? Module, TextSpan At, bool IsExported);

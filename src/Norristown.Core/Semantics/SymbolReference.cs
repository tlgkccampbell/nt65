using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Represents one occurrence of a symbol's name, either its declaration or a use of it. A scoped
/// name records one reference per part, so <c>gfx::init</c> is a reference to the scope and one
/// to the routine. An editor needs both to rename either of them.
/// </summary>
/// <param name="Symbol">The symbol the name refers to at this point.</param>
/// <param name="Span">The span of the name itself, without any <c>::</c> around it.</param>
/// <param name="IsDeclaration">Whether this is where the symbol is declared.</param>
/// <param name="IsAlias">
/// Whether the name used is not the symbol's own, but the alias a <c>.use ... as</c> brings it
/// in under. Renaming the symbol leaves aliases alone, and renaming an alias renames only that
/// alias's references.
/// </param>
/// <param name="InUse">
/// Whether the name is in a <c>.use</c>, which brings the name in rather than using it. A module
/// that only re-exports a name does not use it.
/// </param>
/// <param name="IsStep">
/// Whether the name is a step on a path to another name, as <c>gfx</c> is in <c>gfx::init</c>.
/// The path uses what it leads to and only passes through this name.
/// </param>
/// <param name="InMacro">
/// Whether the name is in a macro body, whose names are used wherever the macro is called rather
/// than where the body is declared.
/// </param>
public sealed record SymbolReference(
    Symbol Symbol, TextSpan Span, bool IsDeclaration, bool IsAlias = false, bool InUse = false,
    bool IsStep = false, bool InMacro = false);

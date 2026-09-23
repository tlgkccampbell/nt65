using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// One place a symbol's name is written: its declaration, or a use of it. A scoped name
/// records one reference per part, so <c>gfx::init</c> is a reference to the scope and one
/// to the routine, which is what an editor needs to rename either of them.
/// </summary>
/// <param name="Symbol">What the name means there.</param>
/// <param name="Span">The name itself, without any <c>::</c> around it.</param>
/// <param name="IsDeclaration">Whether this is where the symbol is declared.</param>
/// <param name="IsAlias">
/// Whether the name written is not the symbol's own, but the one a <c>.use ... as</c> brings it
/// in under. Renaming the symbol leaves those alone, and renaming one of them renames only them.
/// </param>
/// <param name="InUse">
/// Whether it is written in a <c>.use</c>, which brings the name in rather than using it: a
/// module that only re-exports a name does not use it.
/// </param>
/// <param name="IsStep">
/// Whether it is a step on a path to another name, as <c>gfx</c> is in <c>gfx::init</c>: the
/// path uses what it leads to, and only walks through this.
/// </param>
/// <param name="InMacro">
/// Whether it is written in a macro body, whose names are used wherever the macro is called
/// rather than where the body is written.
/// </param>
public sealed record SymbolReference(
    Symbol Symbol, TextSpan Span, bool IsDeclaration, bool IsAlias = false, bool InUse = false,
    bool IsStep = false, bool InMacro = false);

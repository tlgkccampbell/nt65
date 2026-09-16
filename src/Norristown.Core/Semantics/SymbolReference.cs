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
public sealed record SymbolReference(Symbol Symbol, TextSpan Span, bool IsDeclaration);

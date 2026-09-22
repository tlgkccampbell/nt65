using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// A <c>.next</c> saying that flow runs on from a statement into a routine, which is true only
/// where the routine's first byte is the one after the statement's last.
/// </summary>
/// <param name="Statement">The statement flow runs on from.</param>
/// <param name="On">The writing of it, where it is written more than once.</param>
/// <param name="Routine">The routine the <c>.next</c> names.</param>
/// <param name="Written">Where the <c>.next</c> names it, which is where a mistake is reported.</param>
public sealed record RunningOn(SyntaxNode Statement, Expansion? On, Symbol Routine, SyntaxNode Written);

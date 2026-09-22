using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// A <c>.fallthrough</c> saying that flow runs on from the end of a routine into another, which
/// is true only where that routine's first byte is the one after this routine's last.
/// </summary>
/// <param name="Statement">The <c>.fallthrough</c>, which stands where the routine's bytes end.</param>
/// <param name="On">The writing of it, where it is written more than once.</param>
/// <param name="Routine">The routine the <c>.fallthrough</c> names.</param>
/// <param name="Written">Where the <c>.fallthrough</c> names it, which is where a mistake is reported.</param>
public sealed record RunningOn(SyntaxNode Statement, Expansion? On, Symbol Routine, SyntaxNode Written);

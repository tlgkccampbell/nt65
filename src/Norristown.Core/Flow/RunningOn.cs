using Norristown.Semantics;
using Norristown.Syntax;

namespace Norristown.Flow;

/// <summary>
/// Represents a <c>.fallthrough</c> claim that flow runs on from the end of a routine into
/// another routine. The claim is true only where that routine's first byte is the one after
/// this routine's last.
/// </summary>
/// <param name="Statement">The <c>.fallthrough</c>, which stands where the routine's bytes end.</param>
/// <param name="On">
/// The <see cref="Expansion"/> that contains the statement, where it is emitted more than once.
/// </param>
/// <param name="Routine">The routine the <c>.fallthrough</c> names.</param>
/// <param name="Target">
/// The node where the <c>.fallthrough</c> names the routine, which is where a mistake is reported.
/// </param>
public sealed record RunningOn(SyntaxNode Statement, Expansion? On, Symbol Routine, SyntaxNode Target);

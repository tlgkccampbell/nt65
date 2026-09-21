using Norristown.Project;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// What a name and a call mean in a build's condition, which is answered before a single
/// declaration has been collected. A name in one is a define the build set or a
/// <c>.config</c> a file writes and nothing else, and the built-ins that measure the program
/// have nothing to measure yet.
/// </summary>
/// <param name="Cpu">The CPU the build is for, which <c>.target</c> and <c>.has</c> ask about.</param>
/// <param name="Defines">What the build defines, by name.</param>
/// <param name="Setting">
/// What a written name is worth as a <c>.config</c>, with what is wrong with it reported
/// through the second argument. Null where the name names no setting and nothing was said
/// about it, which is what leaves it to be reported as naming nothing at all.
/// </param>
internal sealed record Conditions(
    Cpu Cpu,
    IReadOnlyDictionary<string, long> Defines,
    Func<NameExpressionSyntax, Action<SyntaxNode, string>, Value?> Setting);

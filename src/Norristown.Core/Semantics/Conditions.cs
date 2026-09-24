using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Provides the meaning of names and calls in a build's condition, which is evaluated before any
/// declaration has been collected. A name in a condition can only be a define the build set or
/// a <c>.config</c> setting a file declares. The built-ins that measure the program have
/// nothing to measure yet.
/// </summary>
/// <param name="Cpu">The CPU the build is for, which <c>.target</c> and <c>.has</c> ask about.</param>
/// <param name="Defines">The values the build defines, by name.</param>
/// <param name="Setting">
/// Returns the value of a name as a <c>.config</c> setting, reporting any problem with it
/// through the second argument. Returns null when the name is not a setting and nothing has
/// been reported about it, which leaves the caller to read it as a define or report it as an
/// unknown name. It is asked before the defines, because a setting the file declares or a
/// <c>.use</c> brings in comes before a define of the same name.
/// </param>
/// <param name="Constant">
/// Returns where the constant a name refers to is declared, when it is a constant at file level
/// that <c>.config</c> could make a setting, or null. It is asked about a name that is neither a
/// setting nor a define, so that the report can say how to make it one.
/// </param>
internal sealed record Conditions(
    Cpu Cpu,
    IReadOnlyDictionary<string, long> Defines,
    Func<NameExpressionSyntax, Action<SyntaxNode, DiagnosticMessage>, Value?> Setting,
    Func<NameExpressionSyntax, Span?>? Constant = null);

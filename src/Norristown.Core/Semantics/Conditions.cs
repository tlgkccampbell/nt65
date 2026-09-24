using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Provides the meaning of names and calls in a build's condition, which is evaluated before any
/// declaration has been collected. A name there may be a setting, or a constant, function or enum
/// member that the configuration alone decides. The built-ins that measure the program have
/// nothing to measure yet.
/// </summary>
/// <param name="Cpu">The CPU the build is for, which <c>.target</c> and <c>.has</c> ask about.</param>
/// <param name="Name">
/// Returns the value of a name, or the reason the configuration does not decide it. Returns null
/// when a problem with the name has already been reported, such as a name another module does not
/// export.
/// </param>
/// <param name="Call">
/// Returns the value of a call to a <c>.func</c> or a charmap with the given argument values, or
/// the reason the configuration does not decide it, or null when a problem has been reported.
/// </param>
/// <param name="Undecided">
/// Receives each name, call or measurement the configuration does not decide, with the name it
/// refers to, or null for a measurement or a call, and the reason.
/// </param>
internal sealed record Conditions(
    Cpu Cpu,
    Func<NameExpressionSyntax, Decision?> Name,
    Func<CallExpressionSyntax, IReadOnlyList<Value>, Decision?> Call,
    Action<SyntaxNode, string?, Undecided> Undecided);

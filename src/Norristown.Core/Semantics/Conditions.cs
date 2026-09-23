using Norristown.Processor;
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
/// The value of a written name as a <c>.config</c> setting, with any problem with it reported
/// through the second argument. Returns null when the name is not a setting and nothing has
/// been reported about it, which leaves the caller to report it as an unknown name.
/// </param>
internal sealed record Conditions(
    Cpu Cpu,
    IReadOnlyDictionary<string, long> Defines,
    Func<NameExpressionSyntax, Action<SyntaxNode, DiagnosticMessage>, Value?> Setting);

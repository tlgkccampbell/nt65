using Norristown.Syntax;

namespace Norristown.Tests.Syntax;

/// <summary>
/// Represents a rewrite that overrides nothing, which is the one rewrite that must change
/// nothing: it has to give back the very root it was given, tags and all.
/// </summary>
internal sealed class Untouched : SyntaxRewriter
{
}

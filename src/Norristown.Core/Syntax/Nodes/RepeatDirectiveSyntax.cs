using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.repeat count, name {</c>.</summary>
public sealed class RepeatDirectiveSyntax : RepetitionDirectiveSyntax
{
    internal RepeatDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}

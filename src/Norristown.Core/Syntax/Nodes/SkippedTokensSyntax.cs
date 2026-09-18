using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>What was left on a line after its statement: tokens the parser walked past.</summary>
public sealed class SkippedTokensSyntax : SyntaxNode
{
    internal SkippedTokensSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }
}

using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.cpu name</c>.</summary>
public sealed class CpuDirectiveSyntax : StatementSyntax
{
    internal CpuDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.cpu</c> that starts the line.</summary>
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The CPU's name, or null.</summary>
    public SyntaxToken? Cpu => TokenAt(1) is { Kind: not SyntaxKind.EndOfLine } cpu ? cpu : null;
}

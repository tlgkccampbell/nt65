// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>The <c>: entry -&gt; exit</c> of a proc, an extern proc or a macro.</summary>
public sealed class ProcSignatureSyntax : SyntaxNode
{
    internal ProcSignatureSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>:</c>.</summary>
    public SyntaxToken ColonToken => ChildTokens[0];

    /// <summary>The state on entry.</summary>
    public StateListSyntax Entry => (StateListSyntax)ChildNodes[0];

    /// <summary>The <c>-&gt;</c>, or null.</summary>
    public SyntaxToken? ArrowToken => FirstToken(SyntaxKind.Arrow);

    /// <summary>The state on exit, or null.</summary>
    public StateListSyntax? Exit => ChildNodes.Length > 1 ? (StateListSyntax)ChildNodes[1] : null;

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitProcSignature(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitProcSignature(this);
}

// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.proc name: entry -&gt; exit {</c>.</summary>
public sealed class ProcDeclarationSyntax : StatementSyntax
{
    internal ProcDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.proc</c> that starts the line.</summary>
    public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The routine's name, or null.</summary>
    public SyntaxToken? Name => Green is GreenSyntax ? NameAt(1) : SlotToken(1);

    /// <summary>The signature, or null.</summary>
    public ProcSignatureSyntax? Signature =>
        Green is GreenSyntax ? FirstNode<ProcSignatureSyntax>() : SlotNodeOrNull<ProcSignatureSyntax>(2);

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public SyntaxToken? OpenBraceToken => Green is GreenSyntax ? FirstToken(SyntaxKind.OpenBrace) : SlotToken(3);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitProcDeclaration(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitProcDeclaration(this);
}

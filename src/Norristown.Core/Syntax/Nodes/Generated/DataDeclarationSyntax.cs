// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.data name: element</c>, or <c>.data name {</c> for mixed data.</summary>
public sealed class DataDeclarationSyntax : StatementSyntax
{
    internal DataDeclarationSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.data</c> that starts the line.</summary>
    public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The declared name, or null.</summary>
    public SyntaxToken? Name => Green is GreenSyntax ? NameAt(1) : SlotToken(1);

    /// <summary>The <c>:</c> before what the data is, or null.</summary>
    public SyntaxToken? ColonToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Colon) : SlotTokenOrNull(2);

    /// <summary>What the data is, or null.</summary>
    public DataDirectiveSyntax? Directive =>
        Green is GreenSyntax ? FirstNode<DataDirectiveSyntax>() : SlotNodeOrNull<DataDirectiveSyntax>(3);

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public SyntaxToken? OpenBraceToken => Green is GreenSyntax ? FirstToken(SyntaxKind.OpenBrace) : SlotTokenOrNull(4);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitDataDeclaration(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitDataDeclaration(this);
}

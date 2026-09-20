// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>proc(entry -&gt; exit)</c>: the signature of an imported routine.</summary>
public sealed class ImportSignatureSyntax : SyntaxNode
{
    internal ImportSignatureSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>proc</c>.</summary>
    public SyntaxToken ProcKeyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The <c>(</c>, or null.</summary>
    public SyntaxToken? OpenParenToken => Green is GreenSyntax ? FirstToken(SyntaxKind.OpenParen) : SlotToken(1);

    /// <summary>The state on entry, or null when it is left out.</summary>
    public StateListSyntax? Entry =>
        Green is GreenSyntax ? ArrowToken is { } arrow ? NodeBefore(arrow) as StateListSyntax : FirstNode<StateListSyntax>() : SlotNodeOrNull<StateListSyntax>(2);

    /// <summary>The <c>-&gt;</c>, or null.</summary>
    public SyntaxToken? ArrowToken => Green is GreenSyntax ? FirstToken(SyntaxKind.Arrow) : SlotTokenOrNull(3);

    /// <summary>The state on exit, or null when it is left out.</summary>
    public StateListSyntax? Exit =>
        Green is GreenSyntax ? NodeAfter(ArrowToken) as StateListSyntax : SlotNodeOrNull<StateListSyntax>(4);

    /// <summary>The <c>)</c>, or null.</summary>
    public SyntaxToken? CloseParenToken => Green is GreenSyntax ? FirstToken(SyntaxKind.CloseParen) : SlotToken(5);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitImportSignature(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitImportSignature(this);
}

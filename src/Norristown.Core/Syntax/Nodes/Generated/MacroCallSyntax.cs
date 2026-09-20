// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>name!(args)</c>, with the <c>{</c> of a trailing block argument when one follows.</summary>
public sealed class MacroCallSyntax : StatementSyntax
{
    internal MacroCallSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The macro's name.</summary>
    public SyntaxToken Name => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The <c>!</c>.</summary>
    public SyntaxToken BangToken => Green is GreenSyntax ? ChildTokens[1] : SlotToken(1);

    /// <summary>The arguments, or null.</summary>
    public ArgumentListSyntax? Arguments =>
        Green is GreenSyntax ? FirstNode<ArgumentListSyntax>() : SlotNode<ArgumentListSyntax>(2);

    /// <summary>The <c>{</c> that opens the block, or null when it is not written.</summary>
    public SyntaxToken? OpenBraceToken => Green is GreenSyntax ? FirstToken(SyntaxKind.OpenBrace) : SlotTokenOrNull(3);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitMacroCall(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitMacroCall(this);
}

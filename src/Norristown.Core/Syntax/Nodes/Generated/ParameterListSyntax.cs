// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>(a, b)</c>: the parameters of a <c>.func</c>.</summary>
public sealed class ParameterListSyntax : SyntaxNode
{
    private ImmutableArray<SyntaxToken> parameters;

    internal ParameterListSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>(</c>.</summary>
    public SyntaxToken OpenParenToken => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The parameters' names.</summary>
    public ImmutableArray<SyntaxToken> Parameters
    {
        get
        {
            if (parameters.IsDefault)
                ImmutableInterlocked.InterlockedInitialize(ref parameters, [.. ChildTokens.Where(token => token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic)]);
            return parameters;
        }
    }

    /// <summary>The <c>)</c>, or null.</summary>
    public SyntaxToken? CloseParenToken => Green is GreenSyntax ? FirstToken(SyntaxKind.CloseParen) : SlotToken(2);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitParameterList(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitParameterList(this);
}

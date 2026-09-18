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
    public SyntaxToken OpenParenToken => ChildTokens[0];

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
    public SyntaxToken? CloseParenToken => FirstToken(SyntaxKind.CloseParen);
}

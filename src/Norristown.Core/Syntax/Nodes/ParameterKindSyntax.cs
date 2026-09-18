using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>What a macro parameter takes: a fixed word, a <c>one(...)</c> of listed words, or a <c>list(...)</c> of one of those.</summary>
public sealed class ParameterKindSyntax : SyntaxNode
{
    private ImmutableArray<SyntaxToken> words;

    internal ParameterKindSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The kind's word, or null when none was written.</summary>
    public SyntaxToken? Keyword => TokenAt(0);

    /// <summary>The <c>(</c> of a <c>one</c> or a <c>list</c>, or null.</summary>
    public SyntaxToken? OpenParenToken => FirstToken(SyntaxKind.OpenParen);

    /// <summary>The words a <c>one(...)</c> accepts.</summary>
    public ImmutableArray<SyntaxToken> Words
    {
        get
        {
            if (words.IsDefault)
                ImmutableInterlocked.InterlockedInitialize(ref words, [.. ChildTokens.Skip(2).Where(token => token.Kind is SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic)]);
            return words;
        }
    }

    /// <summary>What each item of a <c>list(...)</c> is, or null.</summary>
    public ParameterKindSyntax? Element => FirstNode<ParameterKindSyntax>();

    /// <summary>The <c>)</c>, or null.</summary>
    public SyntaxToken? CloseParenToken => FirstToken(SyntaxKind.CloseParen);
}

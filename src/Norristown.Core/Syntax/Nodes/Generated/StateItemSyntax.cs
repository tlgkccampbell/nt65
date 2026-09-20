// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// One processor-state item: <c>a16</c>, <c>i*</c>, <c>dp = 0</c>, <c>args 2</c>, <c>inline .strz</c>, <c>keeps a, x</c>,
/// <c>?</c> on its own, or the name of a signature set.
/// </summary>
public class StateItemSyntax : SyntaxNode
{
    private ImmutableArray<SyntaxToken> registers;

    internal StateItemSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The signature set the item names, or null for an item that is a word of its own.</summary>
    public NameExpressionSyntax? SetName => ChildTokens.Length == 0 ? FirstNode<NameExpressionSyntax>() : null;

    /// <summary>The item's word, or null for a set's name or a <c>?</c> on its own.</summary>
    public SyntaxToken? Name => TokenAt(0) is { Kind: not SyntaxKind.Question } name ? name : null;

    /// <summary>The <c>?</c> that is the whole item, or null.</summary>
    public SyntaxToken? AllUnknownToken => TokenAt(0) is { Kind: SyntaxKind.Question } question ? question : null;

    /// <summary>The <c>*</c>, <c>?</c> or <c>=</c> after the word, or null.</summary>
    public SyntaxToken? Suffix =>
        Name is not null && TokenAt(1) is { Kind: SyntaxKind.Star or SyntaxKind.Question or SyntaxKind.Equals } suffix ? suffix : null;

    /// <summary>The expression after the word: the <c>n</c> of <c>inline n</c>, the <c>e</c> of <c>dp = e</c>. Null when there is none.</summary>
    public ExpressionSyntax? Value => FirstNode<ExpressionSyntax>();

    /// <summary>The <c>.strz</c> of <c>inline .strz</c>, or null.</summary>
    public SyntaxToken? StrzToken => FirstToken(SyntaxKind.Directive);

    /// <summary>The registers a <c>keeps</c> names.</summary>
    public ImmutableArray<SyntaxToken> Registers
    {
        get
        {
            if (registers.IsDefault)
                ImmutableInterlocked.InterlockedInitialize(ref registers, [.. ChildTokens.Skip(1).Where(token => token.Kind is SyntaxKind.Identifier or SyntaxKind.Register)]);
            return registers;
        }
    }

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitStateItem(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitStateItem(this);
}

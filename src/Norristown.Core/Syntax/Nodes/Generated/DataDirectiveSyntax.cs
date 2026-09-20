// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A data directive: <c>.byte 1, 2</c>, <c>.word[4] { … }</c>, <c>.type T { x = 1 }</c>, <c>.incbin "f"</c>.</summary>
public sealed class DataDirectiveSyntax : StatementSyntax
{
    private ImmutableArray<SyntaxNode> values;

    internal DataDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The directive.</summary>
    public SyntaxToken Directive => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The <c>T</c> of <c>.type T</c>, or null.</summary>
    public NameExpressionSyntax? Type =>
        Green is GreenSyntax ? IsRecord && ChildNodes.Length > 0 ? ChildNodes[0] as NameExpressionSyntax : null : SlotNodeOrNull<NameExpressionSyntax>(1);

    /// <summary>The <c>[n]</c> or <c>[]</c> after the element type, or null.</summary>
    public ElementCountSyntax? Count =>
        Green is GreenSyntax ? FirstNode<ElementCountSyntax>() : SlotNodeOrNull<ElementCountSyntax>(2);

    /// <summary>Whether the directive is <c>.type</c>, whose element is a named type.</summary>
    public bool IsRecord => Directive.Text.Equals(".type", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>{</c> that opens a body holding the values, or null.</summary>
    public SyntaxToken? OpenBraceToken => FirstToken(SyntaxKind.OpenBrace);

    /// <summary>What is written after the element type and its count: expressions, or one braced list or record.</summary>
    public ImmutableArray<SyntaxNode> Values
    {
        get
        {
            if (values.IsDefault)
                ImmutableInterlocked.InterlockedInitialize(ref values, [.. ChildNodes.Where(node => node != Type && node is not ElementCountSyntax)]);
            return values;
        }
    }

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitDataDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitDataDirective(this);
}

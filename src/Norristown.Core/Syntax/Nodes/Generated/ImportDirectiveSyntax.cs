// Generated from src/Norristown.Core/Syntax/Syntax.nodes by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.import a, b: far, c: proc(a8 -&gt; a8)</c>.</summary>
public sealed class ImportDirectiveSyntax : StatementSyntax
{
    private ImmutableArray<ImportItemSyntax> items;

    internal ImportDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.import</c> that starts the line.</summary>
    public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The names imported.</summary>
    public ImmutableArray<ImportItemSyntax> Items => Nodes(ref items);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitImportDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitImportDirective(this);
}

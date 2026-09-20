// Generated from src/Norristown.Core/Syntax/Syntax.xml by scripts/generate-syntax.ps1. Change the table, not this file.
using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary><c>.export a, outer::inner, K: abs, init as "_init"</c>: a list of names to export.</summary>
public sealed class ExportDirectiveSyntax : StatementSyntax
{
    private ImmutableArray<ExportItemSyntax> items;

    internal ExportDirectiveSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The <c>.export</c> that starts the line.</summary>
    public SyntaxToken Keyword => Green is GreenSyntax ? ChildTokens[0] : SlotToken(0);

    /// <summary>The names exported.</summary>
    public ImmutableArray<ExportItemSyntax> Items => Nodes(ref items);

    /// <inheritdoc/>
    public override void Accept(SyntaxVisitor visitor) => visitor.VisitExportDirective(this);

    /// <inheritdoc/>
    public override TResult? Accept<TResult>(SyntaxVisitor<TResult> visitor) where TResult : default =>
        visitor.VisitExportDirective(this);
}

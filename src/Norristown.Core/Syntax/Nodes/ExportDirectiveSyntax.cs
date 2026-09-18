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
    public SyntaxToken Keyword => ChildTokens[0];

    /// <summary>The names exported.</summary>
    public ImmutableArray<ExportItemSyntax> Items => Nodes(ref items);
}

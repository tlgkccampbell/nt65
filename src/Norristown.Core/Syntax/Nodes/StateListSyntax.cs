using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>Processor-state items separated by commas.</summary>
public sealed class StateListSyntax : SyntaxNode
{
    private ImmutableArray<StateItemSyntax> items;

    internal StateListSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The items.</summary>
    public ImmutableArray<StateItemSyntax> Items => Nodes(ref items);
}

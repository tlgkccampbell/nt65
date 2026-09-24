using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// Represents the red node over a green list. It creates and caches the red nodes of a list's
/// items, and <see cref="SyntaxList{T}"/>, <see cref="SeparatedSyntaxList{T}"/> and
/// <see cref="SyntaxTokenList"/> read them from it. A consumer asks the node that holds the list
/// for one of those types rather than for this node.
/// </summary>
internal sealed partial class SyntaxListNode : SyntaxNode
{
    internal SyntaxListNode(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>
    /// Gets the node that holds the list. The list's items and separators take that node as their
    /// parent rather than this one, so a list node never appears as the parent of anything.
    /// </summary>
    internal override SyntaxNode ChildParent => Parent ?? this;
}

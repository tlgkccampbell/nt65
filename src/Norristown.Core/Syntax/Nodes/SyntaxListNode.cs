using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>
/// The red node over a green list. It is where the red nodes of a list's items are made and
/// kept, and <see cref="SyntaxList{T}"/>, <see cref="SeparatedSyntaxList{T}"/> and
/// <see cref="SyntaxTokenList"/> read them from it; a consumer asks the node that holds the
/// list for one of those rather than for this node.
/// </summary>
internal sealed partial class SyntaxListNode : SyntaxNode
{
    internal SyntaxListNode(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>
    /// The node holding the list. Its items and separators hang from that node rather than from
    /// this one, so a list never shows up as anyone's parent.
    /// </summary>
    internal override SyntaxNode ChildParent => Parent ?? this;
}

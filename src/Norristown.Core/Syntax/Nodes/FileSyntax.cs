using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>A whole file: the lines and blocks at its top level.</summary>
public sealed partial class FileSyntax : SyntaxNode
{
    internal FileSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The file's top-level lines and blocks, in source order.</summary>
    public ImmutableArray<SyntaxNode> Members => ChildNodes;
}

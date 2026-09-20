using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>One line of a data body: values separated by commas, one element each.</summary>
public sealed class DataValuesSyntax : StatementSyntax
{
    internal DataValuesSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The values: expressions and braced lists or records.</summary>
    public ImmutableArray<SyntaxNode> Values => ChildNodes;
}

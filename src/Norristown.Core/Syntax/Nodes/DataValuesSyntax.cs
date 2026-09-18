using System.Collections.Immutable;
using Norristown.Syntax.InternalSyntax;

namespace Norristown.Syntax;

/// <summary>One line of a data body: values separated by commas, one element each.</summary>
public sealed class DataValuesSyntax : StatementSyntax
{
    private ImmutableArray<SyntaxNode> values;

    internal DataValuesSyntax(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
        : base(tree, parent, green, position)
    {
    }

    /// <summary>The values: expressions and braced lists or records.</summary>
    public ImmutableArray<SyntaxNode> Values
    {
        get
        {
            if (values.IsDefault)
                ImmutableInterlocked.InterlockedInitialize(ref values, [.. ChildNodes.Where(node => node is not SkippedTokensSyntax)]);
            return values;
        }
    }
}

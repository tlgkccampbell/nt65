using System.Collections.Immutable;

namespace Norristown.Syntax;

/// <summary>
/// A red node: a green node with its parent and absolute position, created on demand.
/// Everything above the syntax layer works with these.
/// </summary>
public sealed class SyntaxNode
{
    private ImmutableArray<SyntaxNode> childNodes;
    private ImmutableArray<SyntaxToken> childTokens;

    internal SyntaxNode(SyntaxTree tree, SyntaxNode? parent, GreenNode green, int position)
    {
        Tree = tree;
        Parent = parent;
        Green = green;
        Position = position;
    }

    public SyntaxTree Tree { get; }
    public SyntaxNode? Parent { get; }
    public GreenNode Green { get; }
    public int Position { get; }

    public SyntaxKind Kind => Green.Kind;
    public TextSpan FullSpan => new(Position, Green.FullWidth);

    /// <summary>The 0-based line this node starts on.</summary>
    public int LineIndex => Tree.GetLineIndex(Position);

    /// <summary>Child lines and blocks of a file or block.</summary>
    public ImmutableArray<SyntaxNode> ChildNodes
    {
        get
        {
            if (childNodes.IsDefault)
            {
                var builder = ImmutableArray.CreateBuilder<SyntaxNode>();
                var position = Position;
                for (var i = 0; i < Green.SlotCount; i++)
                {
                    var slot = Green.GetSlot(i);
                    if (slot is not GreenToken)
                        builder.Add(new SyntaxNode(Tree, this, slot, position));
                    position += slot.FullWidth;
                }
                ImmutableInterlocked.InterlockedInitialize(ref childNodes, builder.ToImmutable());
            }
            return childNodes;
        }
    }

    /// <summary>Tokens directly under this node, such as a line's.</summary>
    public ImmutableArray<SyntaxToken> ChildTokens
    {
        get
        {
            if (childTokens.IsDefault)
            {
                var builder = ImmutableArray.CreateBuilder<SyntaxToken>();
                var position = Position;
                for (var i = 0; i < Green.SlotCount; i++)
                {
                    var slot = Green.GetSlot(i);
                    if (slot is GreenToken token)
                        builder.Add(new SyntaxToken(this, token, position));
                    position += slot.FullWidth;
                }
                ImmutableInterlocked.InterlockedInitialize(ref childTokens, builder.ToImmutable());
            }
            return childTokens;
        }
    }

    public IEnumerable<SyntaxNode> DescendantNodes()
    {
        foreach (var child in ChildNodes)
        {
            yield return child;
            foreach (var descendant in child.DescendantNodes())
                yield return descendant;
        }
    }

    public string ToFullString() => Green.ToFullString();

    public override string ToString() => $"{Kind} at {FullSpan}";
}

public readonly record struct SyntaxToken(SyntaxNode Parent, GreenToken Green, int Position)
{
    public SyntaxKind Kind => Green.Kind;
    public string Text => Green.Text;

    /// <summary>The token's text, without trivia.</summary>
    public TextSpan Span => new(Position + Green.LeadingWidth, Green.Text.Length);

    public TextSpan FullSpan => new(Position, Green.FullWidth);

    public override string ToString() => Text;
}

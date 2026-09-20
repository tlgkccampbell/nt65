using System.Collections.Immutable;

namespace Norristown.Syntax;

// The half of the node the table cannot say: the questions asked of a path often enough that
// asking them over the parts would be the same few lines in twenty places. The rest of the class,
// and its summary, are generated.
public sealed partial class NameExpressionSyntax
{
    private ImmutableArray<SyntaxToken> names;

    /// <summary>
    /// The names between the <c>::</c> that the source wrote, outermost first: the names of
    /// <see cref="Parts"/>, without a part that is only the place one belongs. Empty when not even
    /// the first name was written.
    /// </summary>
    public ImmutableArray<SyntaxToken> Names
    {
        get
        {
            if (names.IsDefault)
                ImmutableInterlocked.InterlockedInitialize(ref names, ReadNames());
            return names;
        }
    }

    /// <summary>
    /// The one name a path of a single component is: nothing before it and nothing after it. An
    /// <c>[i]</c> is a place in what the name stands for rather than part of the name, so
    /// <c>table[2]</c> is one name as much as <c>table</c> is. Null for a path, and for a name the
    /// source did not write.
    /// </summary>
    public SyntaxToken? SimpleName =>
        GlobalToken is null && Parts is [{ Name: { IsMissing: false } only }] ? only : null;

    /// <summary>
    /// The innermost part of the path, which is what the whole of it stands for, or null where the
    /// source wrote none: a name that ends in a <c>::</c>, or one that is only the place a name
    /// belongs.
    /// </summary>
    public IdentifierNameSyntax? LastPart => Parts is [.., { Name.IsMissing: false } last] ? last : null;

    /// <summary>Whether any part of the path has an <c>[i]</c> after it.</summary>
    public bool IsIndexed
    {
        get
        {
            foreach (var part in Parts)
            {
                if (part.Index is not null)
                    return true;
            }
            return false;
        }
    }

    private ImmutableArray<SyntaxToken> ReadNames()
    {
        var builder = ImmutableArray.CreateBuilder<SyntaxToken>(Parts.Count);
        foreach (var part in Parts)
        {
            if (!part.Name.IsMissing)
                builder.Add(part.Name);
        }
        return builder.ToImmutable();
    }
}

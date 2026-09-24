using System.Collections.Immutable;

namespace Norristown.Syntax;

// Hand-written members that Syntax.xml cannot describe. They answer questions asked of a path
// often enough that computing them from its parts each time would repeat the same few lines in
// many places. The rest of the class, and its summary, are generated.
public sealed partial class NameExpressionSyntax
{
    private ImmutableArray<SyntaxToken> names;

    /// <summary>
    /// Gets the names between the <c>::</c> separators, outermost first. This is the name of each
    /// of the <see cref="Parts"/>, skipping any part whose name is missing from the source. The
    /// array is empty if the source omits even the first name.
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
    /// Gets the name if the path is a single name with no <c>::</c> before or after it. An
    /// <c>[i]</c> indexes into what the name refers to rather than being part of the name, so
    /// <c>table[2]</c> is a single name just as <c>table</c> is. The value is null for a path of
    /// more than one part, and for a name the source omits.
    /// </summary>
    public SyntaxToken? SimpleName =>
        GlobalToken is null && Parts is [{ Name: { IsMissing: false } only }] ? only : null;

    /// <summary>
    /// Gets the innermost part of the path, which names what the whole path refers to. The value
    /// is null if the source has no name there, as in a path that ends in <c>::</c> or a path
    /// whose last name is missing.
    /// </summary>
    public IdentifierNameSyntax? LastPart => Parts is [.., { Name.IsMissing: false } last] ? last : null;

    /// <summary>Gets a value indicating whether any part of the path has an <c>[i]</c> after it.</summary>
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

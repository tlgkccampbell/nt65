using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// Collects a list's items, and the separators between them, in source order, and makes the
/// green node over them. A builder that was given nothing makes no node, because an empty list
/// is a slot with nothing in it.
/// </summary>
internal sealed class GreenListBuilder
{
    private readonly ImmutableArray<GreenNode>.Builder pieces = ImmutableArray.CreateBuilder<GreenNode>();

    /// <summary>Gets how many items have been added, not counting separators.</summary>
    public int Count { get; private set; }

    /// <summary>Adds an item after everything added so far.</summary>
    /// <param name="item">The item.</param>
    public void Add(GreenNode item)
    {
        pieces.Add(item);
        Count++;
    }

    /// <summary>Adds the separator that follows the most recently added item.</summary>
    /// <param name="separator">The separator, nearly always a comma.</param>
    public void AddSeparator(GreenToken separator) => pieces.Add(separator);

    /// <summary>Returns a list of everything added, or null if nothing was added.</summary>
    public GreenList? ToList() => pieces.Count == 0 ? null : new GreenList(pieces.ToImmutable());

    /// <summary>Returns a separated list of everything added, or null if nothing was added.</summary>
    public GreenSeparatedList? ToSeparatedList() =>
        pieces.Count == 0 ? null : new GreenSeparatedList(pieces.ToImmutable());
}

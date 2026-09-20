using System.Collections.Immutable;

namespace Norristown.Syntax.InternalSyntax;

/// <summary>
/// Collects a list's items, and the separators between them, in source order, and makes the
/// green node over them. A builder that was given nothing makes no node: an empty list is a
/// slot with nothing in it.
/// </summary>
internal sealed class GreenListBuilder
{
    private readonly ImmutableArray<GreenNode>.Builder pieces = ImmutableArray.CreateBuilder<GreenNode>();

    /// <summary>How many items have been added, separators aside.</summary>
    public int Count { get; private set; }

    /// <summary>Adds an item after everything added so far.</summary>
    /// <param name="item">The item.</param>
    public void Add(GreenNode item)
    {
        pieces.Add(item);
        Count++;
    }

    /// <summary>Adds the separator written after the item before it.</summary>
    /// <param name="separator">The separator, nearly always a comma.</param>
    public void AddSeparator(GreenToken separator) => pieces.Add(separator);

    /// <summary>The list of everything added, or null when nothing was.</summary>
    public GreenList? ToList() => pieces.Count == 0 ? null : new GreenList(pieces.ToImmutable());

    /// <summary>The separated list of everything added, or null when nothing was.</summary>
    public GreenSeparatedList? ToSeparatedList() =>
        pieces.Count == 0 ? null : new GreenSeparatedList(pieces.ToImmutable());
}

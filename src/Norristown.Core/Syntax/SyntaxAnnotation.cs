namespace Norristown.Syntax;

/// <summary>
/// A tag put on a node or a token so that whoever put it there can find that piece again in the
/// tree a rewrite gives back. It is what an analyzer's fix marks the node it is inserting with,
/// so that the editor knows afterwards where to put the caret or start a rename — the place the
/// piece ended up is the tree's to say, and no amount of counting characters will say it.
/// <para>
/// An annotation is itself, and nothing else: two with the same <see cref="Kind"/> and the same
/// <see cref="Data"/> are two different annotations, and a search finds the very one that was put
/// on. <see cref="Kind"/> is for finding a whole class of them — every renamed name, say — and
/// <see cref="Data"/> is whatever the one who made it wants to carry along.
/// </para>
/// <para>
/// Annotations are no part of the text: a node carrying one writes exactly what it wrote before,
/// takes up the same width, holds the same diagnostics and is equal to itself as text. What they
/// change is which object the node is, which is what makes it findable.
/// </para>
/// </summary>
public sealed class SyntaxAnnotation
{
    /// <summary>An annotation of no kind, which is a bare mark to find one piece by.</summary>
    public SyntaxAnnotation()
    {
    }

    /// <summary>An annotation of <paramref name="kind"/>.</summary>
    /// <param name="kind">What class of annotation this is, for finding them together.</param>
    public SyntaxAnnotation(string? kind) => Kind = kind;

    /// <summary>An annotation of <paramref name="kind"/> carrying <paramref name="data"/>.</summary>
    /// <param name="kind">What class of annotation this is, for finding them together.</param>
    /// <param name="data">Whatever the one who made it wants to carry along.</param>
    public SyntaxAnnotation(string? kind, string? data)
    {
        Kind = kind;
        Data = data;
    }

    /// <summary>What class of annotation this is, or null for one of no kind.</summary>
    public string? Kind { get; }

    /// <summary>What it carries, or null for one carrying nothing.</summary>
    public string? Data { get; }

    /// <summary>The annotation's kind and data, for debugging.</summary>
    public override string ToString() => (Kind, Data) switch
    {
        (null, null) => "annotation",
        (null, { } data) => $"annotation: {data}",
        ({ } kind, null) => kind,
        var (kind, data) => $"{kind}: {data}",
    };
}

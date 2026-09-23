namespace Norristown.Syntax;

/// <summary>
/// A tag put on a node or a token so that whoever put it there can find that piece again in the
/// tree a rewrite gives back. It is what an analyzer's fix marks the node it is inserting with,
/// so that the editor knows afterwards where to put the caret or start a rename — only the new
/// tree knows where the piece ended up, and counting characters cannot work it out.
/// <para>
/// Annotations compare by reference: two with the same <see cref="Kind"/> and the same
/// <see cref="Data"/> are two different annotations, and a search finds the very one that was put
/// on. <see cref="Kind"/> is for finding a whole class of them — every renamed name, say — and
/// <see cref="Data"/> is whatever its creator wants to carry along.
/// </para>
/// <para>
/// Annotations are not part of the text: a node carrying one has exactly the same text, width
/// and diagnostics as before. What changes is the node object itself, and that new object is
/// what the annotation makes findable.
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

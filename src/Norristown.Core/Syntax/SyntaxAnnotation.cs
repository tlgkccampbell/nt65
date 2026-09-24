namespace Norristown.Syntax;

/// <summary>
/// Represents a tag on a node or a token that lets the code that added it find that node or
/// token again in the tree a rewrite returns. An analyzer's fix marks the node it inserts with an
/// annotation, so that the editor knows afterwards where to put the caret or start a rename. Only
/// the new tree knows where the node ended up; counting characters cannot work it out.
/// <para>
/// Annotations compare by reference. Two annotations with the same <see cref="Kind"/> and the
/// same <see cref="Data"/> are different annotations, and a search finds the exact instance that
/// was added. <see cref="Kind"/> is for finding a whole class of annotations, such as every
/// renamed name. <see cref="Data"/> holds any string its creator wants to attach.
/// </para>
/// <para>
/// Annotations are not part of the text. An annotated node has exactly the same text, width and
/// diagnostics as before. Only the node object changes, and the annotation makes that new object
/// findable.
/// </para>
/// </summary>
public sealed class SyntaxAnnotation
{
    /// <summary>
    /// Initializes a new annotation with no kind, which serves as a bare mark for finding a single
    /// node or token.
    /// </summary>
    public SyntaxAnnotation()
    {
    }

    /// <summary>Initializes a new annotation of kind <paramref name="kind"/>.</summary>
    /// <param name="kind">The class of this annotation, used to find annotations of that class together.</param>
    public SyntaxAnnotation(string? kind) => Kind = kind;

    /// <summary>
    /// Initializes a new annotation of kind <paramref name="kind"/> that holds <paramref name="data"/>.
    /// </summary>
    /// <param name="kind">The class of this annotation, used to find annotations of that class together.</param>
    /// <param name="data">Any data the annotation's creator wants to attach.</param>
    public SyntaxAnnotation(string? kind, string? data)
    {
        Kind = kind;
        Data = data;
    }

    /// <summary>Gets the class of this annotation, or null if the annotation has no kind.</summary>
    public string? Kind { get; }

    /// <summary>Gets the data stored in this annotation, or null if the annotation has no data.</summary>
    public string? Data { get; }

    /// <summary>Returns the annotation's kind and data, for debugging.</summary>
    public override string ToString() => (Kind, Data) switch
    {
        (null, null) => "annotation",
        (null, { } data) => $"annotation: {data}",
        ({ } kind, null) => kind,
        var (kind, data) => $"{kind}: {data}",
    };
}

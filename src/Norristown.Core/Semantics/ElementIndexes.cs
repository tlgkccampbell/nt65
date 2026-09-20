using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// The <c>[i]</c> a path writes after one of its components, <c>actors[1]::hp</c>: which
/// element of a counted declaration the path reaches. An index is a place in a declaration
/// rather than part of a name, so it never changes what a name resolves to — only how many
/// bytes on from it the path lands.
/// </summary>
public static class ElementIndexes
{
    /// <summary>
    /// Each component of the path that carries an index, with the index written after it, in
    /// the order they are written.
    /// </summary>
    public static IEnumerable<(SyntaxToken Part, ElementIndexSyntax Index)> Of(NameExpressionSyntax name)
    {
        foreach (var part in name.Parts)
        {
            if (!part.Name.IsMissing && part.Index is { } index)
                yield return (part.Name, index);
        }
    }

    /// <summary>
    /// How many bytes one element of <paramref name="symbol"/> takes — its size divided among
    /// its elements, which is a record's type size for an array of records — or null where it
    /// has no elements to count.
    /// </summary>
    public static long? Stride(Symbol symbol) =>
        symbol.Size is { } size && symbol.Count is { } count and > 0 ? size / count : null;
}

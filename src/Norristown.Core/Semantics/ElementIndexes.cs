using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>
/// Provides the element indexes in a path, such as the <c>[1]</c> in <c>actors[1]::hp</c>. Each
/// index selects the element of a counted declaration that the path reaches. An index is a
/// position in a declaration rather than part of a name. It therefore never changes what a name
/// resolves to, only how many bytes past it the path lands.
/// </summary>
public static class ElementIndexes
{
    /// <summary>
    /// Returns each component of the path that has an index, paired with the index that follows
    /// it, in source order.
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
    /// Returns the number of bytes one element of <paramref name="symbol"/> takes, or null when
    /// it has no elements to count. This is its size divided by its element count, which for an
    /// array of records is the record type's size.
    /// </summary>
    public static long? Stride(Symbol symbol) =>
        symbol.Size is { } size && symbol.Count is { } count and > 0 ? size / count : null;
}

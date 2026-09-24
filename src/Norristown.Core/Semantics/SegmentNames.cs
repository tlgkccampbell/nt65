using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>Reads a segment's name and address size from the source.</summary>
public static class SegmentNames
{
    /// <summary>
    /// Returns the segment <paramref name="token"/> names. The token is an identifier, or a name
    /// in quotes, which is an error that still names the segment. Segment names contain no
    /// escapes, so the quotes are simply trimmed. A missing token names nothing, and this is the
    /// one place where that is checked.
    /// </summary>
    public static string? Of(SyntaxToken token) => token.IsMissing ? null : token.Kind switch
    {
        SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic => token.Text,
        SyntaxKind.StringLiteral => token.Text.Trim('"'),
        _ => null,
    };

    /// <summary>
    /// Returns the address size that <c>zp</c>, <c>abs</c> or <c>far</c> names, or null for any
    /// other text.
    /// </summary>
    public static AddressSize? ParseSize(string text) => text.ToLowerInvariant() switch
    {
        "zp" => AddressSize.ZeroPage,
        "abs" => AddressSize.Absolute,
        "far" => AddressSize.Far,
        _ => null,
    };
}

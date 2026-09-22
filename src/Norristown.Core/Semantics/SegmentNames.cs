using Norristown.Processor;
using Norristown.Syntax;

namespace Norristown.Semantics;

/// <summary>How a segment is written: its name and its size.</summary>
public static class SegmentNames
{
    /// <summary>
    /// The segment a token names: an identifier, or a name in quotes, which is an error that
    /// still names the segment. Segment names hold no escapes, so the quotes come off by hand.
    /// A missing token names nothing, which is the one place that is said.
    /// </summary>
    public static string? Of(SyntaxToken token) => token.IsMissing ? null : token.Kind switch
    {
        SyntaxKind.Identifier or SyntaxKind.Register or SyntaxKind.Mnemonic => token.Text,
        SyntaxKind.StringLiteral => token.Text.Trim('"'),
        _ => null,
    };

    /// <summary>The address size <c>zp</c>, <c>abs</c> or <c>far</c> names, or null.</summary>
    public static AddressSize? ParseSize(string text) => text.ToLowerInvariant() switch
    {
        "zp" => AddressSize.ZeroPage,
        "abs" => AddressSize.Absolute,
        "far" => AddressSize.Far,
        _ => null,
    };
}

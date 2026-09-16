using System.Collections.Frozen;

namespace Norristown.Semantics;

/// <summary>How a segment is written: the shortcut directives, the quoted name and the size.</summary>
public static class SegmentNames
{
    private static readonly FrozenDictionary<string, string> shortcuts = new Dictionary<string, string>
    {
        [".zeropage"] = "ZEROPAGE",
        [".code"] = "CODE",
        [".bss"] = "BSS",
        [".data"] = "DATA",
        [".rodata"] = "RODATA",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The standard segment a shortcut directive names (§5.2), or null for <c>.segment</c>.</summary>
    public static string? Shortcut(string directive) => shortcuts.GetValueOrDefault(directive);

    /// <summary>
    /// The text of a quoted segment name. Segment names hold no escapes, so the quotes come
    /// off by hand rather than through string evaluation.
    /// </summary>
    public static string Unquote(string literal) => literal.Trim('"');

    /// <summary>The address size <c>zp</c>, <c>abs</c> or <c>far</c> names, or null.</summary>
    public static AddressSize? ParseSize(string text) => text.ToLowerInvariant() switch
    {
        "zp" => AddressSize.ZeroPage,
        "abs" => AddressSize.Absolute,
        "far" => AddressSize.Far,
        _ => null,
    };
}
